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
            .WithQuality(new H264QualityOptions(Crf: 18, Preset: "slow", Profile: "high"))
            .Build();

        var result = RoundTrip(original);

        result.TargetQuality.Should().BeOfType<H264QualityOptions>();
        var q = (H264QualityOptions)result.TargetQuality;
        q.Crf.Should().Be(18);
        q.Preset.Should().Be("slow");
        q.Profile.Should().Be("high");
    }

    [Fact]
    public void RoundTrip_H265Quality_PreservesAllFields()
    {
        var original = new TestOutputSettingsBuilder()
            .WithH265()
            .WithQuality(new H265QualityOptions(Crf: 24, Preset: "fast", Profile: "main10"))
            .Build();

        var result = RoundTrip(original);

        result.TargetQuality.Should().BeOfType<H265QualityOptions>();
        var q = (H265QualityOptions)result.TargetQuality;
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
    public void RoundTrip_Vp8Quality_PreservesFields()
    {
        var original = new TestOutputSettingsBuilder()
            .WithVp8(twoPass: false)
            .WithQuality(new Vp8QualityOptions(Quality: 25, TwoPass: false))
            .Build();

        var result = RoundTrip(original);

        result.TargetQuality.Should().BeOfType<Vp8QualityOptions>();
        var q = (Vp8QualityOptions)result.TargetQuality;
        q.Quality.Should().Be(25);
        q.TwoPass.Should().BeFalse();
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
            .WithAudioReencode(AudioCodec.Aac, 192_000, remix: true)
            .Build();

        var result = RoundTrip(original);

        result.AudioOptions.Mode.Should().Be(AudioMode.Reencode);
        result.AudioOptions.Codec.Should().Be(AudioCodec.Aac);
        result.AudioOptions.Bitrate.Should().Be(192_000);
        result.AudioOptions.RemixToStereo.Should().BeTrue();
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
        result.AudioOptions.RemixToStereo.Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_StreamHandling_PreservesFlags()
    {
        var original = new TestOutputSettingsBuilder()
            .WithStreamHandling(new StreamHandlingOptions(
                RemoveSubtitles: true, RemoveData: true, RemoveAudio: true))
            .Build();

        var result = RoundTrip(original);

        result.StreamHandling.RemoveSubtitles.Should().BeTrue();
        result.StreamHandling.RemoveData.Should().BeTrue();
        result.StreamHandling.RemoveAudio.Should().BeTrue();
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
            .WithEncoderOptions(new H264EncoderOptions(Preset: "veryslow", Profile: "baseline"))
            .Build();

        var result = RoundTrip(original);

        result.VideoEncoderOptions.Should().BeOfType<H264EncoderOptions>();
        var opts = (H264EncoderOptions)result.VideoEncoderOptions!;
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
}
