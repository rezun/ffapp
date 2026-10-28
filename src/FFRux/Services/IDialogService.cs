using FFRux.Core.Models;

namespace FFRux.Services;

/// <summary>Pickers, confirmations and shell actions that need a window. Keeps Avalonia out of the ViewModels.</summary>
public interface IDialogService
{
    /// <summary>Lets the user choose source files; returns an empty list when the picker is cancelled.</summary>
    Task<IReadOnlyList<string>> PickSourceFilesAsync();

    /// <summary>Asks where to write the output; returns null when cancelled.</summary>
    Task<string?> PickOutputFileAsync(string suggestedPath, OutputFormatInfo format);

    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Opens the folder that contains the file in the system file manager.</summary>
    Task ShowInFolderAsync(string filePath);

    Task SetClipboardTextAsync(string text);
}
