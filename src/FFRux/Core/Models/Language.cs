using System.Collections.Frozen;

namespace FFRux.Core.Models;

/// <summary>
/// An ISO 639-2 language. <see cref="Code"/> is the bibliographic code that Matroska and most
/// players expect (<c>ger</c>); <see cref="TerminologyCode"/> is the alternative some files use (<c>deu</c>).
/// </summary>
public sealed record Language(string Code, string Name, string? TerminologyCode = null, string? TwoLetterCode = null)
{
    public string Label => string.Concat(Name, " (", Code, ")");

    public override string ToString() => Label;
}

/// <summary>The languages FFRux offers in the language picker, and lookups for codes found in files.</summary>
public static class Languages
{
    // Format: bibliographic code | terminology code | ISO 639-1 code | English name.
    private static readonly string[] Table =
    [
        "und|||Undetermined", "mul|||Multiple languages", "zxx|||No linguistic content",
        "aar||aa|Afar", "abk||ab|Abkhazian", "afr||af|Afrikaans", "aka||ak|Akan", "alb|sqi|sq|Albanian",
        "amh||am|Amharic", "ara||ar|Arabic", "arg||an|Aragonese", "arm|hye|hy|Armenian", "asm||as|Assamese",
        "ast|||Asturian", "ava||av|Avaric", "ave||ae|Avestan", "aym||ay|Aymara", "aze||az|Azerbaijani",
        "bak||ba|Bashkir", "bam||bm|Bambara", "baq|eus|eu|Basque", "bel||be|Belarusian", "ben||bn|Bengali",
        "bis||bi|Bislama", "bos||bs|Bosnian", "bre||br|Breton", "bul||bg|Bulgarian", "bur|mya|my|Burmese",
        "cat||ca|Catalan", "cha||ch|Chamorro", "che||ce|Chechen", "chi|zho|zh|Chinese", "chu||cu|Church Slavic",
        "chv||cv|Chuvash", "cor||kw|Cornish", "cos||co|Corsican", "cre||cr|Cree", "cze|ces|cs|Czech",
        "dan||da|Danish", "div||dv|Divehi", "dut|nld|nl|Dutch", "dzo||dz|Dzongkha", "eng||en|English",
        "epo||eo|Esperanto", "est||et|Estonian", "ewe||ee|Ewe", "fao||fo|Faroese", "fij||fj|Fijian",
        "fil|||Filipino", "fin||fi|Finnish", "fre|fra|fr|French", "fry||fy|Western Frisian", "ful||ff|Fulah",
        "geo|kat|ka|Georgian", "ger|deu|de|German", "gla||gd|Scottish Gaelic", "gle||ga|Irish",
        "glg||gl|Galician", "glv||gv|Manx", "gre|ell|el|Greek", "grn||gn|Guarani", "gsw|||Swiss German",
        "guj||gu|Gujarati", "hat||ht|Haitian", "hau||ha|Hausa", "haw|||Hawaiian", "heb||he|Hebrew",
        "her||hz|Herero", "hin||hi|Hindi", "hmo||ho|Hiri Motu", "hrv||hr|Croatian", "hun||hu|Hungarian",
        "ibo||ig|Igbo", "ice|isl|is|Icelandic", "ido||io|Ido", "iii||ii|Sichuan Yi", "iku||iu|Inuktitut",
        "ile||ie|Interlingue", "ina||ia|Interlingua", "ind||id|Indonesian", "ipk||ik|Inupiaq",
        "ita||it|Italian", "jav||jv|Javanese", "jpn||ja|Japanese", "kal||kl|Kalaallisut", "kan||kn|Kannada",
        "kas||ks|Kashmiri", "kau||kr|Kanuri", "kaz||kk|Kazakh", "khm||km|Khmer", "kik||ki|Kikuyu",
        "kin||rw|Kinyarwanda", "kir||ky|Kyrgyz", "kom||kv|Komi", "kon||kg|Kongo", "kor||ko|Korean",
        "kua||kj|Kuanyama", "kur||ku|Kurdish", "lao||lo|Lao", "lat||la|Latin", "lav||lv|Latvian",
        "lim||li|Limburgish", "lin||ln|Lingala", "lit||lt|Lithuanian", "ltz||lb|Luxembourgish",
        "lub||lu|Luba-Katanga", "lug||lg|Ganda", "mac|mkd|mk|Macedonian", "mah||mh|Marshallese",
        "mal||ml|Malayalam", "mao|mri|mi|Maori", "mar||mr|Marathi", "may|msa|ms|Malay", "mlg||mg|Malagasy",
        "mlt||mt|Maltese", "mon||mn|Mongolian", "nau||na|Nauru", "nav||nv|Navajo", "nbl||nr|South Ndebele",
        "nde||nd|North Ndebele", "ndo||ng|Ndonga", "nds|||Low German", "nep||ne|Nepali",
        "nno||nn|Norwegian Nynorsk", "nob||nb|Norwegian Bokmål", "nor||no|Norwegian", "nya||ny|Chichewa",
        "oci||oc|Occitan", "oji||oj|Ojibwa", "ori||or|Oriya", "orm||om|Oromo", "oss||os|Ossetian",
        "pan||pa|Punjabi", "per|fas|fa|Persian", "pli||pi|Pali", "pol||pl|Polish", "por||pt|Portuguese",
        "pus||ps|Pashto", "que||qu|Quechua", "roh||rm|Romansh", "rum|ron|ro|Romanian", "run||rn|Rundi",
        "rus||ru|Russian", "sag||sg|Sango", "san||sa|Sanskrit", "sin||si|Sinhala", "slo|slk|sk|Slovak",
        "slv||sl|Slovenian", "sme||se|Northern Sami", "smo||sm|Samoan", "sna||sn|Shona", "snd||sd|Sindhi",
        "som||so|Somali", "sot||st|Southern Sotho", "spa||es|Spanish", "srd||sc|Sardinian", "srp||sr|Serbian",
        "ssw||ss|Swati", "sun||su|Sundanese", "swa||sw|Swahili", "swe||sv|Swedish", "tah||ty|Tahitian",
        "tam||ta|Tamil", "tat||tt|Tatar", "tel||te|Telugu", "tgk||tg|Tajik", "tgl||tl|Tagalog", "tha||th|Thai",
        "tib|bod|bo|Tibetan", "tir||ti|Tigrinya", "ton||to|Tonga", "tsn||tn|Tswana", "tso||ts|Tsonga",
        "tuk||tk|Turkmen", "tur||tr|Turkish", "twi||tw|Twi", "uig||ug|Uyghur", "ukr||uk|Ukrainian",
        "urd||ur|Urdu", "uzb||uz|Uzbek", "ven||ve|Venda", "vie||vi|Vietnamese", "vol||vo|Volapük",
        "wel|cym|cy|Welsh", "wln||wa|Walloon", "wol||wo|Wolof", "xho||xh|Xhosa", "yid||yi|Yiddish",
        "yor||yo|Yoruba", "zha||za|Zhuang", "zul||zu|Zulu",
    ];

    public static IReadOnlyList<Language> All { get; } = Parse();

    public static Language Undetermined { get; } = All[0];

    private static readonly FrozenDictionary<string, Language> ByCode = BuildLookup();

    /// <summary>Finds a language by its ISO 639-2 (B or T) or ISO 639-1 code.</summary>
    public static Language? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : ByCode.GetValueOrDefault(code.Trim());

    /// <summary>
    /// The language for a code from a file. Unknown codes are kept as they are, so a remux never
    /// rewrites a language FFRux does not know.
    /// </summary>
    public static Language FromFileValue(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Undetermined;
        return Find(code) ?? new Language(code.Trim(), code.Trim());
    }

    private static Language[] Parse()
    {
        var languages = new Language[Table.Length];
        for (var i = 0; i < Table.Length; i++)
        {
            var parts = Table[i].Split('|');
            languages[i] = new Language(
                Code: parts[0],
                Name: parts[3],
                TerminologyCode: parts[1].Length > 0 ? parts[1] : null,
                TwoLetterCode: parts[2].Length > 0 ? parts[2] : null);
        }
        return languages;
    }

    private static FrozenDictionary<string, Language> BuildLookup()
    {
        var lookup = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in All)
        {
            lookup[language.Code] = language;
            if (language.TerminologyCode is { } terminology)
                lookup[terminology] = language;
            if (language.TwoLetterCode is { } twoLetter)
                lookup[twoLetter] = language;
        }
        return lookup.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
