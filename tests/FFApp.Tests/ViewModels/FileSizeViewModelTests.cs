using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class FileSizeViewModelTests
{
    private readonly FileSizeViewModel _sut = new();

    [Theory]
    [InlineData("125", 125)]
    [InlineData("0", 1)]
    [InlineData("20000", 10000)]
    public void TargetSizeMbText_ParsesAndClampsToSupportedRange(string text, int expectedMb)
    {
        _sut.TargetSizeMbText = text;

        _sut.TargetSizeMb.Should().Be(expectedMb);
        _sut.TargetSizeMbText.Should().Be(expectedMb.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
