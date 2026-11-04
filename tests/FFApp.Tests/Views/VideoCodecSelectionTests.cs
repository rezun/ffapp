using Avalonia.Controls;
using Avalonia.LogicalTree;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class VideoCodecSelectionTests(QueueTestSession fixture)
{
    // Regression: an audio-only container used to empty the codec list, so the ComboBox wrote
    // null into the non-nullable SelectedVideoCodec binding and kept showing a conversion error.
    [Fact]
    public async Task AudioOnlyContainerInRemoveMode_LeavesCodecComboSelectedWithoutBindingErrors()
    {
        await fixture.Session.Dispatch(() =>
        {
            var validation = Substitute.For<IValidationService>();
            validation.Validate(Arg.Any<OutputSettings>(), Arg.Any<SourceMediaInfo>()).Returns(new ValidationResult());
            var vm = new OutputSettingsViewModel(validation, new WeakReferenceMessenger(),
                Substitute.For<IFfmpegCapabilityService>(), new ExecutablePathsConfig());
            vm.UpdateSourceInfo(new TestMediaInfoBuilder().Build());
            vm.ApplySettings(new OutputSettings(Container: OutputContainer.Mkv, VideoCodec: VideoCodec.Av1));
            var view = new OutputSettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var codec = view.GetLogicalDescendants().OfType<ComboBox>()
                    .Single(c => ReferenceEquals(c.ItemsSource, vm.Format.AvailableVideoCodecs));

                vm.Format.SelectedVideoMode = VideoMode.Remove;
                vm.Format.SelectedContainer = OutputContainer.Mka;
                window.UpdateLayout();
                vm.Format.SelectedVideoMode = VideoMode.Reencode;
                vm.Format.SelectedContainer = OutputContainer.Mkv;
                window.UpdateLayout();

                codec.SelectedItem.Should().Be(vm.Format.SelectedVideoCodec);
                DataValidationErrors.GetHasErrors(codec).Should().BeFalse();
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
