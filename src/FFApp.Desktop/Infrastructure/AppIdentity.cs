namespace FFApp.Infrastructure;

/// <summary>
/// Everything that distinguishes one desktop app in this repository from another at runtime.
/// Two apps with different identities keep separate settings and never hand files to each other.
/// </summary>
/// <param name="Name">Display and process name, also the prefix of the single-instance pipe.</param>
/// <param name="DataDirectoryName">Folder name below the platform's per-user application data directory.</param>
public sealed record AppIdentity(string Name, string DataDirectoryName)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(Name)
        ? throw new ArgumentException("An app name is required.", nameof(Name))
        : Name;

    public string DataDirectoryName { get; } = IsValidDirectoryName(DataDirectoryName)
        ? DataDirectoryName
        : throw new ArgumentException(
            "The data directory name must be a single, non-empty folder name.", nameof(DataDirectoryName));

    private static bool IsValidDirectoryName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && value.IndexOfAny(['/', '\\']) < 0;
}
