using App.Core.Abstractions;
using App.Core.Domain;

namespace App.Infrastructure.Chunking;

public sealed class DefaultChunker : IChunker
{
    public IReadOnlyList<Chunk> Chunk(string documentId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<Chunk>();

        // split by blank lines (handles \r\n and \n)
        var parts = text
            .Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var chunks = new List<Chunk>();
        var idx = 0;

        foreach (var p in parts)
        {
            var cleaned = p.Trim();
            if (cleaned.Length < 40) continue; // ignore very short

            var chunkId = $"{documentId}::chunk::{idx}";
            chunks.Add(new Chunk(
                Id: chunkId,
                DocumentId: documentId,
                IndexInDocument: idx,
                Text: cleaned
            ));
            idx++;
        }

        return chunks;
    }
}
