using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IValidationService
{
    IReadOnlyList<VideoCodec> AllowedVideoCodecs(OutputContainer container);
    IReadOnlyList<AudioCodec> AllowedAudioCodecs(OutputContainer container);
    Resolution? ResolveOutputResolution(OutputSettings settings, SourceMediaInfo mediaInfo);
    ValidationResult Validate(OutputSettings settings, SourceMediaInfo mediaInfo);
}
