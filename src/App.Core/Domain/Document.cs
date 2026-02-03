namespace App.Core.Domain;

public sealed record Document(
    string Id,              // stable id (e.g., hash) - can be placeholder for now
    string FilePath,
    string? Title,
    DateTimeOffset LastModifiedUtc,
    long FileSizeBytes
);