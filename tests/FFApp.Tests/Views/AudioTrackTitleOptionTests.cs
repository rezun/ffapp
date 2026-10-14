using Avalonia.Controls;
using FFApp.Core.Models;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class AudioTrackTitleOptionTests(QueueTestSession fixture)
{
    [Fact]
    public async Task RemoveTrackTitles_BindsChoiceAndFollowsAudioMode()
    {
        await fixture.Session.Dispatch(() =>
        {
            var vm = new AudioViewModel();
            var view = new AudioView { DataContext = vm };
            var window = new Window { Content = view, Width = 600, Height = 400 };
            window.Show();
            try
            {
                var checkbox = view.FindControl<CheckBox>("RemoveTrackTitlesCheckBox")
                    ?? throw new InvalidOperationException("Track-title option missing.");
                checkbox.IsVisible.Should().BeTrue();
                checkbox.IsChecked.Should().BeFalse();

                checkbox.IsChecked = true;
                vm.RemoveTrackTitles.Should().BeTrue();
                vm.SelectedAudioMode = AudioMode.Reencode;
                checkbox.IsVisible.Should().BeTrue();
                vm.SelectedAudioMode = AudioMode.Remove;
                checkbox.IsVisible.Should().BeFalse();
                vm.SelectedAudioMode = AudioMode.Copy;
                checkbox.IsVisible.Should().BeTrue();
                checkbox.IsChecked.Should().BeTrue();

                vm.RemoveTrackTitles = false;
                checkbox.IsChecked.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
