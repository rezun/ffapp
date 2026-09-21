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

    public TestOutputSettingsBuilder WithVideoMode(VideoMode mode) =>
        Apply(s => s with { VideoMode = mode });

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

    public TestOutputSettingsBuilder WithMatroskaStatistics(bool enabled = true) =>
        Apply(s => s with
        {
            PostProcessing = s.PostProcessing with
            {
                RecalculateMatroskaStatistics = enabled,
            },
        });

    public TestOutputSettingsBuilder WithH265() =>
        Apply(s => s with
        {
            VideoCodec = VideoCodec.H265,
            VideoEncoderOptions = H26xEncoderOptions.ForCodec(VideoCodec.H265),
            TargetQuality = H26xQualityOptions.ForCodec(VideoCodec.H265),
        });

    public TestOutputSettingsBuilder WithVp9(bool twoPass = false) =>
        Apply(s => s with
        {
            VideoCodec = VideoCodec.Vp9,
            Container = OutputContainer.Webm,
            VideoEncoderOptions = new VpxEncoderOptions(VideoCodec.Vp9),
            TargetQuality = new Vp9QualityOptions(TwoPass: twoPass),
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

    public TestOutputSettingsBuilder WithAdditionalOptions(string options) =>
        Apply(s => s with
        {
            VideoEncoderOptions = s.VideoEncoderOptions switch
            {
                H26xEncoderOptions h26x => h26x with { AdditionalOptions = options },
                VpxEncoderOptions vpx => vpx with { AdditionalOptions = options },
                Av1EncoderOptions av1 => av1 with { AdditionalOptions = options },
                null => H26xEncoderOptions.ForCodec(VideoCodec.H264) with { AdditionalOptions = options },
                _ => s.VideoEncoderOptions,
            },
        });

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
            ToneMapSettings = new ToneMapSettings(Algorithm: algorithm, Desaturation: desaturation, PeakNits: peakNits),
        });

    public TestOutputSettingsBuilder WithLibplaceboTonemapToSdr(
        LibplaceboToneMapAlgorithm algorithm = LibplaceboToneMapAlgorithm.Bt2446a,
        GamutMode gamutMode = GamutMode.Perceptual,
        double saturation = 1.0,
        double contrastRecovery = 0.3,
        bool peakDetect = true,
        double? tonemappingParam = null) =>
        Apply(s => s with
        {
            HdrMode = HdrMode.TonemapToSdr,
            ToneMapSettings = new ToneMapSettings(
                Engine: ToneMapEngine.Libplacebo,
                LibplaceboAlgorithm: algorithm,
                GamutMode: gamutMode,
                Saturation: saturation,
                ContrastRecovery: contrastRecovery,
                PeakDetect: peakDetect,
                TonemappingParam: tonemappingParam),
        });

    private TestOutputSettingsBuilder Apply(Func<OutputSettings, OutputSettings> transform)
    {
        _settings = transform(_settings);
        return this;
    }
}
