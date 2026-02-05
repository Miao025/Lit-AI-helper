using App.Core.Abstractions;
using App.Core.Domain;
using Microsoft.Data.Sqlite;

namespace App.Infrastructure.Storage.Sqlite;

public sealed class SqliteLocalStore : ILocalStore
{
    private readonly string _dbPath;

    public SqliteLocalStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        Initialize();
    }

    private string ConnStr => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        ForeignKeys = true
    }.ToString();

    private void Initialize()
    {
        using var conn = new SqliteConnection(ConnStr);
        conn.Open();

        // Load schema from embedded file (simple version: read from disk)
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Schema.sql");

        // If Schema.sql isn't copied to output, fallback to inline schema.
        var schemaSql = File.Exists(schemaPath)
            ? File.ReadAllText(schemaPath)
            : @"
CREATE TABLE IF NOT EXISTS documents (
  id TEXT PRIMARY KEY,
  file_path TEXT NOT NULL,
  title TEXT NULL,
  last_modified_utc TEXT NOT NULL,
  file_size_bytes INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS chunks (
  id TEXT PRIMARY KEY,
  document_id TEXT NOT NULL,
  index_in_document INTEGER NOT NULL,
  text TEXT NOT NULL,
  page_start INTEGER NULL,
  page_end INTEGER NULL,
  FOREIGN KEY(document_id) REFERENCES documents(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_chunks_document_id ON chunks(document_id);";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schemaSql;
        cmd.ExecuteNonQuery();
    }

    public async Task UpsertDocumentAsync(Document doc, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"INSERT INTO documents (id, file_path, title, last_modified_utc, file_size_bytes)
              VALUES ($id, $path, $title, $lm, $size)
              ON CONFLICT(id) DO UPDATE SET
                file_path = excluded.file_path,
                title = excluded.title,
                last_modified_utc = excluded.last_modified_utc,
                file_size_bytes = excluded.file_size_bytes;";
        cmd.Parameters.AddWithValue("$id", doc.Id);
        cmd.Parameters.AddWithValue("$path", doc.FilePath);
        cmd.Parameters.AddWithValue("$title", (object?)doc.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lm", doc.LastModifiedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$size", doc.FileSizeBytes);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Document?> GetDocumentAsync(string documentId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT id, file_path, title, last_modified_utc, file_size_bytes
              FROM documents WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", documentId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new Document(
            Id: reader.GetString(0),
            FilePath: reader.GetString(1),
            Title: reader.IsDBNull(2) ? null : reader.GetString(2),
            LastModifiedUtc: DateTimeOffset.Parse(reader.GetString(3)),
            FileSizeBytes: reader.GetInt64(4)
        );
    }

    public async Task RemoveDocumentAsync(string documentId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"DELETE FROM documents WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", documentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertChunksAsync(IReadOnlyList<Chunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        using var tx = conn.BeginTransaction();

        foreach (var c in chunks)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                @"INSERT INTO chunks (id, document_id, index_in_document, text, page_start, page_end)
                  VALUES ($id, $doc, $idx, $text, $ps, $pe)
                  ON CONFLICT(id) DO UPDATE SET
                    document_id = excluded.document_id,
                    index_in_document = excluded.index_in_document,
                    text = excluded.text,
                    page_start = excluded.page_start,
                    page_end = excluded.page_end;";
            cmd.Parameters.AddWithValue("$id", c.Id);
            cmd.Parameters.AddWithValue("$doc", c.DocumentId);
            cmd.Parameters.AddWithValue("$idx", c.IndexInDocument);
            cmd.Parameters.AddWithValue("$text", c.Text);
            cmd.Parameters.AddWithValue("$ps", (object?)c.PageStart ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pe", (object?)c.PageEnd ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<Chunk?> GetChunkAsync(string chunkId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT id, document_id, index_in_document, text, page_start, page_end
              FROM chunks WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", chunkId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new Chunk(
            Id: reader.GetString(0),
            DocumentId: reader.GetString(1),
            IndexInDocument: reader.GetInt32(2),
            Text: reader.GetString(3),
            PageStart: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            PageEnd: reader.IsDBNull(5) ? null : reader.GetInt32(5)
        );
    }

    public async Task<IReadOnlyList<Chunk>> GetChunksByDocumentAsync(string documentId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT id, document_id, index_in_document, text, page_start, page_end
              FROM chunks WHERE document_id = $doc
              ORDER BY index_in_document ASC;";
        cmd.Parameters.AddWithValue("$doc", documentId);

        var list = new List<Chunk>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Chunk(
                Id: reader.GetString(0),
                DocumentId: reader.GetString(1),
                IndexInDocument: reader.GetInt32(2),
                Text: reader.GetString(3),
                PageStart: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                PageEnd: reader.IsDBNull(5) ? null : reader.GetInt32(5)
            ));
        }

        return list;
    }

    public async Task RemoveChunksByDocumentAsync(string documentId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"DELETE FROM chunks WHERE document_id = $doc;";
        cmd.Parameters.AddWithValue("$doc", documentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Chunk>> GetAllChunksAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT id, document_id, index_in_document, text, page_start, page_end
          FROM chunks
          ORDER BY document_id ASC, index_in_document ASC;";

        var list = new List<Chunk>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Chunk(
                Id: reader.GetString(0),
                DocumentId: reader.GetString(1),
                IndexInDocument: reader.GetInt32(2),
                Text: reader.GetString(3),
                PageStart: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                PageEnd: reader.IsDBNull(5) ? null : reader.GetInt32(5)
            ));
        }

        return list;
    }

}