namespace App.Core.Abstractions;

public interface ITextCleaner
{
    /// <summary>
    /// Clean raw extracted text to improve chunking & retrieval.
    /// </summary>
    string Clean(string rawText);
}
