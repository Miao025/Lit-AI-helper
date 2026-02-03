using App.Core.Abstractions;
using App.Core.Domain;

namespace App.Core.Services;

public sealed class SearchService(IEmbedder embedder, IVectorIndex index, ILocalStore store)
{
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken ct = default)
    {
        var qv = await embedder.EmbedAsync(query, ct);
        var hits = await index.SearchAsync(qv, topK, ct);

        var results = new List<SearchResult>(hits.Count);
        foreach (var (chunkId, score) in hits)
        {
            var chunk = await store.GetChunkAsync(chunkId, ct);
            if (chunk is null) continue;

            var doc = await store.GetDocumentAsync(chunk.DocumentId, ct);
            if (doc is null) continue;

            results.Add(new SearchResult(chunk, doc, score));
        }

        return results;
    }
}
