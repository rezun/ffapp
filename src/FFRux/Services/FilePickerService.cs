using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace FFRux.Services;

public sealed class FilePickerService(Func<Window> windowAccessor) : IFilePickerService
{
    public async Task<IReadOnlyList<string>> PickMediaFilesAsync()
    {
        var window = windowAccessor();
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open media files",
            AllowMultiple = true,
        });

        var paths = new List<string>(files.Count);
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { Length: > 0 } path)
                paths.Add(path);
        }
        return paths;
    }
}
