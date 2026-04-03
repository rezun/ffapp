using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class JobContextTests
{
    private static SourceMediaInfo MakeMedia(double? duration = 60.0) =>
        new TestMediaInfoBuilder().WithDuration(duration).Build();

    [Fact]
    public void EffectiveDurationSeconds_NoTrim_ReturnsNull()
    {
        var ctx = new JobContext(MakeMedia(), new OutputSettings());

        ctx.EffectiveDurationSeconds.Should().BeNull();
    }

    [Fact]
    public void EffectiveDurationSeconds_BothTrimPoints_ReturnsCorrectDuration()
    {
        var ctx = new JobContext(MakeMedia(120), new OutputSettings(),
            TrimStartSeconds: 10, TrimEndSeconds: 40);

        ctx.EffectiveDurationSeconds.Should().Be(30);
    }

    [Fact]
    public void EffectiveDurationSeconds_TrimStartOnly_ReturnsDurationFromStartToEnd()
    {
        var ctx = new JobContext(MakeMedia(60), new OutputSettings(),
            TrimStartSeconds: 15);

        ctx.EffectiveDurationSeconds.Should().Be(45);
    }

    [Fact]
    public void EffectiveDurationSeconds_TrimEndOnly_ReturnsDurationFromZeroToEnd()
    {
        var ctx = new JobContext(MakeMedia(60), new OutputSettings(),
            TrimEndSeconds: 20);

        ctx.EffectiveDurationSeconds.Should().Be(20);
    }

    [Fact]
    public void EffectiveDurationSeconds_NoDuration_ReturnsNull()
    {
        var ctx = new JobContext(MakeMedia(null), new OutputSettings(),
            TrimStartSeconds: 0, TrimEndSeconds: 10);

        ctx.EffectiveDurationSeconds.Should().BeNull();
    }

    [Fact]
    public void EffectiveDurationSeconds_ZeroDuration_ReturnsNull()
    {
        var ctx = new JobContext(MakeMedia(0), new OutputSettings(),
            TrimStartSeconds: 0, TrimEndSeconds: 10);

        ctx.EffectiveDurationSeconds.Should().BeNull();
    }

    [Fact]
    public void BuildJobDefinition_ProducesCorrectJob()
    {
        var media = MakeMedia();
        var settings = new OutputSettings(Container: OutputContainer.Mkv);
        var ctx = new JobContext(media, settings,
            TrimStartSeconds: 5, TrimEndSeconds: 25,
            Crop: new CropRect(10, 20, 800, 600));

        var job = FormatHelpers.BuildJobDefinition(ctx);

        job.SourcePath.Should().Be(media.SourcePath);
        job.Container.Should().Be(OutputContainer.Mkv);
        job.TrimStartSeconds.Should().Be(5);
        job.TrimEndSeconds.Should().Be(25);
        job.Crop.Should().Be(new CropRect(10, 20, 800, 600));
    }

    [Fact]
    public void ResolveOutputPath_ReturnsNonEmptyPath()
    {
        var ctx = new JobContext(MakeMedia(), new OutputSettings());

        var path = FormatHelpers.ResolveOutputPath(ctx);

        path.Should().NotBeNullOrEmpty();
    }
}
