using FFApp.Core.Models;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Core.Models;

public sealed class JobContextTests
{
    private static SourceMediaInfo MakeMedia(double? duration = 60.0) =>
        new TestMediaInfoBuilder().WithDuration(duration).Build();

    [Theory]
    [InlineData(60.0, null, null, null)]
    [InlineData(60.0, 15.0, null, 45.0)]
    [InlineData(60.0, null, 20.0, 20.0)]
    [InlineData(60.0, 30.0, 30.0, null)]
    [InlineData(0.0, 0.0, 10.0, null)]
    [InlineData(null, 0.0, 10.0, null)]
    public void EffectiveDurationSeconds_IsTrimmedSpanOnlyWhenTrimmedAndPositive(
        double? sourceDuration, double? trimStart, double? trimEnd, double? expected)
    {
        var ctx = new JobContext(MakeMedia(sourceDuration), new OutputSettings(),
            TrimStartSeconds: trimStart, TrimEndSeconds: trimEnd);

        ctx.EffectiveDurationSeconds.Should().Be(expected);
    }
}
