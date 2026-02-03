using App.Core.Abstractions;
using App.Core.Domain;

namespace App.Core.Services;

public sealed class IndexingOrchestrator(
    IPdfTextExtractor extractor,
    IChunker chunker,
    IEmbedder embedder,
    IVectorIndex vectorIndex,
    ILocalStore store)
{
    public async Task IndexPdfAsync(string filePath, CancellationToken ct = default)
    {
        var fi = new FileInfo(filePath);
        var docId = $"{fi.FullName}:{fi.LastWriteTimeUtc.Ticks}:{fi.Length}"; // placeholder
        var doc = new Document(
            Id: docId,
            FilePath: fi.FullName,
            Title: Path.GetFileNameWithoutExtension(fi.Name),
            LastModifiedUtc: fi.LastWriteTimeUtc,
            FileSizeBytes: fi.Length);

        await store.UpsertDocumentAsync(doc, ct);

        // Rebuild doc chunks for now (simple & safe)
        await store.RemoveChunksByDocumentAsync(docId, ct);

        var text = await extractor.ExtractTextAsync(filePath, ct);
        var chunks = chunker.Chunk(docId, text);

        await store.UpsertChunksAsync(chunks, ct);

        // Embed + index
        foreach (var chunk in chunks)
        {
            var vec = await embedder.EmbedAsync(chunk.Text, ct);
            await vectorIndex.UpsertAsync(chunk.Id, vec, ct);
        }
    }
}
