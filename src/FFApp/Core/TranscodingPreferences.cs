namespace FFApp.Core;

/// <summary>Application-wide preferences read when starting each FFmpeg process.</summary>
public sealed class TranscodingPreferences
{
    public bool UseLowPriority { get; set; }
}
