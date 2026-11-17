using FFApp.Core.Models;

namespace FFApp.Core.Validation;

public static class FfmpegFeatureValidation
{
    public static IReadOnlyList<ValidationIssue> Validate(OutputSettings settings, SourceMediaInfo media, FfmpegCapabilities available) =>
        Validate(new JobDefinition(media.SourcePath, "", settings.Container,
            VideoMode: settings.VideoMode, VideoCodec: settings.VideoCodec, AudioOptions: settings.AudioOptions,
            HdrMode: settings.HdrMode, ToneMapSettings: settings.ToneMapSettings,
            ResolutionPreset: settings.ResolutionPreset, FrameRatePreset: settings.FrameRatePreset,
            ColorMetadata: settings.ColorMetadata), media, available);

    public static IReadOnlyList<ValidationIssue> Validate(JobDefinition job, SourceMediaInfo media, FfmpegCapabilities available)
    {
        var issues = new List<ValidationIssue>();
        if (job.AudioOptions.Mode == AudioMode.Reencode && job.AudioOptions.Codec is { } audioCodec && media.AudioStreams.Length > 0)
            RequireEncoder(DefaultCommandBuilder.AudioEncoderName(audioCodec));
        if (job.VideoMode == VideoMode.Reencode && media.PrimaryVideoStream is not null)
        {
            RequireEncoder(DefaultCommandBuilder.VideoEncoderName(job.VideoCodec));
            foreach (var name in DefaultCommandBuilder.VideoFilters(job, media)
                .Select(filter => filter.Split('=', 2)[0]).Distinct(StringComparer.Ordinal))
                RequireFilter(name);
        }
        return issues;

        void RequireEncoder(string name)
        {
            if (!available.Encoders.Contains(name))
                issues.Add(new(ValidationSeverity.Error, "ffmpeg_encoder_missing",
                    $"The configured FFmpeg build does not include the {name} encoder. Choose another codec or install a build with {name}."));
        }
        void RequireFilter(string name)
        {
            if (!available.Filters.Contains(name))
                issues.Add(new(ValidationSeverity.Error, "ffmpeg_filter_missing",
                    $"The configured FFmpeg build does not include the {name} filter required by these settings. Install a build with this filter."));
        }
    }
}
