namespace App.Core.Abstractions;

public interface IModelManager
{
    Task<bool> IsModelReadyAsync(CancellationToken ct = default);
    Task EnsureModelAsync(IProgress<double>? progress = null, CancellationToken ct = default);
    string GetModelPath(); // folder path
}