using System.Collections.Frozen;
using FFRux.Core.Models;

namespace FFRux.Core;

/// <summary>
/// QuickTime files store languages as classic Macintosh language codes, and FFmpeg drops every
/// language it cannot map. This list was found by muxing each language into a .mov file with
/// FFmpeg 9 and reading it back.
/// </summary>
public static class QuickTimeLanguages
{
    private static readonly FrozenSet<string> Supported = FrozenSet.Create(StringComparer.Ordinal,
        "alb", "amh", "ara", "arm", "asm", "aym", "aze", "baq", "bel", "ben", "bul", "bur", "cat", "chi", "dan",
        "dut", "dzo", "eng", "epo", "est", "fin", "geo", "ger", "gre", "grn", "guj", "heb", "hin", "hun", "ice",
        "ind", "ita", "jav", "jpn", "kan", "kas", "kaz", "khm", "kin", "kir", "kor", "kur", "lao", "lat", "lav",
        "lit", "mac", "mal", "mar", "may", "mlg", "mlt", "mon", "nep", "nor", "nya", "ori", "orm", "per", "pol",
        "por", "pus", "que", "run", "rus", "san", "sin", "slv", "snd", "som", "spa", "swa", "tam", "tat", "tel",
        "tgk", "tgl", "tha", "tib", "tir", "tuk", "tur", "uig", "ukr", "urd", "uzb", "vie", "wel", "yid");

    // FFmpeg's table uses the terminology code for these languages.
    private static readonly FrozenSet<string> UsesTerminologyCode = FrozenSet.Create(StringComparer.Ordinal,
        "cze", "fre", "rum", "slo");

    /// <summary>The code to write into a QuickTime file, or null when QuickTime cannot store the language.</summary>
    public static string? Map(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (UsesTerminologyCode.Contains(language.Code))
            return language.TerminologyCode;
        return Supported.Contains(language.Code) ? language.Code : null;
    }
}
