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
    public async Task CodecChange_UpdatesDropdownAndPreservesOnlySupportedSelection()
    {
        await fixture.Session.Dispatch(() =>
        {
            var vm = new AudioViewModel
            {
                AvailableAudioCodecs = [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus, AudioCodec.Vorbis],
                SelectedAudioMode = AudioMode.Reencode,
                SelectedSampleRate = 44100,
            };
            var view = new AudioView { DataContext = vm };
            var window = new Window { Content = view, Width = 600, Height = 300 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var combo = view.FindControl<ComboBox>("SampleRateComboBox")
                    ?? throw new InvalidOperationException("Sample-rate selector missing.");
                combo.SelectedItem.Should().Be(44100);

                vm.SelectedAudioCodec = AudioCodec.Opus;
                window.UpdateLayout();

                combo.Items.Cast<int>().Should().Equal(0, 8000, 12000, 16000, 24000, 48000);
                vm.SelectedSampleRate.Should().Be(0);
                combo.SelectedItem.Should().Be(0);

                combo.SelectedItem = 48000;
                vm.SelectedSampleRate.Should().Be(48000);
                vm.SelectedAudioCodec = AudioCodec.Aac;
                window.UpdateLayout();

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
