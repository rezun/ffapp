namespace FFApp.Core.Models;

public sealed record PresetDefinition(
    string Name,
    OutputSettings? Settings = null,
    int SchemaVersion = 1)
{
    public OutputSettings Settings { get; init; } = Settings ?? new OutputSettings();
}
