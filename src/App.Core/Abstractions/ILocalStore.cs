using App.Core.Domain;

namespace App.Core.Abstractions;

public interface ILocalStore
{
    // Documents
    Task UpsertDocumentAsync(Document doc, CancellationToken ct = default);
    Task<Document?> GetDocumentAsync(string documentId, CancellationToken ct = default);
    Task RemoveDocumentAsync(string documentId, CancellationToken ct = default);

    // Chunks
    Task UpsertChunksAsync(IReadOnlyList<Chunk> chunks, CancellationToken ct = default);
    Task<Chunk?> GetChunkAsync(string chunkId, CancellationToken ct = default);
    Task<IReadOnlyList<Chunk>> GetChunksByDocumentAsync(string documentId, CancellationToken ct = default);
    Task RemoveChunksByDocumentAsync(string documentId, CancellationToken ct = default);
}