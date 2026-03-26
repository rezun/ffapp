using FFApp.Core.Models;

namespace FFApp.Core;

public sealed class ExecutablePathsConfig
{
    public string FfmpegPath { get; private set; } = "ffmpeg";
    public string FfprobePath { get; private set; } = "ffprobe";

    public void Configure(ExecutablePaths paths)
    {
        FfmpegPath = paths.Ffmpeg ?? "ffmpeg";
        FfprobePath = paths.Ffprobe ?? "ffprobe";
    }
}
