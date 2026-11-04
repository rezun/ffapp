using FFApp.Core.Models;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class SettingsServiceTests
{
    [Theory]
    [InlineData(1, OutputLocationMode.SourceFolder, null, true)]
    [InlineData(1024, OutputLocationMode.SourceFolder, "/kept/for/later", false)]
    [InlineData(1024, OutputLocationMode.CustomFolder, "/encodes", true)]
    public async Task RoundTrip_PreservesCacheLimitOutputLocationAndLowPriority(
        int cacheLimitGiB, OutputLocationMode mode, string? folder, bool lowPriority)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-settings-{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            var saved = new AppSettings
            {
                ComparisonCacheLimitGiB = cacheLimitGiB,
                OutputLocation = new OutputLocation(mode, folder),
                UseLowPriorityTranscoding = lowPriority,
            };
            await service.SaveAsync(saved);
            (await service.LoadAsync()).Should().BeEquivalentTo(new
            {
                saved.ComparisonCacheLimitGiB,
                saved.OutputLocation,
                saved.UseLowPriorityTranscoding,
            });
        }
        finally { File.Delete(path); }
    }

    // Each row breaks every setting differently; all must fall back independently.
    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "comparison_cache_limit_gib": "bad", "output_location": "custom_folder", "low_priority_transcoding": "invalid" }""")]
    [InlineData("""{ "comparison_cache_limit_gib": 0, "output_location": { "mode": "elsewhere" } }""")]
    [InlineData("""{ "comparison_cache_limit_gib": 1025, "output_location": { "mode": 1, "custom_folder": 5 } }""")]
    [InlineData("""{ "comparison_cache_limit_gib": 1.5 }""")]
    public async Task Load_MissingOrInvalidValues_FallBackToDefaults(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-settings-{Guid.NewGuid()}.json");
        try
        {
            await File.WriteAllTextAsync(path, json);
            (await new SettingsService(path).LoadAsync()).Should().BeEquivalentTo(new
            {
                ComparisonCacheLimitGiB = 5,
                OutputLocation = new OutputLocation(),
                UseLowPriorityTranscoding = false,
            });
        }
        finally { File.Delete(path); }
    }
}
