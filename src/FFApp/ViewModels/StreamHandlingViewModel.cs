using System.Collections.Immutable;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public partial class StreamHandlingViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _removeSubtitles;

    [ObservableProperty]
    private bool _removeData;

    [ObservableProperty]
    private bool _removeImages;

    [ObservableProperty]
    private bool _allSubtitleTracks = true;

    [ObservableProperty]
    private IReadOnlyList<SubtitleTrackViewModel> _subtitleTracks = [];

    public bool IsSubtitleTrackSelectionVisible => !RemoveSubtitles && SubtitleTracks.Count > 0;
    public bool IsCustomSubtitleTrackSelection => !AllSubtitleTracks;

    /// <summary>Per-source selection: null keeps every subtitle track. Not part of the preset.</summary>
    public ImmutableArray<int>? SelectedSubtitleStreamIndices => AllSubtitleTracks
        ? null
        : [.. SubtitleTracks.Where(t => t.IsSelected).Select(t => t.StreamIndex)];

    public void UpdateSubtitleTracks(SourceMediaInfo mediaInfo, ImmutableArray<int>? selectedIndices = null)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);
        foreach (var track in SubtitleTracks)
            track.PropertyChanged -= OnSubtitleTrackPropertyChanged;
        SubtitleTracks = mediaInfo.SubtitleStreams.Select((stream, index) =>
            new SubtitleTrackViewModel(stream, index + 1, selectedIndices is null || selectedIndices.Value.Contains(stream.Index))).ToArray();
        foreach (var track in SubtitleTracks)
            track.PropertyChanged += OnSubtitleTrackPropertyChanged;
        AllSubtitleTracks = selectedIndices is null;
        OnPropertyChanged(nameof(IsSubtitleTrackSelectionVisible));
        OnPropertyChanged(nameof(SelectedSubtitleStreamIndices));
    }

    [RelayCommand]
    private void SelectAllSubtitleTracks() => SetAllSubtitleTracksSelected(true);

    [RelayCommand]
    private void DeselectAllSubtitleTracks() => SetAllSubtitleTracksSelected(false);

    private void SetAllSubtitleTracksSelected(bool isSelected)
    {
        foreach (var track in SubtitleTracks)
            track.IsSelected = isSelected;
        AllSubtitleTracks = false;
    }

    private void OnSubtitleTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubtitleTrackViewModel.IsSelected))
            OnPropertyChanged(nameof(SelectedSubtitleStreamIndices));
    }

    partial void OnAllSubtitleTracksChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCustomSubtitleTrackSelection));
        OnPropertyChanged(nameof(SelectedSubtitleStreamIndices));
    }

    partial void OnRemoveSubtitlesChanged(bool value) =>
        OnPropertyChanged(nameof(IsSubtitleTrackSelectionVisible));
}
