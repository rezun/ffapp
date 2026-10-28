namespace FFRux.Core;

/// <param name="Charset">The iconv name FFmpeg expects in <c>-sub_charenc</c>.</param>
public sealed record TextEncoding(string Charset, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Character sets offered for standalone text subtitle files that are not UTF-8.</summary>
public static class TextEncodings
{
    public static TextEncoding Utf8 { get; } = new("UTF-8", "UTF-8");

    /// <summary>The usual encoding of old Western European subtitle files, used when a file is not UTF-8.</summary>
    public static TextEncoding Fallback { get; } = new("CP1252", "Windows-1252 (Western European)");

    public static IReadOnlyList<TextEncoding> All { get; } =
    [
        Utf8,
        Fallback,
        new("ISO-8859-1", "ISO-8859-1 (Western European)"),
        new("ISO-8859-15", "ISO-8859-15 (Western European, euro)"),
        new("CP1250", "Windows-1250 (Central European)"),
        new("ISO-8859-2", "ISO-8859-2 (Central European)"),
        new("CP1251", "Windows-1251 (Cyrillic)"),
        new("KOI8-R", "KOI8-R (Russian)"),
        new("CP1253", "Windows-1253 (Greek)"),
        new("CP1254", "Windows-1254 (Turkish)"),
        new("CP1255", "Windows-1255 (Hebrew)"),
        new("CP1256", "Windows-1256 (Arabic)"),
        new("CP1257", "Windows-1257 (Baltic)"),
        new("CP874", "Windows-874 (Thai)"),
        new("SHIFT_JIS", "Shift JIS (Japanese)"),
        new("GB18030", "GB18030 (Chinese, simplified)"),
        new("BIG5", "Big5 (Chinese, traditional)"),
        new("EUC-KR", "EUC-KR (Korean)"),
    ];

    public static TextEncoding Find(string? charset) =>
        charset is null
            ? Utf8
            : All.FirstOrDefault(e => e.Charset.Equals(charset, StringComparison.OrdinalIgnoreCase)) ?? new TextEncoding(charset, charset);

    public static string Label(string charset) => Find(charset).Label;

    /// <summary>
    /// True when the bytes are valid UTF-8, with or without BOM, or start with a UTF-16 BOM that
    /// FFmpeg's subtitle readers convert on their own.
    /// </summary>
    public static bool IsUnicode(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || content.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return true;
        return System.Text.Unicode.Utf8.IsValid(content);
    }
}
