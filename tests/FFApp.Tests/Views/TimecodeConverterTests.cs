using System.Globalization;
using FFApp.Converters;
using FluentAssertions;

namespace FFApp.Tests.Views;

public sealed class TimecodeConverterTests
{
    private readonly SecondsToTimecodeConverter _converter = new();

    [Theory]
    [InlineData("0", "00:00:00.000")]
    [InlineData("1684.656", "00:28:04.656")]
    [InlineData("3600", "01:00:00.000")]
    [InlineData("90000", "25:00:00.000")]
    [InlineData("59.9996", "00:01:00.000")]
    public void Format_PreservesMillisecondsAndTotalHours(string seconds, string expected)
    {
        _converter.ConvertBack(decimal.Parse(seconds, CultureInfo.InvariantCulture), typeof(string), null,
            CultureInfo.GetCultureInfo("de-DE")).Should().Be(expected);
    }

    [Theory]
    [InlineData("00:28:04.656", "1684.656")]
    [InlineData("01:00:00", "3600")]
    [InlineData("25:00:00.000", "90000")]
    public void Parse_AcceptsTimecodeWithOptionalMilliseconds(string text, string expected)
    {
        _converter.Convert(text, typeof(decimal), null, CultureInfo.GetCultureInfo("de-DE"))
            .Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("00:60:00")]
    [InlineData("00:00:60")]
    [InlineData("-01:00:00")]
    [InlineData("")]
    public void Parse_RejectsInvalidTimecodes(string text)
    {
        var parse = () => _converter.Convert(text, typeof(decimal), null, CultureInfo.InvariantCulture);
        parse.Should().Throw<FormatException>();
    }
}
