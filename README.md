# Lit-AI-helper

Desktop app for indexing local PDF files into a lightweight local store and running semantic search over the extracted text.

## Tech stack / framework

- **.NET**: `net10.0` (all projects target `.NET 10`)
- **UI**: **Avalonia UI** (`Avalonia`, `Avalonia.Desktop`, Fluent theme)
- **Storage**: **SQLite** via `Microsoft.Data.Sqlite` (plus an in-memory store for dev/tests)
- **PDF text extraction**: `PdfPig`
- **Embeddings & tokenization**:
  - `Tokenizers.HuggingFace`
  - `Microsoft.ML.OnnxRuntime` (for running local ONNX models)
- **Tests**: `xUnit` + `Microsoft.NET.Test.Sdk` + `coverlet.collector`

## Solution layout

The repository is organized as a Clean Architecture-style solution:

- `src/App.Desktop` – Avalonia desktop application (views + view-models)
- `src/App.Core` – Domain models + application services + abstractions (interfaces)
- `src/App.Infrastructure` – Implementations of Core abstractions (SQLite, PDF, ONNX, etc.)
- `src/App.Tests` – Unit tests

Key abstraction (Core):

- `ILocalStore` in `src/App.Core/Abstractions/ILocalStore.cs` defines persistence for documents, chunks, and embeddings.

Key infrastructure implementations:

- `SqliteLocalStore` in `src/App.Infrastructure/Storage/Sqlite/SqliteLocalStore.cs`
- `InMemoryLocalStore` in `src/App.Infrastructure/Storage/InMemoryLocalStore.cs`

## Core workflow

### 1) Indexing a PDF

Implemented by `IndexingOrchestrator` (`src/App.Core/Services/IndexingOrchestractor.cs`). Pipeline:

1. Build a stable document id (full path + last write time + size).
2. Upsert document record (`ILocalStore.UpsertDocumentAsync`).
3. Remove old chunks for the same document/version (`ILocalStore.RemoveChunksByDocumentAsync`).
4. Extract raw PDF text (`IPdfTextExtractor`, infrastructure uses `PdfPig`).
5. Clean/normalize text (`ITextCleaner`).
6. Chunk text (`IChunker`).
7. Persist chunks (`ILocalStore.UpsertChunksAsync`).
8. Embed each chunk (`IEmbedder.EmbedAsync` → `float[]`).
9. Upsert vectors into index (`IVectorIndex.UpsertAsync`).
10. Persist embeddings (`ILocalStore.UpsertChunkEmbeddingsAsync`).
11. Return `IndexReport` (document id, lengths, chunk count).

### 2) Searching

Implemented by `SearchService` (`src/App.Core/Services/SearchService.cs`):

1. Embed query text (`IEmbedder.EmbedAsync`).
2. Vector search (`IVectorIndex.SearchAsync`) returns `(chunkId, score)` hits.
3. Hydrate results by loading `Chunk` + owning `Document` from `ILocalStore`.
4. Return results with similarity score and source metadata.

## Techniques used

### Clean Architecture / separation of concerns

- `App.Core`: interfaces + application services.
- `App.Infrastructure`: concrete implementations.
- `App.Desktop`: UI layer (MVVM) consuming Core.

### Local-first persistence with SQLite

- Schema is created on startup by `SqliteLocalStore.Initialize()`.
- Documents/chunks stored relationally.
- Embeddings stored as BLOBs.

### Semantic search via embeddings

- Chunks and queries map to vectors.
- Similarity search retrieves `topK` relevant chunks.

### Async/await + cancellation

- Indexing/search/store operations are async.
- `CancellationToken` flows through the pipeline for responsiveness.

### Batch/transactional writes

- SQLite chunk and embedding operations use transactions and reused commands.

## Build & run

Prerequisites:

- .NET SDK with support for `net10.0`

Commands:

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/App.Desktop/App.Desktop.csproj
```
