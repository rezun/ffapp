using System.Collections.Frozen;
using System.Globalization;
using FFApp.Core;
using FFApp.Core.Models;
using FFRux.Core.Models;

namespace FFRux.Core;

/// <param name="Ffmpeg">The remux itself.</param>
/// <param name="PostProcess">mkvpropedit for Matroska outputs, when it is installed and the plan asks for it.</param>
public sealed record RemuxCommands(GeneratedCommand Ffmpeg, GeneratedCommand? PostProcess);

/// <summary>
/// Builds the FFmpeg command for a plan. Every source is opened once per distinct timeshift,
/// because <c>-itsoffset</c> and <c>-ss</c> apply to a whole input file.
/// </summary>
public sealed class RemuxCommandBuilder(ExecutablePathsConfig pathsConfig)
{
    // Statistics a Matroska writer stored for the source; they are wrong after a remux.
    private static readonly FrozenSet<string> StaleTagNames = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "BPS", "DURATION", "NUMBER_OF_FRAMES", "NUMBER_OF_BYTES", "_STATISTICS_WRITING_APP",
        "_STATISTICS_WRITING_DATE_UTC", "_STATISTICS_TAGS", "HANDLER_NAME", "VENDOR_ID");

    /// <param name="outputPath">Where FFmpeg writes; the job passes a temporary file next to the output.</param>
    public RemuxCommands Build(RemuxPlan plan, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var format = plan.FormatInfo;
        var report = CompatibilityChecker.Check(plan);
        var tracks = plan.EnabledTracks.OrderBy(track => track.Kind).ToList();
        var inputs = new InputList(plan);
        foreach (var track in tracks)
            inputs.For(track.Source.SourceNumber, track.Kind == TrackKind.Video ? 0 : track.Settings.TimeshiftMs);

        int? chapterInput = plan.ChapterSourceNumber is { } chapterSource && format.SupportsChapters
            ? inputs.For(chapterSource, 0)
            : null;
        var attachments = FontAttachments(plan, tracks, format)
            .Select(attachment => (Input: inputs.Any(attachment.SourceNumber), attachment.StreamIndex))
            .ToList();

        var args = new List<string> { "-nostdin", "-hide_banner", "-y" };
        args.AddRange(inputs.Arguments());

        var output = 0;
        var outputArgs = new List<string>();
        var needsUnofficial = false;
        foreach (var track in tracks)
        {
            var shift = track.Kind == TrackKind.Video ? 0 : track.Settings.TimeshiftMs;
            args.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"{inputs.For(track.Source.SourceNumber, shift)}:{track.Source.StreamIndex}")]);
            outputArgs.AddRange(TrackArguments(track, output, format, report.For(track.Key)));
            if (shift < 0)
                outputArgs.AddRange([Stream("-copypriorss", output), "0"]);
            if (track.Source.Video?.HasDolbyVision == true && format.IsMp4Family)
                needsUnofficial = true;
            output++;
        }
        foreach (var (input, streamIndex) in attachments)
            args.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"{input}:{streamIndex}")]);

        args.AddRange(["-c", "copy"]);
        args.AddRange(outputArgs);
        args.AddRange(["-map_chapters", chapterInput?.ToString(CultureInfo.InvariantCulture) ?? "-1"]);
        if (format.SupportsTitle)
            args.AddRange(["-metadata", $"title={plan.Title?.Trim() ?? ""}"]);
        if (needsUnofficial)
            args.AddRange(["-strict", "unofficial"]);
        args.AddRange(["-max_muxing_queue_size", "4096"]);
        args.AddRange(["-f", format.Muxer]);
        args.AddRange(format.MuxerArguments);
        if (plan.OptimizeForStreaming)
            args.AddRange(StreamingMuxerArguments.For(format.StreamingLayout, ExpectedDurationSeconds(plan)));
        args.Add(outputPath);

        var ffmpeg = GeneratedCommand.Create(pathsConfig.FfmpegPath, args);
        GeneratedCommand? postProcess = format.IsMatroska && plan.UpdateMatroskaStatistics && pathsConfig.MkvpropeditPath is { } mkvpropedit
            ? GeneratedCommand.Create(mkvpropedit,
                [outputPath, "--delete-track-statistics-tags", "--add-track-statistics-tags"], ProcessProtocol.Plain)
            : null;
        return new RemuxCommands(ffmpeg, postProcess);
    }

    /// <summary>The length of the output, for progress: the longest enabled track including its shift.</summary>
    public static double? ExpectedDurationSeconds(RemuxPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        double? longest = null;
        foreach (var track in plan.EnabledTracks)
        {
            var source = plan.Sources.FirstOrDefault(s => s.Number == track.Source.SourceNumber);
            if ((track.Source.Details.DurationSeconds ?? source?.MediaInfo.DurationSeconds) is not { } duration)
                continue;
            var shifted = duration + (track.Kind == TrackKind.Video ? 0 : track.Settings.TimeshiftMs / 1000.0);
            longest = Math.Max(longest ?? 0, shifted);
        }
        return longest;
    }

    private static IEnumerable<string> TrackArguments(RemuxTrack track, int output, OutputFormatInfo format, TrackCompatibility? status)
    {
        if (status?.ConvertToCodec is { } codec)
        {
            yield return Stream("-c", output);
            yield return codec;
        }

        var settings = track.Settings;
        var name = settings.Name?.Trim() ?? "";
        switch (format.TrackNames)
        {
            case TrackNameStorage.Title:
                yield return Stream("-metadata:s", output);
                yield return $"title={name}";
                break;
            case TrackNameStorage.HandlerName:
                yield return Stream("-metadata:s", output);
                yield return $"handler_name={name}";
                break;
        }

        if (format.SupportsLanguage)
        {
            var language = format.Format == OutputFormat.Mov
                ? QuickTimeLanguages.Map(settings.Language) ?? Languages.Undetermined.Code
                : settings.Language.Code;
            yield return Stream("-metadata:s", output);
            yield return $"language={language}";
        }

        if (format.IsMatroska)
        {
            foreach (var key in track.Source.Details.Tags.Keys.Where(IsStaleTag))
            {
                yield return Stream("-metadata:s", output);
                yield return $"{key}=";
            }
        }

        yield return Stream("-disposition", output);
        yield return TrackFlagNames.ToDisposition(settings.Flags & format.SupportedFlags);

        if (track.Source.CodecName == "hevc" && format.IsMp4Family)
        {
            yield return Stream("-tag", output);
            yield return "hvc1";
        }
    }

    private static bool IsStaleTag(string key)
    {
        var dash = key.IndexOf('-', StringComparison.Ordinal);
        return StaleTagNames.Contains(dash > 0 ? key[..dash] : key);
    }

    // Matroska needs the fonts an ASS/SSA track was styled with; WebM cannot hold attachments.
    private static IEnumerable<(int SourceNumber, int StreamIndex)> FontAttachments(
        RemuxPlan plan, IReadOnlyList<RemuxTrack> tracks, OutputFormatInfo format)
    {
        if (!format.IsMatroska || format.Format == OutputFormat.Webm)
            yield break;

        var sources = tracks
            .Where(track => track.Source.CodecName is "ass" or "ssa")
            .Select(track => track.Source.SourceNumber)
            .Distinct();
        foreach (var number in sources)
        {
            foreach (var attachment in plan.GetSource(number).Attachments.Where(IsFont))
                yield return (number, attachment.Index);
        }
    }

    private static bool IsFont(DataStreamInfo attachment) =>
        attachment.MimeType?.Contains("font", StringComparison.OrdinalIgnoreCase) == true
        || attachment.FileName is { } name
            && (name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase));

    private static string Stream(string option, int output) =>
        string.Create(CultureInfo.InvariantCulture, $"{option}:{output}");

    /// <summary>The FFmpeg inputs, one per source and timeshift, in the order they are first needed.</summary>
    private sealed class InputList(RemuxPlan plan)
    {
        private readonly List<(int SourceNumber, int ShiftMs)> _inputs = [];

        public int For(int sourceNumber, int shiftMs)
        {
            var index = _inputs.IndexOf((sourceNumber, shiftMs));
            if (index >= 0)
                return index;
            _inputs.Add((sourceNumber, shiftMs));
            return _inputs.Count - 1;
        }

        public int Any(int sourceNumber)
        {
            var index = _inputs.FindIndex(input => input.SourceNumber == sourceNumber);
            return index >= 0 ? index : For(sourceNumber, 0);
        }

        public IEnumerable<string> Arguments()
        {
            foreach (var (sourceNumber, shiftMs) in _inputs)
            {
                var source = plan.GetSource(sourceNumber);
                if (source.SubtitleCharset is { } charset)
                {
                    yield return "-sub_charenc";
                    yield return charset;
                }
                if (shiftMs > 0)
                {
                    yield return "-itsoffset";
                    yield return Seconds(shiftMs);
                }
                // A negative -itsoffset would move the whole file instead; seeking drops the start exactly.
                if (shiftMs < 0)
                {
                    yield return "-ss";
                    yield return Seconds(-shiftMs);
                }
                yield return "-i";
                yield return source.Path;
            }
        }

        private static string Seconds(int milliseconds) =>
            (milliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
