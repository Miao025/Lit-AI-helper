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
  file_size_bytes INTEGER NOT NULL,

  
  file_name TEXT NULL,
  content_hash TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_documents_file_name ON documents(file_name);
CREATE INDEX IF NOT EXISTS idx_documents_content_hash ON documents(content_hash);

CREATE TABLE IF NOT EXISTS chunks (
  id TEXT PRIMARY KEY,
  document_id TEXT NOT NULL,
  index_in_document INTEGER NOT NULL,
  text TEXT NOT NULL,
  page_start INTEGER NULL,
  page_end INTEGER NULL,

  embedding BLOB NULL,
  embedding_dim INTEGER NULL,

  FOREIGN KEY(document_id) REFERENCES documents(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_chunks_document_id ON chunks(document_id);
CREATE INDEX IF NOT EXISTS idx_chunks_has_embedding ON chunks(embedding_dim);";

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
            @"INSERT INTO documents (id, file_path, title, last_modified_utc, file_size_bytes, file_name, content_hash)
            VALUES ($id, $path, $title, $lm, $size, $fname, $hash)
            ON CONFLICT(id) DO UPDATE SET
              file_path = excluded.file_path,
              title = excluded.title,
              last_modified_utc = excluded.last_modified_utc,
              file_size_bytes = excluded.file_size_bytes,
              file_name = excluded.file_name,
              content_hash = excluded.content_hash;";
        cmd.Parameters.AddWithValue("$id", doc.Id);
        cmd.Parameters.AddWithValue("$path", doc.FilePath);
        cmd.Parameters.AddWithValue("$title", (object?)doc.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lm", doc.LastModifiedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$size", doc.FileSizeBytes);
        cmd.Parameters.AddWithValue("$fname", Path.GetFileName(doc.FilePath));
        cmd.Parameters.AddWithValue("$hash", DBNull.Value); // Blank first, later fill with UpdateDocumentHashAsync

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

        // Command created once, executed multiple times with different parameters for each chunk
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

        // Parameters created once, values updated in loop
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);
        var pIdx = cmd.Parameters.Add("$idx", SqliteType.Integer);
        var pText = cmd.Parameters.Add("$text", SqliteType.Text);
        var pPs = cmd.Parameters.Add("$ps", SqliteType.Integer);
        var pPe = cmd.Parameters.Add("$pe", SqliteType.Integer);

        foreach (var c in chunks)
        {
            pId.Value = c.Id;
            pDoc.Value = c.DocumentId;
            pIdx.Value = c.IndexInDocument;
            pText.Value = c.Text;

            pPs.Value = c.PageStart.HasValue ? c.PageStart.Value : DBNull.Value;
            pPe.Value = c.PageEnd.HasValue ? c.PageEnd.Value : DBNull.Value;

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task UpsertChunkEmbeddingsAsync(
    IReadOnlyList<(string ChunkId, float[] Embedding)> items,
    CancellationToken ct = default)
    {
        if (items.Count == 0) return;

        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        using var tx = conn.BeginTransaction();

        // Command only created once, executed multiple times with different parameters
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            @"UPDATE chunks
          SET embedding = $emb, embedding_dim = $dim
          WHERE id = $id;";

        // only parameters for embedding update, other chunk data should be unchanged
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pDim = cmd.Parameters.Add("$dim", SqliteType.Integer);
        var pEmb = cmd.Parameters.Add("$emb", SqliteType.Blob);

        foreach (var (chunkId, emb) in items)
        {
            // only update embedding columns, other chunk data should be unchanged
            pId.Value = chunkId;
            pDim.Value = emb.Length;
            pEmb.Value = EmbeddingBlob.ToBytes(emb);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<(string ChunkId, float[] Embedding)>> GetAllChunkEmbeddingsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT id, embedding
          FROM chunks
          WHERE embedding IS NOT NULL
          ORDER BY id ASC;";

        var list = new List<(string, float[])>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var bytes = (byte[])reader["embedding"];
            var vec = EmbeddingBlob.FromBytes(bytes);
            list.Add((id, vec));
        }

        return list;
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

    public async Task UpdateDocumentContentHashAsync(string documentId, string contentHash, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE documents SET content_hash = $h WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$h", contentHash);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Document>> GetDocumentsByContentHashAsync(string contentHash, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT id, file_path, title, last_modified_utc, file_size_bytes
          FROM documents
          WHERE content_hash = $h
          ORDER BY last_modified_utc DESC;";
        cmd.Parameters.AddWithValue("$h", contentHash);

        var list = new List<Document>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Document(
                Id: reader.GetString(0),
                FilePath: reader.GetString(1),
                Title: reader.IsDBNull(2) ? null : reader.GetString(2),
                LastModifiedUtc: DateTimeOffset.Parse(reader.GetString(3)),
                FileSizeBytes: reader.GetInt64(4)
            ));
        }
        return list;
    }

    public async Task<IReadOnlyList<Document>> GetAllDocumentsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT id, file_path, title, last_modified_utc, file_size_bytes
          FROM documents
          ORDER BY last_modified_utc DESC;";

        var list = new List<Document>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Document(
                Id: reader.GetString(0),
                FilePath: reader.GetString(1),
                Title: reader.IsDBNull(2) ? null : reader.GetString(2),
                LastModifiedUtc: DateTimeOffset.Parse(reader.GetString(3)),
                FileSizeBytes: reader.GetInt64(4)
            ));
        }

        return list;
    }

}