using Avalonia.Data.Converters;
using FFApp.Core.Models;
using FFApp.Helpers;

namespace FFApp.Converters;

public static class EnumLabelConverters
{
    public static readonly FuncValueConverter<OutputContainer, string> Container =
        new(v => FormatHelpers.ContainerLabel(v));

    public static readonly FuncValueConverter<VideoCodec, string> VideoCodec =
        new(v => FormatHelpers.VideoCodecLabel(v));

    public static readonly FuncValueConverter<AudioCodec, string> AudioCodec =
        new(v => FormatHelpers.AudioCodecLabel(v));

    public static readonly FuncValueConverter<ResolutionPreset, string> Resolution =
        new(v => FormatHelpers.ResolutionPresetLabel(v));

    public static readonly FuncValueConverter<FrameRatePreset, string> FrameRate =
        new(v => FormatHelpers.FrameRatePresetLabel(v));

    public static readonly FuncValueConverter<AudioCodec?, string> AudioCodecNullable =
        new(v => v is { } c ? FormatHelpers.AudioCodecLabel(c) : "");

    public static readonly FuncValueConverter<VideoMode, string> VideoMode =
        new(v => FormatHelpers.VideoModeLabel(v));

    public static readonly FuncValueConverter<int, string> AudioBitrateOption =
        new(v => v == -1 ? "Custom" : $"{v} kbps");

    public static readonly FuncValueConverter<int, string> AudioSampleRateOption =
        new(v => v switch
        {
            0 => "Auto",
            _ => $"{v / 1000.0:G} kHz",
        });

    public static readonly FuncValueConverter<int, string> AudioChannelOption =
        new(v => v switch
        {
            0 => "Source",
            1 => "Mono",
            2 => "Stereo",
            6 => "5.1 Surround",
            _ => $"{v} ch",
        });

    public static readonly FuncValueConverter<int, string> Av1Tune =
        new(v => v switch
        {
            0 => "0 — VQ",
            1 => "1 — PSNR",
            2 => "2 — SSIM",
            3 => "3 — IQ",
            4 => "4 — MS-SSIM",
            _ => v.ToString(),
        });


    public static readonly FuncValueConverter<int, string> Av1Scm =
        new(v => v switch
        {
            0 => "Off",
            1 => "Block Copy + Palette",
            2 => "Content Adaptive",
            3 => "Content Adaptive (AA)",
            _ => v.ToString(),
        });

    public static readonly FuncValueConverter<KeyframeUnit, string> KeyframeUnitLabel =
        new(v => v switch
        {
            Core.Models.KeyframeUnit.Frames => "frames",
            Core.Models.KeyframeUnit.Seconds => "seconds",
            _ => v.ToString(),
        });

    public static readonly FuncValueConverter<HdrMode, string> HdrMode =
        new(v => FormatHelpers.HdrModeLabel(v));

    public static readonly FuncValueConverter<ToneMapAlgorithm, string> ToneMapAlgorithm =
        new(v => FormatHelpers.ToneMapAlgorithmLabel(v));

    public static readonly FuncValueConverter<ToneMapEngine, string> ToneMapEngine =
        new(v => FormatHelpers.ToneMapEngineLabel(v));

    public static readonly FuncValueConverter<LibplaceboToneMapAlgorithm, string> LibplaceboToneMapAlgorithm =
        new(v => FormatHelpers.LibplaceboToneMapAlgorithmLabel(v));

    public static readonly FuncValueConverter<GamutMode, string> GamutMode =
        new(v => FormatHelpers.GamutModeLabel(v));
}
