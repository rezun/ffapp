using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class FileSizeViewModelTests
{
    private readonly FileSizeViewModel _sut = new();

    [Fact]
    public void TargetSizeMbText_SetToDigits_UpdatesTargetSizeMb()
    {
        _sut.TargetSizeMbText = "125";

        _sut.TargetSizeMb.Should().Be(125);
        _sut.TargetSizeMbText.Should().Be("125");
    }

    [Fact]
    public void TargetSizeMbText_SetBelowMinimum_ClampsToMinimum()
    {
        _sut.TargetSizeMbText = "0";

        _sut.TargetSizeMb.Should().Be(1);
        _sut.TargetSizeMbText.Should().Be("1");
    }

    [Fact]
    public void TargetSizeMbText_SetAboveMaximum_ClampsToMaximum()
    {
        _sut.TargetSizeMbText = "20000";

        _sut.TargetSizeMb.Should().Be(10000);
        _sut.TargetSizeMbText.Should().Be("10000");
    }
}
