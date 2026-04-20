using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;

namespace FFApp.ViewModels.Quality;

public abstract partial class CodecQualitySettingsViewModel : ObservableObject
{
    public abstract VideoCodec Codec { get; }

    public abstract void Reset();

    public abstract QualityOptions BuildQualityOptions();

    public abstract VideoEncoderOptions BuildEncoderOptions();

    public abstract void ApplyQuality(QualityOptions options);

    public abstract void ApplyEncoder(VideoEncoderOptions options);
}
