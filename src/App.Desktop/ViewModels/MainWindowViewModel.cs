using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using App.Core.Services;
using App.Infrastructure.Chunking;
using App.Infrastructure.Embedding;
using App.Infrastructure.Pdf;
using App.Infrastructure.Storage;
using System.IO;
using App.Infrastructure.Storage.Sqlite;
using App.Infrastructure.Vector;

namespace App.Desktop.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string? _selectedFilePath;
    public string? SelectedFilePath
    {
        get => _selectedFilePath;
        set { _selectedFilePath = value; OnPropertyChanged(); }
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

    public ObservableCollection<string> Results { get; } = new();

    private readonly IndexingOrchestrator _indexer;
    private readonly SearchService _search;

    public MainWindowViewModel()
    {
        // Wire up stub pipeline (no real PDF parsing yet)
        var extractor = new PdfPigTextExtractor();
        var chunker = new DefaultChunker();
        var embedder = new EmbedderStub(384);
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lit-AI-helper",
            "library.sqlite");
        var store = new SqliteLocalStore(dbPath);
        var index = new InMemoryVectorIndex(embedder.Dimension);

        _indexer = new IndexingOrchestrator(extractor, chunker, embedder, index, store);
        _search = new SearchService(embedder, index, store);
    }

    public async Task IndexAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilePath))
        {
            Results.Clear();
            Results.Add("Please pick a .txt file first (PDF is stubbed for now).");
            return;
        }

        try
        {
            IsBusy = true;
            Results.Clear();
            Results.Add("Indexing...");

            await _indexer.IndexPdfAsync(SelectedFilePath);

            Results.Clear();
            Results.Add("Index completed. Now type a query and click Search.");
        }
        catch (Exception ex)
        {
            Results.Clear();
            Results.Add("Index failed:");
            Results.Add(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            Results.Clear();
            Results.Add("Please type a query first.");
            return;
        }

        try
        {
            IsBusy = true;
            Results.Clear();

            var hits = await _search.SearchAsync(Query, topK: 10);

            if (hits.Count == 0)
            {
                Results.Add("No results.");
                return;
            }

            foreach (var h in hits)
            {
                var preview = h.Chunk.Text.Length > 200 ? h.Chunk.Text[..200] + "..." : h.Chunk.Text;
                Results.Add($"{h.Score:F3} | {h.Document.Title} | {preview}");
            }
        }
        catch (Exception ex)
        {
            Results.Clear();
            Results.Add("Search failed:");
            Results.Add(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
