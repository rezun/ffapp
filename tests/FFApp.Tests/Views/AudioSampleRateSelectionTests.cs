using Avalonia.Controls;
using FFApp.Core.Models;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class AudioSampleRateSelectionTests(QueueTestSession fixture)
{
    [Fact]
    public async Task CodecChange_ReplacesItemList_WithoutLosingTheSelection()
    {
        await fixture.Session.Dispatch(() =>
        {
            var vm = new AudioViewModel
            {
                AvailableAudioCodecs = [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus, AudioCodec.Vorbis],
                SelectedAudioMode = AudioMode.Reencode,
            };
            var view = new AudioView { DataContext = vm };
            var window = new Window { Content = view, Width = 600, Height = 300 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var combo = view.FindControl<ComboBox>("SampleRateComboBox")
                    ?? throw new InvalidOperationException("Sample-rate selector missing.");
                combo.SelectedItem = 48000;
                vm.SelectedSampleRate.Should().Be(48000);

                // Replacing ItemsSource makes the ComboBox drop its selection; the binding must restore it.
                vm.SelectedAudioCodec = AudioCodec.Opus;
                window.UpdateLayout();

                combo.ItemsSource.Should().BeSameAs(vm.AvailableSampleRateOptions);
                vm.SelectedSampleRate.Should().Be(48000);
                combo.SelectedItem.Should().Be(48000);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
