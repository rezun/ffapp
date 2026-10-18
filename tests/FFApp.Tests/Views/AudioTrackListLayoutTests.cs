using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using FFApp.Core.Models;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class AudioTrackListLayoutTests(QueueTestSession fixture)
{
    [Fact]
    public async Task TrackList_UsesContentWidthWithScrollbarBesideTracks()
    {
        await fixture.Session.Dispatch(() =>
        {
            var vm = new AudioViewModel { AllAudioTracks = false };
            vm.AudioTracks = Enumerable.Range(0, 20)
                .Select(index => new AudioTrackViewModel(new AudioStreamInfo(index, "aac"), index + 1, true))
                .ToArray();
            var view = new AudioView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 600 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var viewer = view.GetVisualDescendants().OfType<ScrollViewer>()
                    .Single(control => control.Content is ItemsControl);
                var items = viewer.Content as ItemsControl
                    ?? throw new InvalidOperationException("Track list is missing.");
                var bar = viewer.GetVisualDescendants().OfType<ScrollBar>().Single(control => control.IsVisible);

                viewer.Bounds.Width.Should().BeApproximately(items.DesiredSize.Width + bar.Bounds.Width, 1);
                viewer.Bounds.Width.Should().BeLessThan(view.Bounds.Width - viewer.Bounds.Left);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
