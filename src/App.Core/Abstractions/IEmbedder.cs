namespace App.Core.Abstractions;

public interface IEmbedder
{
    int Dimension { get; }

    /// Convert text to an embedding vector (length = Dimension).
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}