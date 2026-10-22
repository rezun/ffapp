using CommunityToolkit.Mvvm.ComponentModel;

namespace FFApp.ViewModels;

public partial class StreamHandlingViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _removeSubtitles;

    [ObservableProperty]
    private bool _removeData;

    [ObservableProperty]
    private bool _removeImages;
}
