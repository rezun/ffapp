using Avalonia.Controls;
using FFRux.Core.Models;

namespace FFRux.Views;

public partial class PropertiesView : UserControl
{
    public PropertiesView()
    {
        InitializeComponent();
        LanguageBox.ItemFilter = MatchesLanguage;
    }

    // Matches the English name anywhere and the ISO codes from their start, so "ger", "deu", "de" and "germ" all find German.
    private static bool MatchesLanguage(string? search, object? item)
    {
        if (item is not Language language || string.IsNullOrWhiteSpace(search))
            return false;
        var text = search.Trim();
        return language.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || language.Label.Equals(text, StringComparison.OrdinalIgnoreCase)
            || language.Code.StartsWith(text, StringComparison.OrdinalIgnoreCase)
            || language.TerminologyCode?.StartsWith(text, StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(language.TwoLetterCode, text, StringComparison.OrdinalIgnoreCase);
    }
}
