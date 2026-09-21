using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFApp.Tests.Builders;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.Infrastructure;

public class JobExecutionServiceTests
{
    private readonly ICommandBuilder _commandBuilder = Substitute.For<ICommandBuilder>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly IMatroskaPostProcessor _postProcessor =
        Substitute.For<IMatroskaPostProcessor>();

    [Fact]
    public async Task ExecuteAsync_SuccessfulEncode_RunsMatroskaPostProcessing()
    {
        var encodeCommand = Command("ffmpeg", ProcessProtocol.Ffmpeg);
        var postProcessCommand = Command("mkvpropedit", ProcessProtocol.Plain);
        ConfigureCommands(encodeCommand, postProcessCommand);
        ConfigureResult(encodeCommand, JobState.Completed);
        ConfigureResult(postProcessCommand, JobState.Completed);
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(DefaultJob(), DefaultMedia());

        result.State.Should().Be(JobState.Completed);
        result.Commands.Should().Equal(encodeCommand, postProcessCommand);
        result.ProcessResults.Select(process => process.Command)
            .Should().Equal(encodeCommand, postProcessCommand);
        await _processRunner.Received(1).RunAsync(
            postProcessCommand,
            Arg.Any<double?>(),
            Arg.Any<int?>(),
            Arg.Any<Action<ProcessProgress>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_FailedEncode_DoesNotRunPostProcessing()
    {
        var encodeCommand = Command("ffmpeg", ProcessProtocol.Ffmpeg);
        var postProcessCommand = Command("mkvpropedit", ProcessProtocol.Plain);
        ConfigureCommands(encodeCommand, postProcessCommand);
        ConfigureResult(encodeCommand, JobState.Failed);
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(DefaultJob(), DefaultMedia());

        result.State.Should().Be(JobState.Failed);
        await _processRunner.DidNotReceive().RunAsync(
            postProcessCommand,
            Arg.Any<double?>(),
            Arg.Any<int?>(),
            Arg.Any<Action<ProcessProgress>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_FailedPostProcessing_MarksJobFailed()
    {
        var encodeCommand = Command("ffmpeg", ProcessProtocol.Ffmpeg);
        var postProcessCommand = Command("mkvpropedit", ProcessProtocol.Plain);
        ConfigureCommands(encodeCommand, postProcessCommand);
        ConfigureResult(encodeCommand, JobState.Completed);
        ConfigureResult(postProcessCommand, JobState.Failed);
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(DefaultJob(), DefaultMedia());

        result.State.Should().Be(JobState.Failed);
        result.ProcessResults.Should().HaveCount(2);
    }

    private DefaultJobExecutionService CreateSut() =>
        new(_commandBuilder, _processRunner, _postProcessor);

    private void ConfigureCommands(
        GeneratedCommand encodeCommand,
        GeneratedCommand postProcessCommand)
    {
        _commandBuilder.Build(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>())
            .Returns([encodeCommand]);
        _postProcessor.BuildCommand(Arg.Any<JobDefinition>())
            .Returns(postProcessCommand);
    }

    private void ConfigureResult(GeneratedCommand command, JobState state)
    {
        _processRunner.RunAsync(
                command,
                Arg.Any<double?>(),
                Arg.Any<int?>(),
                Arg.Any<Action<ProcessProgress>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessExecutionResult(
                command,
                state,
                state == JobState.Completed ? 0 : 1,
                1,
                ""));
    }

    private static GeneratedCommand Command(string executable, ProcessProtocol protocol) =>
        new(executable, [], executable, protocol);

    private static JobDefinition DefaultJob() =>
        new TestJobDefinitionBuilder().Build();

    private static SourceMediaInfo DefaultMedia() =>
        new TestMediaInfoBuilder().Build();
}
