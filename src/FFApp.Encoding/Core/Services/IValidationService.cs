using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IValidationService
{
    IReadOnlyList<VideoCodec> AllowedVideoCodecs(OutputContainer container);
    IReadOnlyList<AudioCodec> AllowedAudioCodecs(OutputContainer container);
    Resolution? ResolveOutputResolution(OutputSettings settings, SourceMediaInfo mediaInfo);
    ValidationResult Validate(OutputSettings settings, SourceMediaInfo mediaInfo, double? trimmedDurationSeconds = null);

    /// <summary>
    /// Validates against a specific FFmpeg build, such as the server agent's, instead of the local
    /// one. Null skips the encoder and filter checks.
    /// </summary>
    ValidationResult Validate(OutputSettings settings, SourceMediaInfo mediaInfo, double? trimmedDurationSeconds, FfmpegCapabilities? capabilities);
}
