namespace FFRux.Services;

public interface IFilePickerService
{
    /// <summary>Lets the user choose media files; returns an empty list when the picker is cancelled.</summary>
    Task<IReadOnlyList<string>> PickMediaFilesAsync();
}
