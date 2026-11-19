using System.Text.Json;
using FFRux.Core.Models;
using FFApp.Core.Models;

namespace FFRux.Infrastructure;

/// <summary>What FFRux remembers between runs.</summary>
public sealed record FFRuxSettings
{
    public OutputFormat? LastFormat { get; init; }
    public bool? OptimizeForStreaming { get; init; }

    /// <summary>Null until the user changes the option; the default is on.</summary>
    public bool? UpdateMatroskaStatistics { get; init; }
    public ExecutablePaths ExecutablePaths { get; init; } = ExecutablePaths.Empty;
}

public interface ISettingsService
{
    Task<FFRuxSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(FFRuxSettings settings, CancellationToken ct = default);
}

public sealed class SettingsService(JsonSerializerOptions jsonOptions) : ISettingsService
{
    private readonly string _path = Path.Combine(FFRuxPaths.AppDataDirectory(), "settings.json");

    public async Task<FFRuxSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return new FFRuxSettings();

        try
        {
            await using var stream = File.OpenRead(_path);
            var settings = await JsonSerializer.DeserializeAsync<FFRuxSettings>(stream, jsonOptions, ct) ?? new FFRuxSettings();
            return settings.LastFormat is { } format && !Enum.IsDefined(format)
                ? settings with { LastFormat = null }
                : settings;
        }
        catch (JsonException)
        {
            // A damaged settings file only costs the remembered options; start with defaults.
            return new FFRuxSettings();
        }
    }

    // A single File.WriteAllBytesAsync is atomic enough here, so the token is honored.
    public async Task SaveAsync(FFRuxSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, jsonOptions);
        await File.WriteAllBytesAsync(_path, bytes, ct);
    }
}
