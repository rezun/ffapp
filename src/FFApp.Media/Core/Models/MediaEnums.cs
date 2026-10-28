using System.Text.Json.Serialization;

namespace FFApp.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("canceled")] Canceled,
}

public enum ProcessProtocol
{
    Ffmpeg,
    Plain,
}

[JsonConverter(typeof(JsonStringEnumConverter<HdrFormat>))]
public enum HdrFormat
{
    [JsonStringEnumMemberName("sdr")] Sdr,
    [JsonStringEnumMemberName("hdr10")] Hdr10,
    [JsonStringEnumMemberName("hlg")] Hlg,
}
