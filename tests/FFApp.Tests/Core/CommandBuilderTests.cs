using System.Globalization;
using System.Runtime.InteropServices;

using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Core;

public class CommandBuilderTests
{
    private static readonly string NullOutput =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "NUL" : "/dev/null";

    private readonly DefaultCommandBuilder _sut;

    public CommandBuilderTests()
    {
        var config = new ExecutablePathsConfig();
        config.Configure(new ExecutablePaths("ffmpeg", "ffprobe"));
        _sut = new DefaultCommandBuilder(config);
    }

    private static string JoinArgs(GeneratedCommand cmd) => string.Join(" ", cmd.Arguments);

    private static SourceMediaInfo DefaultMedia() => new TestMediaInfoBuilder().Build();

    private static JobDefinition DefaultJob() => new TestJobDefinitionBuilder().Build();

    private static JobDefinition JobWith(Action<TestOutputSettingsBuilder> configure) =>
        new TestJobDefinitionBuilder().WithOutputSettings(configure).Build();

    private static TestOutputSettingsBuilder WithCodec(TestOutputSettingsBuilder settings, VideoCodec codec) =>
        codec switch
        {
            VideoCodec.H264 => settings,
            VideoCodec.H265 => settings.WithH265(),
            VideoCodec.Vp9 => settings.WithVp9(),
            VideoCodec.Av1 => settings.WithAv1(),
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
        };

    private static TestOutputSettingsBuilder WithToneMap(TestOutputSettingsBuilder settings, ToneMapEngine engine) =>
        engine switch
        {
            ToneMapEngine.Standard => settings.WithToneMapToSdr(),
            ToneMapEngine.Libplacebo => settings.WithLibplaceboTonemapToSdr(),
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null),
        };

    // ---------------------------------------------------------------
    // Basic structure
    // ---------------------------------------------------------------

    [Fact]
    public void Build_DefaultSettings_ProducesSingleFfmpegCommand()
    {
        var cmd = _sut.Build(DefaultJob(), DefaultMedia()).Should().ContainSingle().Subject;
        var args = JoinArgs(cmd);

        cmd.ExecutablePath.Should().Be("ffmpeg");
        cmd.DisplayString.Should().StartWith("ffmpeg").And.Contain("-hide_banner");
        cmd.Arguments[^1].Should().Be("/test/output.mp4");
        args.Should().StartWith("-hide_banner -y -i /test/source.mp4").And.Contain("-map 0");
        // A source with one video stream, no subtitles and no data needs no exclusions or extra codecs.
        args.Should().NotContain("-map -0:").And.NotContain("-c:s").And.NotContain("-c:d");
    }

    // ---------------------------------------------------------------
    // Output path suggestion
    // ---------------------------------------------------------------

    [Fact]
    public void SuggestOutputPath_AutoNameSkipsReservedQueuePath()
    {
        var stem = $"ffapp-{Guid.NewGuid():N}";
        var sourcePath = Path.Combine(Path.GetTempPath(), $"{stem}.mp4");
        var reservedPath = Path.Combine(Path.GetTempPath(), $"{stem}.mkv");

        var result = DefaultCommandBuilder.SuggestOutputPath(
            sourcePath,
            OutputContainer.Mkv,
            isAutoName: true,
            reservedOutputPaths: [reservedPath]);

        result.Should().Be(Path.Combine(Path.GetTempPath(), $"{stem}-1.mkv"));
    }

    [Fact]
    public void SuggestOutputPath_ExplicitNameDoesNotSkipReservedQueuePath()
    {
        var stem = $"ffapp-{Guid.NewGuid():N}";
        var sourcePath = Path.Combine(Path.GetTempPath(), $"{stem}.mp4");
        var reservedPath = Path.Combine(Path.GetTempPath(), $"{stem}-chosen.mkv");

        var result = DefaultCommandBuilder.SuggestOutputPath(
            sourcePath,
            OutputContainer.Mkv,
            outputFilename: $"{stem}-chosen",
            isAutoName: false,
            reservedOutputPaths: [reservedPath]);

        result.Should().Be(reservedPath);
    }

    // ---------------------------------------------------------------
    // Metadata handling
    // ---------------------------------------------------------------

    [Fact]
    public void Build_Reencode_RemovesOnlyInvalidatedMetadataUsingExactSourceKeys()
    {
        var media = new TestMediaInfoBuilder()
            .WithMetadataKeys("title", "encoding_tool")
            .WithVideoMetadataKeys("language", "BPS-eng", "NUMBER_OF_FRAMES-deu")
            .WithAudioMetadataKeys("title", "DURATION-eng", "iTunSMPB")
            .Build();
        var job = JobWith(s => s
            .WithContainer(OutputContainer.Mkv)
            .WithAudioReencode(AudioCodec.Opus, 96_000));

        var args = _sut.Build(job, media)[0].Arguments;

        args[args.IndexOf("encoding_tool=") - 1].Should().Be("-metadata");
        args[args.IndexOf("BPS-eng=") - 1].Should().Be("-metadata:s:v:0");
        args[args.IndexOf("NUMBER_OF_FRAMES-deu=") - 1].Should().Be("-metadata:s:v:0");
        args[args.IndexOf("DURATION-eng=") - 1].Should().Be("-metadata:s:a:0");
        args[args.IndexOf("iTunSMPB=") - 1].Should().Be("-metadata:s:a:0");
        args.Should().NotContain("title=");
        args.Should().NotContain("language=");
    }

    [Fact]
    public void Build_StreamCopy_PreservesTrackStatistics()
    {
        var media = new TestMediaInfoBuilder()
            .WithVideoMetadataKeys("BPS-eng", "NUMBER_OF_FRAMES-eng")
            .WithAudioMetadataKeys("BPS-eng", "NUMBER_OF_BYTES-eng")
            .Build();
        var job = JobWith(s => s
            .WithContainer(OutputContainer.Mkv)
            .WithVideoMode(VideoMode.Copy)
            .WithAudioCopy());

        var args = _sut.Build(job, media)[0].Arguments;

        args.Should().NotContain("BPS-eng=");
        args.Should().NotContain("NUMBER_OF_FRAMES-eng=");
        args.Should().NotContain("NUMBER_OF_BYTES-eng=");
    }

    [Fact]
    public void Build_ReencodeInMkv_WritesVideoEncoderSettingsWithoutFilePaths()
    {
        var job = JobWith(s => s.WithAv1());

        var args = _sut.Build(job, DefaultMedia())[0].Arguments;
        var settings = args.Single(argument => argument.StartsWith(
            "ENCODER_SETTINGS=c:v=", StringComparison.Ordinal));

        settings.Should().Contain("c:v=libsvtav1")
            .And.Contain("pix_fmt=yuv420p10le")
            .And.Contain("svtav1-params=")
            .And.NotContain(job.SourcePath)
            .And.NotContain(job.OutputPath);
    }

    [Fact]
    public void Build_AudioReencodeInMkv_WritesAudioEncoderSettings()
    {
        var job = JobWith(s => s
            .WithAv1()
            .WithAudioReencode(AudioCodec.Opus, 96_000, channels: 2));

        var args = _sut.Build(job, DefaultMedia())[0].Arguments;
        var settings = args.Single(argument => argument.StartsWith(
            "ENCODER_SETTINGS=c:a=", StringComparison.Ordinal));

        settings.Should().Be("ENCODER_SETTINGS=c:a=libopus; b:a=96000; ac=2");
    }

    [Fact]
    public void Build_ReencodeInMp4_DoesNotWriteUnsupportedEncoderSettingsTag()
    {
        var args = _sut.Build(DefaultJob(), DefaultMedia())[0].Arguments;

        args.Should().NotContain(argument => argument.StartsWith(
            "ENCODER_SETTINGS=", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_TwoPass_WritesEncoderSettingsOnlyToFinalOutput()
    {
        var job = JobWith(s => s.WithVp9(twoPass: true));

        var commands = _sut.Build(job, DefaultMedia());

        commands[0].Arguments.Should().NotContain(argument => argument.StartsWith(
            "ENCODER_SETTINGS=", StringComparison.Ordinal));
        commands[1].Arguments.Should().Contain(argument =>
            argument.StartsWith("ENCODER_SETTINGS=c:v=", StringComparison.Ordinal)
            && argument.EndsWith("passes=2", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------
    // Quality encoding per codec
    // ---------------------------------------------------------------

    [Fact]
    public void Build_H264Quality_ProducesCorrectCodecArguments()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().Contain("-c:v libx264 -pix_fmt yuv420p -vf")
            .And.Contain("-crf 23 -preset medium -profile:v high");
    }

    [Fact]
    public void Build_H265Quality_ProducesCorrectCodecArguments()
    {
        var job = JobWith(s => s.WithH265());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:v libx265 -tag:v hvc1")
            .And.Contain("-crf 28 -preset medium -profile:v main");
    }

    [Fact]
    public void Build_H265QualityWithMkv_DoesNotSetMp4CodecTag()
    {
        var job = JobWith(s => s.WithH265().WithContainer(OutputContainer.Mkv));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-tag:v hvc1");
    }

    [Fact]
    public void Build_Vp9Quality_ProducesCorrectCodecArguments()
    {
        var job = JobWith(s => s.WithVp9());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:v libvpx-vp9")
            .And.Contain("-crf 32 -b:v 0")
            .And.Contain("-deadline good -cpu-used 2 -row-mt 1");
    }

    [Theory]
    [InlineData(VideoCodec.H264, 18, "slow", "high")]
    [InlineData(VideoCodec.H265, 22, "fast", "main10")]
    public void Build_H26xQuality_CustomOptions_UsesProvidedValues(
        VideoCodec codec, int crf, string preset, string profile)
    {
        var job = JobWith(s => WithCodec(s, codec)
            .WithQuality(new H26xQualityOptions(codec, Crf: crf, Preset: preset, Profile: profile)));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain($"-crf {crf} -preset {preset} -profile:v {profile}");
    }

    // ---------------------------------------------------------------
    // Two-pass encoding
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_Vp9TwoPass_SplitsFastAnalysisAndFinalOutput(bool targetBitrate)
    {
        var job = JobWith(s =>
        {
            s.WithVp9(twoPass: true);
            if (targetBitrate)
                s.WithTargetBitrate(2_000_000, twoPass: true);
        });
        var commands = _sut.Build(job, DefaultMedia());

        commands.Should().HaveCount(2);
        var rateControl = targetBitrate ? "-b:v 2000k" : "-crf 32 -b:v 0";
        JoinArgs(commands[0]).Should().Contain(rateControl)
            .And.Contain("-cpu-used 4")
            .And.NotContain("-auto-alt-ref")
            .And.Contain("-pass 1 -passlogfile")
            .And.Contain("-an -sn -dn -f null")
            .And.EndWith(NullOutput);
        JoinArgs(commands[1]).Should().Contain(rateControl)
            .And.Contain("-cpu-used 2")
            .And.Contain("-auto-alt-ref 1")
            .And.Contain("-pass 2 -passlogfile")
            .And.EndWith("/test/output.mp4");
    }

    // ---------------------------------------------------------------
    // Target bitrate encoding
    // ---------------------------------------------------------------

    [Fact]
    public void Build_TargetBitrate_SinglePass_EmitsBitratePresetAndProfile()
    {
        var commands = _sut.Build(JobWith(s => s.WithTargetBitrate(3_000_000)), DefaultMedia());

        commands.Should().ContainSingle();
        JoinArgs(commands[0]).Should().Contain("-b:v 3000k").And.Contain("-preset medium -profile:v high");
    }

    [Fact]
    public void Build_TargetBitrate_TwoPass_UsesBitrateInBothPasses()
    {
        var commands = _sut.Build(JobWith(s => s.WithTargetBitrate(3_000_000, twoPass: true)), DefaultMedia());

        commands.Should().HaveCount(2);
        JoinArgs(commands[0]).Should().Contain("-b:v 3000k")
            .And.Contain("-pass 1")
            .And.Contain("-an -sn -dn -f null")
            .And.EndWith(NullOutput);
        JoinArgs(commands[1]).Should().Contain("-b:v 3000k")
            .And.Contain("-pass 2")
            .And.EndWith("/test/output.mp4");
    }

    [Fact]
    public void Build_TargetBitrate_RoundsToNearestKbps()
    {
        var job = JobWith(s => s.WithTargetBitrate(1_500_600));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-b:v 1501k");
    }

    [Fact]
    public void Build_H265Bitrate_UsesLibx265WithPresetProfile()
    {
        var job = JobWith(s => s.WithH265().WithTargetBitrate(5_000_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:v libx265")
            .And.Contain("-preset medium -profile:v main")
            .And.Contain("-b:v 5000k");
    }

    [Fact]
    public void Build_Vp9Bitrate_SinglePass_UsesDefaultTuning()
    {
        var job = JobWith(s => s.WithVp9().WithTargetBitrate(1_000_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:v libvpx-vp9")
            .And.Contain("-cpu-used 2")
            .And.Contain("-row-mt 1")
            .And.Contain("-b:v 1000k");
    }

    // ---------------------------------------------------------------
    // Target file size encoding
    // ---------------------------------------------------------------

    [Fact]
    public void Build_TargetFileSize_CalculatesBitrateFromSizeAndDuration()
    {
        // 50 MB target, 60s duration, 128kbps audio copy (128000 bps),
        // overhead 3% (percentage wins over 512KiB absolute at this size)
        // totalBits = 50 * 1024 * 1024 * 8 = 419430400
        // overheadBits = max(524288*8, 419430400*0.03) = max(4194304, 12582912) = 12582912
        // usable = 419430400 - 12582912 = 406847488
        // audioBits = 128000 * 60 = 7680000
        // videoBudget = 406847488 - 7680000 = 399167488
        // bitrate = 399167488 / 60 = 6652791
        // formatted: 6653k
        var job = JobWith(s => s.WithTargetFileSize(50));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-b:v 6653k");
    }

    [Fact]
    public void Build_TargetFileSize_SmallTarget_UsesAbsoluteOverheadFloor()
    {
        // 10 MB target, 60s duration, 128kbps audio copy (128000 bps),
        // 512KiB absolute floor wins over 3% (3% of 10MB = 2516582 < 4194304)
        // totalBits = 10 * 1024 * 1024 * 8 = 83886080
        // overheadBits = max(524288*8, 83886080*0.03) = max(4194304, 2516582) = 4194304
        // usable = 83886080 - 4194304 = 79691776
        // audioBits = 128000 * 60 = 7680000
        // videoBudget = 79691776 - 7680000 = 72011776
        // bitrate = 72011776 / 60 = 1200196
        // formatted: 1200k
        var job = JobWith(s => s.WithTargetFileSize(10));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-b:v 1200k");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_TargetSize_SelectionIgnoresUnselectedTrackWithUnknownBitrate(bool twoPass)
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(2, "aac", 128_000), new AudioStreamInfo(5, "aac")).Build();
        var job = new TestJobDefinitionBuilder().WithAudioTracks(2)
            .WithOutputSettings(s => s.WithTargetFileSize(50, twoPass: twoPass)).Build();
        var commands = _sut.Build(job, media);
        var reference = _sut.Build(job with { SelectedAudioStreamIndices = null },
            media with { AudioStreams = [media.AudioStreams[0]] });
        commands.Should().HaveCount(twoPass ? 2 : 1);
        commands.Last().Arguments.SkipWhile(a => a != "-b:v").Take(2)
            .Should().Equal(reference.Last().Arguments.SkipWhile(a => a != "-b:v").Take(2));
        foreach (var command in commands)
            JoinArgs(command).Should().Contain("-map -0:5");
    }

    // ---------------------------------------------------------------
    // Audio track selection
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(AudioMode.Copy, VideoMode.Copy)]
    [InlineData(AudioMode.Reencode, VideoMode.Reencode)]
    [InlineData(AudioMode.Reencode, VideoMode.Remove)]
    public void Build_RemoveAudioTrackTitles_TargetsOnlyIncludedAudio(AudioMode audioMode, VideoMode videoMode)
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(2, "aac") { MetadataKeys = ["title", "language"] },
            new AudioStreamInfo(5, "aac") { MetadataKeys = ["TITLE", "language"] }).Build();
        var job = new TestJobDefinitionBuilder().WithAudioTracks(5)
            .WithOutputSettings(s => s.WithVideoMode(videoMode)
                .WithAudio(new AudioOptions(audioMode, AudioCodec.Aac, 128_000))
                .WithRemoveAudioTrackTitles()).Build();

        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-metadata:s:a title=").And.Contain("-map -0:2");
        args.Should().NotContain("language=").And.NotContain("-map_metadata")
            .And.NotContain("-metadata title=").And.NotContain("-metadata:s:v:0 title=");
    }

    [Theory]
    [InlineData(AudioMode.Copy)]
    [InlineData(AudioMode.Reencode)]
    public void Build_DefaultAudioOptions_PreserveTrackTitles(AudioMode mode)
    {
        var media = new TestMediaInfoBuilder().WithAudioMetadataKeys("title", "language").Build();
        var job = new TestJobDefinitionBuilder().WithOutputSettings(s =>
            s.WithAudio(new AudioOptions(mode, AudioCodec.Aac, 128_000))).Build();

        JoinArgs(_sut.Build(job, media)[0]).Should().NotContain("title=");
    }

    [Fact]
    public void Build_RemoveAudioTrackTitles_TwoPassOnlyClearsFinalOutput()
    {
        var job = new TestJobDefinitionBuilder().WithOutputSettings(s =>
            s.WithTargetBitrate(3_000_000, twoPass: true).WithRemoveAudioTrackTitles()).Build();

        var commands = _sut.Build(job, DefaultMedia());

        commands.Should().HaveCount(2);
        JoinArgs(commands[0]).Should().NotContain("title=");
        JoinArgs(commands[1]).Should().Contain("-metadata:s:a title=");
    }

    [Fact]
    public void Build_RemoveAudioTrackTitles_NoIncludedAudioOmitsDeletion()
    {
        var removed = new TestJobDefinitionBuilder().WithOutputSettings(s =>
            s.WithAudioRemove().WithRemoveAudioTrackTitles()).Build();
        var deselected = new TestJobDefinitionBuilder().WithAudioTracks()
            .WithOutputSettings(s => s.WithRemoveAudioTrackTitles()).Build();
        var noAudio = new TestMediaInfoBuilder().WithAudioStreams().Build();
        var enabled = new TestJobDefinitionBuilder().WithOutputSettings(s =>
            s.WithRemoveAudioTrackTitles()).Build();

        JoinArgs(_sut.Build(removed, DefaultMedia())[0]).Should().NotContain("title=");
        JoinArgs(_sut.Build(deselected, DefaultMedia())[0]).Should().NotContain("title=");
        JoinArgs(_sut.Build(enabled, noAudio)[0]).Should().NotContain("title=");
    }

    [Theory]
    [InlineData(AudioMode.Copy, "copy")]
    [InlineData(AudioMode.Reencode, "aac")]
    public void Build_SelectedAudioTracks_ExcludesOnlyUnselectedAudio(AudioMode mode, string codec)
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(2, "aac"), new AudioStreamInfo(5, "aac"), new AudioStreamInfo(8, "aac")).Build();
        var job = new TestJobDefinitionBuilder().WithAudioTracks(8, 2, 2)
            .WithOutputSettings(s => s.WithAudio(new AudioOptions(mode, AudioCodec.Aac, 128_000))).Build();

        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-map 0").And.Contain("-map -0:5").And.Contain($"-c:a {codec}");
        args.Should().NotContain("-map -0:2").And.NotContain("-map -0:8");
    }

    [Fact]
    public void Build_AllAudioTracks_DoesNotExcludeAnyAudio()
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(new AudioStreamInfo(2, "aac"), new AudioStreamInfo(5, "aac")).Build();
        var args = JoinArgs(_sut.Build(DefaultJob(), media)[0]);
        args.Should().NotContain("-map -0:2").And.NotContain("-map -0:5");
    }

    [Fact]
    public void Build_EmptyAudioSelection_ExcludesEveryAudioTrack()
    {
        var job = new TestJobDefinitionBuilder().WithAudioTracks().Build();
        JoinArgs(_sut.Build(job, DefaultMedia())[0]).Should().Contain("-map -0:1");
    }

    [Fact]
    public void Build_AudioRemove_MapsOutAudioIgnoringTrackSelection()
    {
        var job = new TestJobDefinitionBuilder().WithAudioTracks(99)
            .WithOutputSettings(s => s.WithAudioRemove()).Build();
        JoinArgs(_sut.Build(job, DefaultMedia())[0]).Should().Contain("-map -0:a?").And.NotContain("-c:a");
    }

    [Fact]
    public void Build_UnknownAudioTrack_Throws()
    {
        var job = new TestJobDefinitionBuilder().WithAudioTracks(99).Build();
        var act = () => _sut.Build(job, DefaultMedia());
        act.Should().Throw<CommandBuildException>();
    }

    [Fact]
    public void Build_SelectedAudio_MetadataCleanupUsesOutputAudioIndex()
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(2, "aac") { MetadataKeys = ["BPS"] },
            new AudioStreamInfo(5, "aac") { MetadataKeys = ["NUMBER_OF_BYTES"] }).Build();
        var job = new TestJobDefinitionBuilder().WithAudioTracks(5)
            .WithOutputSettings(s => s.WithAudioReencode(AudioCodec.Aac, 128_000)).Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);
        args.Should().Contain("-metadata:s:a:0 NUMBER_OF_BYTES=").And.NotContain("-metadata:s:a:1").And.NotContain("BPS=");
    }

    // ---------------------------------------------------------------
    // Audio modes
    // ---------------------------------------------------------------

    [Fact]
    public void Build_AudioCopy_EmitsCopyEvenWithoutAudioStreams()
    {
        var job = JobWith(s => s.WithAudioCopy());
        var noAudio = new TestMediaInfoBuilder().WithNoAudio().Build();

        JoinArgs(_sut.Build(job, DefaultMedia())[0]).Should().Contain("-c:a copy");
        JoinArgs(_sut.Build(job, noAudio)[0]).Should().Contain("-c:a copy");
    }

    [Theory]
    [InlineData(AudioCodec.Aac, 128_000, "aac")]
    [InlineData(AudioCodec.Mp3, 192_000, "libmp3lame")]
    [InlineData(AudioCodec.Vorbis, 128_000, "libvorbis")]
    [InlineData(AudioCodec.Opus, 96_000, "libopus")]
    public void Build_AudioReencode_UsesCodecEncoderAndBitrate(AudioCodec codec, int bitrate, string encoder)
    {
        var job = JobWith(s => s.WithAudioReencode(codec, bitrate));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain($"-c:a {encoder} -b:a {bitrate}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void Build_AudioReencode_EmitsAcOnlyForRequestedChannels(int? channels)
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Aac, 128_000, channels));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        if (channels is null)
            args.Should().NotContain("-ac");
        else
            args.Should().Contain($"-ac {channels}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(6)]
    public void Build_OpusSideSurround_PreservesSixChannels(int? channels)
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Opus, 320_000, channels));
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(1, "dts", Channels: 6) { ChannelLayout = "5.1(side)" }).Build();

        var args = _sut.Build(job, media)[0].Arguments;

        args.Should().ContainInOrder("-filter:a:0", "pan=5.1|FL=FL|FR=FR|FC=FC|LFE=LFE|BL=SL|BR=SR");
        if (channels is null)
            args.Should().NotContain("-ac");
        else
            args.Should().ContainInOrder("-ac", "6");
    }

    [Theory]
    [InlineData(AudioCodec.Aac, "5.1(side)", "loudnorm", null)]
    [InlineData(AudioCodec.Opus, "5.1(side)", " volume=2dB ",
        "pan=5.1|FL=FL|FR=FR|FC=FC|LFE=LFE|BL=SL|BR=SR,volume=2dB")]
    public void Build_AudioFilter_AppliesToEveryReencodedTrack(
        AudioCodec codec, string layout, string filter, string? expectedTrackFilter)
    {
        var job = JobWith(s => s.WithAudio(new AudioOptions(AudioMode.Reencode, codec, 320_000, AudioFilter: filter)));
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(1, "dts") { ChannelLayout = layout }).Build();

        var args = _sut.Build(job, media)[0].Arguments;

        args.Should().ContainInOrder("-af", filter.Trim());
        // A per-stream filter overrides -af for its track, so it must carry the user's filter too.
        var perStream = args.IndexOf("-filter:a:0");
        if (expectedTrackFilter is null)
            perStream.Should().Be(-1);
        else
            args[perStream + 1].Should().Be(expectedTrackFilter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_EmptyAudioFilter_OmitsAf(string filter)
    {
        var job = JobWith(s => s.WithAudio(new AudioOptions(AudioMode.Reencode, AudioCodec.Aac, 128_000, AudioFilter: filter)));
        var media = new TestMediaInfoBuilder().WithAudioStreams(new AudioStreamInfo(1, "aac")).Build();

        _sut.Build(job, media)[0].Arguments.Should().NotContain("-af");
    }

    [Theory]
    [InlineData(AudioCodec.Opus, "5.1(side)", 2)]
    [InlineData(AudioCodec.Opus, "5.1(side)", 1)]
    [InlineData(AudioCodec.Opus, "5.1", null)]
    [InlineData(AudioCodec.Opus, "7.1", null)]
    [InlineData(AudioCodec.Opus, "stereo", null)]
    [InlineData(AudioCodec.Opus, null, null)]
    [InlineData(AudioCodec.Aac, "5.1(side)", null)]
    public void Build_AudioWithoutOpusSideSurroundConversion_OmitsFilter(AudioCodec codec, string? layout, int? channels)
    {
        var job = JobWith(s => s.WithAudioReencode(codec, 320_000, channels));
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(1, "dts") { ChannelLayout = layout }).Build();

        _sut.Build(job, media)[0].Arguments.Should().NotContain(arg => arg.StartsWith("-filter:a:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AudioCopySideSurround_OmitsFilter()
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(1, "dts") { ChannelLayout = "5.1(side)" }).Build();

        _sut.Build(JobWith(s => s.WithAudioCopy()), media)[0].Arguments.Should()
            .NotContain(arg => arg.StartsWith("-filter:a:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_OpusMixedTracks_FiltersOnlyIncludedSideSurroundAtOutputIndex(bool selectTracks)
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(2, "dts") { ChannelLayout = "5.1(side)" },
            new AudioStreamInfo(5, "aac") { ChannelLayout = "stereo" },
            new AudioStreamInfo(8, "dts") { ChannelLayout = "5.1(side)" }).Build();
        var builder = new TestJobDefinitionBuilder()
            .WithOutputSettings(s => s.WithAudioReencode(AudioCodec.Opus, 320_000));
        if (selectTracks)
            builder.WithAudioTracks(5, 8);

        var args = _sut.Build(builder.Build(), media)[0].Arguments;

        args.Where(arg => arg.StartsWith("-filter:a:", StringComparison.Ordinal)).Should()
            .Equal(selectTracks ? ["-filter:a:1"] : ["-filter:a:0", "-filter:a:2"]);
    }

    [Theory]
    [InlineData(AudioCodec.Opus, 12000)]
    [InlineData(AudioCodec.Vorbis, 64000)]
    public void Build_SupportedAudioSampleRate_EmitsAr(AudioCodec codec, int rate)
    {
        var job = JobWith(s => s.WithAudioReencode(codec, 128_000, sampleRate: rate));

        JoinArgs(_sut.Build(job, DefaultMedia())[0]).Should().Contain($"-ar {rate}");
    }

    [Theory]
    [InlineData(AudioCodec.Opus, 44100)]
    [InlineData(AudioCodec.Mp3, 96000)]
    [InlineData(AudioCodec.Aac, -1)]
    public void Build_UnsupportedAudioSampleRate_ThrowsBeforeExecution(AudioCodec codec, int rate)
    {
        var job = JobWith(s => s.WithAudioReencode(codec, 128_000, sampleRate: rate));
        var act = () => _sut.Build(job, DefaultMedia());

        act.Should().Throw<CommandBuildException>().WithMessage($"*{rate} Hz*{codec}*");
    }

    [Fact]
    public void Build_AutoOpusSampleRate_OmitsArEvenWhenSourceIs44100()
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Opus, 128_000));
        var media = new TestMediaInfoBuilder().WithAudioStreams(new AudioStreamInfo(1, "aac", SampleRate: 44100)).Build();

        _sut.Build(job, media)[0].Arguments.Should().NotContain("-ar");
    }

    [Theory]
    [InlineData(6, "5.1(side)")]
    [InlineData(2, "stereo")]
    public void Build_TrueHdDownmix_RequestsEmbeddedPresentationBeforeInput(int channels, string layout)
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Opus, 256_000, channels));
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(1, "truehd", Channels: 8) { ChannelLayout = "7.1" }).Build();

        var args = _sut.Build(job, media)[0].Arguments;

        args.Should().ContainInOrder("-downmix:1", layout, "-i");
        args.Should().ContainInOrder("-ac", channels.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("truehd", 8, 1)]
    [InlineData("truehd", 6, 6)]
    [InlineData("truehd", 2, 2)]
    [InlineData("truehd", 8, null)]
    [InlineData("ac3", 6, 2)]
    public void Build_AudioWithoutTrueHdDownmix_OmitsDecoderOption(string codec, int sourceChannels, int? channels)
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Opus, 256_000, channels));
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(1, codec, Channels: sourceChannels)).Build();

        _sut.Build(job, media)[0].Arguments.Should().NotContain(arg => arg.StartsWith("-downmix", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AudioCopyTrueHd_OmitsDecoderOption()
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(1, "truehd", Channels: 8)).Build();

        _sut.Build(JobWith(s => s.WithAudioCopy()), media)[0].Arguments.Should()
            .NotContain(arg => arg.StartsWith("-downmix", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_TrueHdDownmix_TargetsOnlySelectedTrueHdTracksBySourceIndex()
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(1, "truehd", Channels: 8),
            new AudioStreamInfo(2, "ac3", Channels: 6),
            new AudioStreamInfo(3, "truehd", Channels: 8),
            new AudioStreamInfo(4, "truehd", Channels: 8)).Build();
        var job = new TestJobDefinitionBuilder()
            .WithOutputSettings(s => s.WithAudioReencode(AudioCodec.Opus, 256_000, 6))
            .WithAudioTracks(1, 2, 3)
            .Build();

        var args = _sut.Build(job, media)[0].Arguments;

        args.Where(arg => arg.StartsWith("-downmix", StringComparison.Ordinal)).Should()
            .Equal("-downmix:1", "-downmix:3");
    }

    // ---------------------------------------------------------------
    // Subtitle and data stream handling
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("subrip", OutputContainer.Mp4, "mov_text")]
    [InlineData("mov_text", OutputContainer.Mp4, "copy")]
    [InlineData("subrip", OutputContainer.Webm, "webvtt")]
    [InlineData("subrip", OutputContainer.Mkv, "copy")]
    public void Build_Subtitles_CopiesOrConvertsForContainer(string codec, OutputContainer container, string encoder)
    {
        var media = new TestMediaInfoBuilder().WithSubtitles(codec).Build();
        var job = JobWith(s => s.WithContainer(container));

        JoinArgs(_sut.Build(job, media)[0]).Should().Contain($"-c:s {encoder}");
    }

    [Fact]
    public void Build_UnsupportedSubtitle_Throws()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles("hdmv_pgs_subtitle").Build();

        var act = () => _sut.Build(DefaultJob(), media);

        act.Should().Throw<CommandBuildException>();
    }

    [Fact]
    public void Build_SubtitlesPresent_RemoveSubtitles_MapsOutAndNoCopy()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles().Build();
        var job = JobWith(s => s.WithRemoveSubtitles());
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-map -0:s?");
        args.Should().NotContain("-c:s copy");
    }

    [Fact]
    public void Build_SelectedSubtitleTracks_MapsOutUnselectedTracks()
    {
        var media = new TestMediaInfoBuilder().Build() with
        {
            SubtitleStreams = [new SubtitleStreamInfo(2, "subrip", "eng"), new SubtitleStreamInfo(4, "subrip", "deu")],
        };
        var job = DefaultJob() with { SelectedSubtitleStreamIndices = [4] };
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-map -0:2");
        args.Should().NotContain("-map -0:4");
        args.Should().Contain("-c:s mov_text");
    }

    [Fact]
    public void Build_UnselectedUnsupportedSubtitle_DoesNotBlockMp4()
    {
        var media = new TestMediaInfoBuilder().Build() with
        {
            SubtitleStreams = [new SubtitleStreamInfo(2, "hdmv_pgs_subtitle", "eng"), new SubtitleStreamInfo(4, "subrip", "deu")],
        };
        var job = DefaultJob() with { SelectedSubtitleStreamIndices = [4] };
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-map -0:2");
        args.Should().Contain("-c:s mov_text");
    }

    [Fact]
    public void Build_NoSubtitleTracksSelected_MapsOutAllWithoutCodec()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles("hdmv_pgs_subtitle").Build();
        var job = DefaultJob() with { SelectedSubtitleStreamIndices = [] };
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-map -0:2");
        args.Should().NotContain("-c:s");
    }

    [Fact]
    public void Build_SelectedSubtitleTrackMissing_Throws()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles().Build();
        var job = DefaultJob() with { SelectedSubtitleStreamIndices = [9] };

        var act = () => _sut.Build(job, media);

        act.Should().Throw<CommandBuildException>();
    }

    [Theory]
    [InlineData(OutputContainer.Mp4, false)]
    [InlineData(OutputContainer.Mkv, true)]
    public void Build_DataNotCopyable_MapsOutData(OutputContainer container, bool removeData)
    {
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();
        var job = JobWith(s =>
        {
            s.WithContainer(container);
            if (removeData)
                s.WithRemoveData();
        });
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-map -0:d?").And.NotContain("-c:d");
    }

    [Fact]
    public void Build_DataPresentInMkv_CopiesData()
    {
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();
        var job = JobWith(s => s.WithContainer(OutputContainer.Mkv));
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-c:d copy");
    }

    // ---------------------------------------------------------------
    // Multiple video streams
    // ---------------------------------------------------------------

    [Fact]
    public void Build_MultipleVideoStreams_MapsOutExtras()
    {
        var media = new TestMediaInfoBuilder().WithMultipleVideoStreams().Build();
        var args = JoinArgs(_sut.Build(DefaultJob(), media)[0]);

        args.Should().Contain("-map -0:2");
    }

    // ---------------------------------------------------------------
    // Scale filters
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(ResolutionPreset.Original, "scale=trunc(iw/2)*2:trunc(ih/2)*2")]
    [InlineData(ResolutionPreset.P720, "scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2")]
    [InlineData(ResolutionPreset.P1080, "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2")]
    [InlineData(ResolutionPreset.P4K, "scale=3840:2160:force_original_aspect_ratio=decrease:force_divisible_by=2")]
    public void Build_ResolutionPreset_ScalesWithinBoundingBox(ResolutionPreset preset, string scale)
    {
        var job = JobWith(s => s.WithResolution(preset));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain($"-vf {scale}");
    }

    [Theory]
    [InlineData(false, ResolutionPreset.P720, "scale=720:1280:")]
    [InlineData(true, ResolutionPreset.P1080, "scale=1080:1920:")]
    public void Build_PortraitSource_SwapsDimensionsInScale(bool rotated, ResolutionPreset preset, string scale)
    {
        var media = rotated
            ? new TestMediaInfoBuilder().WithRotatedPortrait().Build()
            : new TestMediaInfoBuilder().WithPortrait().Build();
        var job = JobWith(s => s.WithResolution(preset));
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain($"-vf {scale}");
    }

    // ---------------------------------------------------------------
    // Frame rate
    // ---------------------------------------------------------------

    [Fact]
    public void Build_OriginalFrameRate_DoesNotContainRateArg()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().NotContain("-r ");
    }

    [Theory]
    [InlineData(FrameRatePreset.Fps24, "24")]
    [InlineData(FrameRatePreset.Fps30, "30")]
    [InlineData(FrameRatePreset.Fps60, "60")]
    public void Build_FrameRatePreset_EmitsRate(FrameRatePreset preset, string rate)
    {
        var job = JobWith(s => s.WithFrameRate(preset));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain($"-r {rate}");
    }

    // ---------------------------------------------------------------
    // Streaming optimization
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(OutputContainer.Mp4)]
    [InlineData(OutputContainer.Mkv)]
    [InlineData(OutputContainer.Webm)]
    public void Build_OmitsStreamingOptimizationByDefault(OutputContainer container)
    {
        var job = JobWith(s => s.WithContainer(container));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-movflags").And.NotContain("-cues_to_front");
    }

    [Theory]
    [InlineData(OutputContainer.Mp4, VideoMode.Reencode, false)]
    [InlineData(OutputContainer.Mp4, VideoMode.Reencode, true, "-movflags", "+faststart")]
    [InlineData(OutputContainer.Mkv, VideoMode.Reencode, true, "-reserve_index_space", "20224", "-cues_to_front", "1")]
    [InlineData(OutputContainer.Webm, VideoMode.Copy, true, "-reserve_index_space", "20224", "-cues_to_front", "1")]
    [InlineData(OutputContainer.Mkv, VideoMode.Copy, false)]
    [InlineData(OutputContainer.M4a, VideoMode.Remove, true, "-movflags", "+faststart")]
    [InlineData(OutputContainer.M4a, VideoMode.Remove, false)]
    [InlineData(OutputContainer.Ogg, VideoMode.Remove, true)]
    public void Build_StreamingOptimization_UsesTheSelectedContainerAndPreference(
        OutputContainer container, VideoMode mode, bool enabled, params string[] expected)
    {
        var settings = new OutputSettings(Container: container, VideoMode: mode, OptimizeForStreaming: enabled);
        var job = FormatHelpers.BuildJobDefinition("/test/source.mp4", $"/test/out{DefaultCommandBuilder.ContainerExtension(container)}", settings);
        var args = _sut.Build(job, DefaultMedia()).Should().ContainSingle().Subject.Arguments;

        if (expected.Length == 0)
            args.Should().NotContain("-movflags").And.NotContain("-cues_to_front").And.NotContain("-reserve_index_space");
        else
            args.TakeLast(expected.Length + 1).Should().Equal([.. expected, job.OutputPath]);
    }

    [Fact]
    public void Build_StreamingOptimization_ReservesCueSpaceForTheTrimmedLength()
    {
        var job = JobWith(s => s.WithContainer(OutputContainer.Mkv)) with
        {
            OptimizeForStreaming = true,
            TrimStartSeconds = 10,
            TrimEndSeconds = 40,
        };
        var media = new TestMediaInfoBuilder().WithDuration(3600).Build();

        var args = _sut.Build(job, media)[0].Arguments;

        // 16 KiB plus 64 bytes for each of the 30 trimmed seconds.
        args.TakeLast(5).Should().Equal("-reserve_index_space", "18304", "-cues_to_front", "1", job.OutputPath);
    }

    [Fact]
    public void Build_StreamingOptimization_KeepsCuesToFrontWithoutAKnownDuration()
    {
        var job = JobWith(s => s.WithContainer(OutputContainer.Mkv)) with { OptimizeForStreaming = true };
        var media = new TestMediaInfoBuilder().WithDuration(null).Build();

        var args = _sut.Build(job, media)[0].Arguments;

        args.Should().NotContain("-reserve_index_space");
        args.TakeLast(3).Should().Equal("-cues_to_front", "1", job.OutputPath);
    }

    [Fact]
    public void Build_TwoPassStreamingOptimization_OnlyAddsCuesToTheFinalOutput()
    {
        var job = DefaultJob() with
        {
            Container = OutputContainer.Webm,
            VideoCodec = VideoCodec.Vp9,
            TargetQuality = new Vp9QualityOptions(TwoPass: true),
            OptimizeForStreaming = true,
        };

        var commands = _sut.Build(job, DefaultMedia());

        commands.Should().HaveCount(2);
        commands[0].Arguments.Should().NotContain("-cues_to_front").And.NotContain("-reserve_index_space");
        commands[1].Arguments.TakeLast(5).Should().Equal("-reserve_index_space", "20224", "-cues_to_front", "1", job.OutputPath);
    }

    // ---------------------------------------------------------------
    // Pixel formats and HDR
    // ---------------------------------------------------------------

    // --- HDR: SDR backward compatibility ---

    [Theory]
    [InlineData(VideoCodec.H264, "yuv420p")]
    [InlineData(VideoCodec.Av1, "yuv420p10le")]
    [InlineData(VideoCodec.H265, null)]
    [InlineData(VideoCodec.Vp9, null)]
    public void Build_SdrSource_UsesCodecPixelFormatWithoutHdrArguments(VideoCodec codec, string? pixelFormat)
    {
        var cmd = _sut.Build(JobWith(s => WithCodec(s, codec)), DefaultMedia())[0];
        var args = JoinArgs(cmd);

        if (pixelFormat is null)
            args.Should().NotContain("-pix_fmt");
        else
            cmd.Arguments[cmd.Arguments.IndexOf("-pix_fmt") + 1].Should().Be(pixelFormat);
        args.Should().NotContain("color_primaries")
            .And.NotContain("x265-params")
            .And.NotContain("zscale");
    }

    // --- HDR-to-HDR preserve ---

    [Fact]
    public void Build_H265Quality_Hdr10Preserve_SetsMain10AndX265HdrParams()
    {
        // The -color_* tags themselves are covered by ColorMetadataTests.
        var job = JobWith(s => s.WithH265().WithHdrPreserve());
        var media = new TestMediaInfoBuilder().WithHdr10AndMasteringDisplay().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-pix_fmt yuv420p10le").And.Contain("-profile:v main10");
        args.Should().Contain("-x265-params hdr-opt=1:repeat-headers=1"
            + ":colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc"
            + ":max-cll=1000,400"
            + ":master-display=G(7500,3000)B(34000,16000)R(13250,34500)WP(15635,16450)L(10000000,50)");
    }

    [Fact]
    public void Build_H265Quality_HlgPreserve_UsesHlgTransfer()
    {
        var job = JobWith(s => s.WithH265().WithHdrPreserve());
        var media = new TestMediaInfoBuilder().WithHlg().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-color_trc arib-std-b67");
        args.Should().Contain("transfer=arib-std-b67");
    }

    [Fact]
    public void Build_Av1Quality_Hdr10Preserve_IncludesSvtav1Params()
    {
        var job = JobWith(s => s.WithAv1().WithHdrPreserve());
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-pix_fmt yuv420p10le");
        args.Should().Contain("-color_primaries bt2020");
        args.Should().Contain("-svtav1-params");
        args.Should().Contain("enable-hdr=1");
        args.Should().NotContain("x265-params");
    }

    // --- HDR-to-SDR tone mapping ---

    [Fact]
    public void Build_H264Quality_Hdr10Tonemap_IncludesZscaleFilterChain()
    {
        var job = JobWith(s => s.WithToneMapToSdr());
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("zscale=t=linear");
        args.Should().Contain("format=gbrpf32le");
        args.Should().Contain("zscale=p=bt709");
        args.Should().Contain("tonemap=hable");
        args.Should().Contain("zscale=t=bt709:m=bt709:r=tv");
        args.Should().Contain("format=yuv420p");
        args.Should().Contain("-color_primaries bt709");
        args.Should().NotContain("x265-params");
    }

    [Fact]
    public void Build_H264Quality_Hdr10Tonemap_CustomAlgorithmDesatAndPeak()
    {
        var job = JobWith(s => s.WithToneMapToSdr(ToneMapAlgorithm.Mobius, desaturation: 0.0, peakNits: 1000));
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("tonemap=mobius:desat=0.0:peak=1000");
    }

    [Theory]
    [InlineData(ToneMapEngine.Standard, "zscale=t=linear")]
    [InlineData(ToneMapEngine.Libplacebo, "libplacebo=")]
    public void Build_Av1Quality_Hdr10Tonemap_OutputFormatIs10bit(ToneMapEngine engine, string toneMapFilter)
    {
        var job = JobWith(s => WithToneMap(s.WithAv1(), engine));
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain(toneMapFilter).And.Contain("format=yuv420p10le");
    }

    [Theory]
    [InlineData(ToneMapEngine.Standard, "zscale=")]
    [InlineData(ToneMapEngine.Libplacebo, "libplacebo=")]
    public void Build_H264Quality_Hdr10TonemapWithCrop_TonemapComesFirst(ToneMapEngine engine, string toneMapFilter)
    {
        var job = JobWith(s => WithToneMap(s, engine)) with { Crop = new CropRect(100, 100, 1600, 900) };
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = _sut.Build(job, media)[0].Arguments;

        args[args.IndexOf("-vf") + 1].Should().StartWith(toneMapFilter)
            .And.Contain(",crop=1600:900:100:100");
    }

    [Fact]
    public void Build_H265Quality_Hdr10Tonemap_NoHdrPreserveArgs()
    {
        var job = JobWith(s => s.WithH265().WithToneMapToSdr());
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("zscale=t=linear");
        args.Should().NotContain("x265-params");
        args.Should().Contain("-color_primaries bt709");
    }

    [Fact]
    public void Build_H264Quality_SdrSource_ToneMapIgnored()
    {
        var job = JobWith(s => s.WithToneMapToSdr());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("zscale");
        args.Should().NotContain("tonemap");
    }

    // --- HDR-to-SDR libplacebo tone mapping ---

    [Fact]
    public void Build_H264Quality_Hdr10LibplaceboTonemap_IncludesLibplaceboFilter()
    {
        var job = JobWith(s => s.WithLibplaceboTonemapToSdr());
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("libplacebo=tonemapping=bt.2446a");
        args.Should().Contain("gamut_mode=perceptual");
        args.Should().Contain("saturation=1.00");
        args.Should().Contain("contrast_recovery=0.30");
        args.Should().Contain("peak_detect=1");
        args.Should().Contain("color_primaries=bt709");
        args.Should().Contain("color_trc=bt709");
        args.Should().Contain("colorspace=bt709");
        args.Should().Contain("format=yuv420p");
        args.Should().NotContain("zscale");
        args.Should().NotContain("tonemap=hable");
    }

    [Fact]
    public void Build_H264Quality_Hdr10LibplaceboTonemap_CustomSettings()
    {
        var job = JobWith(s => s.WithLibplaceboTonemapToSdr(
            algorithm: LibplaceboToneMapAlgorithm.Spline,
            gamutMode: GamutMode.Darken,
            saturation: 1.2,
            contrastRecovery: 0.5,
            peakDetect: false,
            tonemappingParam: 2.5));
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("libplacebo=tonemapping=spline");
        args.Should().Contain("gamut_mode=darken");
        args.Should().Contain("saturation=1.20");
        args.Should().Contain("contrast_recovery=0.50");
        args.Should().Contain("peak_detect=0");
        args.Should().Contain("tonemapping_param=2.50");
    }

    // ---------------------------------------------------------------
    // Keyframe interval
    // ---------------------------------------------------------------

    [Fact]
    public void Build_KeyframeIntervalFrames_EmitsGopFlag()
    {
        var job = JobWith(s => s.WithKeyframeInterval(120));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-g 120");
    }

    [Fact]
    public void Build_KeyframeIntervalFrames_Av1_EmitsGopFlag()
    {
        var job = JobWith(s => s.WithAv1().WithKeyframeInterval(240));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-g 240");
        args.Should().NotContain("keyint=");
    }

    [Fact]
    public void Build_KeyframeIntervalSeconds_Av1_EmitsViaSvtav1Params()
    {
        var job = JobWith(s => s.WithAv1().WithKeyframeIntervalSeconds(5));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-svtav1-params");
        args.Should().Contain("keyint=5s");
        args.Should().NotContain("-g ");
    }

    [Fact]
    public void Build_NoKeyframeInterval_DoesNotEmitGopFlag()
    {
        var job = JobWith(s => s.WithAv1());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-g ");
    }

    // ---------------------------------------------------------------
    // AV1 encoder tuning (tune, enable-overlays, scm, variance boost, mbr)
    // ---------------------------------------------------------------

    [Fact]
    public void Build_Av1Quality_AppDefaults_EmitsOpinionatedParams()
    {
        // App defaults (tune=0, overlays=1, scm=2) — tune and overlays differ
        // from SVT-AV1 encoder defaults (tune=1, overlays=0, scm=2)
        var job = JobWith(s => s.WithAv1());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-svtav1-params");
        args.Should().Contain("tune=0");
        args.Should().Contain("enable-overlays=1");
        args.Should().NotContain("scm="); // scm=2 matches encoder default
    }

    [Fact]
    public void Build_Av1Quality_EncoderDefaults_OmitsSvtAv1Params()
    {
        // When tuning values match SVT-AV1 encoder defaults, no parameter overrides are emitted
        var job = JobWith(s => s.WithAv1()
            .WithQuality(new Av1QualityOptions(Tune: 1, EnableOverlays: 0, Scm: 2)));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-svtav1-params");
    }

    [Fact]
    public void Build_Av1Quality_ScmOff_EmitsScmParam()
    {
        // scm=0 differs from SVT-AV1 encoder default (2)
        var job = JobWith(s => s.WithAv1()
            .WithQuality(new Av1QualityOptions(Tune: 1, EnableOverlays: 0, Scm: 0)));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-svtav1-params scm=0");
    }

    [Fact]
    public void Build_Av1Quality_AllNonEncoderDefault_CombinesAllParams()
    {
        // All values differ from SVT-AV1 encoder defaults (tune=1, overlays=0, scm=2)
        var job = JobWith(s => s.WithAv1()
            .WithQuality(new Av1QualityOptions(Tune: 0, EnableOverlays: 1, Scm: 3)));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("tune=0");
        args.Should().Contain("enable-overlays=1");
        args.Should().Contain("scm=3");
    }

    [Fact]
    public void Build_Av1Bitrate_NonDefaultTune_EmitsTuneParam()
    {
        var job = JobWith(s => s.WithAv1()
            .WithEncoderOptions(new Av1EncoderOptions(Tune: 3))
            .WithTargetBitrate(5_000_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-svtav1-params");
        args.Should().Contain("tune=3");
    }

    [Theory]
    [InlineData(EncodingStrategy.TargetQuality, 1, 1, 1)]
    [InlineData(EncodingStrategy.TargetBitrate, 1, 4, 8)]
    [InlineData(EncodingStrategy.TargetQuality, 0, 4, 8)]
    [InlineData(EncodingStrategy.TargetBitrate, 0, 1, 1)]
    public void Build_Av1VarianceBoost_EmitsSelectedOptionsOnlyWhenEnabled(
        EncodingStrategy strategy, int enabled, int strength, int octile)
    {
        var job = JobWith(s => s.WithAv1().WithAv1VarianceBoost(enabled, strength, octile)
            .WithStrategy(strategy));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        if (enabled == 1)
        {
            args.Should().Contain(
                $"enable-variance-boost=1:variance-boost-strength={strength}:variance-octile={octile}");
        }
        else
        {
            args.Should().NotContain("variance-boost").And.NotContain("variance-octile");
        }
    }

    [Theory]
    [InlineData(EncodingStrategy.TargetQuality, 5000, true)]
    [InlineData(EncodingStrategy.TargetQuality, 0, false)]
    [InlineData(EncodingStrategy.TargetBitrate, 5000, false)]
    public void Build_Av1MaxBitrate_EmitsMbrOnlyForCappedCrf(EncodingStrategy strategy, int kbps, bool emitted)
    {
        // mbr is only meaningful with CRF; in bitrate mode MaxBitrateKbps on quality options is moot.
        var job = JobWith(s => s.WithAv1().WithAv1MaxBitrateKbps(kbps).WithStrategy(strategy));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        if (emitted)
            args.Should().Contain("-svtav1-params").And.Contain($"mbr={kbps}k");
        else
            args.Should().NotContain("mbr=");
    }

    // ---------------------------------------------------------------
    // Film grain
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(EncodingStrategy.TargetQuality, 1, 0, 1)]
    [InlineData(EncodingStrategy.TargetQuality, 50, 1, 0)]
    [InlineData(EncodingStrategy.TargetBitrate, 8, 0, 0)]
    [InlineData(EncodingStrategy.TargetBitrate, 15, 1, 1)]
    public void Build_Av1FilmGrain_EmitsSelectedSettings(
        EncodingStrategy strategy, int strength, int denoise, int adaptive)
    {
        var job = JobWith(s => s.WithAv1().WithAv1FilmGrain(strength, denoise, adaptive)
            .WithStrategy(strategy));
        var command = _sut.Build(job, DefaultMedia())[0];
        var args = JoinArgs(command);

        args.Should().Contain($"film-grain={strength}");
        args.Should().Contain($"film-grain-denoise={denoise}");
        if (adaptive == 0)
            args.Should().Contain("adaptive-film-grain=0");
        else
            args.Should().NotContain("adaptive-film-grain=");
        command.Arguments.Count(a => a == "-svtav1-params").Should().Be(1);
    }

    [Theory]
    [InlineData(EncodingStrategy.TargetQuality)]
    [InlineData(EncodingStrategy.TargetBitrate)]
    public void Build_Av1FilmGrainOff_OmitsDependentSettings(EncodingStrategy strategy)
    {
        var job = JobWith(s => s.WithAv1().WithAv1FilmGrain(0, denoise: 1, adaptive: 0)
            .WithStrategy(strategy));

        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("film-grain=");
        args.Should().NotContain("film-grain-denoise=");
        args.Should().NotContain("adaptive-film-grain=");
    }

    [Theory]
    [InlineData(EncodingStrategy.TargetQuality, 8, 0, 1)]
    [InlineData(EncodingStrategy.TargetBitrate, 20, 1, 0)]
    public void Build_Av1FilmGrain_UsesOptionsForActiveStrategy(
        EncodingStrategy strategy, int strength, int denoise, int adaptive)
    {
        var job = JobWith(s => s.WithAv1()
            .WithQuality(new Av1QualityOptions(FilmGrain: 8, FilmGrainDenoise: 0, AdaptiveFilmGrain: 1))
            .WithEncoderOptions(new Av1EncoderOptions(FilmGrain: 20, FilmGrainDenoise: 1, AdaptiveFilmGrain: 0))
            .WithStrategy(strategy));

        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain($"film-grain={strength}");
        args.Should().Contain($"film-grain-denoise={denoise}");
        if (adaptive == 0)
            args.Should().Contain("adaptive-film-grain=0");
        else
            args.Should().NotContain("adaptive-film-grain=");
    }

    [Fact]
    public void Build_Av1FilmGrain_MergesWithHdrKeyframesAndAdditionalOptions()
    {
        var job = JobWith(s => s.WithAv1().WithAv1FilmGrain(8)
            .WithHdrPreserve().WithKeyframeIntervalSeconds(5).WithAdditionalOptions("sharpness=1"));
        var command = _sut.Build(job, new TestMediaInfoBuilder().WithHdr10().Build())[0];
        var args = JoinArgs(command);

        args.Should().Contain("film-grain=8").And.Contain("film-grain-denoise=0")
            .And.Contain("enable-hdr=1").And.Contain("keyint=5s").And.Contain("sharpness=1");
        command.Arguments.Count(a => a == "-svtav1-params").Should().Be(1);
    }

    // ---------------------------------------------------------------
    // Additional options (pass-through escape hatch)
    // ---------------------------------------------------------------

    [Fact]
    public void Build_H264_AdditionalOptions_EmittedAsX264Params()
    {
        var job = JobWith(s => s.WithAdditionalOptions("ref=4:bframes=3"));
        var cmd = _sut.Build(job, DefaultMedia())[0];

        cmd.Arguments.Should().ContainInOrder("-x264-params", "ref=4:bframes=3");
    }

    [Fact]
    public void Build_H265_AdditionalOptions_EmittedAsX265Params()
    {
        var job = JobWith(s => s.WithH265()
            .WithQuality(H26xQualityOptions.ForCodec(VideoCodec.H265))
            .WithAdditionalOptions("aq-mode=3:psy-rd=1.0"));
        var cmd = _sut.Build(job, DefaultMedia())[0];

        cmd.Arguments.Should().ContainInOrder("-x265-params", "aq-mode=3:psy-rd=1.0");
    }

    [Fact]
    public void Build_Vp9_AdditionalOptions_SplitIntoSeparateFlags()
    {
        var job = JobWith(s => s.WithVp9()
            .WithAdditionalOptions("lag-in-frames=25:tune-content=screen"));
        var cmd = _sut.Build(job, DefaultMedia())[0];

        cmd.Arguments.Should().ContainInOrder(
            "-lag-in-frames", "25", "-tune-content", "screen");
    }

    [Fact]
    public void Build_Av1_AdditionalOptions_MergedIntoSvtAv1Params()
    {
        var job = JobWith(s => s.WithAv1()
            .WithAdditionalOptions("sharpness=1:film-grain=8"));
        var cmd = _sut.Build(job, DefaultMedia())[0];
        var paramsIdx = cmd.Arguments.IndexOf("-svtav1-params");
        paramsIdx.Should().BeGreaterThanOrEqualTo(0);
        var value = cmd.Arguments[paramsIdx + 1];

        value.Should().Contain("sharpness=1");
        value.Should().Contain("film-grain=8");
        // Merged into existing colon-separated params string, not duplicated.
        cmd.Arguments.Count(a => a == "-svtav1-params").Should().Be(1);
    }

    [Fact]
    public void Build_EmptyAdditionalOptions_AddsNoArgs()
    {
        var baseline = _sut.Build(DefaultJob(), DefaultMedia())[0].Arguments.Length;
        var job = JobWith(s => s.WithAdditionalOptions("   "));

        _sut.Build(job, DefaultMedia())[0].Arguments.Length.Should().Be(baseline);
    }

    // ---------------------------------------------------------------
    // Error cases
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void Build_NonPositiveTargetBitrate_Throws(int bitrate)
    {
        var job = JobWith(s => s.WithTargetBitrate(bitrate));

        var act = () => _sut.Build(job, DefaultMedia());

        act.Should().Throw<CommandBuildException>();
    }

    [Fact]
    public void Build_TargetFileSize_NoDuration_Throws()
    {
        var media = new TestMediaInfoBuilder().WithNoDuration().Build();
        var job = JobWith(s => s.WithTargetFileSize(50));

        var act = () => _sut.Build(job, media);

        act.Should().Throw<CommandBuildException>();
    }

    [Theory]
    [InlineData(null, 128_000)]
    [InlineData(AudioCodec.Aac, null)]
    [InlineData(AudioCodec.Aac, 0)]
    public void Build_AudioReencode_MissingCodecOrPositiveBitrate_Throws(AudioCodec? codec, int? bitrate)
    {
        var job = JobWith(s => s.WithAudio(new AudioOptions(AudioMode.Reencode, codec, bitrate)));

        var act = () => _sut.Build(job, DefaultMedia());

        act.Should().Throw<CommandBuildException>();
    }

    [Fact]
    public void Build_QualityEncoding_MissingQualityOptions_Throws()
    {
        // Manually build a job with quality strategy but null TargetQuality
        var job = new JobDefinition(
            SourcePath: "/test/source.mp4",
            OutputPath: "/test/output.mp4",
            Container: OutputContainer.Mp4,
            VideoCodec: VideoCodec.H264,
            EncodingStrategy: EncodingStrategy.TargetQuality,
            TargetQuality: null);

        var act = () => _sut.Build(job, DefaultMedia());

        act.Should().Throw<CommandBuildException>();
    }

    [Theory]
    [InlineData(VideoCodec.Vp9)]
    [InlineData(VideoCodec.H264)]
    public void Build_TargetBitrate_MismatchedEncoderOptions_Throws(VideoCodec codec)
    {
        VideoEncoderOptions mismatched = codec == VideoCodec.Vp9
            ? H26xEncoderOptions.ForCodec(VideoCodec.H264)
            : new VpxEncoderOptions(VideoCodec.Vp9);
        var job = JobWith(s => s.WithVideoCodec(codec).WithEncoderOptions(mismatched)
            .WithTargetBitrate(2_000_000));

        var act = () => _sut.Build(job, DefaultMedia());

        act.Should().Throw<CommandBuildException>();
    }
}
