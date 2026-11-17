using FFApp.Core.Services;
using FFApp.Core.Models;

namespace FFApp.Infrastructure;

public sealed class FfmpegCapabilityService : IFfmpegCapabilityService
{
    public bool IsLibplaceboAvailable { get; set; }
    public FfmpegCapabilities? Capabilities { get; set; }
}
