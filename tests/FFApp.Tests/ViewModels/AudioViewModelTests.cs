using FFApp.Core.Models;
using FFApp.Core.Validation;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class AudioViewModelTests
{
    private readonly AudioViewModel _sut = new();

    [Fact]
    public void UpdateTracks_DefaultSelectsAll_AndCustomSelectionUsesSourceIndices()
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(2, "aac", Channels: 2, Language: "eng"), new AudioStreamInfo(5, "ac3")).Build();
        _sut.UpdateTracks(media);
        _sut.AllAudioTracks.Should().BeTrue();
        _sut.SelectedAudioStreamIndices.Should().BeNull();
        _sut.AudioTracks[0].Label.Should().Contain("Track 1").And.Contain("eng").And.Contain("aac").And.Contain("2 channels");

        _sut.AllAudioTracks = false;
        _sut.AudioTracks[0].IsSelected = false;
        _sut.SelectedAudioStreamIndices.Should().NotBeNull();
        _sut.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(5);
        _sut.AllAudioTracks = true;
        _sut.SelectedAudioStreamIndices.Should().BeNull();
    }

    [Fact]
    public void UpdateTracks_RestoresSelection_AndNewSourceDefaultsToAll()
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(new AudioStreamInfo(2), new AudioStreamInfo(5)).Build();
        _sut.UpdateTracks(media, [5]);
        _sut.AllAudioTracks.Should().BeFalse();
        _sut.AudioTracks.Select(t => t.IsSelected).Should().Equal(false, true);
        _sut.UpdateTracks(media, []);
        _sut.SelectedAudioStreamIndices.GetValueOrDefault().Should().BeEmpty();
        _sut.UpdateTracks(media);
        _sut.SelectedAudioStreamIndices.Should().BeNull();
    }

    [Fact]
    public void SelectAndDeselectAllTracks_SetEveryTrack_AndSwitchToIndividualSelection()
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(2), new AudioStreamInfo(5)).Build();
        _sut.UpdateTracks(media);
        using var monitor = _sut.Monitor();

        _sut.DeselectAllTracksCommand.Execute(null);

        _sut.AudioTracks.Should().OnlyContain(t => !t.IsSelected);
        _sut.IsCustomTrackSelection.Should().BeTrue();
        _sut.SelectedAudioStreamIndices.Should().NotBeNull();
        _sut.SelectedAudioStreamIndices.GetValueOrDefault().Should().BeEmpty();
        monitor.Should().RaisePropertyChangeFor(s => s.SelectedAudioStreamIndices);

        _sut.SelectAllTracksCommand.Execute(null);

        _sut.AudioTracks.Should().OnlyContain(t => t.IsSelected);
        _sut.IsCustomTrackSelection.Should().BeTrue();
        _sut.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(2, 5);
    }

    [Fact]
    public void SampleRateOptions_AreAutoPlusTheSelectedCodecsRates()
    {
        int[] autoPlusOpusRates = [0, .. CompatibilityData.AudioSampleRateOptions[AudioCodec.Opus]];

        _sut.SelectedAudioCodec = AudioCodec.Opus;

        _sut.AvailableSampleRateOptions.Should().Equal(autoPlusOpusRates);
    }

    [Theory]
    [InlineData(44100, 0)]
    [InlineData(48000, 48000)]
    public void CodecChange_KeepsSampleRateOnlyIfTheNewCodecSupportsIt(int rate, int expected)
    {
        _sut.SelectedSampleRate = rate;
        using var monitor = _sut.Monitor();

        _sut.SelectedAudioCodec = AudioCodec.Opus;

        _sut.SelectedSampleRate.Should().Be(expected);
        monitor.Should().RaisePropertyChangeFor(vm => vm.AvailableSampleRateOptions);
    }

    [Theory]
    [InlineData(44100, 0)]
    [InlineData(24000, 24000)]
    public void AssigningSampleRate_KeepsOnlyRatesTheCodecOffers(int rate, int expected)
    {
        _sut.SelectedAudioCodec = AudioCodec.Opus;
        _sut.SelectedSampleRate = rate;

        _sut.SelectedSampleRate.Should().Be(expected);
    }

    [Fact]
    public void NoCodec_OffersOnlyAutoAndClearsExplicitRate()
    {
        _sut.SelectedSampleRate = 44100;
        _sut.SelectedAudioCodec = null;

        _sut.AvailableSampleRateOptions.Should().Equal(0);
        _sut.SelectedSampleRate.Should().Be(0);
    }

    [Theory]
    [InlineData(768_000, 768, 128)]
    [InlineData(200_000, -1, 200)]
    public void AudioBitrate_UsesListedOptionOrFallsBackToCustom(int bitrate, int expectedOption, int expectedCustomKbps)
    {
        _sut.AudioBitrate = bitrate;

        (_sut.SelectedBitrateOption, _sut.CustomBitrateKbps).Should().Be((expectedOption, expectedCustomKbps));
        _sut.AudioBitrate.Should().Be(bitrate);
    }

    [Theory]
    [InlineData(null, AudioCodec.Aac)]
    [InlineData(AudioCodec.Mp3, AudioCodec.Mp3)]
    public void SwitchToReencode_SelectsFirstAvailableCodecOnlyWhenNoneIsSet(AudioCodec? current, AudioCodec expected)
    {
        _sut.AvailableAudioCodecs = [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus];
        _sut.SelectedAudioCodec = current;

        _sut.SelectedAudioMode = AudioMode.Reencode;

        _sut.SelectedAudioCodec.Should().Be(expected);
    }
}
