using FFApp.Core.Models;

namespace FFApp.Tests.Builders;

public class TestOutputSettingsBuilder
{
    private OutputSettings _settings = new();

    public OutputSettings Build() => _settings;

    public TestOutputSettingsBuilder WithContainer(OutputContainer container) =>
        Apply(s => s with { Container = container });

    public TestOutputSettingsBuilder WithVideoCodec(VideoCodec codec) =>
        Apply(s => s with { VideoCodec = codec });

    public TestOutputSettingsBuilder WithStrategy(EncodingStrategy strategy) =>
        Apply(s => s with { EncodingStrategy = strategy });

    public TestOutputSettingsBuilder WithResolution(ResolutionPreset preset) =>
        Apply(s => s with { ResolutionPreset = preset });

    public TestOutputSettingsBuilder WithEncoderOptions(VideoEncoderOptions options) =>
        Apply(s => s with { VideoEncoderOptions = options });

    public TestOutputSettingsBuilder WithQuality(QualityOptions quality) =>
        Apply(s => s with { EncodingStrategy = EncodingStrategy.TargetQuality, TargetQuality = quality });

    public TestOutputSettingsBuilder WithTargetFileSize(
        int sizeMb,
        bool twoPass = false,
        double overhead = FileSizeDefaults.MuxingOverheadRatio,
        int absoluteOverheadBytes = FileSizeDefaults.AbsoluteOverheadBytes) =>
        Apply(s => s with
        {
            EncodingStrategy = EncodingStrategy.TargetFileSize,
            TargetFileSize = new TargetFileSizeOptions(sizeMb, twoPass, overhead, absoluteOverheadBytes),
        });

    public TestOutputSettingsBuilder WithTargetBitrate(int bitrate, bool twoPass = false) =>
        Apply(s => s with
        {
            EncodingStrategy = EncodingStrategy.TargetBitrate,
            TargetBitrate = new TargetBitrateOptions(bitrate, twoPass),
        });

    public TestOutputSettingsBuilder WithAudio(AudioOptions audio) =>
        Apply(s => s with { AudioOptions = audio });

    public TestOutputSettingsBuilder WithAudioCopy() =>
        WithAudio(new AudioOptions(AudioMode.Copy));

    public TestOutputSettingsBuilder WithAudioReencode(
        AudioCodec codec, int bitrate, int? channels = null, int? sampleRate = null) =>
        WithAudio(new AudioOptions(AudioMode.Reencode, codec, bitrate, channels, sampleRate));

    public TestOutputSettingsBuilder WithAudioRemove() =>
        WithAudio(new AudioOptions(AudioMode.Remove));

    public TestOutputSettingsBuilder WithStreamHandling(StreamHandlingOptions handling) =>
        Apply(s => s with { StreamHandling = handling });

    public TestOutputSettingsBuilder WithRemoveSubtitles() =>
        Apply(s => s with { StreamHandling = s.StreamHandling with { RemoveSubtitles = true } });

    public TestOutputSettingsBuilder WithRemoveData() =>
        Apply(s => s with { StreamHandling = s.StreamHandling with { RemoveData = true } });

    public TestOutputSettingsBuilder WithH265() =>
        Apply(s => s with
        {
            VideoCodec = VideoCodec.H265,
            VideoEncoderOptions = new H265EncoderOptions(),
            TargetQuality = new H265QualityOptions(),
        });

    public TestOutputSettingsBuilder WithVp9(bool twoPass = false) =>
        Apply(s => s with
        {
            VideoCodec = VideoCodec.Vp9,
            Container = OutputContainer.Webm,
            VideoEncoderOptions = new VpxEncoderOptions(VideoCodec.Vp9),
            TargetQuality = new Vp9QualityOptions(TwoPass: twoPass),
        });

    public TestOutputSettingsBuilder WithVp8(bool twoPass = false) =>
        Apply(s => s with
        {
            VideoCodec = VideoCodec.Vp8,
            Container = OutputContainer.Webm,
            VideoEncoderOptions = new VpxEncoderOptions(VideoCodec.Vp8, CpuUsed: 4),
            TargetQuality = new Vp8QualityOptions(TwoPass: twoPass),
        });

    public TestOutputSettingsBuilder WithFrameRate(FrameRatePreset preset) =>
        Apply(s => s with { FrameRatePreset = preset });

    public TestOutputSettingsBuilder WithAv1() =>
        Apply(s => s with
        {
            VideoCodec = VideoCodec.Av1,
            Container = OutputContainer.Mkv,
            VideoEncoderOptions = new Av1EncoderOptions(),
            TargetQuality = new Av1QualityOptions(),
        });

    public TestOutputSettingsBuilder WithAv1MaxBitrateKbps(int kbps) =>
        Apply(s => s.TargetQuality is Av1QualityOptions av1
            ? s with { TargetQuality = av1 with { MaxBitrateKbps = kbps } }
            : s);

    public TestOutputSettingsBuilder WithKeyframeInterval(int frames) =>
        Apply(s => s with { KeyframeInterval = frames, KeyframeUnit = KeyframeUnit.Frames });

    public TestOutputSettingsBuilder WithKeyframeIntervalSeconds(int seconds) =>
        Apply(s => s with { KeyframeInterval = seconds, KeyframeUnit = KeyframeUnit.Seconds });

    public TestOutputSettingsBuilder WithHdrPreserve() =>
        Apply(s => s with { HdrMode = HdrMode.Preserve });

    public TestOutputSettingsBuilder WithToneMapToSdr(
        ToneMapAlgorithm algorithm = ToneMapAlgorithm.Hable,
        double desaturation = 2.0,
        int? peakNits = null) =>
        Apply(s => s with
        {
            HdrMode = HdrMode.TonemapToSdr,
            ToneMapSettings = new ToneMapSettings(algorithm, desaturation, peakNits),
        });

    private TestOutputSettingsBuilder Apply(Func<OutputSettings, OutputSettings> transform)
    {
        _settings = transform(_settings);
        return this;
    }
}
