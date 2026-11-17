namespace FFApp.Core.Models;

public sealed record ColorMetadataSettings(
    ColorMetadataMode Mode = ColorMetadataMode.Automatic,
    string Primaries = "bt709",
    string Transfer = "bt709",
    string Matrix = "bt709",
    string Range = "tv");

public sealed record ColorMetadata(
    string? Primaries = null,
    string? Transfer = null,
    string? Matrix = null,
    string? Range = null);
