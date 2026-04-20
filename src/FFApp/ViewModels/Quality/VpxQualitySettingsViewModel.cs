using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;

namespace FFApp.ViewModels.Quality;

public sealed partial class VpxQualitySettingsViewModel : CodecQualitySettingsViewModel
{
    public override VideoCodec Codec => VideoCodec.Vp9;

    public IReadOnlyList<int> AvailableTileColumns { get; } = [0, 1, 2, 3, 4, 5, 6];
    public IReadOnlyList<int> AvailableTileRows { get; } = [0, 1, 2];

    [ObservableProperty]
    private int _quality = 32;

    [ObservableProperty]
    private bool _twoPass;

    [ObservableProperty]
    private int _cpuUsed = 2;

    [ObservableProperty]
    private int _tileColumns;

    [ObservableProperty]
    private int _tileRows;

    [ObservableProperty]
    private string _additionalOptions = "";

    public override void Reset()
    {
        Quality = 32;
        TwoPass = false;
        CpuUsed = 2;
        TileColumns = 0;
        TileRows = 0;
        AdditionalOptions = "";
    }

    public override QualityOptions BuildQualityOptions() =>
        new Vp9QualityOptions(Quality, TwoPass);

    public override VideoEncoderOptions BuildEncoderOptions() =>
        new VpxEncoderOptions(Codec, CpuUsed, TileColumns, TileRows, AdditionalOptions);

    public override void ApplyQuality(QualityOptions options)
    {
        if (options is not Vp9QualityOptions vp9)
            return;
        Quality = vp9.Quality;
        TwoPass = vp9.TwoPass;
    }

    public override void ApplyEncoder(VideoEncoderOptions options)
    {
        if (options is not VpxEncoderOptions vpx)
            return;
        CpuUsed = vpx.CpuUsed;
        TileColumns = vpx.TileColumns;
        TileRows = vpx.TileRows;
        AdditionalOptions = vpx.AdditionalOptions;
    }
}
