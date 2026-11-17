using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IFfmpegCapabilityService
{
    bool IsLibplaceboAvailable { get; set; }
    FfmpegCapabilities? Capabilities { get; set; }
}
