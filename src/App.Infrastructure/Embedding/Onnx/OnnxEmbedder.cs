using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using App.Core.Abstractions;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Tokenizers.HuggingFace.Tokenizer;

namespace App.Infrastructure.Embedding.Onnx;

public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;

    private readonly int _maxLength;
    private readonly int _padTokenId;

    // Set after first inference
    public int Dimension { get; private set; }

    /// <param name="modelOnnxPath">Path to model.onnx</param>
    /// <param name="tokenizerJsonPath">Path to tokenizer.json</param>
    /// <param name="maxLength">Max sequence length (typical 512)</param>
    /// <param name="padTokenId">Pad token id (often 0; if your model uses another PAD id, change this)</param>
    public OnnxEmbedder(string modelOnnxPath, string tokenizerJsonPath, int maxLength = 512, int padTokenId = 0)
    {
        if (!File.Exists(modelOnnxPath)) throw new FileNotFoundException("ONNX model not found.", modelOnnxPath);
        if (!File.Exists(tokenizerJsonPath)) throw new FileNotFoundException("tokenizer.json not found.", tokenizerJsonPath);
        if (maxLength <= 0) throw new ArgumentOutOfRangeException(nameof(maxLength));

        _maxLength = maxLength;
        _padTokenId = padTokenId;

        _tokenizer = Tokenizer.FromFile(tokenizerJsonPath);
        _session = new InferenceSession(modelOnnxPath, new SessionOptions());
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        text ??= string.Empty;

        // Tokenize (adds special tokens). This returns IDs + attention mask + type ids.
        // API demonstrated by the package author on NuGet.
        var encoding = _tokenizer
            .Encode(text, addSpecialTokens: true, includeTypeIds: true, includeAttentionMask: true)
            .First();

        // Convert to fixed length (truncate/pad)
        var ids = ToFixedLength(encoding.Ids.Select(i => (int)i).ToArray(), _maxLength, _padTokenId);
        var typeIds = ToFixedLength(encoding.TypeIds.Select(i => (int)i).ToArray(), _maxLength, 0);
        var attn = ToFixedLength(encoding.AttentionMask.Select(i => (int)i).ToArray(), _maxLength, 0);

        // Build tensors (batch=1, seq=maxLength)
        var inputIdsTensor = new DenseTensor<long>(ids.Select(x => (long)x).ToArray(), new[] { 1, _maxLength });
        var attentionTensor = new DenseTensor<long>(attn.Select(x => (long)x).ToArray(), new[] { 1, _maxLength });
        var typeIdsTensor = new DenseTensor<long>(typeIds.Select(x => (long)x).ToArray(), new[] { 1, _maxLength });

        // Prepare inputs based on what the model expects.
        // Some ONNX models do NOT have token_type_ids.
        var inputs = new List<NamedOnnxValue>(3);
        var meta = _session.InputMetadata;

        // Common names: "input_ids", "attention_mask", "token_type_ids"
        if (meta.ContainsKey("input_ids"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor));
        else
            throw new InvalidOperationException($"Model input 'input_ids' not found. Available: {string.Join(", ", meta.Keys)}");

        if (meta.ContainsKey("attention_mask"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", attentionTensor));

        if (meta.ContainsKey("token_type_ids"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", typeIdsTensor));

        using var results = _session.Run(inputs);

        // Take first output tensor
        var outTensor = results.First().AsTensor<float>();
        var dims = outTensor.Dimensions.ToArray();

        float[] embedding;

        if (dims.Length == 3)
        {
            // [1, seq, hidden] -> mean pool over tokens where attention=1
            var seq = dims[1];
            var hidden = dims[2];
            embedding = MeanPool(outTensor, attn, seq, hidden);
            Dimension = hidden;
        }
        else if (dims.Length == 2)
        {
            // [1, hidden]
            var hidden = dims[1];
            embedding = new float[hidden];
            for (int i = 0; i < hidden; i++)
                embedding[i] = outTensor[0, i];
            Dimension = hidden;
        }
        else if (dims.Length == 1)
        {
            // [hidden]
            var hidden = dims[0];
            embedding = outTensor.ToArray();
            Dimension = hidden;
        }
        else
        {
            throw new InvalidOperationException($"Unexpected output shape: [{string.Join(",", dims)}]");
        }

        L2Normalize(embedding);
        return Task.FromResult(embedding);
    }

    private static int[] ToFixedLength(int[] src, int maxLen, int padValue)
    {
        if (src.Length == maxLen) return src;

        if (src.Length > maxLen)
        {
            var cut = new int[maxLen];
            Array.Copy(src, 0, cut, 0, maxLen);
            return cut;
        }

        var padded = new int[maxLen];
        Array.Copy(src, 0, padded, 0, src.Length);
        for (int i = src.Length; i < maxLen; i++)
            padded[i] = padValue;
        return padded;
    }

    private static float[] MeanPool(Tensor<float> lastHidden, int[] attentionMask, int seq, int hidden)
    {
        var v = new float[hidden];
        double denom = 0;

        var len = Math.Min(attentionMask.Length, seq);
        for (int t = 0; t < len; t++)
        {
            if (attentionMask[t] == 0) continue;

            denom += 1;
            for (int h = 0; h < hidden; h++)
                v[h] += lastHidden[0, t, h];
        }

        if (denom < 1e-9) return v;

        var inv = (float)(1.0 / denom);
        for (int h = 0; h < hidden; h++)
            v[h] *= inv;

        return v;
    }

    private static void L2Normalize(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += v[i] * v[i];
        var norm = Math.Sqrt(sum);
        if (norm < 1e-12) return;

        var inv = (float)(1.0 / norm);
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public void Dispose()
    {
        _session.Dispose();
        _tokenizer.Dispose();
    }
}
