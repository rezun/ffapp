using System.Text.Json.Serialization;

namespace FFApp.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<OutputContainer>))]
public enum OutputContainer
{
    [JsonStringEnumMemberName("mp4")] Mp4,
    [JsonStringEnumMemberName("mkv")] Mkv,
    [JsonStringEnumMemberName("webm")] Webm,
    [JsonStringEnumMemberName("ogg")] Ogg,
}

[JsonConverter(typeof(JsonStringEnumConverter<VideoCodec>))]
public enum VideoCodec
{
    [JsonStringEnumMemberName("h264")] H264,
    [JsonStringEnumMemberName("h265")] H265,
    [JsonStringEnumMemberName("vp8")] Vp8,
    [JsonStringEnumMemberName("vp9")] Vp9,
}

[JsonConverter(typeof(JsonStringEnumConverter<AudioCodec>))]
public enum AudioCodec
{
    [JsonStringEnumMemberName("mp3")] Mp3,
    [JsonStringEnumMemberName("aac")] Aac,
    [JsonStringEnumMemberName("vorbis")] Vorbis,
    [JsonStringEnumMemberName("opus")] Opus,
}

[JsonConverter(typeof(JsonStringEnumConverter<EncodingStrategy>))]
public enum EncodingStrategy
{
    [JsonStringEnumMemberName("target_file_size")] TargetFileSize,
    [JsonStringEnumMemberName("target_bitrate")] TargetBitrate,
    [JsonStringEnumMemberName("target_quality")] TargetQuality,
}

[JsonConverter(typeof(JsonStringEnumConverter<AudioMode>))]
public enum AudioMode
{
    [JsonStringEnumMemberName("copy")] Copy,
    [JsonStringEnumMemberName("reencode")] Reencode,
    [JsonStringEnumMemberName("remove")] Remove,
}

[JsonConverter(typeof(JsonStringEnumConverter<ResolutionPreset>))]
public enum ResolutionPreset
{
    [JsonStringEnumMemberName("original")] Original,
    [JsonStringEnumMemberName("720p")] P720,
    [JsonStringEnumMemberName("1080p")] P1080,
    [JsonStringEnumMemberName("1440p")] P1440,
    [JsonStringEnumMemberName("4k")] P4K,
}

[JsonConverter(typeof(JsonStringEnumConverter<FrameRatePreset>))]
public enum FrameRatePreset
{
    [JsonStringEnumMemberName("original")] Original,
    [JsonStringEnumMemberName("24fps")] Fps24,
    [JsonStringEnumMemberName("25fps")] Fps25,
    [JsonStringEnumMemberName("30fps")] Fps30,
    [JsonStringEnumMemberName("50fps")] Fps50,
    [JsonStringEnumMemberName("60fps")] Fps60,
}

[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("canceled")] Canceled,
}

[JsonConverter(typeof(JsonStringEnumConverter<ValidationSeverity>))]
public enum ValidationSeverity
{
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("warning")] Warning,
}
