namespace FFApp.Core.Models;

// --- Video Encoder Options (discriminated union via abstract record) ---

public abstract record VideoEncoderOptions(VideoCodec Codec);

public sealed record H264EncoderOptions(
    string Preset = "medium",
    string Profile = "high") : VideoEncoderOptions(VideoCodec.H264);

public sealed record H265EncoderOptions(
    string Preset = "medium",
    string Profile = "main") : VideoEncoderOptions(VideoCodec.H265);

public sealed record VpxEncoderOptions(
    VideoCodec Codec,
    int CpuUsed = 2,
    int TileColumns = 0,
    int TileRows = 0) : VideoEncoderOptions(Codec);

public sealed record Av1EncoderOptions(
    int Preset = 6,
    int Tune = 0,
    int EnableOverlays = 1,
    int Scm = 2) : VideoEncoderOptions(VideoCodec.Av1);

// --- Quality Options (discriminated union via abstract record) ---

public abstract record QualityOptions(VideoCodec Codec);

public sealed record H264QualityOptions(
    int Crf = 23,
    string Preset = "medium",
    string Profile = "high") : QualityOptions(VideoCodec.H264);

public sealed record H265QualityOptions(
    int Crf = 28,
    string Preset = "medium",
    string Profile = "main") : QualityOptions(VideoCodec.H265);

public sealed record Vp9QualityOptions(
    int Quality = 32,
    bool TwoPass = false) : QualityOptions(VideoCodec.Vp9);

public sealed record Av1QualityOptions(
    int Crf = 30,
    int Preset = 6,
    int Tune = 0,
    int EnableOverlays = 1,
    int Scm = 2,
    int MaxBitrateKbps = 0) : QualityOptions(VideoCodec.Av1);

