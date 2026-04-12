using FFApp.Core.Models;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class AudioViewModelTests
{
    private readonly AudioViewModel _sut = new();

    [Fact]
    public void Default_ModeIsCopy()
    {
        _sut.SelectedAudioMode.Should().Be(AudioMode.Copy);
    }

    [Fact]
    public void SetAudioBitrate_StandardValue_SetsSelectedBitrateOption()
    {
        _sut.AudioBitrate = 128_000;

        _sut.SelectedBitrateOption.Should().Be(128);
    }

    [Fact]
    public void SetAudioBitrate_NonStandardValue_SetsCustomMode()
    {
        _sut.AudioBitrate = 200_000;

        _sut.SelectedBitrateOption.Should().Be(-1);
        _sut.CustomBitrateKbps.Should().Be(200);
    }

    [Fact]
    public void IsCustomBitrate_WhenMinusOne_ReturnsTrue()
    {
        _sut.SelectedBitrateOption = -1;

        _sut.IsCustomBitrate.Should().BeTrue();
    }

    [Fact]
    public void IsCustomBitrate_WhenStandard_ReturnsFalse()
    {
        _sut.SelectedBitrateOption = 128;

        _sut.IsCustomBitrate.Should().BeFalse();
    }

    [Fact]
    public void AudioBitrateGetter_CustomMode_UsesCustomBitrateKbps()
    {
        _sut.SelectedBitrateOption = -1;
        _sut.CustomBitrateKbps = 250;

        _sut.AudioBitrate.Should().Be(250_000);
    }

    [Fact]
    public void AudioBitrateGetter_StandardMode_UsesSelectedOption()
    {
        _sut.SelectedBitrateOption = 192;

        _sut.AudioBitrate.Should().Be(192_000);
    }

    [Fact]
    public void IsReencodeMode_WhenReencode_ReturnsTrue()
    {
        _sut.SelectedAudioMode = AudioMode.Reencode;

        _sut.IsReencodeMode.Should().BeTrue();
    }

    [Fact]
    public void IsReencodeMode_WhenCopy_ReturnsFalse()
    {
        _sut.SelectedAudioMode = AudioMode.Copy;

        _sut.IsReencodeMode.Should().BeFalse();
    }

    [Fact]
    public void SwitchToReencode_WhenCodecIsNull_SelectsFirstAvailable()
    {
        _sut.AvailableAudioCodecs = [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus];
        _sut.SelectedAudioCodec = null;

        _sut.SelectedAudioMode = AudioMode.Reencode;

        _sut.SelectedAudioCodec.Should().Be(AudioCodec.Aac);
    }

    [Fact]
    public void SwitchToReencode_WhenCodecAlreadySet_LeavesSelectionAlone()
    {
        _sut.AvailableAudioCodecs = [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus];
        _sut.SelectedAudioCodec = AudioCodec.Mp3;

        _sut.SelectedAudioMode = AudioMode.Reencode;

        _sut.SelectedAudioCodec.Should().Be(AudioCodec.Mp3);
    }
}
