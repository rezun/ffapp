using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Infrastructure;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public class MatroskaPostProcessorTests
{
    [Theory]
    [InlineData(OutputContainer.Mkv, ".mkv")]
    [InlineData(OutputContainer.Mka, ".mka")]
    [InlineData(OutputContainer.Webm, ".webm")]
    public void BuildCommand_EnabledMatroskaContainer_ReturnsMkvpropeditCommand(
        OutputContainer container, string extension)
    {
        var config = new ExecutablePathsConfig();
        config.Configure(new ExecutablePaths(Mkvpropedit: "/tools/mkvpropedit"));
        var sut = new MatroskaPostProcessor(config);
        var outputPath = $"/output/movie{extension}";
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(container)
            .WithMatroskaStatistics()
            .Build();
        var job = FormatHelpers.BuildJobDefinition("/input/movie.mkv", outputPath, settings);

        var command = sut.BuildCommand(job);

        command.Should().NotBeNull();
        command!.ExecutablePath.Should().Be("/tools/mkvpropedit");
        command.Protocol.Should().Be(ProcessProtocol.Plain);
        command.Arguments.Should().Equal(
            outputPath,
            "--delete-track-statistics-tags",
            "--add-track-statistics-tags");
    }

    [Fact]
    public void BuildCommand_Disabled_ReturnsNull()
    {
        var sut = new MatroskaPostProcessor(new ExecutablePathsConfig());
        var job = new TestJobDefinitionBuilder()
            .WithOutputSettings(settings => settings.WithContainer(OutputContainer.Mkv))
            .Build();

        sut.BuildCommand(job).Should().BeNull();
    }

    [Fact]
    public void BuildCommand_NonMatroskaContainer_ReturnsNull()
    {
        var sut = new MatroskaPostProcessor(new ExecutablePathsConfig());
        var job = new TestJobDefinitionBuilder()
            .WithOutputSettings(settings => settings.WithMatroskaStatistics())
            .Build();

        sut.BuildCommand(job).Should().BeNull();
    }

    [Fact]
    public void BuildCommand_EnabledWithoutMkvpropedit_Throws()
    {
        var sut = new MatroskaPostProcessor(new ExecutablePathsConfig());
        var job = new TestJobDefinitionBuilder()
            .WithOutputSettings(settings => settings
                .WithContainer(OutputContainer.Mkv)
                .WithMatroskaStatistics())
            .Build();

        var act = () => sut.BuildCommand(job);

        act.Should().Throw<CommandBuildException>()
            .WithMessage("*mkvpropedit*");
    }
}
