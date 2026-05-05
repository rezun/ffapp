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
            .WithQuality(new H26xQualityOptions(VideoCodec.H264, Crf: 18, Preset: "slow", Profile: "high")));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-crf 18 -preset slow -profile:v high");
    }

    [Fact]
    public void Build_H265Quality_CustomCrf_UsesProvidedValue()
    {
        var job = JobWith(s => s.WithH265()
            .WithQuality(new H26xQualityOptions(VideoCodec.H265, Crf: 22, Preset: "fast", Profile: "main10")));
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
    public void Build_SubripSubtitlesInMp4_ConvertsToMovText()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles().Build();
        var args = JoinArgs(_sut.Build(DefaultJob(), media)[0]);

        args.Should().Contain("-c:s mov_text");
    }

    [Fact]
    public void Build_MovTextSubtitlesInMp4_CopiesSubtitles()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles("mov_text").Build();
        var args = JoinArgs(_sut.Build(DefaultJob(), media)[0]);

        args.Should().Contain("-c:s copy");
    }

    [Fact]
    public void Build_SubripSubtitlesInWebm_ConvertsToWebVtt()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles("subrip").Build();
        var job = JobWith(s => s.WithVp9());
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-c:s webvtt");
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
    public void Build_NoSubtitles_DoesNotAddSubtitleCodec()
    {
        var args = JoinArgs(_sut.Build(DefaultJob(), DefaultMedia())[0]);

        args.Should().NotContain("-c:s");
    }

    [Fact]
    public void Build_DataPresentInMp4_MapsOutAndNoCopy()
    {
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();
        var args = JoinArgs(_sut.Build(DefaultJob(), media)[0]);

        args.Should().Contain("-map -0:d?");
        args.Should().NotContain("-c:d copy");
    }

    [Fact]
    public void Build_DataPresentInMkv_CopiesData()
    {
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();
        var job = JobWith(s => s.WithContainer(OutputContainer.Mkv));
        var args = JoinArgs(_sut.Build(job, media)[0]);

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

    [Fact]
    public void Build_RotatedPortraitSource_1080p_SwapsDimensionsInScale()
    {
        var media = new TestMediaInfoBuilder().WithRotatedPortrait().Build();
        var job = JobWith(s => s.WithResolution(ResolutionPreset.P1080));
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-vf scale=1080:1920:");
    }

    [Fact]
    public void Build_RotatedPortraitSource_720p_SwapsDimensionsInScale()
    {
        var media = new TestMediaInfoBuilder().WithRotatedPortrait().Build();
        var job = JobWith(s => s.WithResolution(ResolutionPreset.P720));
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-vf scale=720:1280:");
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
            VideoEncoderOptions: H26xEncoderOptions.ForCodec(VideoCodec.H264),
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
    public void Build_Mp4SubtitlesAndData_ConvertsSubtitlesAndMapsOutData()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles().WithDataStreams().Build();
        var args = JoinArgs(_sut.Build(DefaultJob(), media)[0]);

        args.Should().Contain("-c:s mov_text").And.Contain("-map -0:d?");
        args.Should().NotContain("-c:d copy");
    }

    [Fact]
    public void Build_MkvSubtitlesAndData_BothCopied()
    {
        var media = new TestMediaInfoBuilder().WithSubtitles().WithDataStreams().Build();
        var job = JobWith(s => s.WithContainer(OutputContainer.Mkv));
        var args = JoinArgs(_sut.Build(job, media)[0]);

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
    public void Build_Vp9Bitrate_SinglePass_UsesDefaultCpuUsed2()
    {
        var job = JobWith(s => s.WithVp9().WithTargetBitrate(1_000_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-c:v libvpx-vp9")
            .And.Contain("-cpu-used 2")
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

    // --- HDR: SDR backward compatibility ---

    [Fact]
    public void Build_H264Quality_SdrSource_NoHdrArguments()
    {
        var job = DefaultJob();
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-pix_fmt yuv420p");
        args.Should().NotContain("color_primaries");
        args.Should().NotContain("x265-params");
        args.Should().NotContain("zscale");
    }

    [Fact]
    public void Build_Av1Quality_SdrSource_UsesYuv420p10le()
    {
        var job = JobWith(s => s.WithAv1());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-pix_fmt yuv420p10le");
        args.Should().NotContain("color_primaries");
    }

    // --- HDR-to-HDR preserve ---

    [Fact]
    public void Build_H265Quality_Hdr10Preserve_SetsMain10AndColorMetadata()
    {
        var job = JobWith(s => s.WithH265().WithHdrPreserve());
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-pix_fmt yuv420p10le");
        args.Should().Contain("-color_primaries bt2020");
        args.Should().Contain("-color_trc smpte2084");
        args.Should().Contain("-colorspace bt2020nc");
        args.Should().Contain("-profile:v main10");
    }

    [Fact]
    public void Build_H265Quality_Hdr10Preserve_IncludesX265Params()
    {
        var job = JobWith(s => s.WithH265().WithHdrPreserve());
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("-x265-params");
        args.Should().Contain("hdr-opt=1");
        args.Should().Contain("repeat-headers=1");
        args.Should().Contain("colorprim=bt2020");
        args.Should().Contain("transfer=smpte2084");
        args.Should().Contain("colormatrix=bt2020nc");
    }

    [Fact]
    public void Build_H265Quality_Hdr10WithMaxCll_IncludesMaxCll()
    {
        var job = JobWith(s => s.WithH265().WithHdrPreserve());
        var media = new TestMediaInfoBuilder().WithHdr10(maxCll: 1000, maxFall: 400).Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("max-cll=1000,400");
    }

    [Fact]
    public void Build_H265Quality_Hdr10WithMasteringDisplay_IncludesMasterDisplay()
    {
        var job = JobWith(s => s.WithH265().WithHdrPreserve());
        var media = new TestMediaInfoBuilder().WithHdr10AndMasteringDisplay().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("master-display=G(7500,3000)B(34000,16000)R(13250,34500)WP(15635,16450)L(10000000,50)");
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
        args.Should().NotContain("color_primaries");
        args.Should().NotContain("x265-params");
    }

    [Fact]
    public void Build_H264Quality_Hdr10Tonemap_CustomAlgorithm()
    {
        var job = JobWith(s => s.WithToneMapToSdr(ToneMapAlgorithm.Mobius));
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("tonemap=mobius");
    }

    [Fact]
    public void Build_H264Quality_Hdr10Tonemap_CustomDesatAndPeak()
    {
        var job = JobWith(s => s.WithToneMapToSdr(desaturation: 0.0, peakNits: 1000));
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("tonemap=hable:desat=0.0:peak=1000");
    }

    [Fact]
    public void Build_Av1Quality_Hdr10Tonemap_ToneMapOutputFormatIs10bit()
    {
        var job = JobWith(s => s.WithAv1().WithToneMapToSdr());
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        // Tonemap chain output should be 10-bit for AV1
        args.Should().Contain("format=yuv420p10le");
        args.Should().Contain("zscale=t=linear");
    }

    [Fact]
    public void Build_H264Quality_Hdr10Tonemap_WithCrop_TonemapComesFirst()
    {
        var settings = new TestOutputSettingsBuilder().WithToneMapToSdr().Build();
        var job = new TestJobDefinitionBuilder()
            .WithOutputSettings(settings)
            .Build() with { Crop = new CropRect(100, 100, 1600, 900) };
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        var vfIndex = args.IndexOf("-vf");
        vfIndex.Should().BeGreaterThan(-1);

        // Extract the filter chain value
        var filterStart = vfIndex + 4; // "-vf " length
        var filterChain = args[filterStart..];
        var zscalePos = filterChain.IndexOf("zscale");
        var cropPos = filterChain.IndexOf("crop=");
        zscalePos.Should().BeLessThan(cropPos, "tonemap filters should come before crop");
    }

    [Fact]
    public void Build_H265Quality_Hdr10Tonemap_NoHdrPreserveArgs()
    {
        var job = JobWith(s => s.WithH265().WithToneMapToSdr());
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("zscale=t=linear");
        args.Should().NotContain("x265-params");
        args.Should().NotContain("-color_primaries");
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

    [Fact]
    public void Build_Av1Quality_Hdr10LibplaceboTonemap_OutputFormatIs10bit()
    {
        var job = JobWith(s => s.WithAv1().WithLibplaceboTonemapToSdr());
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("format=yuv420p10le");
        args.Should().Contain("libplacebo=");
    }

    [Fact]
    public void Build_H264Quality_Hdr10LibplaceboTonemap_WithCrop_LibplaceboComesFirst()
    {
        var settings = new TestOutputSettingsBuilder().WithLibplaceboTonemapToSdr().Build();
        var job = new TestJobDefinitionBuilder()
            .WithOutputSettings(settings)
            .Build() with { Crop = new CropRect(100, 100, 1600, 900) };
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        var vfIndex = args.IndexOf("-vf");
        vfIndex.Should().BeGreaterThan(-1);

        var filterStart = vfIndex + 4;
        var filterChain = args[filterStart..];
        var libplaceboPos = filterChain.IndexOf("libplacebo");
        var cropPos = filterChain.IndexOf("crop=");
        libplaceboPos.Should().BeLessThan(cropPos, "libplacebo filter should come before crop");
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
    public void Build_KeyframeIntervalSeconds_Av1Hdr_CombinesWithHdrParam()
    {
        var job = JobWith(s => s.WithAv1().WithHdrPreserve().WithKeyframeIntervalSeconds(10));
        var media = new TestMediaInfoBuilder().WithHdr10().Build();
        var args = JoinArgs(_sut.Build(job, media)[0]);

        args.Should().Contain("keyint=10s");
        args.Should().Contain("enable-hdr=1");
        // Both should be in a single -svtav1-params value
        var svtIdx = args.IndexOf("-svtav1-params");
        args.IndexOf("-svtav1-params", svtIdx + 1).Should().Be(-1,
            "there should be only one -svtav1-params flag");
    }

    [Fact]
    public void Build_NoKeyframeInterval_DoesNotEmitGopFlag()
    {
        var job = JobWith(s => s.WithAv1());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("-g ");
    }

    // ---------------------------------------------------------------
    // AV1 encoder tuning (tune, enable-overlays, scm)
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
    public void Build_Av1Quality_EncoderDefaults_OnlyEmitsScd()
    {
        // When tuning values match SVT-AV1 encoder defaults, only scd and qm params are emitted
        var job = JobWith(s => s.WithAv1()
            .WithQuality(new Av1QualityOptions(Tune: 1, EnableOverlays: 0, Scm: 2)));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("scd=1");
        args.Should().Contain("enable-qm=1");
        args.Should().Contain("qm-min=2");
        args.Should().Contain("qm-max=15");
        args.Should().NotContain("tune=");
        args.Should().NotContain("enable-overlays=");
        args.Should().NotContain("scm=");
    }

    [Fact]
    public void Build_Av1Quality_NonDefaultTune_EmitsTuneParam()
    {
        var job = JobWith(s => s.WithAv1()
            .WithQuality(new Av1QualityOptions(Tune: 2)));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-svtav1-params");
        args.Should().Contain("tune=2");
    }

    [Fact]
    public void Build_Av1Quality_OverlaysEnabled_EmitsOverlayParam()
    {
        // enable-overlays=1 differs from SVT-AV1 encoder default (0)
        var job = JobWith(s => s.WithAv1()
            .WithQuality(new Av1QualityOptions(Tune: 1, EnableOverlays: 1, Scm: 2)));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-svtav1-params");
        args.Should().Contain("enable-overlays=1");
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
    public void Build_Av1Quality_VarianceBoostDefault_Omitted()
    {
        var job = JobWith(s => s.WithAv1());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("enable-variance-boost=");
    }

    [Fact]
    public void Build_Av1Quality_VarianceBoostEnabled_EmitsParam()
    {
        var job = JobWith(s => s.WithAv1()
            .WithQuality(new Av1QualityOptions(EnableVarianceBoost: 1)));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-svtav1-params");
        args.Should().Contain("enable-variance-boost=1");
    }

    [Fact]
    public void Build_Av1Encoder_VarianceBoostEnabled_EmitsParam()
    {
        var job = JobWith(s => s.WithAv1()
            .WithStrategy(EncodingStrategy.TargetBitrate)
            .WithEncoderOptions(new Av1EncoderOptions(EnableVarianceBoost: 1)));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("enable-variance-boost=1");
    }

    [Fact]
    public void Build_Av1Quality_MaxBitrateSet_EmitsMbrParam()
    {
        var job = JobWith(s => s.WithAv1().WithAv1MaxBitrateKbps(5000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().Contain("-svtav1-params");
        args.Should().Contain("mbr=5000k");
    }

    [Fact]
    public void Build_Av1Quality_MaxBitrateZero_OmitsMbrParam()
    {
        var job = JobWith(s => s.WithAv1());
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("mbr=");
    }

    [Fact]
    public void Build_Av1Bitrate_MaxBitrateIgnored_OmitsMbrParam()
    {
        // mbr only meaningful with CRF; in bitrate mode MaxBitrateKbps on quality options is moot.
        var job = JobWith(s => s.WithAv1()
            .WithAv1MaxBitrateKbps(5000)
            .WithTargetBitrate(3_000_000));
        var args = JoinArgs(_sut.Build(job, DefaultMedia())[0]);

        args.Should().NotContain("mbr=");
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
}
