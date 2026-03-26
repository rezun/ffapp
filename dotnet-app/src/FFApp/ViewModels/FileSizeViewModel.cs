using CommunityToolkit.Mvvm.ComponentModel;

namespace FFApp.ViewModels;

public partial class FileSizeViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _targetSizeMb = 50;

    [ObservableProperty]
    private bool _twoPass;

    [ObservableProperty]
    private double _muxingOverheadRatio = 0.015;
}
