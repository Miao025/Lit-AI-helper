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
    Task UpsertChunkEmbeddingsAsync(IReadOnlyList<(string ChunkId, float[] Embedding)> items, CancellationToken ct = default);
    Task<IReadOnlyList<(string ChunkId, float[] Embedding)>> GetAllChunkEmbeddingsAsync(CancellationToken ct = default);
    Task<Chunk?> GetChunkAsync(string chunkId, CancellationToken ct = default);
    Task<IReadOnlyList<Chunk>> GetChunksByDocumentAsync(string documentId, CancellationToken ct = default);
    Task<IReadOnlyList<Chunk>> GetAllChunksAsync(CancellationToken ct = default);
    Task RemoveChunksByDocumentAsync(string documentId, CancellationToken ct = default);
    Task<IReadOnlyList<App.Core.Domain.Document>> GetDocumentsByContentHashAsync(string contentHash, CancellationToken ct = default);
    Task<IReadOnlyList<App.Core.Domain.Document>> GetAllDocumentsAsync(CancellationToken ct = default);
    Task UpdateDocumentContentHashAsync(string documentId, string contentHash, CancellationToken ct = default);
}