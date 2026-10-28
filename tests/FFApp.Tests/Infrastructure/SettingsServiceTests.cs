using FFApp.Core.Models;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class SettingsServiceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(1024)]
    public async Task RoundTrip_PreservesComparisonCacheLimit(int gib)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-settings-{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            await service.SaveAsync(new AppSettings { ComparisonCacheLimitGiB = gib });
            (await service.LoadAsync()).ComparisonCacheLimitGiB.Should().Be(gib);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"comparison_cache_limit_gib\": null}")]
    [InlineData("{\"comparison_cache_limit_gib\": \"bad\"}")]
    [InlineData("{\"comparison_cache_limit_gib\": 0}")]
    [InlineData("{\"comparison_cache_limit_gib\": -1}")]
    [InlineData("{\"comparison_cache_limit_gib\": 1025}")]
    [InlineData("{\"comparison_cache_limit_gib\": 1.5}")]
    public async Task Load_MissingOrInvalidCacheLimit_DefaultsToFiveGiB(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-settings-{Guid.NewGuid()}.json");
        try
        {
            await File.WriteAllTextAsync(path, json);
            (await new SettingsService(path).LoadAsync()).ComparisonCacheLimitGiB.Should().Be(5);
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData(OutputLocationMode.SourceFolder, null)]
    [InlineData(OutputLocationMode.SourceFolder, "/kept/for/later")]
    [InlineData(OutputLocationMode.CustomFolder, "/encodes")]
    public async Task RoundTrip_PreservesOutputLocation(OutputLocationMode mode, string? folder)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-settings-{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            await service.SaveAsync(new AppSettings { OutputLocation = new OutputLocation(mode, folder) });
            (await service.LoadAsync()).OutputLocation.Should().Be(new OutputLocation(mode, folder));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"output_location\": null}")]
    [InlineData("{\"output_location\": \"custom_folder\"}")]
    [InlineData("{\"output_location\": {\"mode\": \"elsewhere\"}}")]
    [InlineData("{\"output_location\": {\"mode\": 1, \"custom_folder\": 5}}")]
    public async Task Load_MissingOrInvalidOutputLocation_DefaultsToSourceFolder(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-settings-{Guid.NewGuid()}.json");
        try
        {
            await File.WriteAllTextAsync(path, json);
            (await new SettingsService(path).LoadAsync()).OutputLocation.Should().Be(new OutputLocation());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTrip_PreservesLowPriorityPreference(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-settings-{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            await service.SaveAsync(new AppSettings { UseLowPriorityTranscoding = enabled });
            var restored = await service.LoadAsync();
            restored.UseLowPriorityTranscoding.Should().Be(enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"low_priority_transcoding\": null}")]
    [InlineData("{\"low_priority_transcoding\": \"invalid\"}")]
    public async Task Load_OldOrInvalidSettings_DefaultsToNormalPriority(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-settings-{Guid.NewGuid()}.json");
        try
        {
            await File.WriteAllTextAsync(path, json);
            var restored = await new SettingsService(path).LoadAsync();
            restored.UseLowPriorityTranscoding.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
