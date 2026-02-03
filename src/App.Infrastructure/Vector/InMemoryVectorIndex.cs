using App.Core.Abstractions;

namespace App.Infrastructure.Vector;

public sealed class InMemoryVectorIndex : IVectorIndex
{
    private readonly Dictionary<string, float[]> _vectors = new();
    public int Dimension { get; }

    public InMemoryVectorIndex(int dimension)
    {
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
        Dimension = dimension;
    }

    public Task UpsertAsync(string itemId, float[] vector, CancellationToken ct = default)
    {
        Validate(vector);
        _vectors[itemId] = vector;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string itemId, CancellationToken ct = default)
    {
        _vectors.Remove(itemId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(string Id, float Score)>> SearchAsync(float[] queryVector, int topK, CancellationToken ct = default)
    {
        Validate(queryVector);
        if (topK <= 0) return Task.FromResult((IReadOnlyList<(string, float)>)Array.Empty<(string, float)>());

        var results = new List<(string Id, float Score)>(_vectors.Count);
        foreach (var (id, vec) in _vectors)
        {
            var score = Cosine(queryVector, vec);
            results.Add((id, score));
        }

        var top = results
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        return Task.FromResult((IReadOnlyList<(string, float)>)top);
    }

    public Task SaveAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task LoadAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    private void Validate(float[] v)
    {
        if (v is null) throw new ArgumentNullException(nameof(v));
        if (v.Length != Dimension) throw new ArgumentException($"Vector dim {v.Length} != {Dimension}");
    }

    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom < 1e-12 ? 0f : (float)(dot / denom);
    }
}
