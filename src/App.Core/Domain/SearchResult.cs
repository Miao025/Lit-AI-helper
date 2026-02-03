namespace App.Core.Domain;

public sealed record SearchResult(
    Chunk Chunk,
    Document Document,
    float Score
);