namespace App.Core.Domain;

public sealed record IndexReport(
    string DocumentId,
    int RawLength,
    int CleanedLength,
    int ChunkCount
);
