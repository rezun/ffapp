using FFApp.Core.Models;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class SettingsServiceTests
{
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
