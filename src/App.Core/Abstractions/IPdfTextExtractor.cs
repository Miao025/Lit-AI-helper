namespace App.Core.Abstractions;

public interface IPdfTextExtractor
{
    /// Extract raw text from a PDF file. Should throw a meaningful exception on failure.
    Task<string> ExtractTextAsync(string filePath, CancellationToken ct = default);
}