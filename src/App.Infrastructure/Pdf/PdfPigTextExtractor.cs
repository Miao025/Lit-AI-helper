using System.Text;
using App.Core.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace App.Infrastructure.Pdf;

public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public Task<string> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath is empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("PDF file not found.", filePath);

        // PdfPig is synchronous; wrap as Task for interface compatibility.
        // (We'll keep it simple for now.)
        var sb = new StringBuilder(capacity: 64 * 1024);

        try
        {
            using var document = PdfDocument.Open(filePath);

            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                // Simple text extraction (layout won't be perfect, but good enough to start)
                var text = ContentOrderTextExtractor.GetText(page);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text.Trim());

                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Make error message helpful for end users
            throw new InvalidOperationException($"Failed to extract text from PDF: {Path.GetFileName(filePath)}. {ex.Message}", ex);
        }

        return Task.FromResult(sb.ToString());
    }
}
