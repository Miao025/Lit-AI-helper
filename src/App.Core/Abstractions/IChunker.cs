using App.Core.Domain;

namespace App.Core.Abstractions;

public interface IChunker
{
    /// Split a document text into searchable chunks.
    IReadOnlyList<Chunk> Chunk(string documentId, string text);
}