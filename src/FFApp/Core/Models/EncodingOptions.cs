namespace FFApp.Core.Models;

// --- Video Encoder Options (discriminated union via abstract record) ---

public abstract record VideoEncoderOptions(VideoCodec Codec);

public sealed record H26xEncoderOptions(
    VideoCodec Codec,
    string Preset = "medium",
    string Profile = "main",
    string AdditionalOptions = "") : VideoEncoderOptions(Codec)
{
    public static H26xEncoderOptions ForCodec(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => new H26xEncoderOptions(VideoCodec.H264, Profile: "high"),
        VideoCodec.H265 => new H26xEncoderOptions(VideoCodec.H265, Profile: "main"),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "H26x encoder options require H.264 or H.265."),
    };
}

public sealed record VpxEncoderOptions(
    VideoCodec Codec,
    int CpuUsed = 2,
    int TileColumns = 0,
    int TileRows = 0,
    string AdditionalOptions = "") : VideoEncoderOptions(Codec);

public sealed record Av1EncoderOptions(
    int Preset = 6,
    int Tune = 0,
    int EnableOverlays = 1,
    int Scm = 2,
    int EnableVarianceBoost = 0,
    string AdditionalOptions = "",
    int FilmGrain = 0,
    int FilmGrainDenoise = 0,
    int AdaptiveFilmGrain = 1,
    int VarianceBoostStrength = 2,
    int VarianceOctile = 5) : VideoEncoderOptions(VideoCodec.Av1);

// --- Quality Options (discriminated union via abstract record) ---

public abstract record QualityOptions(VideoCodec Codec);

public sealed record H26xQualityOptions(
    VideoCodec Codec,
    int Crf = 23,
    string Preset = "medium",
    string Profile = "main") : QualityOptions(Codec)
{
    public static H26xQualityOptions ForCodec(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => new H26xQualityOptions(VideoCodec.H264, Crf: 23, Profile: "high"),
        VideoCodec.H265 => new H26xQualityOptions(VideoCodec.H265, Crf: 28, Profile: "main"),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "H26x quality options require H.264 or H.265."),
    };
}

public sealed record Vp9QualityOptions(
    int Quality = 32,
    bool TwoPass = false) : QualityOptions(VideoCodec.Vp9);

public sealed record Av1QualityOptions(
    int Crf = 30,
    int Preset = 6,
    int Tune = 0,
    int EnableOverlays = 1,
    int Scm = 2,
    int MaxBitrateKbps = 0,
    int EnableVarianceBoost = 0,
    int FilmGrain = 0,
    int FilmGrainDenoise = 0,
    int AdaptiveFilmGrain = 1,
    int VarianceBoostStrength = 2,
    int VarianceOctile = 5) : QualityOptions(VideoCodec.Av1);
