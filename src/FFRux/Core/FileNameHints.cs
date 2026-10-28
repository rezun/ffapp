using System.Collections.Frozen;
using FFRux.Core.Models;

namespace FFRux.Core;

/// <summary>Language and flags read from suffixes such as <c>Movie.de.forced.srt</c>.</summary>
public sealed record FileNameHint(Language? Language, TrackFlags Flags)
{
    public static FileNameHint None { get; } = new(null, TrackFlags.None);

    public bool IsEmpty => Language is null && Flags == TrackFlags.None;
}

public static class FileNameHints
{
    private static readonly FrozenDictionary<string, TrackFlags> FlagWords = new Dictionary<string, TrackFlags>
    {
        ["forced"] = TrackFlags.Forced,
        ["sdh"] = TrackFlags.HearingImpaired,
        ["cc"] = TrackFlags.HearingImpaired,
        ["hi"] = TrackFlags.HearingImpaired,
        ["commentary"] = TrackFlags.Commentary,
        ["comment"] = TrackFlags.Commentary,
        ["default"] = TrackFlags.Default,
        ["ad"] = TrackFlags.VisualImpaired,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads known words from the end of the file name, stopping at the first unknown one, so
    /// "The.Movie.2020.de.srt" yields German and nothing from the title. "hi" means hearing
    /// impaired next to another language and Hindi on its own.
    /// </summary>
    public static FileNameHint Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = Path.GetFileNameWithoutExtension(path).Split('.');
        if (parts.Length < 2)
            return FileNameHint.None;

        var words = new List<string>();
        for (var i = parts.Length - 1; i >= 1; i--)
        {
            var word = parts[i];
            if (!FlagWords.ContainsKey(word) && !IsLanguageCode(word))
                break;
            words.Add(word);
        }

        Language? language = null;
        foreach (var word in words)
        {
            if (!FlagWords.ContainsKey(word) && Languages.Find(word) is { } found)
                language ??= found;
        }

        var flags = TrackFlags.None;
        foreach (var word in words)
        {
            if (!FlagWords.TryGetValue(word, out var flag))
                continue;
            if (word.Equals("hi", StringComparison.OrdinalIgnoreCase) && language is null)
                language = Languages.Find("hin");
            else
                flags |= flag;
        }

        return new FileNameHint(language, flags);
    }

    private static bool IsLanguageCode(string word) =>
        word.Length is 2 or 3 && Languages.Find(word) is not null;
}
