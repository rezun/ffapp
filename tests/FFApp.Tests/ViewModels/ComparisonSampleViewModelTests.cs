using System.Globalization;
using FFApp.Core.Models;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class ComparisonSampleViewModelTests
{
    [Theory]
    [InlineData(628.014, 12, "0:10:28", "12 s")]
    [InlineData(8064, 12.5, "2:14:24", "12.5 s")]
    public void ListLabels_UseFixedWidthStart_AndInvariantDuration(double start, double duration, string startLabel, string durationLabel)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var sample = new ComparisonSampleViewModel(new ComparisonSample("id", "Sample 1", start, duration));
            sample.StartLabel.Should().Be(startLabel);
            sample.DurationLabel.Should().Be(durationLabel);
            sample.Label.Should().EndWith(durationLabel);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void EditingStartOrDuration_NotifiesListLabels()
    {
        var sample = new ComparisonSampleViewModel(new ComparisonSample("id", "Sample 1", 10, 12));
        using var changes = sample.Monitor();
        sample.StartSeconds = 20;
        sample.DurationSeconds = 8;
        changes.Should().RaisePropertyChangeFor(s => s.StartLabel);
        changes.Should().RaisePropertyChangeFor(s => s.DurationLabel);
        sample.StartLabel.Should().Be("0:00:20");
        sample.DurationLabel.Should().Be("8 s");
    }
}
