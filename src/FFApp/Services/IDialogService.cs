namespace FFApp.Services;

public interface IDialogService
{
    Task OpenUrlAsync(Uri url);
    Task<string?> OpenFileAsync(string title, string? filter = null);
    Task<string[]?> OpenFilesAsync(string title, string? filter = null);
    Task<string?> OpenFolderAsync(string title);
    Task<string?> SaveFileAsync(string title, string suggestedName, string? filter = null);
    Task<string?> PromptTextAsync(string title, string message, string? defaultValue = null);
    Task ShowMessageAsync(string title, string message);
    Task ShowErrorAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
    Task<string?> GetClipboardTextAsync();
    Task SetClipboardTextAsync(string text);
    /// <summary>Opens the file in the app the system associates with it, such as a text editor for a log.</summary>
    Task OpenWithDefaultAppAsync(string filePath);
}
