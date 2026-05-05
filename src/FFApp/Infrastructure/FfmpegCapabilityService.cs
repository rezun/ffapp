using FFApp.Core.Services;

namespace FFApp.Infrastructure;

public sealed class FfmpegCapabilityService : IFfmpegCapabilityService
{
    public bool IsLibplaceboAvailable { get; set; }
}
