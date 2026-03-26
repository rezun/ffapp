using CommunityToolkit.Mvvm.ComponentModel;

namespace FFApp.ViewModels;

public partial class BitrateViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _videoBitrateKbps = 3000;

    [ObservableProperty]
    private bool _twoPass;
}
