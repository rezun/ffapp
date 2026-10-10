using FFApp.Core.Models;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class AudioViewModelTests
{

    [Fact]
    public void UpdateTracks_DefaultSelectsAll_AndCustomSelectionUsesSourceIndices()
    {
        var media = new FFApp.Tests.Builders.TestMediaInfoBuilder().WithAudioStreams(
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
        var media = new FFApp.Tests.Builders.TestMediaInfoBuilder().WithAudioStreams(new AudioStreamInfo(2), new AudioStreamInfo(5)).Build();
        _sut.UpdateTracks(media, [5]);
        _sut.AllAudioTracks.Should().BeFalse();
        _sut.AudioTracks.Select(t => t.IsSelected).Should().Equal(false, true);
        _sut.UpdateTracks(media, []);
        _sut.SelectedAudioStreamIndices.GetValueOrDefault().Should().BeEmpty();
        _sut.UpdateTracks(media);
        _sut.SelectedAudioStreamIndices.Should().BeNull();
    }

    [Fact]
    public void TrackSelection_NotifiesChanges_AndHidesWhenRemovingAudio()
    {
        var media = new FFApp.Tests.Builders.TestMediaInfoBuilder().Build();
        _sut.UpdateTracks(media);
        using var monitor = _sut.Monitor();
        _sut.AllAudioTracks = false;
        _sut.AudioTracks[0].IsSelected = false;
        monitor.Should().RaisePropertyChangeFor(s => s.SelectedAudioStreamIndices);
        _sut.SelectedAudioMode = AudioMode.Remove;
        _sut.IsTrackSelectionVisible.Should().BeFalse();
        _sut.SelectedAudioMode = AudioMode.Copy;
        _sut.IsTrackSelectionVisible.Should().BeTrue();
        _sut.UpdateTracks(media with { AudioStreams = [] });
        _sut.IsTrackSelectionVisible.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectAllTracks_SelectsEveryTrack_AndKeepsIndividualSelectionVisible(bool allMode)
    {
        var media = new FFApp.Tests.Builders.TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(2), new AudioStreamInfo(5)).Build();
        _sut.UpdateTracks(media, [5]);
        _sut.AllAudioTracks = allMode;

        _sut.SelectAllTracksCommand.Execute(null);

        _sut.AudioTracks.Should().OnlyContain(t => t.IsSelected);
        _sut.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(2, 5);
        _sut.IsCustomTrackSelection.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeselectAllTracks_ClearsEveryTrack_AndKeepsIndividualSelectionVisible(bool allMode)
    {
        var media = new FFApp.Tests.Builders.TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(2), new AudioStreamInfo(5)).Build();
        _sut.UpdateTracks(media);
        _sut.AllAudioTracks = allMode;
        using var monitor = _sut.Monitor();

        _sut.DeselectAllTracksCommand.Execute(null);

        _sut.AudioTracks.Should().OnlyContain(t => !t.IsSelected);
        _sut.SelectedAudioStreamIndices.Should().NotBeNull();
        _sut.SelectedAudioStreamIndices.GetValueOrDefault().Should().BeEmpty();
        _sut.IsCustomTrackSelection.Should().BeTrue();
        monitor.Should().RaisePropertyChangeFor(s => s.SelectedAudioStreamIndices);

        _sut.SelectAllTracksCommand.Execute(null);
        _sut.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(2, 5);
    }

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
