using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Validation;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Core;

public sealed class ColorMetadataTests
{
    private readonly DefaultCommandBuilder _builder = new(new ExecutablePathsConfig());

    private static VideoStreamInfo Source(string? primaries = null, string? transfer = null,
        string? matrix = null, string? range = null) => new(0, "h264", 4_500_000, 1920, 1080,
            ColorPrimaries: primaries, ColorTransfer: transfer, ColorSpace: matrix, ColorRange: range);
    private static SourceMediaInfo Media(VideoStreamInfo video) => new TestMediaInfoBuilder().WithVideoStreams(video).Build();
    private static string? Value(GeneratedCommand command, string option)
    {
        var index = command.Arguments.IndexOf(option);
        return index < 0 ? null : command.Arguments[index + 1];
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    [InlineData(VideoCodec.Vp9)]
    [InlineData(VideoCodec.Av1)]
    public void Automatic_PreservesSourceValuesAcrossEncoders(VideoCodec codec)
    {
        var settings = new TestOutputSettingsBuilder().WithContainer(OutputContainer.Mkv)
            .WithVideoCodec(codec).Build() with
        {
            TargetQuality = codec switch
            {
                VideoCodec.Av1 => new Av1QualityOptions(),
                VideoCodec.Vp9 => new Vp9QualityOptions(),
                _ => H26xQualityOptions.ForCodec(codec),
            },
        };
        var job = new TestJobDefinitionBuilder().WithOutputSettings(settings).Build();
        var cmd = _builder.Build(job, Media(Source("smpte170m", "smpte170m", "smpte170m", "pc")))[0];
        Value(cmd, "-color_primaries").Should().Be("smpte170m");
        Value(cmd, "-color_trc").Should().Be("smpte170m");
        Value(cmd, "-colorspace").Should().Be("smpte170m");
        Value(cmd, "-color_range").Should().Be("pc");
        Value(cmd, "-vf").Should().StartWith("setparams=color_primaries=smpte170m:color_trc=smpte170m:colorspace=smpte170m:range=pc,");
        cmd.Arguments.Count(x => x == "-color_primaries").Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("unspecified")]
    [InlineData("reserved")]
    [InlineData(" UNKNOWN ")]
    public void Automatic_DoesNotGuessMissingValues(string? missing)
    {
        var job = new TestJobDefinitionBuilder().Build();
        var cmd = _builder.Build(job, Media(Source(missing, missing, missing, "tv")))[0];
        Value(cmd, "-color_primaries").Should().BeNull();
        Value(cmd, "-color_trc").Should().BeNull();
        Value(cmd, "-colorspace").Should().BeNull();
        Value(cmd, "-color_range").Should().Be("tv");
    }

    [Fact]
    public void Fallback_FillsOnlyMissingFieldsAndPreservesFullRange()
    {
        var settings = new ColorMetadataSettings(ColorMetadataMode.AssumeBt709);
        var source = Source("smpte170m", "unknown", null, "pc");
        ColorMetadataResolver.Resolve(source, settings, HdrMode.Preserve)
            .Should().Be(new ColorMetadata("smpte170m", "bt709", "bt709", "pc"));
        ColorMetadataResolver.Resolve(Source(), settings, HdrMode.Preserve)
            .Should().Be(new ColorMetadata("bt709", "bt709", "bt709", "tv"));
    }

    [Theory]
    [InlineData(ColorMetadataMode.Automatic)]
    [InlineData(ColorMetadataMode.AssumeBt709)]
    [InlineData(ColorMetadataMode.Custom)]
    public void HdrPreserve_TakesPrecedenceAndPreservesHdrMetadata(ColorMetadataMode mode)
    {
        var settings = new TestOutputSettingsBuilder().WithH265().WithHdrPreserve()
            .WithColorMetadata(new(mode)).Build();
        var job = new TestJobDefinitionBuilder().WithOutputSettings(settings).Build();
        var cmd = _builder.Build(job, new TestMediaInfoBuilder().WithHdr10AndMasteringDisplay().Build())[0];
        Value(cmd, "-color_primaries").Should().Be("bt2020");
        Value(cmd, "-color_trc").Should().Be("smpte2084");
        Value(cmd, "-colorspace").Should().Be("bt2020nc");
        string.Join(" ", cmd.Arguments).Should().Contain("master-display=");
    }

    [Theory]
    [InlineData(ColorMetadataMode.AssumeBt709)]
    [InlineData(ColorMetadataMode.Custom)]
    public void IncompleteHdr_IsNeverRelabeledAsSdr(ColorMetadataMode mode)
    {
        var video = Source(transfer: "smpte2084");
        ColorMetadataResolver.Resolve(video, new(mode), HdrMode.Preserve)
            .Should().Be(new ColorMetadata(Transfer: "smpte2084"));
    }

    [Theory]
    [InlineData(ToneMapEngine.Standard)]
    [InlineData(ToneMapEngine.Libplacebo)]
    public void ToneMapping_LabelsTheConvertedOutputAndUsesLimitedRange(ToneMapEngine engine)
    {
        var settings = new TestOutputSettingsBuilder().WithH265().WithToneMapToSdr()
            .WithColorMetadata(new(ColorMetadataMode.Custom, "bt2020", "smpte2084", "bt2020nc", "pc")).Build();
        settings = settings with { ToneMapSettings = settings.ToneMapSettings with { Engine = engine } };
        var cmd = _builder.Build(new TestJobDefinitionBuilder().WithOutputSettings(settings).Build(),
            new TestMediaInfoBuilder().WithHdr10().Build())[0];
        Value(cmd, "-color_primaries").Should().Be("bt709");
        Value(cmd, "-color_trc").Should().Be("bt709");
        Value(cmd, "-colorspace").Should().Be("bt709");
        Value(cmd, "-color_range").Should().Be("tv");
        Value(cmd, "-vf").Should().Contain(engine == ToneMapEngine.Standard ? "r=tv" : "range=limited")
            .And.EndWith("setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv");
        Value(cmd, "-vf").Should().NotStartWith("setparams=");
    }

    [Theory]
    [InlineData(VideoMode.Copy)]
    [InlineData(VideoMode.Remove)]
    public void NonEncoding_IgnoresColorOverrides(VideoMode mode)
    {
        var job = new TestJobDefinitionBuilder().WithOutputSettings(s => s.WithVideoMode(mode)
            .WithColorMetadata(new(ColorMetadataMode.Custom))).Build();
        var cmd = _builder.Build(job, Media(Source("bt709", "bt709", "bt709", "tv")))[0];
        Value(cmd, "-color_primaries").Should().BeNull();
        Value(cmd, "-color_range").Should().BeNull();
    }

    [Fact]
    public void TwoPass_AppliesManualMetadataToBothPasses()
    {
        var settings = new ColorMetadataSettings(ColorMetadataMode.Custom, "bt470bg", "gamma28", "bt470bg", "pc");
        var job = new TestJobDefinitionBuilder().WithOutputSettings(s => s.WithTargetBitrate(3_000_000, twoPass: true)
            .WithColorMetadata(settings)).Build();
        job.ColorMetadata.Should().Be(settings);
        var commands = _builder.Build(job, Media(Source()));
        commands.Should().HaveCount(2);
        foreach (var cmd in commands)
        {
            Value(cmd, "-color_primaries").Should().Be("bt470bg");
            Value(cmd, "-color_trc").Should().Be("gamma28");
            Value(cmd, "-colorspace").Should().Be("bt470bg");
            Value(cmd, "-color_range").Should().Be("pc");
            Value(cmd, "-vf").Should().StartWith("setparams=color_primaries=bt470bg:color_trc=bt470bg:colorspace=bt470bg:range=pc,");
        }
    }

    [Fact]
    public void HdrPreserve_KeepsSourceMatrixAndRangeInX265Parameters()
    {
        var video = Source("bt2020", "arib-std-b67", "bt2020c", "pc");
        var job = new TestJobDefinitionBuilder().WithOutputSettings(s => s.WithH265()).Build();
        var cmd = _builder.Build(job, Media(video))[0];
        Value(cmd, "-colorspace").Should().Be("bt2020c");
        Value(cmd, "-color_range").Should().Be("pc");
        Value(cmd, "-x265-params").Should().Contain("colormatrix=bt2020c").And.Contain("transfer=arib-std-b67");
    }

    [Fact]
    public void InvalidManualMetadata_IsRejectedByValidationAndBuilder()
    {
        var settings = new TestOutputSettingsBuilder().WithColorMetadata(
            new(ColorMetadataMode.Custom, Matrix: "invalid")).Build();
        var media = Media(Source());
        new ValidationService().Validate(settings, media).Issues.Should()
            .Contain(x => x.Code == "invalid_color_metadata");
        var job = new TestJobDefinitionBuilder().WithOutputSettings(settings).Build();
        var build = () => _builder.Build(job, media);
        build.Should().Throw<CommandBuildException>();
    }
}
