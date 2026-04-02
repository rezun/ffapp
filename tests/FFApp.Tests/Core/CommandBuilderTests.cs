using System.Runtime.InteropServices;

using FFApp.Core;
using FFApp.Core.Models;
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

    // ---------------------------------------------------------------
    // Basic structure
    // ---------------------------------------------------------------

    [Fact]
    public void Build_DefaultSettings_ReturnsSingleCommand()
    {
        var commands = _sut.Build(DefaultJob(), DefaultMedia());

        commands.Should().HaveCount(1);
    }

    [Fact]
    public void Build_DefaultSettings_UsesConfiguredFfmpegPath()
    {
        var commands = _sut.Build(DefaultJob(), DefaultMedia());

        commands[0].ExecutablePath.Should().Be("ffmpeg");
    }

    [Fact]
    public void Build_DefaultSettings_StartsWithHideBannerAndInput()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().StartWith("-hide_banner -y -i /test/source.mp4");
    }

    [Fact]
    public void Build_DefaultSettings_EndsWithOutputPath()
    {
        var commands = _sut.Build(DefaultJob(), DefaultMedia());

        commands[0].Arguments[^1].Should().Be("/test/output.mp4");
    }

    [Fact]
    public void Build_DefaultSettings_ContainsMapAll()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().Contain("-map 0");
    }

    [Fact]
    public void Build_DisplayString_ContainsExecutableAndArguments()
    {
        var cmd = _sut.Build(DefaultJob(), DefaultMedia())[0];

        cmd.DisplayString.Should().StartWith("ffmpeg");
        cmd.DisplayString.Should().Contain("-hide_banner");
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

        args.Should().Contain("-c:v libx265")
            .And.Contain("-crf 28 -preset medium -profile:v main");
    }

    [Fact]
    public void Build_Vp8Quality_ProducesCorrectCodecArguments()
    {
        var job = JobWith(s => s.WithVp8());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:v libvpx")
            .And.Contain("-crf 31 -b:v 0")
            .And.Contain("-deadline good -cpu-used 4");
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

    [Fact]
    public void Build_H264Quality_CustomCrf_UsesProvidedValue()
    {
        var job = JobWith(s => s.WithVideoCodec(VideoCodec.H264)
            .WithQuality(new H264QualityOptions(Crf: 18, Preset: "slow", Profile: "high")));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-crf 18 -preset slow -profile:v high");
    }

    [Fact]
    public void Build_H265Quality_CustomCrf_UsesProvidedValue()
    {
        var job = JobWith(s => s.WithH265()
            .WithQuality(new H265QualityOptions(Crf: 22, Preset: "fast", Profile: "main10")));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-crf 22 -preset fast -profile:v main10");
    }

    // ---------------------------------------------------------------
    // Two-pass encoding
    // ---------------------------------------------------------------

    [Fact]
    public void Build_Vp9QualityTwoPass_ReturnsTwoCommands()
    {
        var job = JobWith(s => s.WithVp9(twoPass: true));
        var commands = _sut.Build(job, DefaultMedia());

        commands.Should().HaveCount(2);
    }

    [Fact]
    public void Build_Vp9QualityTwoPass_Pass1_HasNullOutputAndNoAudio()
    {
        var job = JobWith(s => s.WithVp9(twoPass: true));
        var pass1Args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        pass1Args.Should().Contain("-pass 1")
            .And.Contain("-an -sn -dn -f null")
            .And.EndWith(NullOutput);
    }

    [Fact]
    public void Build_Vp9QualityTwoPass_Pass2_HasOutputPathAndPassFlag()
    {
        var job = JobWith(s => s.WithVp9(twoPass: true));
        var pass2Args = JoinArgs(_sut.Build(job, DefaultMedia())[1]);

        pass2Args.Should().Contain("-pass 2")
            .And.EndWith("/test/output.mp4");
    }

    [Fact]
    public void Build_Vp9QualityTwoPass_Pass1_BumpsCpuUsedToMin4()
    {
        var job = JobWith(s => s.WithVp9(twoPass: true));
        var pass1Args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        pass1Args.Should().Contain("-cpu-used 4");
    }

    [Fact]
    public void Build_Vp9QualityTwoPass_Pass2_KeepsOriginalCpuUsed()
    {
        var job = JobWith(s => s.WithVp9(twoPass: true));
        var pass2Args = JoinArgs(_sut.Build(job, DefaultMedia())[1]);

        pass2Args.Should().Contain("-cpu-used 2");
    }

    [Fact]
    public void Build_Vp9QualityTwoPass_Pass2_HasAutoAltRef()
    {
        var job = JobWith(s => s.WithVp9(twoPass: true));
        var pass2Args = JoinArgs(_sut.Build(job, DefaultMedia())[1]);

        pass2Args.Should().Contain("-auto-alt-ref 1");
    }

    [Fact]
    public void Build_Vp9QualityTwoPass_Pass1_DoesNotHaveAutoAltRef()
    {
        var job = JobWith(s => s.WithVp9(twoPass: true));
        var pass1Args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        pass1Args.Should().NotContain("-auto-alt-ref");
    }

    [Fact]
    public void Build_Vp8QualityTwoPass_ReturnsTwoCommands()
    {
        var job = JobWith(s => s.WithVp8(twoPass: true));
        var commands = _sut.Build(job, DefaultMedia());

        commands.Should().HaveCount(2);
    }

    [Fact]
    public void Build_Vp8QualityTwoPass_Pass2_DoesNotHaveRowMt()
    {
        var job = JobWith(s => s.WithVp8(twoPass: true));
        var pass2Args = JoinArgs(_sut.Build(job, DefaultMedia())[1]);

        pass2Args.Should().NotContain("-row-mt");
    }

    [Fact]
    public void Build_TwoPass_BothCommandsHavePasslogfile()
    {
        var job = JobWith(s => s.WithVp9(twoPass: true));
        var commands = _sut.Build(job, DefaultMedia());

        JoinArgs(commands[0]).Should().Contain("-passlogfile");
        JoinArgs(commands[1]).Should().Contain("-passlogfile");
    }

    // ---------------------------------------------------------------
    // Target bitrate encoding
    // ---------------------------------------------------------------

    [Fact]
    public void Build_TargetBitrate_SinglePass_ContainsBitrateArg()
    {
        var job = JobWith(s => s.WithTargetBitrate(3_000_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-b:v 3000k");
    }

    [Fact]
    public void Build_TargetBitrate_SinglePass_ContainsPresetAndProfile()
    {
        var job = JobWith(s => s.WithTargetBitrate(3_000_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-preset medium -profile:v high");
    }

    [Fact]
    public void Build_TargetBitrate_TwoPass_ReturnsTwoCommands()
    {
        var job = JobWith(s => s.WithTargetBitrate(3_000_000, twoPass: true));
        var commands = _sut.Build(job, DefaultMedia());

        commands.Should().HaveCount(2);
    }

    [Fact]
    public void Build_TargetBitrate_TwoPass_BothPassesHaveSameBitrate()
    {
        var job = JobWith(s => s.WithTargetBitrate(3_000_000, twoPass: true));
        var commands = _sut.Build(job, DefaultMedia());

        JoinArgs(commands[0]).Should().Contain("-b:v 3000k");
        JoinArgs(commands[1]).Should().Contain("-b:v 3000k");
    }

    [Fact]
    public void Build_TargetBitrate_TwoPass_Pass1HasNullOutput()
    {
        var job = JobWith(s => s.WithTargetBitrate(3_000_000, twoPass: true));
        var pass1Args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        pass1Args.Should().Contain("-pass 1")
            .And.Contain("-an -sn -dn -f null")
            .And.EndWith(NullOutput);
    }

    [Fact]
    public void Build_TargetBitrate_RoundsToNearestKbps()
    {
        var job = JobWith(s => s.WithTargetBitrate(1_500_600));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-b:v 1501k");
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

    [Fact]
    public void Build_TargetFileSize_TwoPass_ReturnsTwoCommands()
    {
        var job = JobWith(s => s.WithTargetFileSize(50, twoPass: true));
        var commands = _sut.Build(job, DefaultMedia());

        commands.Should().HaveCount(2);
    }

    // ---------------------------------------------------------------
    // Audio modes
    // ---------------------------------------------------------------

    [Fact]
    public void Build_AudioCopy_ProducesCopyArg()
    {
        var job = JobWith(s => s.WithAudioCopy());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:a copy");
    }

    [Fact]
    public void Build_AudioReencode_Aac_ProducesCorrectArgs()
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Aac, 128_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:a aac -b:a 128000");
    }

    [Fact]
    public void Build_AudioReencode_Mp3_UsesLibmp3lame()
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Mp3, 192_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:a libmp3lame -b:a 192000");
    }

    [Fact]
    public void Build_AudioReencode_Vorbis_UsesLibvorbis()
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Vorbis, 128_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:a libvorbis -b:a 128000");
    }

    [Fact]
    public void Build_AudioReencode_Opus_UsesLibopus()
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Opus, 96_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:a libopus -b:a 96000");
    }

    [Fact]
    public void Build_AudioReencode_WithChannels_AddsAcFlag()
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Aac, 128_000, channels: 2));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-ac 2");
    }

    [Fact]
    public void Build_AudioReencode_WithMono_AddsAcOne()
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Aac, 128_000, channels: 1));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-ac 1");
    }

    [Fact]
    public void Build_AudioReencode_WithoutChannels_DoesNotAddAcFlag()
    {
        var job = JobWith(s => s.WithAudioReencode(AudioCodec.Aac, 128_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-ac");
    }

    [Fact]
    public void Build_AudioRemove_MapsOutAudioStreams()
    {
        var job = JobWith(s => s.WithAudioRemove());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-map -0:a?");
    }

    [Fact]
    public void Build_AudioRemove_DoesNotContainAudioCodecArg()
    {
        var job = JobWith(s => s.WithAudioRemove());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-c:a");
    }

    // ---------------------------------------------------------------
    // Audio mode: Remove strips audio streams
    // ---------------------------------------------------------------

    [Fact]
    public void Build_AudioModeRemove_MapsOutAudioStreams()
    {
        var job = JobWith(s => s.WithAudioRemove());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-map -0:a?");
        args.Should().NotContain("-c:a copy");
    }

    // ---------------------------------------------------------------
    // Subtitle and data stream handling
    // ---------------------------------------------------------------

    [Fact]
    public void Build_SubtitlesPresent_CopiesSubtitles()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles().Build();
        var args = JoinArgs(_sut.Build(DefaultJob(), media)[0]);

        args.Should().Contain("-c:s copy");
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
    public void Build_NoSubtitles_DoesNotAddSubtitleCodec()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().NotContain("-c:s");
    }

    [Fact]
    public void Build_DataPresent_CopiesData()
    {
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();
        var args = JoinArgs(_sut.Build(DefaultJob(), media)[0]);

        args.Should().Contain("-c:d copy");
    }

    [Fact]
    public void Build_DataPresent_RemoveData_MapsOutAndNoCopy()
    {
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();
        var job = JobWith(s => s.WithRemoveData());
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-map -0:d?");
        args.Should().NotContain("-c:d copy");
    }

    [Fact]
    public void Build_NoDataStreams_DoesNotAddDataCodec()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().NotContain("-c:d");
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

    [Fact]
    public void Build_SingleVideoStream_DoesNotMapOutExtras()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().NotContain("-map -0:");
    }

    // ---------------------------------------------------------------
    // Scale filters
    // ---------------------------------------------------------------

    [Fact]
    public void Build_OriginalResolution_UsesIdentityScale()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().Contain("-vf scale=trunc(iw/2)*2:trunc(ih/2)*2");
    }

    [Fact]
    public void Build_720pResolution_UsesScaleWithForceAspect()
    {
        var job = JobWith(s => s.WithResolution(ResolutionPreset.P720));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain(
            "-vf scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2");
    }

    [Fact]
    public void Build_1080pResolution_Uses1920x1080()
    {
        var job = JobWith(s => s.WithResolution(ResolutionPreset.P1080));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-vf scale=1920:1080:");
    }

    [Fact]
    public void Build_4kResolution_Uses3840x2160()
    {
        var job = JobWith(s => s.WithResolution(ResolutionPreset.P4K));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-vf scale=3840:2160:");
    }

    [Fact]
    public void Build_PortraitSource_720p_SwapsDimensionsInScale()
    {
        var media = new TestMediaInfoBuilder().WithPortrait().Build();
        var job = JobWith(s => s.WithResolution(ResolutionPreset.P720));
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-vf scale=720:1280:");
    }

    [Fact]
    public void Build_PortraitSource_OriginalResolution_UsesIdentityScale()
    {
        var media = new TestMediaInfoBuilder().WithPortrait().Build();
        var args = JoinArgs(_sut.Build(DefaultJob(), media)[0]);

        args.Should().Contain("-vf scale=trunc(iw/2)*2:trunc(ih/2)*2");
    }

    // ---------------------------------------------------------------
    // VP9 bitrate two-pass
    // ---------------------------------------------------------------

    [Fact]
    public void Build_Vp9Bitrate_TwoPass_Pass1HasBumpedCpuUsed()
    {
        var job = JobWith(s => s
            .WithVp9()
            .WithTargetBitrate(2_000_000, twoPass: true));
        var pass1Args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        pass1Args.Should().Contain("-cpu-used 4");
    }

    [Fact]
    public void Build_Vp9Bitrate_TwoPass_Pass2KeepsCpuUsed2()
    {
        var job = JobWith(s => s
            .WithVp9()
            .WithTargetBitrate(2_000_000, twoPass: true));
        var pass2Args = JoinArgs(_sut.Build(job, DefaultMedia())[1]);

        pass2Args.Should().Contain("-cpu-used 2");
    }

    [Fact]
    public void Build_Vp9Bitrate_SinglePass_ContainsRowMt()
    {
        var job = JobWith(s => s
            .WithVp9()
            .WithTargetBitrate(2_000_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-row-mt 1");
    }

    // ---------------------------------------------------------------
    // H.265 bitrate encoding
    // ---------------------------------------------------------------

    [Fact]
    public void Build_H265Bitrate_UsesLibx265WithPresetProfile()
    {
        var job = JobWith(s => s.WithH265().WithTargetBitrate(5_000_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:v libx265")
            .And.Contain("-preset medium -profile:v main")
            .And.Contain("-b:v 5000k");
    }

    // ---------------------------------------------------------------
    // Error cases
    // ---------------------------------------------------------------

    [Fact]
    public void Build_TargetBitrate_ZeroBitrate_Throws()
    {
        var job = JobWith(s => s.WithTargetBitrate(0));

        var act = () => _sut.Build(job, DefaultMedia());

        act.Should().Throw<CommandBuildException>();
    }

    [Fact]
    public void Build_TargetBitrate_NegativeBitrate_Throws()
    {
        var job = JobWith(s => s.WithTargetBitrate(-1000));

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

    [Fact]
    public void Build_AudioReencode_NullCodec_Throws()
    {
        var job = JobWith(s => s.WithAudio(new AudioOptions(AudioMode.Reencode, null, 128_000)));

        var act = () => _sut.Build(job, DefaultMedia());

        act.Should().Throw<CommandBuildException>();
    }

    [Fact]
    public void Build_AudioReencode_NullBitrate_Throws()
    {
        var job = JobWith(s => s.WithAudio(new AudioOptions(AudioMode.Reencode, AudioCodec.Aac, null)));

        var act = () => _sut.Build(job, DefaultMedia());

        act.Should().Throw<CommandBuildException>();
    }

    [Fact]
    public void Build_AudioReencode_ZeroBitrate_Throws()
    {
        var job = JobWith(s => s.WithAudio(new AudioOptions(AudioMode.Reencode, AudioCodec.Aac, 0)));

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

    [Fact]
    public void Build_VpxCodec_MismatchedEncoderOptions_Throws()
    {
        // VP9 codec with H264 encoder options
        var job = new JobDefinition(
            SourcePath: "/test/source.mp4",
            OutputPath: "/test/output.webm",
            Container: OutputContainer.Webm,
            VideoCodec: VideoCodec.Vp9,
            EncodingStrategy: EncodingStrategy.TargetBitrate,
            VideoEncoderOptions: new H264EncoderOptions(),
            TargetBitrate: new TargetBitrateOptions(2_000_000));

        var act = () => _sut.Build(job, DefaultMedia());

        act.Should().Throw<CommandBuildException>();
    }

    [Fact]
    public void Build_H264Bitrate_MismatchedVpxEncoderOptions_Throws()
    {
        var job = new JobDefinition(
            SourcePath: "/test/source.mp4",
            OutputPath: "/test/output.mp4",
            Container: OutputContainer.Mp4,
            VideoCodec: VideoCodec.H264,
            EncodingStrategy: EncodingStrategy.TargetBitrate,
            VideoEncoderOptions: new VpxEncoderOptions(VideoCodec.Vp9),
            TargetBitrate: new TargetBitrateOptions(2_000_000));

        var act = () => _sut.Build(job, DefaultMedia());

        act.Should().Throw<CommandBuildException>();
    }

    // ---------------------------------------------------------------
    // Combined scenarios
    // ---------------------------------------------------------------

    [Fact]
    public void Build_SubtitlesAndData_BothCopied()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles().WithDataStreams().Build();
        var args = JoinArgs(_sut.Build(DefaultJob(), media)[0]);

        args.Should().Contain("-c:s copy").And.Contain("-c:d copy");
    }

    [Fact]
    public void Build_RemoveSubtitlesAndData_BothMappedOut()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles().WithDataStreams().Build();
        var job = JobWith(s => s.WithRemoveSubtitles().WithRemoveData());
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-map -0:s?").And.Contain("-map -0:d?");
    }

    [Fact]
    public void Build_Vp8Bitrate_SinglePass_UsesDefaultCpuUsed4()
    {
        var job = JobWith(s => s.WithVp8().WithTargetBitrate(1_000_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:v libvpx")
            .And.Contain("-cpu-used 4")
            .And.Contain("-b:v 1000k");
    }

    [Fact]
    public void Build_NoAudioStreams_AudioCopy_StillEmitsCopyArg()
    {
        var media = new TestMediaInfoBuilder().WithNoAudio().Build();
        var job = JobWith(s => s.WithAudioCopy());
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-c:a copy");
    }

    [Fact]
    public void Build_CustomOutputPath_AppearsInArguments()
    {
        var job = new TestJobDefinitionBuilder()
            .WithOutputPath("/custom/dir/video.mkv")
            .Build();
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().EndWith("/custom/dir/video.mkv");
    }

    [Fact]
    public void Build_CustomSourcePath_AppearsInInputArg()
    {
        var job = new TestJobDefinitionBuilder()
            .WithSourcePath("/my/video.avi")
            .Build();
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-i /my/video.avi");
    }

    // ---------------------------------------------------------------
    // MP4 faststart
    // ---------------------------------------------------------------

    [Fact]
    public void Build_Mp4Container_ContainsMovflagsFaststart()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().Contain("-movflags +faststart");
    }

    [Fact]
    public void Build_MkvContainer_DoesNotContainMovflagsFaststart()
    {
        var job = JobWith(s => s.WithContainer(OutputContainer.Mkv));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-movflags");
    }

    [Fact]
    public void Build_WebmContainer_DoesNotContainMovflagsFaststart()
    {
        var job = JobWith(s => s.WithVp9());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-movflags");
    }

    [Fact]
    public void Build_Mp4TwoPass_Pass2ContainsMovflagsFaststart()
    {
        var job = JobWith(s => s.WithTargetBitrate(3_000_000, twoPass: true));
        var pass2Args = JoinArgs(_sut.Build(job, DefaultMedia())[1]);

        pass2Args.Should().Contain("-movflags +faststart");
    }

    // ---------------------------------------------------------------
    // H.264 pixel format
    // ---------------------------------------------------------------

    [Fact]
    public void Build_H264_ContainsPixFmtYuv420p()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().Contain("-pix_fmt yuv420p");
    }

    [Fact]
    public void Build_H265_DoesNotContainPixFmt()
    {
        var job = JobWith(s => s.WithH265());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-pix_fmt");
    }

    [Fact]
    public void Build_Vp9_DoesNotContainPixFmt()
    {
        var job = JobWith(s => s.WithVp9());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-pix_fmt");
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

    [Fact]
    public void Build_Fps30_ContainsRate30()
    {
        var job = JobWith(s => s.WithFrameRate(FrameRatePreset.Fps30));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-r 30");
    }

    [Fact]
    public void Build_Fps24_ContainsRate24()
    {
        var job = JobWith(s => s.WithFrameRate(FrameRatePreset.Fps24));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-r 24");
    }

    [Fact]
    public void Build_Fps60_ContainsRate60()
    {
        var job = JobWith(s => s.WithFrameRate(FrameRatePreset.Fps60));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-r 60");
    }
}
