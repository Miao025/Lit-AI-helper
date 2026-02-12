using System.IO;
using App.Core.Abstractions;
using App.Core.Domain;

namespace App.Core.Services;

public sealed class IndexingOrchestrator(
    IPdfTextExtractor extractor,
    ITextCleaner cleaner,
    IChunker chunker,
    IEmbedder embedder,
    IVectorIndex vectorIndex,
    ILocalStore store)
{
    public async Task<IndexReport> IndexPdfAsync(string filePath, CancellationToken ct = default)
    {
        var fi = new FileInfo(filePath);

        // IMPORTANT: keep docId stable for the same file version
        var docId = $"{fi.FullName}:{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";

        var doc = new Document(
            Id: docId,
            FilePath: fi.FullName,
            Title: Path.GetFileNameWithoutExtension(fi.Name),
            LastModifiedUtc: fi.LastWriteTimeUtc,
            FileSizeBytes: fi.Length);

        await store.UpsertDocumentAsync(doc, ct);

        // Remove old chunks for same docId (same file version)
        await store.RemoveChunksByDocumentAsync(docId, ct);

        var raw = await extractor.ExtractTextAsync(filePath, ct);
        var cleaned = cleaner.Clean(raw);

        var chunks = chunker.Chunk(docId, cleaned);

        await store.UpsertChunksAsync(chunks, ct);

        var embeddingItems = new List<(string ChunkId, float[] Embedding)>(chunks.Count);
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var vec = await embedder.EmbedAsync(chunk.Text, ct);

            await vectorIndex.UpsertAsync(chunk.Id, vec, ct);
            embeddingItems.Add((chunk.Id, vec));
        }

        await store.UpsertChunkEmbeddingsAsync(embeddingItems, ct);

        return new IndexReport(
            DocumentId: docId,
            RawLength: raw?.Length ?? 0,
            CleanedLength: cleaned?.Length ?? 0,
            ChunkCount: chunks.Count
        );
    }
}
