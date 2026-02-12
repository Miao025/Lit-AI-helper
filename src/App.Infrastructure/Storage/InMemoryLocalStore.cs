using App.Core.Abstractions;
using App.Core.Domain;
using System.Linq;

namespace App.Infrastructure.Storage;

public sealed class InMemoryLocalStore : ILocalStore
{
    private readonly Dictionary<string, Document> _docs = new();
    private readonly Dictionary<string, Chunk> _chunks = new();
    private readonly Dictionary<string, List<string>> _docToChunkIds = new();
    private readonly Dictionary<string, float[]> _embeddings = new();
    private readonly Dictionary<string, string> _docIdToHash = new();
    private readonly Dictionary<string, List<string>> _hashToDocIds = new(StringComparer.OrdinalIgnoreCase);

    public Task UpsertDocumentAsync(Document doc, CancellationToken ct = default)
    {
        _docs[doc.Id] = doc;
        _docToChunkIds.TryAdd(doc.Id, new List<string>());
        return Task.CompletedTask;
    }

    public Task<Document?> GetDocumentAsync(string documentId, CancellationToken ct = default)
        => Task.FromResult(_docs.TryGetValue(documentId, out var d) ? d : null);

    public Task RemoveDocumentAsync(string documentId, CancellationToken ct = default)
    {
        _docs.Remove(documentId);
        if (_docToChunkIds.TryGetValue(documentId, out var ids))
        {
            foreach (var id in ids) _chunks.Remove(id);
            _docToChunkIds.Remove(documentId);
        }

        // remove hash mappings
        if (_docIdToHash.TryGetValue(documentId, out var h))
        {
            _docIdToHash.Remove(documentId);
            if (_hashToDocIds.TryGetValue(h, out var list))
            {
                list.Remove(documentId);
                if (list.Count == 0) _hashToDocIds.Remove(h);
            }
        }

        return Task.CompletedTask;
    }

    public Task UpsertChunksAsync(IReadOnlyList<Chunk> chunks, CancellationToken ct = default)
    {
        foreach (var c in chunks)
        {
            _chunks[c.Id] = c;
            if (!_docToChunkIds.TryGetValue(c.DocumentId, out var list))
            {
                list = new List<string>();
                _docToChunkIds[c.DocumentId] = list;
            }
            if (!list.Contains(c.Id)) list.Add(c.Id);
        }
        return Task.CompletedTask;
    }

    public Task UpsertChunkEmbeddingsAsync(IReadOnlyList<(string ChunkId, float[] Embedding)> items, CancellationToken ct = default)
    {
        foreach (var (id, e) in items) _embeddings[id] = e;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(string ChunkId, float[] Embedding)>> GetAllChunkEmbeddingsAsync(CancellationToken ct = default)
    {
        var list = _embeddings.Select(kv => (kv.Key, kv.Value)).ToList();
        return Task.FromResult((IReadOnlyList<(string, float[])>)list);
    }

    public Task<Chunk?> GetChunkAsync(string chunkId, CancellationToken ct = default)
        => Task.FromResult(_chunks.TryGetValue(chunkId, out var c) ? c : null);

    public Task<IReadOnlyList<Chunk>> GetChunksByDocumentAsync(string documentId, CancellationToken ct = default)
    {
        if (!_docToChunkIds.TryGetValue(documentId, out var ids))
            return Task.FromResult((IReadOnlyList<Chunk>)Array.Empty<Chunk>());

        var list = new List<Chunk>(ids.Count);
        foreach (var id in ids)
            if (_chunks.TryGetValue(id, out var c)) list.Add(c);

        return Task.FromResult((IReadOnlyList<Chunk>)list);
    }

    public Task RemoveChunksByDocumentAsync(string documentId, CancellationToken ct = default)
    {
        if (_docToChunkIds.TryGetValue(documentId, out var ids))
        {
            foreach (var id in ids) _chunks.Remove(id);
            ids.Clear();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Chunk>> GetAllChunksAsync(CancellationToken ct = default)
    => Task.FromResult((IReadOnlyList<Chunk>)_chunks.Values.ToList());

    public Task<IReadOnlyList<Document>> GetAllDocumentsAsync(CancellationToken ct = default)
    => Task.FromResult((IReadOnlyList<Document>)_docs.Values.ToList());

    public Task UpdateDocumentContentHashAsync(string documentId, string contentHash, CancellationToken ct = default)
    {
        // detach old hash mapping if exists
        if (_docIdToHash.TryGetValue(documentId, out var old))
        {
            if (_hashToDocIds.TryGetValue(old, out var oldList))
            {
                oldList.Remove(documentId);
                if (oldList.Count == 0) _hashToDocIds.Remove(old);
            }
        }

        _docIdToHash[documentId] = contentHash;

        if (!_hashToDocIds.TryGetValue(contentHash, out var list))
        {
            list = new List<string>();
            _hashToDocIds[contentHash] = list;
        }

        if (!list.Contains(documentId))
            list.Add(documentId);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Document>> GetDocumentsByContentHashAsync(string contentHash, CancellationToken ct = default)
    {
        if (!_hashToDocIds.TryGetValue(contentHash, out var ids))
            return Task.FromResult((IReadOnlyList<Document>)Array.Empty<Document>());

        var result = new List<Document>();
        foreach (var id in ids)
        {
            if (_docs.TryGetValue(id, out var d))
                result.Add(d);
        }

        return Task.FromResult((IReadOnlyList<Document>)result);
    }
}

