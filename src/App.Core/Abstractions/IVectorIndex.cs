namespace App.Core.Abstractions;

public interface IVectorIndex
{
    int Dimension { get; }

    /// Add or update an item vector.
    Task UpsertAsync(string itemId, float[] vector, CancellationToken ct = default);

    /// Remove an item.
    Task RemoveAsync(string itemId, CancellationToken ct = default);

    /// Search topK nearest items; returns (itemId, score) sorted by score desc.
    Task<IReadOnlyList<(string Id, float Score)>> SearchAsync(float[] queryVector, int topK, CancellationToken ct = default);

    /// Optional persistence hooks (no-op in memory implementation).
    Task SaveAsync(string path, CancellationToken ct = default);
    Task LoadAsync(string path, CancellationToken ct = default);
}