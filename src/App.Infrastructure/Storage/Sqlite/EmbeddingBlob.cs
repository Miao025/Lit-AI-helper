using System;

namespace App.Infrastructure.Storage.Sqlite;

public static class EmbeddingBlob
{
    public static byte[] ToBytes(float[] vector)
    {
        if (vector is null) throw new ArgumentNullException(nameof(vector));
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length % sizeof(float) != 0)
            throw new ArgumentException("Invalid embedding blob length.", nameof(bytes));

        var vec = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vec, 0, bytes.Length);
        return vec;
    }
}
