using System.Text.Json;
using FFApp.Core.Models;
using FFApp.Infrastructure.Serialization;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public class SettingsSerializerTests
{
    private static OutputSettings RoundTrip(OutputSettings settings)
    {
        var json = SettingsSerializer.SerializeOutputSettings(settings);
        var element = JsonDocument.Parse(json.ToJsonString()).RootElement;
        return SettingsSerializer.DeserializeOutputSettings(element);
    }

    // --- Round-trip tests ---

    [Fact]
    public void RoundTrip_DefaultOutputSettings_Identical()
    {
        var original = new OutputSettings();
        var result = RoundTrip(original);

        result.Container.Should().Be(original.Container);
        result.VideoCodec.Should().Be(original.VideoCodec);
        result.EncodingStrategy.Should().Be(original.EncodingStrategy);
        result.ResolutionPreset.Should().Be(original.ResolutionPreset);
    }

    [Fact]
    public void RoundTrip_H264Quality_PreservesAllFields()
    {
        var original = new TestOutputSettingsBuilder()
            .WithQuality(new H26xQualityOptions(VideoCodec.H264, Crf: 18, Preset: "slow", Profile: "high"))
            .Build();

        var result = RoundTrip(original);

        result.TargetQuality.Should().BeOfType<H26xQualityOptions>();
        var q = (H26xQualityOptions)result.TargetQuality;
        q.Codec.Should().Be(VideoCodec.H264);
        q.Crf.Should().Be(18);
        q.Preset.Should().Be("slow");
        q.Profile.Should().Be("high");
    }

    [Fact]
    public void RoundTrip_H265Quality_PreservesAllFields()
    {
        var original = new TestOutputSettingsBuilder()
            .WithH265()
            .WithQuality(new H26xQualityOptions(VideoCodec.H265, Crf: 24, Preset: "fast", Profile: "main10"))
            .Build();

        var result = RoundTrip(original);

        result.TargetQuality.Should().BeOfType<H26xQualityOptions>();
        var q = (H26xQualityOptions)result.TargetQuality;
        q.Codec.Should().Be(VideoCodec.H265);
        q.Crf.Should().Be(24);
        q.Preset.Should().Be("fast");
        q.Profile.Should().Be("main10");
    }

    [Fact]
    public void RoundTrip_Vp9Quality_PreservesQualityAndTwoPass()
    {
        var original = new TestOutputSettingsBuilder()
            .WithVp9(twoPass: true)
            .WithQuality(new Vp9QualityOptions(Quality: 40, TwoPass: true))
            .Build();

        var result = RoundTrip(original);

        result.TargetQuality.Should().BeOfType<Vp9QualityOptions>();
        var q = (Vp9QualityOptions)result.TargetQuality;
        q.Quality.Should().Be(40);
        q.TwoPass.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_TargetFileSize_PreservesAllFields()
    {
        var original = new TestOutputSettingsBuilder()
            .WithTargetFileSize(100, twoPass: true, overhead: 0.02)
            .Build();

        var result = RoundTrip(original);

        result.TargetFileSize.TargetSizeMb.Should().Be(100);
        result.TargetFileSize.TwoPass.Should().BeTrue();
        result.TargetFileSize.MuxingOverheadRatio.Should().BeApproximately(0.02, 0.0001);
    }

    [Fact]
    public void RoundTrip_TargetBitrate_PreservesAllFields()
    {
        var original = new TestOutputSettingsBuilder()
            .WithTargetBitrate(5_000_000, twoPass: true)
            .Build();

        var result = RoundTrip(original);

        result.TargetBitrate.VideoBitrate.Should().Be(5_000_000);
        result.TargetBitrate.TwoPass.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_AudioReencode_PreservesCodecAndBitrate()
    {
        var original = new TestOutputSettingsBuilder()
            .WithAudioReencode(AudioCodec.Aac, 192_000, channels: 2)
            .Build();

        var result = RoundTrip(original);

        result.AudioOptions.Mode.Should().Be(AudioMode.Reencode);
        result.AudioOptions.Codec.Should().Be(AudioCodec.Aac);
        result.AudioOptions.Bitrate.Should().Be(192_000);
        result.AudioOptions.AudioChannels.Should().Be(2);
    }

    [Fact]
    public void RoundTrip_AudioCopy_ClearsReencodeFields()
    {
        var original = new TestOutputSettingsBuilder()
            .WithAudioCopy()
            .Build();

        var result = RoundTrip(original);

        result.AudioOptions.Mode.Should().Be(AudioMode.Copy);
        result.AudioOptions.Codec.Should().BeNull();
        result.AudioOptions.Bitrate.Should().BeNull();
        result.AudioOptions.AudioChannels.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_StreamHandling_PreservesFlags()
    {
        var original = new TestOutputSettingsBuilder()
            .WithStreamHandling(new StreamHandlingOptions(
                RemoveSubtitles: true, RemoveData: true))
            .Build();

        var result = RoundTrip(original);

        result.StreamHandling.RemoveSubtitles.Should().BeTrue();
        result.StreamHandling.RemoveData.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_VpxEncoderOptions_PreservesCpuUsed()
    {
        var original = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithEncoderOptions(new VpxEncoderOptions(VideoCodec.Vp9, CpuUsed: 6))
            .Build();

        var result = RoundTrip(original);

        result.VideoEncoderOptions.Should().BeOfType<VpxEncoderOptions>();
        var opts = (VpxEncoderOptions)result.VideoEncoderOptions!;
        opts.Codec.Should().Be(VideoCodec.Vp9);
        opts.CpuUsed.Should().Be(6);
    }

    [Fact]
    public void RoundTrip_H264EncoderOptions_PreservesPresetProfile()
    {
        var original = new TestOutputSettingsBuilder()
            .WithEncoderOptions(new H26xEncoderOptions(VideoCodec.H264, Preset: "veryslow", Profile: "baseline"))
            .Build();

        var result = RoundTrip(original);

        result.VideoEncoderOptions.Should().BeOfType<H26xEncoderOptions>();
        var opts = (H26xEncoderOptions)result.VideoEncoderOptions!;
        opts.Codec.Should().Be(VideoCodec.H264);
        opts.Preset.Should().Be("veryslow");
        opts.Profile.Should().Be("baseline");
    }

    [Fact]
    public void RoundTrip_ResolutionPreset_Preserved()
    {
        var original = new TestOutputSettingsBuilder()
            .WithResolution(ResolutionPreset.P1440)
            .Build();

        var result = RoundTrip(original);
        result.ResolutionPreset.Should().Be(ResolutionPreset.P1440);
    }

    [Fact]
    public void RoundTrip_Container_Mkv()
    {
        var original = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mkv)
            .Build();

        var result = RoundTrip(original);
        result.Container.Should().Be(OutputContainer.Mkv);
    }

    // --- Deserialize edge cases ---

    [Fact]
    public void Deserialize_EmptyObject_UsesDefaults()
    {
        var element = JsonDocument.Parse("{}").RootElement;
        var result = SettingsSerializer.DeserializeOutputSettings(element);

        result.Container.Should().Be(OutputContainer.Mp4);
        result.VideoCodec.Should().Be(VideoCodec.H264);
        result.EncodingStrategy.Should().Be(EncodingStrategy.TargetQuality);
    }

    [Fact]
    public void Deserialize_MissingFields_UsesDefaults()
    {
        var json = """{ "container": "mp4" }""";
        var element = JsonDocument.Parse(json).RootElement;
        var result = SettingsSerializer.DeserializeOutputSettings(element);

        result.VideoCodec.Should().Be(VideoCodec.H264);
        result.TargetFileSize.TargetSizeMb.Should().Be(50);
        result.TargetBitrate.VideoBitrate.Should().Be(3_000_000);
    }

    [Fact]
    public void Deserialize_IncompatibleContainerCodec_ThrowsSettingsDecodeException()
    {
        var json = """{ "container": "webm", "video_codec": "h264" }""";
        var element = JsonDocument.Parse(json).RootElement;

        var act = () => SettingsSerializer.DeserializeOutputSettings(element);
        act.Should().Throw<SettingsDecodeException>();
    }

    [Fact]
    public void Deserialize_InvalidEnum_ThrowsSettingsDecodeException()
    {
        var json = """{ "container": "avi" }""";
        var element = JsonDocument.Parse(json).RootElement;

        var act = () => SettingsSerializer.DeserializeOutputSettings(element);
        act.Should().Throw<SettingsDecodeException>();
    }

    // --- Preset serialization ---

    [Fact]
    public void RoundTrip_PresetDefinition_PreservesNameAndSettings()
    {
        var preset = new PresetDefinition("My Preset", new OutputSettings());
        var json = SettingsSerializer.SerializePreset(preset);
        var element = JsonDocument.Parse(json.ToJsonString()).RootElement;
        var result = SettingsSerializer.DeserializePreset(element);

        result.Name.Should().Be("My Preset");
        result.Settings.Container.Should().Be(OutputContainer.Mp4);
    }

    [Fact]
    public void SerializePreset_ContainsSchemaVersion()
    {
        var preset = new PresetDefinition("Test");
        var json = SettingsSerializer.SerializePreset(preset);

        json["schema_version"]!.GetValue<int>().Should().Be(SettingsSerializer.PresetSchemaVersion);
    }

    [Fact]
    public void DeserializePreset_EmptyName_ThrowsPresetDecodeException()
    {
        var json = """{ "name": "", "schema_version": 1, "settings": {} }""";
        var element = JsonDocument.Parse(json).RootElement;

        var act = () => SettingsSerializer.DeserializePreset(element);
        act.Should().Throw<PresetDecodeException>();
    }

    [Fact]
    public void DeserializePreset_WrongSchemaVersion_ThrowsPresetDecodeException()
    {
        var json = """{ "name": "Test", "schema_version": 999, "settings": {} }""";
        var element = JsonDocument.Parse(json).RootElement;

        var act = () => SettingsSerializer.DeserializePreset(element);
        act.Should().Throw<PresetDecodeException>();
    }

    // --- HDR settings ---

    [Fact]
    public void RoundTrip_HdrPreserveWithDefaults_PreservesValues()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithH265()
            .WithHdrPreserve()
            .Build();

        var result = RoundTrip(settings);

        result.HdrMode.Should().Be(HdrMode.Preserve);
        result.ToneMapSettings.Algorithm.Should().Be(ToneMapAlgorithm.Hable);
        result.ToneMapSettings.Desaturation.Should().Be(2.0);
        result.ToneMapSettings.PeakNits.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_ToneMapToSdrWithCustomSettings_PreservesValues()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithToneMapToSdr(ToneMapAlgorithm.Mobius, desaturation: 0.5, peakNits: 1000)
            .Build();

        var result = RoundTrip(settings);

        result.HdrMode.Should().Be(HdrMode.TonemapToSdr);
        result.ToneMapSettings.Algorithm.Should().Be(ToneMapAlgorithm.Mobius);
        result.ToneMapSettings.Desaturation.Should().Be(0.5);
        result.ToneMapSettings.PeakNits.Should().Be(1000);
    }

    [Fact]
    public void Deserialize_MissingHdrFields_ReturnsDefaults()
    {
        var json = """{ "video_codec": "h265" }""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = SettingsSerializer.DeserializeOutputSettings(element);

        result.HdrMode.Should().Be(HdrMode.Preserve);
        result.ToneMapSettings.Algorithm.Should().Be(ToneMapAlgorithm.Hable);
        result.ToneMapSettings.Desaturation.Should().Be(2.0);
        result.ToneMapSettings.PeakNits.Should().BeNull();
    }

    // --- Keyframe interval ---

    [Fact]
    public void RoundTrip_KeyframeIntervalFrames_PreservesValues()
    {
        var original = new TestOutputSettingsBuilder()
            .WithKeyframeInterval(120)
            .Build();

        var result = RoundTrip(original);

        result.KeyframeInterval.Should().Be(120);
        result.KeyframeUnit.Should().Be(KeyframeUnit.Frames);
    }

    [Fact]
    public void RoundTrip_KeyframeIntervalSeconds_PreservesValues()
    {
        var original = new TestOutputSettingsBuilder()
            .WithAv1()
            .WithKeyframeIntervalSeconds(5)
            .Build();

        var result = RoundTrip(original);

        result.KeyframeInterval.Should().Be(5);
        result.KeyframeUnit.Should().Be(KeyframeUnit.Seconds);
    }

    [Fact]
    public void Deserialize_MissingKeyframeFields_ReturnsDefaults()
    {
        var json = """{ "video_codec": "av1", "container": "mkv" }""";
        var element = JsonDocument.Parse(json).RootElement;

        var result = SettingsSerializer.DeserializeOutputSettings(element);

        result.KeyframeInterval.Should().BeNull();
        result.KeyframeUnit.Should().Be(KeyframeUnit.Frames);
    }

    // --- AV1 encoder options ---

    [Fact]
    public void RoundTrip_Av1Quality_PreservesAllFields()
    {
        var original = new TestOutputSettingsBuilder()
            .WithAv1()
            .WithQuality(new Av1QualityOptions(
                Crf: 25, Preset: 4, Tune: 2, EnableOverlays: 0, Scm: 3,
                MaxBitrateKbps: 4500, EnableVarianceBoost: 1))
            .Build();

        var result = RoundTrip(original);

        result.TargetQuality.Should().BeOfType<Av1QualityOptions>();
        var q = (Av1QualityOptions)result.TargetQuality;
        q.Crf.Should().Be(25);
        q.Preset.Should().Be(4);
        q.Tune.Should().Be(2);
        q.EnableOverlays.Should().Be(0);
        q.Scm.Should().Be(3);
        q.MaxBitrateKbps.Should().Be(4500);
        q.EnableVarianceBoost.Should().Be(1);
    }

    [Fact]
    public void RoundTrip_Av1EncoderOptions_PreservesAllFields()
    {
        var original = new TestOutputSettingsBuilder()
            .WithAv1()
            .WithEncoderOptions(new Av1EncoderOptions(
                Preset: 8, Tune: 4, EnableOverlays: 1, Scm: 1, EnableVarianceBoost: 1))
            .Build();

        var result = RoundTrip(original);

        result.VideoEncoderOptions.Should().BeOfType<Av1EncoderOptions>();
        var opts = (Av1EncoderOptions)result.VideoEncoderOptions!;
        opts.Preset.Should().Be(8);
        opts.Tune.Should().Be(4);
        opts.EnableOverlays.Should().Be(1);
        opts.Scm.Should().Be(1);
        opts.EnableVarianceBoost.Should().Be(1);
    }

    [Fact]
    public void Deserialize_Av1Quality_MissingNewFields_UsesDefaults()
    {
        var json = """
        {
            "video_codec": "av1",
            "container": "mkv",
            "target_quality": { "codec": "av1", "crf": 30, "preset": 6 }
        }
        """;
        var element = JsonDocument.Parse(json).RootElement;

        var result = SettingsSerializer.DeserializeOutputSettings(element);

        var q = result.TargetQuality.Should().BeOfType<Av1QualityOptions>().Subject;
        q.Tune.Should().Be(0);
        q.EnableOverlays.Should().Be(1);
        q.Scm.Should().Be(2);
        q.MaxBitrateKbps.Should().Be(0);
        q.EnableVarianceBoost.Should().Be(0);
    }

    [Fact]
    public void RoundTrip_AdditionalOptions_PreservedForAllCodecs()
    {
        var h264 = RoundTrip(new TestOutputSettingsBuilder()
            .WithEncoderOptions(new H26xEncoderOptions(VideoCodec.H264, AdditionalOptions: "-refs 4")).Build());
        ((H26xEncoderOptions)h264.VideoEncoderOptions!).AdditionalOptions.Should().Be("-refs 4");

        var h265 = RoundTrip(new TestOutputSettingsBuilder()
            .WithH265()
            .WithEncoderOptions(new H26xEncoderOptions(VideoCodec.H265, AdditionalOptions: "-tune grain")).Build());
        ((H26xEncoderOptions)h265.VideoEncoderOptions!).AdditionalOptions.Should().Be("-tune grain");

        var vp9 = RoundTrip(new TestOutputSettingsBuilder()
            .WithVp9()
            .WithEncoderOptions(new VpxEncoderOptions(VideoCodec.Vp9, AdditionalOptions: "-lag-in-frames 25")).Build());
        ((VpxEncoderOptions)vp9.VideoEncoderOptions!).AdditionalOptions.Should().Be("-lag-in-frames 25");

        var av1 = RoundTrip(new TestOutputSettingsBuilder()
            .WithAv1()
            .WithEncoderOptions(new Av1EncoderOptions(AdditionalOptions: "-g 240")).Build());
        ((Av1EncoderOptions)av1.VideoEncoderOptions!).AdditionalOptions.Should().Be("-g 240");
    }

    [Fact]
    public void Deserialize_EncoderOptions_MissingAdditionalOptions_DefaultsToEmpty()
    {
        var json = """
        {
            "video_codec": "h264",
            "container": "mp4",
            "video_encoder_options": { "codec": "h264", "preset": "medium", "profile": "high" }
        }
        """;
        var element = JsonDocument.Parse(json).RootElement;

        var result = SettingsSerializer.DeserializeOutputSettings(element);

        ((H26xEncoderOptions)result.VideoEncoderOptions!).AdditionalOptions.Should().Be("");
    }

    [Fact]
    public void Deserialize_Av1EncoderOptions_MissingNewFields_UsesDefaults()
    {
        var json = """
        {
            "video_codec": "av1",
            "container": "mkv",
            "video_encoder_options": { "codec": "av1", "preset": 6 }
        }
        """;
        var element = JsonDocument.Parse(json).RootElement;

        var result = SettingsSerializer.DeserializeOutputSettings(element);

        var opts = result.VideoEncoderOptions.Should().BeOfType<Av1EncoderOptions>().Subject;
        opts.Tune.Should().Be(0);
        opts.EnableOverlays.Should().Be(1);
        opts.Scm.Should().Be(2);
        opts.EnableVarianceBoost.Should().Be(0);
    }
}
