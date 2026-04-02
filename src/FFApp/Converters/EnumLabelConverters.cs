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
            0 => "Source",
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
}
