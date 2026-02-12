using System.Text;
using System.Text.RegularExpressions;
using App.Core.Abstractions;
using App.Core.Domain;

namespace App.Infrastructure.Chunking;

public sealed class DefaultChunker : IChunker
{
    private const int ChunkSentenceSize = 8;
    private const int SentenceOverlap = 2;
    private const int MinChunkCharLength = 40;

    private static readonly string[] Abbreviations =
    {
        // Latin / academic
        "e.g.", "i.e.", "et al.", "etc.", "cf.", "approx.", "ca.", "al.",

        // People / titles
        "Dr.", "Prof.", "Mr.", "Ms.", "Mrs.", "Sr.", "Jr.",

        // Figures / tables / equations (all disciplines)
        "Fig.", "Figs.", "Tab.", "Tabs.", "Table.", "Tables.",
        "Eq.", "Eqs.", "Eqn.", "Eqns.",

        // Sections / references
        "Sec.", "Secs.", "Ch.", "Chap.", "Vol.", "No.", "Nos.",
        "Ref.", "Refs.",

        // Publishing / citations
        "ed.", "eds.", "rev.", "repr.",

        // Time / measurement
        "yr.", "yrs.", "wk.", "wks.", "mo.", "mos.",

        // Common scientific abbreviations
        "vs.", "min.", "max.", "avg.", "std.",

        // Geography / institutions
        "U.S.", "U.K.", "E.U.", "U.N.",

        // Degrees
        "Ph.D.", "M.Sc.", "B.Sc.", "M.D.", "D.Phil."
    };


    private const string DotPlaceholder = "∯";

    public IReadOnlyList<Chunk> Chunk(string documentId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<Chunk>();

        var sentences = SplitIntoSentences(text);
        if (sentences.Count == 0)
            return Array.Empty<Chunk>();

        var chunks = new List<Chunk>();
        var idx = 0;

        for (int start = 0; start < sentences.Count; start += (ChunkSentenceSize - SentenceOverlap))
        {
            var sb = new StringBuilder();
            var sentenceCount = 0;

            for (int i = start; i < sentences.Count && sentenceCount < ChunkSentenceSize; i++)
            {
                sb.Append(sentences[i]).Append(' ');
                sentenceCount++;
            }

            var chunkText = sb.ToString().Trim();
            if (chunkText.Length < MinChunkCharLength)
                continue;

            var chunkId = $"{documentId}::chunk::{idx}";

            chunks.Add(new Chunk(
                Id: chunkId,
                DocumentId: documentId,
                IndexInDocument: idx,
                Text: chunkText
            ));

            idx++;

            if (start + ChunkSentenceSize >= sentences.Count)
                break;
        }

        return chunks;
    }

    private static List<string> SplitIntoSentences(string text)
    {
        var protectedText = ProtectAbbreviations(text);

        // 80/20 sentence split: (.!?)(\s+)
        var parts = Regex.Split(
            protectedText,
            @"(?<=[.!?])\s+",
            RegexOptions.Compiled);

        var sentences = new List<string>(parts.Length);

        foreach (var p in parts)
        {
            var restored = RestoreAbbreviations(p.Trim());
            if (!string.IsNullOrWhiteSpace(restored))
            {
                sentences.Add(restored);
            }
        }

        return sentences;
    }

    private static string ProtectAbbreviations(string text)
    {
        var result = text;

        // Whitelisting approach: only protect known abbreviations to minimize false positives.
        foreach (var abbr in Abbreviations)
        {
            var safe = abbr.Replace(".", DotPlaceholder);
            result = Regex.Replace(
                result,
                Regex.Escape(abbr),
                safe,
                RegexOptions.Compiled);
        }

        // Protect patterns like "U.S." or "E.U." where we have multiple single-letter abbreviations separated by dots, which should not split sentences.
        // 例如：U.S., U.K., E.U., H.s., A.B.C.
        result = Regex.Replace(
            result,
            @"\b(?:[A-Za-z]\.){2,}",
            m => m.Value.Replace(".", DotPlaceholder),
            RegexOptions.Compiled);

        // Protect cases like "Fig. 1.2" where we have a number after the dot, which should not split sentences.
        result = Regex.Replace(
            result,
            @"(?<=\d)\.(?=\d)",
            DotPlaceholder,
            RegexOptions.Compiled);

        return result;
    }

    private static string RestoreAbbreviations(string text)
    {
        return text.Replace(DotPlaceholder, ".");
    }
}

