using Avalonia.Controls;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class VideoPreviewAudioControlsTests(QueueTestSession fixture)
{
    [Fact]
    public async Task AudioControls_BindVolumeMuteAndMediaAvailability()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var vm = new VideoPreviewViewModel(null) { IsExpanded = true };
            var view = new VideoPreviewView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 600 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var slider = view.FindControl<Slider>("VolumeSlider")
                    ?? throw new InvalidOperationException("Volume slider missing.");
                var mute = view.FindControl<Button>("MuteButton")
                    ?? throw new InvalidOperationException("Mute button missing.");
                var display = view.FindControl<TextBlock>("VolumeDisplay")
                    ?? throw new InvalidOperationException("Volume display missing.");
                slider.Bounds.Height.Should().BeGreaterThan(0);
                slider.Bounds.Center.Y.Should().BeApproximately(mute.Bounds.Center.Y, 0.5);
                display.Bounds.Center.Y.Should().BeApproximately(mute.Bounds.Center.Y, 0.5);
                slider.IsEffectivelyEnabled.Should().BeFalse();
                mute.IsEffectivelyEnabled.Should().BeFalse();

                vm.HasMedia = true;
                slider.IsEffectivelyEnabled.Should().BeTrue();
                mute.IsEffectivelyEnabled.Should().BeTrue();
                slider.Value = 42;
                vm.Volume.Should().Be(42);
                display.Text.Should().Be("42%");

                mute.Command.Should().BeSameAs(vm.ToggleMuteCommand);
                mute.Command?.Execute(mute.CommandParameter);
                vm.IsMuted.Should().BeTrue();
                mute.Content.Should().Be("Unmute");
                slider.Value.Should().Be(42);

                mute.Command?.Execute(mute.CommandParameter);
                vm.IsMuted.Should().BeFalse();
                mute.Content.Should().Be("Mute");

                vm.Volume = 25;
                slider.Value.Should().Be(25);
                display.Text.Should().Be("25%");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
