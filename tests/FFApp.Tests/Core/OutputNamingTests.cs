using FFApp.Core;
using FluentAssertions;

namespace FFApp.Tests.Core;

public sealed class OutputNamingTests
{
    [Theory]
    [InlineData(null, "John Wick Kapitel 3_t1")]
    [InlineData("  ", "John Wick Kapitel 3_t1")]
    [InlineData("AV1 CRF24", "John Wick Kapitel 3_t1 - AV1 CRF24")]
    public void AutoStem_AppendsPresetNameOnlyWhenPresent(string? presetName, string expected)
    {
        var source = Path.Combine("/rips", "John Wick 3", "John Wick Kapitel 3_t1.mkv");

        OutputNaming.AutoStem(source, presetName).Should().Be(expected);
    }

    [Theory]
    [InlineData("Movie - AV1 CRF24", true)]
    [InlineData("Movie.final", true)]
    [InlineData("   ", false)]
    [InlineData(" Movie", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("sub/Movie", false)]
    [InlineData("sub\\Movie", false)]
    [InlineData("Mo\0vie", false)]
    public void IsValidStem_RejectsBlankPathsAndInvalidCharacters(string stem, bool expected)
    {
        OutputNaming.IsValidStem(stem).Should().Be(expected);
    }
}
