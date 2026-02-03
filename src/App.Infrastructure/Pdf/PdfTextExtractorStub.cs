using App.Core.Abstractions;

namespace App.Infrastructure.Pdf;

public sealed class PdfTextExtractorStub : IPdfTextExtractor
{
    public async Task<string> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".txt")
            return await File.ReadAllTextAsync(filePath, ct);

        // For now: placeholder text to prove the pipeline works.
        // Next step will replace this with PdfPig extraction.
        return $"[PDF STUB] {Path.GetFileName(filePath)}";
    }
}
