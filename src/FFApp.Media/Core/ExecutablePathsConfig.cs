using FFApp.Core.Models;

namespace FFApp.Core;

public sealed class ExecutablePathsConfig
{
    public string FfmpegPath { get; private set; } = "ffmpeg";
    public string FfprobePath { get; private set; } = "ffprobe";
    public string? MkvpropeditPath { get; private set; }

    public void Configure(ExecutablePaths paths)
    {
        FfmpegPath = paths.Ffmpeg ?? "ffmpeg";
        FfprobePath = paths.Ffprobe ?? "ffprobe";
        MkvpropeditPath = paths.Mkvpropedit;
    }
}
