using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using App.Core.Abstractions;
using App.Core.Services;

using App.Infrastructure.Cleaning;
using App.Infrastructure.Chunking;
using App.Infrastructure.Pdf;
using App.Infrastructure.Storage.Sqlite;
using App.Infrastructure.Vector;
using App.Infrastructure.Embedding.Onnx;

namespace App.Desktop.ViewModels;

public enum DuplicateDecision
{
    UseExistingSkip,
    IndexAnyway,
    ReplaceExisting
}

public sealed class DuplicateInfo
{
    public required string NewFilePath { get; init; }
    public required string ContentHash { get; init; }
    public required IReadOnlyList<global::App.Core.Domain.Document> ExistingDocs { get; init; }
}

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ====== UI-bound ======
    private string? _selectedFilePath;
    public string? SelectedFilePath
    {
        get => _selectedFilePath;
        private set { _selectedFilePath = value; OnPropertyChanged(); }
    }

    private string _query = "";
    public string Query
    {
        get => _query;
        set { _query = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();

    private SearchResultItem? _selectedResult;
    public SearchResultItem? SelectedResult
    {
        get => _selectedResult;
        set { _selectedResult = value; OnPropertyChanged(); }
    }

    private string _statusText = "Ready.";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    // ====== Dependency fields ======
    private readonly IPdfTextExtractor _extractor;
    private readonly ITextCleaner _cleaner;
    private readonly IChunker _chunker;
    private readonly ILocalStore _store;
    private readonly IEmbedder _embedder;

    private IVectorIndex _index;
    private IndexingOrchestrator _indexer;
    private SearchService _search;

    // ====== Model paths ======
    private readonly string _modelDir;
    private readonly string _modelOnnxPath;
    private readonly string _tokenizerJsonPath;

    // ====== Selected sources ======
    private readonly HashSet<string> _pdfPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folderPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// UI layer should set this to show a dialog and return user's decision.
    /// If null, default is UseExistingSkip.
    /// </summary>
    public Func<DuplicateInfo, Task<DuplicateDecision>>? DuplicateResolver { get; set; }
    public Func<IReadOnlyList<global::App.Core.Domain.Document>, Task<bool>>? MissingFilesDeleteConfirm { get; set; }

    public MainWindowViewModel()
    {
        _extractor = new PdfPigTextExtractor();
        _cleaner = new DefaultTextCleaner();
        _chunker = new DefaultChunker();

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lit-AI-helper",
            "library.sqlite");

        _store = new SqliteLocalStore(dbPath);

        _modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lit-AI-helper",
            "models",
            "nomic-embed-text-v1.5");

        _modelOnnxPath = Path.Combine(_modelDir, "model.onnx");
        _tokenizerJsonPath = Path.Combine(_modelDir, "tokenizer.json");

        _embedder = CreateEmbedderOrThrow();

        _index = new InMemoryVectorIndex(768); // replaced in RebuildIndexAsync (dim read from DB)
        _indexer = new IndexingOrchestrator(_extractor, _cleaner, _chunker, _embedder, _index, _store);
        _search = new SearchService(_embedder, _index, _store);

        SelectedFilePath = "";
        StatusText = "Ready. Pick files or folders, then index.";
    }

    private IEmbedder CreateEmbedderOrThrow()
    {
        Directory.CreateDirectory(_modelDir);

        if (!File.Exists(_modelOnnxPath) || !File.Exists(_tokenizerJsonPath))
            throw new FileNotFoundException(
                "Model files not found.\n\n" +
                $"Please place:\n- {_modelOnnxPath}\n- {_tokenizerJsonPath}\n\n" +
                "Then restart the app.");

        return new OnnxEmbedder(_modelOnnxPath, _tokenizerJsonPath, maxLength: 512);
    }

    // ====== Add sources ======
    public void AddPdfPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(path)) return;

        _pdfPaths.Add(path);
        UpdateSelectedDisplay();
    }

    public void AddFolderPath(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (!Directory.Exists(folder)) return;

        _folderPaths.Add(folder);
        UpdateSelectedDisplay();
    }

    public void OpenSelectedPdf()
    {
        if (SelectedResult is null) return;

        var path = SelectedResult.FilePath;
        if (!File.Exists(path))
        {
            StatusText = "File not found on disk (moved/deleted).";
            return;
        }

        try
        {
            // Cross-platform: Windows/macOS default app
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open PDF: {ex.Message}";
        }
    }

    private void UpdateSelectedDisplay()
    {
        var count = _pdfPaths.Count + _folderPaths.Count;
        if (count == 0)
        {
            SelectedFilePath = "";
            return;
        }

        // Display: first item + count
        var first = _pdfPaths.Count > 0 ? GetAny(_pdfPaths) : GetAny(_folderPaths);
        SelectedFilePath = $"{first}   (+{count - 1} more)";
    }

    private static string GetAny(HashSet<string> set)
    {
        foreach (var s in set) return s;
        return "";
    }

    // ====== Startup load: DB -> index (no re-embedding) ======
    public async Task CheckMissingFilesAndMaybeDeleteAsync(CancellationToken ct = default)
    {
        var docs = await _store.GetAllDocumentsAsync(ct);

        var missing = new List<global::App.Core.Domain.Document>();
        foreach (var d in docs)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(d.FilePath))
                missing.Add(d);
        }

        if (missing.Count == 0)
            return;

        bool shouldDelete = MissingFilesDeleteConfirm is null
            ? false // default: do nothing if UI didn't wire it
            : await MissingFilesDeleteConfirm(missing);

        if (!shouldDelete)
            return;

        foreach (var d in missing)
            await _store.RemoveDocumentAsync(d.Id, ct);
    }

    public async Task RebuildIndexAsync(CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            await CheckMissingFilesAndMaybeDeleteAsync(ct);

            StatusText = "Loading embeddings from local database...";

            var items = await _store.GetAllChunkEmbeddingsAsync(ct);
            if (items.Count == 0)
            {
                StatusText = "No embeddings found yet. Pick PDFs and click Index.";
                return;
            }

            var dim = items[0].Embedding.Length;
            _index = new InMemoryVectorIndex(dim);

            _indexer = new IndexingOrchestrator(_extractor, _cleaner, _chunker, _embedder, _index, _store);
            _search = new SearchService(_embedder, _index, _store);

            int done = 0;
            foreach (var (chunkId, vec) in items)
            {
                ct.ThrowIfCancellationRequested();
                await _index.UpsertAsync(chunkId, vec, ct);

                done++;
                if (done % 500 == 0)
                {
                    StatusText = $"Loading embeddings... {done}/{items.Count}";
                }
            }

            StatusText = $"Index loaded: {done} chunks ready (dim={dim}).";
        }
        catch (Exception ex)
        {
            StatusText = $"Index load failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ====== Batch index ======
    public async Task IndexAsync(CancellationToken ct = default)
    {
        var files = ResolveAllPdfFiles();
        if (files.Count == 0)
        {
            StatusText = "No PDFs selected. Use 'Pick PDFs' or 'Pick Folder'.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = $"Indexing {files.Count} PDFs...";

            int done = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                StatusText = $"[{done + 1}/{files.Count}] Checking: {Path.GetFileName(file)}";

                // 1) content hash
                var hash = await ComputeSha256HexAsync(file, ct);

                // 2) duplicate check by hash
                var existing = await _store.GetDocumentsByContentHashAsync(hash, ct);

                if (existing.Count > 0)
                {
                    var info = new DuplicateInfo
                    {
                        NewFilePath = file,
                        ContentHash = hash,
                        ExistingDocs = existing
                    };

                    var decision = DuplicateResolver is null
                        ? DuplicateDecision.UseExistingSkip
                        : await DuplicateResolver(info);

                    if (decision == DuplicateDecision.UseExistingSkip)
                    {
                        done++;
                        StatusText = $"Skipped duplicate {done}/{files.Count}: {Path.GetFileName(file)}";
                        continue;
                    }

                    if (decision == DuplicateDecision.ReplaceExisting)
                    {
                        // remove all existing duplicates (simple policy)
                        foreach (var d in existing)
                            await _store.RemoveDocumentAsync(d.Id, ct);
                    }
                    // IndexAnyway: do nothing
                }

                // 3) index this file
                var report = await _indexer.IndexPdfAsync(file, ct);
                await _store.UpdateDocumentContentHashAsync(report.DocumentId, hash, ct);

                done++;

                // Here we can show per-file progress if needed, but it may be too verbose for large batches. Adjust as desired.
                StatusText =
                    $"Indexed {done}/{files.Count}: {Path.GetFileName(file)} (Chunks={report.ChunkCount})";
            }

            StatusText = $"Batch index done. Files processed: {files.Count}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Index cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Index failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }


    private List<string> ResolveAllPdfFiles()
    {
        var list = new List<string>();

        foreach (var f in _pdfPaths)
        {
            if (File.Exists(f) && f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                list.Add(f);
        }

        foreach (var dir in _folderPaths)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var f in Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories))
                list.Add(f);
        }

        // de-dup by path
        var uniq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var f in list)
        {
            if (uniq.Add(f))
                result.Add(f);
        }

        return result;
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();

        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public async Task SearchAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            StatusText = "Please type a query first.";
            return;
        }

        try
        {
            IsBusy = true;
            SearchResults.Clear();
            SelectedResult = null;

            var hits = await _search.SearchAsync(Query, topK: 20, ct);
            if (hits.Count == 0)
            {
                StatusText = "No results.";
                return;
            }

            // 1) 先算本次结果的 min/max，用于归一化（拉开差距）
            float min = float.MaxValue, max = float.MinValue;
            foreach (var h in hits)
            {
                if (h.Score < min) min = h.Score;
                if (h.Score > max) max = h.Score;
            }
            var range = Math.Max(1e-6f, max - min);

            // 2) 填充 SearchResults：Preview 用短摘要，右侧用 FullChunk 完整文本
            foreach (var h in hits)
            {
                var full = h.Chunk.Text ?? string.Empty;

                // 中间卡片用短摘要（可调 240~400）
                var preview = full.Length > 320 ? full[..320] + "…" : full;

                var item = new SearchResultItem
                {
                    Score = h.Score,
                    NormalizedScore = (h.Score - min) / range,   // 0~1：本次结果内拉开差距
                    Title = string.IsNullOrWhiteSpace(h.Document.Title) ? h.Document.FilePath : h.Document.Title,
                    Preview = preview,
                    FullChunk = full,
                    FilePath = h.Document.FilePath
                };

                SearchResults.Add(item);
            }

            SelectedResult = SearchResults[0];
            StatusText = $"Found {hits.Count} results.";
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
