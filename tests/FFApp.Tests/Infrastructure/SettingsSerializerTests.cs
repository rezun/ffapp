using System.Text.Json;
using System.Text.Json.Nodes;
using FFApp.Core.Models;
using FFApp.Infrastructure.Serialization;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public class SettingsSerializerTests
{
    private static OutputSettings RoundTrip(OutputSettings settings)
    {
        var json = SettingsSerializer.SerializeOutputSettings(settings);
        using var document = JsonDocument.Parse(json.ToJsonString());
        return SettingsSerializer.DeserializeOutputSettings(document.RootElement);
    }

    private static OutputSettings Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return SettingsSerializer.DeserializeOutputSettings(document.RootElement);
    }

    // What "{}" decodes to: every field at its default, with encoder options derived from quality.
    private static readonly OutputSettings DecodedDefaults = new(
        VideoEncoderOptions: new H26xEncoderOptions(VideoCodec.H264, Profile: "high"));

    // --- Round trip ---

    // Every field differs from its default somewhere in this set, so a field the serializer
    // forgets to write or read breaks at least one row. Values are already in the normalized
    // form the decoder produces (e.g. no audio codec unless re-encoding).
    private static readonly OutputSettings H264Settings = new(
        Container: OutputContainer.Mp4,
        VideoCodec: VideoCodec.H264,
        EncodingStrategy: EncodingStrategy.TargetFileSize,
        ResolutionPreset: ResolutionPreset.P1440,
        FrameRatePreset: FrameRatePreset.Fps50,
        VideoEncoderOptions: new H26xEncoderOptions(VideoCodec.H264, "veryslow", "baseline", "-refs 4"),
        AudioOptions: new AudioOptions(AudioMode.Reencode, AudioCodec.Aac, 192_000, AudioChannels: 2,
            SampleRate: 96_000, RemoveTrackTitles: true, AudioFilter: "loudnorm=I=-16"),
        StreamHandling: new StreamHandlingOptions(RemoveSubtitles: true, RemoveData: true, RemoveImages: true),
        TargetFileSize: new TargetFileSizeOptions(100, TwoPass: true, MuxingOverheadRatio: 0.02,
            AbsoluteOverheadBytes: 1_000_000),
        TargetBitrate: new TargetBitrateOptions(5_000_000, TwoPass: true),
        TargetQuality: new H26xQualityOptions(VideoCodec.H264, Crf: 18, Preset: "slow", Profile: "high"),
        KeyframeInterval: 120,
        OptimizeForStreaming: false,
        ColorMetadata: new ColorMetadataSettings(ColorMetadataMode.Custom, "bt470bg", "gamma28", "bt470bg", "pc"));

    public static TheoryData<OutputSettings> FullyPopulatedSettings => new()
    {
        H264Settings,
        new OutputSettings(
            Container: OutputContainer.Mkv,
            VideoCodec: VideoCodec.H265,
            EncodingStrategy: EncodingStrategy.TargetBitrate,
            VideoEncoderOptions: new H26xEncoderOptions(VideoCodec.H265, "slow", "main10", "-tune grain"),
            AudioOptions: new AudioOptions(AudioMode.Reencode, AudioCodec.Opus, 128_000, AudioChannels: 6),
            TargetQuality: new H26xQualityOptions(VideoCodec.H265, Crf: 24, Preset: "fast", Profile: "main10"),
            HdrMode: HdrMode.TonemapToSdr,
            ToneMapSettings: new ToneMapSettings(Algorithm: ToneMapAlgorithm.Mobius, Desaturation: 0.5, PeakNits: 1000),
            PostProcessing: new PostProcessingOptions(RecalculateMatroskaStatistics: true),
            OptimizeForStreaming: true,
            ColorMetadata: new ColorMetadataSettings(ColorMetadataMode.AssumeBt709)),
        new OutputSettings(
            Container: OutputContainer.Webm,
            VideoCodec: VideoCodec.Vp9,
            VideoEncoderOptions: new VpxEncoderOptions(VideoCodec.Vp9, CpuUsed: 6, TileColumns: 2, TileRows: 1,
                AdditionalOptions: "-lag-in-frames 25"),
            AudioOptions: new AudioOptions(AudioMode.Reencode, AudioCodec.Opus, 64_000, SampleRate: 12_000),
            TargetQuality: new Vp9QualityOptions(Quality: 40, TwoPass: true),
            HdrMode: HdrMode.TonemapToSdr,
            ToneMapSettings: new ToneMapSettings(ToneMapEngine.Libplacebo,
                LibplaceboAlgorithm: LibplaceboToneMapAlgorithm.Bt2446a, GamutMode: GamutMode.Darken,
                Saturation: 1.2, ContrastRecovery: 0.5, PeakDetect: false, TonemappingParam: 2.5)),
        new OutputSettings(
            Container: OutputContainer.Mkv,
            VideoCodec: VideoCodec.Av1,
            VideoEncoderOptions: new Av1EncoderOptions(Preset: 8, Tune: 4, EnableOverlays: 1, Scm: 1,
                EnableVarianceBoost: 1, AdditionalOptions: "-g 240", FilmGrain: 50, FilmGrainDenoise: 1,
                AdaptiveFilmGrain: 0, VarianceBoostStrength: 3, VarianceOctile: 1),
            AudioOptions: new AudioOptions(AudioMode.Copy, RemoveTrackTitles: true),
            TargetQuality: new Av1QualityOptions(Crf: 25, Preset: 4, Tune: 2, EnableOverlays: 0, Scm: 3,
                MaxBitrateKbps: 4500, EnableVarianceBoost: 1, FilmGrain: 15, FilmGrainDenoise: 1,
                AdaptiveFilmGrain: 0, VarianceBoostStrength: 4, VarianceOctile: 8),
            KeyframeInterval: 5,
            KeyframeUnit: KeyframeUnit.Seconds),
        new OutputSettings(Container: OutputContainer.Mkv, VideoMode: VideoMode.Copy, VideoCodec: VideoCodec.Av1,
            AudioOptions: new AudioOptions(AudioMode.Remove)),
        new OutputSettings(Container: OutputContainer.Mka, VideoMode: VideoMode.Remove, VideoCodec: VideoCodec.H264),
    };

    [Theory]
    [MemberData(nameof(FullyPopulatedSettings))]
    public void RoundTrip_PreservesEverySetting(OutputSettings original)
    {
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void RoundTrip_AudioCopy_ClearsReencodeFields()
    {
        var original = new OutputSettings(AudioOptions: new AudioOptions(
            AudioMode.Copy, AudioCodec.Aac, 192_000, AudioChannels: 2, SampleRate: 48_000));

        RoundTrip(original).AudioOptions.Should().Be(new AudioOptions(AudioMode.Copy));
    }

    [Fact]
    public void RoundTrip_PresetDefinition_PreservesNameAndSettings()
    {
        var preset = new PresetDefinition("My Preset", H264Settings);
        var json = SettingsSerializer.SerializePreset(preset);
        using var document = JsonDocument.Parse(json.ToJsonString());

        SettingsSerializer.DeserializePreset(document.RootElement).Should().Be(preset);
    }

    // --- Missing fields (settings written by older versions) ---

    public static TheoryData<string, OutputSettings> PartialSettings => new()
    {
        { "{}", DecodedDefaults },
        // Settings written before video_mode existed were always re-encodes.
        { """{ "video_codec": "h264" }""", DecodedDefaults },
        {
            """{ "container": "mkv", "audio_options": { "mode": "reencode", "codec": "opus", "bitrate": 128000, "remix_to_stereo": true } }""",
            DecodedDefaults with
            {
                Container = OutputContainer.Mkv,
                AudioOptions = new AudioOptions(AudioMode.Reencode, AudioCodec.Opus, 128_000, AudioChannels: 2),
            }
        },
        {
            """{ "video_encoder_options": { "codec": "h264", "preset": "slow", "profile": "high" } }""",
            DecodedDefaults with { VideoEncoderOptions = new H26xEncoderOptions(VideoCodec.H264, "slow", "high") }
        },
        {
            """{ "video_codec": "av1", "container": "mkv", "target_quality": { "crf": 30, "preset": 6 }, "video_encoder_options": { "preset": 7 } }""",
            DecodedDefaults with
            {
                Container = OutputContainer.Mkv,
                VideoCodec = VideoCodec.Av1,
                TargetQuality = new Av1QualityOptions(),
                VideoEncoderOptions = new Av1EncoderOptions(Preset: 7),
            }
        },
    };

    [Theory]
    [MemberData(nameof(PartialSettings))]
    public void Deserialize_MissingFields_UseDefaults(string json, OutputSettings expected)
    {
        Deserialize(json).Should().Be(expected);
    }

    public static TheoryData<string, VideoEncoderOptions> SettingsWithoutEncoderOptions => new()
    {
        {
            """{ "target_quality": { "crf": 18, "preset": "slow", "profile": "high" } }""",
            new H26xEncoderOptions(VideoCodec.H264, "slow", "high")
        },
        {
            """
            { "video_codec": "av1", "container": "mkv", "video_encoder_options": null,
              "target_quality": { "crf": 26, "preset": 4, "tune": 2, "enable_overlays": 0, "scm": 1,
                "enable_variance_boost": 1, "variance_boost_strength": 4, "variance_octile": 8,
                "film_grain": 12, "film_grain_denoise": 1, "adaptive_film_grain": 0 } }
            """,
            new Av1EncoderOptions(Preset: 4, Tune: 2, EnableOverlays: 0, Scm: 1, EnableVarianceBoost: 1,
                FilmGrain: 12, FilmGrainDenoise: 1, AdaptiveFilmGrain: 0, VarianceBoostStrength: 4, VarianceOctile: 8)
        },
    };

    [Theory]
    [MemberData(nameof(SettingsWithoutEncoderOptions))]
    public void Deserialize_MissingEncoderOptions_DerivesThemFromQuality(string json, VideoEncoderOptions expected)
    {
        Deserialize(json).VideoEncoderOptions.Should().Be(expected);
    }

    // --- Invalid input ---

    [Theory]
    [InlineData("mode", "999")]
    [InlineData("mode", "invalid")]
    [InlineData("primaries", "invalid")]
    [InlineData("transfer", "invalid")]
    [InlineData("matrix", "invalid")]
    [InlineData("range", "invalid")]
    public void Deserialize_InvalidColorSettings_AreRejected(string field, string value)
    {
        var json = SettingsSerializer.SerializeOutputSettings(new OutputSettings());
        var colors = json["color_metadata"] as JsonObject
            ?? throw new InvalidOperationException("Missing color settings.");
        colors[field] = value;

        var decode = () => Deserialize(json.ToJsonString());

        decode.Should().Throw<SettingsDecodeException>();
    }

    [Theory]
    [InlineData("""{ "container": "avi" }""")]
    [InlineData("""{ "container": "webm", "video_codec": "h264" }""")]
    public void Deserialize_UnknownOrIncompatibleValues_AreRejected(string json)
    {
        var decode = () => Deserialize(json);

        decode.Should().Throw<SettingsDecodeException>();
    }

    [Theory]
    [InlineData("target_quality", "variance_boost_strength", 5)]
    [InlineData("target_quality", "variance_octile", 0)]
    [InlineData("video_encoder_options", "variance_boost_strength", 0)]
    [InlineData("video_encoder_options", "variance_octile", 9)]
    [InlineData("target_quality", "film_grain", -1)]
    [InlineData("target_quality", "film_grain", 51)]
    [InlineData("target_quality", "film_grain_denoise", 2)]
    [InlineData("target_quality", "adaptive_film_grain", -1)]
    [InlineData("video_encoder_options", "film_grain", -1)]
    [InlineData("video_encoder_options", "film_grain", 51)]
    [InlineData("video_encoder_options", "film_grain_denoise", 2)]
    [InlineData("video_encoder_options", "adaptive_film_grain", -1)]
    public void Deserialize_InvalidAv1Options_AreRejected(string section, string field, int value)
    {
        var json = $$"""{ "video_codec": "av1", "container": "mkv", "{{section}}": { "{{field}}": {{value}} } }""";

        var decode = () => Deserialize(json);

        decode.Should().Throw<SettingsDecodeException>();
    }

    [Theory]
    [InlineData("""{ "name": "", "schema_version": 1, "settings": {} }""")]
    [InlineData("""{ "name": "Test", "schema_version": 999, "settings": {} }""")]
    [InlineData("""{ "name": "Test", "schema_version": 1, "settings": { "container": "avi" } }""")]
    public void DeserializePreset_InvalidPresets_AreRejected(string json)
    {
        using var document = JsonDocument.Parse(json);

        var decode = () => SettingsSerializer.DeserializePreset(document.RootElement);

        decode.Should().Throw<PresetDecodeException>();
    }
}
