using System.Security.Cryptography;
using System.Text;
using App.Core.Abstractions;

namespace App.Infrastructure.Embedding;

public sealed class EmbedderStub : IEmbedder
{
    public int Dimension { get; }

    public EmbedderStub(int dimension = 384)
    {
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
        Dimension = dimension;
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        text ??= string.Empty;

        // Deterministic "fake embedding": SHA256 -> expand to Dimension floats
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vec = new float[Dimension];

        // map bytes to [-1, 1] and repeat
        for (int i = 0; i < Dimension; i++)
        {
            var b = bytes[i % bytes.Length];
            vec[i] = (b / 255f) * 2f - 1f;
        }

        // L2 normalize
        Normalize(vec);
        return Task.FromResult(vec);
    }

    private static void Normalize(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += v[i] * v[i];
        var norm = Math.Sqrt(sum);
        if (norm < 1e-12) return;
        var inv = (float)(1.0 / norm);
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }
}
