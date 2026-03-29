using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class BitrateViewModelTests
{
    private readonly BitrateViewModel _sut = new();

    [Fact]
    public void VideoBitrateKbpsText_SetToDigits_UpdatesVideoBitrateKbps()
    {
        _sut.VideoBitrateKbpsText = "4500";

        _sut.VideoBitrateKbps.Should().Be(4500);
        _sut.VideoBitrateKbpsText.Should().Be("4500");
    }

    [Fact]
    public void VideoBitrateKbpsText_SetBelowMinimum_ClampsToMinimum()
    {
        _sut.VideoBitrateKbpsText = "1";

        _sut.VideoBitrateKbps.Should().Be(100);
        _sut.VideoBitrateKbpsText.Should().Be("100");
    }

    [Fact]
    public void VideoBitrateKbpsText_SetAboveMaximum_ClampsToMaximum()
    {
        _sut.VideoBitrateKbpsText = "200000";

        _sut.VideoBitrateKbps.Should().Be(100000);
        _sut.VideoBitrateKbpsText.Should().Be("100000");
    }
}
