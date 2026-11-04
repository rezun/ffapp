using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class BitrateViewModelTests
{
    private readonly BitrateViewModel _sut = new();

    [Theory]
    [InlineData("4500", 4500)]
    [InlineData("1", 100)]
    [InlineData("200000", 100000)]
    public void VideoBitrateKbpsText_ParsesAndClampsToSupportedRange(string text, int expectedKbps)
    {
        _sut.VideoBitrateKbpsText = text;

        _sut.VideoBitrateKbps.Should().Be(expectedKbps);
        _sut.VideoBitrateKbpsText.Should().Be(expectedKbps.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
