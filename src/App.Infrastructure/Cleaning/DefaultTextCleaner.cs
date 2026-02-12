using System.Text;
using System.Text.RegularExpressions;
using App.Core.Abstractions;

namespace App.Infrastructure.Cleaning;

public sealed class DefaultTextCleaner : ITextCleaner
{
    private static string ExtractMainBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var abstractMatch = Regex.Match(
            text,
            @"\b(Abstract|ABSTRACT)\b",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        var referencesMatch = Regex.Match(
            text,
            @"\b(References|REFERENCES)\b",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        int startIndex;
        int endIndex;

        if (abstractMatch.Success)
        {
            startIndex = abstractMatch.Index;
        }
        else
        {
            startIndex = 0;
        }

        if (referencesMatch.Success &&
            referencesMatch.Index > startIndex)
        {
            endIndex = referencesMatch.Index;
        }
        else
        {
            endIndex = text.Length;
        }

        return text.Substring(startIndex, endIndex - startIndex);
    }

    // Common header/footer/rights noise (you can extend this list later)
    private static readonly string[] BannedSubstrings =
    {
        "all rights reserved",
        "journal compilation",
        "copyright",
        "©",
        "doi:",
        "issn",
        "isbn",
        "downloaded",
        "https",
        "email",
        "@"
    };

    // Lines that are mostly punctuation or page numbers
    private static readonly Regex MostlyPunctOrNumber = new(@"^[\s\p{P}\d]+$", RegexOptions.Compiled);

    // Long ALL CAPS lines are often titles/headers; we don't always remove them,
    // but we can drop repeated ones and very short shouty lines.
    private static bool IsMostlyUpper(string s)
    {
        int letters = 0, uppers = 0;
        foreach (var ch in s)
        {
            if (char.IsLetter(ch))
            {
                letters++;
                if (char.IsUpper(ch)) uppers++;
            }
        }
        return letters >= 8 && uppers >= (int)(letters * 0.85);
    }

    public string Clean(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;
        // First, we can try to extract the text between Abstract and References
        rawText = ExtractMainBody(rawText);

        // Normalize newlines
        var text = rawText.Replace("\r\n", "\n");

        // Split into lines and filter obvious junk
        var lines = text.Split('\n');

        // Detect repeated lines (headers/footers repeated across pages)
        // We'll count normalized lines and remove those that appear "often enough".
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var key = NormalizeLine(line);
            if (key.Length == 0) continue;
            freq.TryGetValue(key, out var c);
            freq[key] = c + 1;
        }

        // A line repeated 3+ times is very likely a header/footer in academic PDFs.
        var repeated = new HashSet<string>(
            freq.Where(kv => kv.Value >= 3).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder(rawText.Length);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                sb.AppendLine();
                continue;
            }

            var norm = NormalizeLine(trimmed);

            // Remove repeated header/footer lines
            if (repeated.Contains(norm))
                continue;

            // Remove lines that are only numbers/punctuation (page numbers, separators)
            if (trimmed.Length <= 20 && MostlyPunctOrNumber.IsMatch(trimmed))
                continue;

            // Remove obvious rights/citation boilerplate lines
            var lower = trimmed.ToLowerInvariant();
            if (BannedSubstrings.Any(b => lower.Contains(b)))
            {
                // Keep DOI lines sometimes useful? For now drop them (you can change later)
                continue;
            }

            // Drop very short ALL CAPS lines (often running headers)
            if (trimmed.Length <= 60 && IsMostlyUpper(trimmed))
                continue;

            sb.AppendLine(trimmed);
        }

        var cleaned = sb.ToString();

        // Fix hard-wrapped lines:
        // Turn single newlines into spaces when it looks like sentence continuation,
        // while preserving paragraph breaks (double newline).
        cleaned = MergeHardWraps(cleaned);

        // Collapse excessive blank lines
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");

        return cleaned.Trim();
    }

    private static string NormalizeLine(string line)
    {
        // Normalize spaces & remove digits that often change with page numbers
        var s = Regex.Replace(line.Trim(), @"\s+", " ");
        s = Regex.Replace(s, @"\d+", ""); // remove numbers to catch "Page 1/2/3" etc.
        return s.Trim();
    }

    private static string MergeHardWraps(string text)
    {
        // Approach:
        // - Keep paragraph breaks (\n\n)
        // - For single \n within a paragraph:
        //   - If previous line ends with hyphen, join without space (de-hyphenate)
        //   - Else join with a space
        // This is simple but effective for many PDFs.
        var parts = text.Replace("\r\n", "\n").Split("\n\n");

        for (int i = 0; i < parts.Length; i++)
        {
            var lines = parts[i]
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length <= 1) continue;

            var sb = new StringBuilder(parts[i].Length);
            for (int j = 0; j < lines.Length; j++)
            {
                var cur = lines[j];

                if (j == 0)
                {
                    sb.Append(cur);
                    continue;
                }

                // de-hyphenate
                if (sb.Length > 0 && sb[^1] == '-')
                {
                    sb.Length -= 1; // remove trailing '-'
                    sb.Append(cur);
                }
                else
                {
                    sb.Append(' ');
                    sb.Append(cur);
                }
            }

            parts[i] = sb.ToString();
        }

        return string.Join("\n\n", parts);
    }
}
