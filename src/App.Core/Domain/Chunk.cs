namespace App.Core.Domain;

public sealed record Chunk(
    string Id,              // chunk id
    string DocumentId,
    int IndexInDocument,    // 0..N-1
    string Text,
    int? PageStart = null,
    int? PageEnd = null
);