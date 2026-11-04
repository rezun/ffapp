using System.Collections.Frozen;
using System.Globalization;
using FFRux.Core.Models;

namespace FFRux.Core;

/// <summary>
/// Decides for every enabled track whether the chosen format can hold it, and checks the job as a
/// whole. Pure and cheap, so the UI runs it on every change and once per format for the dropdown.
/// </summary>
public static class CompatibilityChecker
{
    // Text subtitle codecs each format stores without conversion.
    private static readonly FrozenDictionary<OutputFormat, FrozenSet<string>> NativeTextSubtitles =
        new Dictionary<OutputFormat, FrozenSet<string>>
        {
            [OutputFormat.Mkv] = FrozenSet.Create(StringComparer.Ordinal, "subrip", "ass", "ssa", "webvtt"),
            [OutputFormat.Mka] = FrozenSet.Create(StringComparer.Ordinal, "subrip", "ass", "ssa", "webvtt"),
            [OutputFormat.Webm] = FrozenSet.Create(StringComparer.Ordinal, "webvtt"),
            [OutputFormat.Mp4] = FrozenSet.Create(StringComparer.Ordinal, "mov_text"),
            [OutputFormat.Mov] = FrozenSet.Create(StringComparer.Ordinal, "mov_text"),
        }.ToFrozenDictionary();

    public static CompatibilityReport Check(RemuxPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var format = plan.FormatInfo;
        var statuses = new Dictionary<TrackKey, TrackCompatibility>();
        var issues = new List<CompatibilityIssue>();
        var enabled = plan.EnabledTracks;

        foreach (var track in enabled)
        {
            var source = plan.Sources.FirstOrDefault(s => s.Number == track.Source.SourceNumber);
            var status = CheckTrack(track, source, format);
            statuses[track.Key] = status;

            var label = TrackLabel(plan, track);
            if (status.Level == CompatibilityLevel.Incompatible)
                issues.Add(new CompatibilityIssue(IssueSeverity.Error, $"{label}: {status.Detail} {Suggestion(track, format)}", track.Key));
            else if (status.Level == CompatibilityLevel.Warning)
                issues.Add(new CompatibilityIssue(IssueSeverity.Warning, $"{label}: {status.Detail}", track.Key));
        }

        issues.AddRange(CheckJob(plan, enabled, format));
        issues.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return new CompatibilityReport(plan.Format, statuses, issues);
    }

    /// <summary>Problems with the output path, kept apart so the format dropdown counts only format problems.</summary>
    public static IReadOnlyList<CompatibilityIssue> CheckOutputPath(RemuxPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.OutputPath))
            return [new CompatibilityIssue(IssueSeverity.Error, "Choose an output file.")];

        var output = Path.GetFullPath(plan.OutputPath);
        if (plan.Sources.Any(source => PathsEqual(Path.GetFullPath(source.Path), output)))
            return [new CompatibilityIssue(IssueSeverity.Error, "The output file is one of the source files. Choose another name.")];

        return [];
    }

    internal static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>A readable reference such as "Audio 3 (TrueHD, eng)". The number is the row in its group.</summary>
    public static string TrackLabel(RemuxPlan plan, RemuxTrack track)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(track);
        var position = 1;
        foreach (var candidate in plan.Tracks)
        {
            if (candidate.Key == track.Key)
                break;
            if (candidate.Kind == track.Kind)
                position++;
        }

        var codec = Codecs.Label(track.Source.CodecName, track.Source.Details.Profile);
        var language = track.Settings.Language.Code;
        var detail = language == Languages.Undetermined.Code ? codec : $"{codec}, {language}";
        return string.Create(CultureInfo.InvariantCulture, $"{KindLabel(track.Kind)} {position} ({detail})");
    }

    public static string KindLabel(TrackKind kind) => kind switch
    {
        TrackKind.Video => "Video",
        TrackKind.Audio => "Audio",
        TrackKind.Subtitle => "Subtitle",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static TrackCompatibility CheckTrack(RemuxTrack track, RemuxSource? source, OutputFormatInfo format)
    {
        var codec = track.Source.CodecName;
        var codecLabel = Codecs.Label(codec, track.Source.Details.Profile);

        if (track.Kind == TrackKind.Video && format.IsAudioOnly)
            return Incompatible($"{format.ShortName} holds no video", $"{format.Name} is an audio format and can't hold video.");
        if (track.Kind == TrackKind.Subtitle && !format.SupportsSubtitles)
            return Incompatible($"{format.ShortName} holds no subtitles", $"{format.Name} can't hold subtitles.");

        var result = track.Source.IsTextSubtitle
            ? CheckTextSubtitle(track, source, format, codecLabel)
            : CheckCopy(track, format, codecLabel);
        if (result.Level == CompatibilityLevel.Incompatible)
            return result;

        return AddNotes(result, track, format);
    }

    private static TrackCompatibility CheckCopy(RemuxTrack track, OutputFormatInfo format, string codecLabel)
    {
        var codec = track.Source.CodecName;
        var rule = CodecRules.Find(track.Kind, codec, format.Format);
        if (rule is null)
        {
            if (!CodecRules.IsKnown(track.Kind, codec) && format.Format is OutputFormat.Mkv or OutputFormat.Mka)
            {
                return new TrackCompatibility(CompatibilityLevel.Warning, "Unchecked codec",
                    $"FFRux has no rule for {codecLabel}. FFmpeg decides at the start of the remux whether {format.Name} accepts it.");
            }

            return track.Source.IsBitmapSubtitle
                ? Incompatible("Bitmap subtitles need MKV",
                    $"{codecLabel} is a bitmap subtitle. {format.Name} can't hold it, and FFRux can't convert bitmap subtitles.")
                : Incompatible($"Not supported in {format.ShortName}", $"{format.Name} can't hold {codecLabel}.");
        }

        return rule.Kind switch
        {
            CodecRuleKind.Copy => new TrackCompatibility(CompatibilityLevel.Copy, "Copy", $"{format.Name} stores {codecLabel} as is."),
            CodecRuleKind.Warning => new TrackCompatibility(CompatibilityLevel.Warning, "Limited player support",
                $"{format.Name} can hold {codecLabel}. {rule.Note}"),
            _ => throw new InvalidOperationException($"Unexpected rule {rule.Kind} for {codec}."),
        };
    }

    private static TrackCompatibility CheckTextSubtitle(RemuxTrack track, RemuxSource? source, OutputFormatInfo format, string codecLabel)
    {
        var codec = track.Source.CodecName;
        if (format.TextSubtitleCodec is not { } target || !NativeTextSubtitles.ContainsKey(format.Format))
            return Incompatible($"Not supported in {format.ShortName}", $"{format.Name} can't hold text subtitles.");

        var charset = source?.SubtitleCharset;
        if (NativeTextSubtitles[format.Format].Contains(codec))
        {
            if (charset is null)
                return new TrackCompatibility(CompatibilityLevel.Copy, "Copy", $"{format.Name} stores {codecLabel} as is.");

            return new TrackCompatibility(CompatibilityLevel.Convert, "Convert to UTF-8",
                $"The file uses {TextEncodings.Label(charset)}. {format.Name} stores text subtitles as UTF-8, so FFRux converts the text. Timing and formatting stay the same.",
                ConvertToCodec: codec);
        }

        var targetLabel = Codecs.Label(target);
        var detail = Codecs.StylesAreLost(codec)
            ? $"{format.Name} can't hold {codecLabel}, so FFRux converts it to {targetLabel}. Styling such as fonts, positions and colors is lost."
            : $"{format.Name} can't hold {codecLabel}, so FFRux converts it to {targetLabel}. Text and timing stay the same.";
        return new TrackCompatibility(CompatibilityLevel.Convert, $"Convert to {targetLabel}", detail, ConvertToCodec: target);
    }

    private static TrackCompatibility AddNotes(TrackCompatibility result, RemuxTrack track, OutputFormatInfo format)
    {
        var notes = new List<string>();
        var level = result.Level;
        var statusText = result.StatusText;

        void Warn(string shortText, string note)
        {
            notes.Add(note);
            if (level < CompatibilityLevel.Warning)
            {
                level = CompatibilityLevel.Warning;
                statusText = shortText;
            }
        }

        if (track.Kind == TrackKind.Audio && track.Settings.TimeshiftMs > 0 && format.IsMp4Family)
            Warn("Shift stored as edit list", $"{format.Name} stores the shift as an edit list, which some players ignore.");

        if (track.Source.Video?.HasDolbyVision == true)
        {
            if (format.Format is OutputFormat.Mkv or OutputFormat.Mp4 or OutputFormat.Mov)
                notes.Add("The Dolby Vision metadata is copied too.");
            else
                Warn("Dolby Vision unchecked", $"FFRux has not checked Dolby Vision in {format.Name}. Players may only show the base layer.");
        }

        var language = track.Settings.Language;
        if (format.Format == OutputFormat.Mov && language != Languages.Undetermined && QuickTimeLanguages.Map(language) is null)
            Warn("Language not stored", $"QuickTime can't store the language {language.Name}, so it is written as undetermined.");

        var dropped = track.Settings.Flags & ~format.SupportedFlags;
        if (dropped != TrackFlags.None)
            notes.Add($"{format.Name} can't store these flags, so they are left out: {TrackFlagNames.Describe(dropped)}.");

        if (notes.Count == 0)
            return result;

        var detail = level == result.Level
            ? string.Join(' ', [result.Detail, .. notes])
            : string.Join(' ', [.. notes, result.Detail]);
        return result with { Level = level, StatusText = statusText, Detail = detail };
    }

    private static IEnumerable<CompatibilityIssue> CheckJob(RemuxPlan plan, IReadOnlyList<RemuxTrack> enabled, OutputFormatInfo format)
    {
        if (enabled.Count == 0)
        {
            yield return new CompatibilityIssue(IssueSeverity.Error, "Enable at least one track.");
            yield break;
        }

        var audioCount = enabled.Count(track => track.Kind == TrackKind.Audio);
        if (format.IsSingleAudioTrack && audioCount > 1)
        {
            yield return new CompatibilityIssue(IssueSeverity.Error, string.Create(CultureInfo.InvariantCulture,
                $"{format.Name} holds exactly one audio track, but {audioCount} are enabled."));
        }
        else if (format.IsAudioOnly && audioCount == 0)
        {
            yield return new CompatibilityIssue(IssueSeverity.Error, $"{format.Name} needs an audio track. Enable one.");
        }
        else if (format.Format is OutputFormat.Ogg or OutputFormat.Opus && audioCount > 1)
        {
            yield return new CompatibilityIssue(IssueSeverity.Warning,
                $"Most players only play the first audio track of an {format.Name} file.");
        }

        if (format.SupportedFlags.HasFlag(TrackFlags.Default))
        {
            foreach (var group in enabled.Where(track => track.Settings.Flags.HasFlag(TrackFlags.Default)).GroupBy(track => track.Kind))
            {
                var count = group.Count();
                if (count > 1)
                {
                    yield return new CompatibilityIssue(IssueSeverity.Warning, string.Create(CultureInfo.InvariantCulture,
                        $"{count} {KindLabel(group.Key).ToLowerInvariant()} tracks are marked as default. Players pick only one of them."));
                }
            }
        }

        if (plan.ChapterSourceNumber is not null && !format.SupportsChapters)
            yield return new CompatibilityIssue(IssueSeverity.Warning, $"{format.Name} can't store chapters, so they are left out.");
    }

    private static string Suggestion(RemuxTrack track, OutputFormatInfo format)
    {
        if (track.Kind == TrackKind.Video && format.IsAudioOnly)
            return "Disable the track or choose a video format.";
        var alternative = format.IsAudioOnly && track.Kind == TrackKind.Audio ? "MKA" : "MKV";
        return format.Format is OutputFormat.Mkv or OutputFormat.Mka
            ? "Disable the track."
            : $"Disable the track or choose {alternative}.";
    }

    private static TrackCompatibility Incompatible(string statusText, string detail) =>
        new(CompatibilityLevel.Incompatible, statusText, detail);
}
