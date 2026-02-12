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
CREATE INDEX IF NOT EXISTS idx_chunks_has_embedding ON chunks(embedding_dim);
