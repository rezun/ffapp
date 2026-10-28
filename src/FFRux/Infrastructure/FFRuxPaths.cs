using FFApp.Infrastructure;

namespace FFRux.Infrastructure;

/// <summary>FFRux's identity: its own data folder and its own single-instance pipe, separate from FFApp.</summary>
public static class FFRuxPaths
{
    public static readonly AppIdentity Identity = new("FFRux", "ffrux");

    public static string AppDataDirectory() => AppPaths.AppDataDirectory(Identity);
}
