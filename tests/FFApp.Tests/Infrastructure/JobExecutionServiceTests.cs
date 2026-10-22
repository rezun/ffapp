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
            Arg.Any<ExecutionPauseState?>(),
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
            Arg.Any<ExecutionPauseState?>(),
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
        new(_commandBuilder, _processRunner, _postProcessor, TimeProvider.System);

    [Theory]
    [InlineData(JobState.Completed)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Canceled)]
    public async Task ExecuteAsync_ImagePreparation_CleansTemporaryFilesAndDoesNotCountAsPass(JobState state)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-test-image-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "image");
        try
        {
            var preparation = Command("extract", ProcessProtocol.Plain) with { TemporaryFiles = [path] };
            var encode = Command("encode", ProcessProtocol.Ffmpeg);
            _commandBuilder.Build(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>()).Returns([preparation, encode]);
            ConfigureResult(preparation, JobState.Completed);
            ConfigureResult(encode, state);
            var progress = new List<JobProgress>();

            var result = await CreateSut().ExecuteAsync(DefaultJob(), DefaultMedia(), progress.Add);

            result.State.Should().Be(state);
            result.ProcessResults.Select(process => process.Command).Should().Equal(preparation, encode);
            progress.Should().OnlyContain(update => update.PassCount == 1 && update.PassIndex == 1);
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ImageExtractionFailure_StopsBeforeEncodingAndCleansFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-test-image-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "partial image");
        try
        {
            var preparation = Command("extract", ProcessProtocol.Plain) with { TemporaryFiles = [path] };
            var encode = Command("encode", ProcessProtocol.Ffmpeg);
            _commandBuilder.Build(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>()).Returns([preparation, encode]);
            ConfigureResult(preparation, JobState.Failed);

            var result = await CreateSut().ExecuteAsync(DefaultJob(), DefaultMedia());

            result.State.Should().Be(JobState.Failed);
            result.ProcessResults.Should().ContainSingle().Which.Command.Should().Be(preparation);
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PassesSamePauseStateToBothPassesAndPostProcessing()
    {
        var first = Command("first", ProcessProtocol.Ffmpeg);
        var second = Command("second", ProcessProtocol.Ffmpeg);
        var post = Command("post", ProcessProtocol.Plain);
        _commandBuilder.Build(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>()).Returns([first, second]);
        _postProcessor.BuildCommand(Arg.Any<JobDefinition>()).Returns(post);
        ConfigureResult(first, JobState.Completed);
        ConfigureResult(second, JobState.Completed);
        ConfigureResult(post, JobState.Completed);
        var state = new ExecutionPauseState(TimeProvider.System);

        await CreateSut().ExecuteAsync(DefaultJob(), DefaultMedia(), pauseState: state);

        foreach (var command in new[] { first, second, post })
            await _processRunner.Received(1).RunAsync(command, Arg.Any<double?>(), Arg.Any<int?>(),
                Arg.Any<Action<ProcessProgress>?>(), Arg.Any<Action<string>?>(), state, Arg.Any<CancellationToken>());
    }

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
                Arg.Any<ExecutionPauseState?>(),
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
