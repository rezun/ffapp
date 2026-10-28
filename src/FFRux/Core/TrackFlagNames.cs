using FFRux.Core.Models;

namespace FFRux.Core;

public static class TrackFlagNames
{
    public static TrackFlags All { get; } = TrackFlags.Default | TrackFlags.Forced | TrackFlags.HearingImpaired
        | TrackFlags.VisualImpaired | TrackFlags.Commentary | TrackFlags.Original;

    public static IReadOnlyList<TrackFlags> Ordered { get; } =
    [
        TrackFlags.Default, TrackFlags.Forced, TrackFlags.HearingImpaired,
        TrackFlags.VisualImpaired, TrackFlags.Commentary, TrackFlags.Original,
    ];

    public static string Label(TrackFlags flag) => flag switch
    {
        TrackFlags.Default => "Default",
        TrackFlags.Forced => "Forced",
        TrackFlags.HearingImpaired => "Hearing impaired",
        TrackFlags.VisualImpaired => "Visual impaired",
        TrackFlags.Commentary => "Commentary",
        TrackFlags.Original => "Original",
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "Expected a single flag."),
    };

    /// <summary>Flags as readable text in a fixed order, for example "forced, hearing impaired".</summary>
    public static string Describe(TrackFlags flags) =>
        string.Join(", ", Ordered.Where(flag => flags.HasFlag(flag)).Select(flag => Label(flag).ToLowerInvariant()));

    /// <summary>The value of FFmpeg's <c>-disposition</c> option; "0" clears every flag.</summary>
    public static string ToDisposition(TrackFlags flags)
    {
        var names = Ordered.Where(flag => flags.HasFlag(flag)).Select(flag => flag switch
        {
            TrackFlags.Default => "default",
            TrackFlags.Forced => "forced",
            TrackFlags.HearingImpaired => "hearing_impaired",
            TrackFlags.VisualImpaired => "visual_impaired",
            TrackFlags.Commentary => "comment",
            TrackFlags.Original => "original",
            _ => throw new ArgumentOutOfRangeException(nameof(flags), flag, null),
        }).ToList();
        return names.Count == 0 ? "0" : string.Join('+', names);
    }
}
