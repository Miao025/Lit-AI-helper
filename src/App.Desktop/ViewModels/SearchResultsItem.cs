using System;

namespace App.Desktop.ViewModels;

public sealed class SearchResultItem
{
    public required float Score { get; init; }
    public float NormalizedScore { get; set; } // 0~1（由VM在一次搜索结果内归一化）

    public required string Title { get; init; }

    // 给中间卡片用：短摘要
    public required string Preview { get; init; }

    // 给右侧 Preview 用：完整 chunk（不截断）
    public required string FullChunk { get; init; }

    public required string FilePath { get; init; }

    public string FileName => System.IO.Path.GetFileName(FilePath);

    // 用归一化后的分数拉开差距（最大宽度 320px）
    public double SimilarityBarWidth => 320 * Math.Clamp(NormalizedScore, 0f, 1f);

    public string SimilarityText => $"Score: {Score:0.00}";
}
