using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
public sealed class ColorMetadataSelectionTests(QueueTestSession fixture)
{
    [Fact]
    public async Task SelectionAndPresetRestore_UpdateControlsAndEffectiveOutput()
    {
        await fixture.Session.Dispatch(() =>
        {
            var validation = Substitute.For<IValidationService>();
            validation.Validate(Arg.Any<OutputSettings>(), Arg.Any<SourceMediaInfo>()).Returns(new ValidationResult());
            var vm = new OutputSettingsViewModel(validation, new WeakReferenceMessenger(),
                Substitute.For<IFfmpegCapabilityService>(), new ExecutablePathsConfig());
            vm.UpdateSourceInfo(new TestMediaInfoBuilder().Build());
            var view = new OutputSettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var mode = view.FindControl<ComboBox>("ColorMetadataModeComboBox")
                    ?? throw new InvalidOperationException("Color mode missing.");
                var panel = view.FindControl<StackPanel>("CustomColorMetadataPanel")
                    ?? throw new InvalidOperationException("Color controls missing.");
                var range = view.FindControl<ComboBox>("ColorRangeComboBox")
                    ?? throw new InvalidOperationException("Range missing.");
                var summary = view.FindControl<TextBlock>("ColorOutputSummary")
                    ?? throw new InvalidOperationException("Summary missing.");
                var section = view.FindControl<Grid>("ColorMetadataSection")
                    ?? throw new InvalidOperationException("Color row missing.");
                var details = view.FindControl<StackPanel>("ColorMetadataDetails")
                    ?? throw new InvalidOperationException("Color details missing.");
                var detailsToggle = view.FindControl<ToggleButton>("ColorDetailsToggle")
                    ?? throw new InvalidOperationException("Details toggle missing.");
                mode.SelectedItem.Should().Be(ColorMetadataMode.Automatic);
                details.IsVisible.Should().BeFalse();
                section.Bounds.Height.Should().BeLessThanOrEqualTo(40);
                detailsToggle.IsChecked = true;
                details.IsVisible.Should().BeTrue();
                detailsToggle.IsChecked = false;
                details.IsVisible.Should().BeFalse();
                panel.IsVisible.Should().BeFalse();
                mode.SelectedItem = ColorMetadataMode.AssumeBt709;
                summary.Text.Should().Contain("assumed");
                vm.Color.CompactSummary.Should().Contain("assumed");
                window.UpdateLayout();
                section.Bounds.Height.Should().BeLessThanOrEqualTo(40);
                mode.SelectedItem = ColorMetadataMode.Custom;
                panel.IsVisible.Should().BeTrue();
                range.SelectedItem = "pc";
                vm.BuildSettings().ColorMetadata.Range.Should().Be("pc");
                summary.Text.Should().Contain("Full");
                vm.Color.CompactSummary.Should().Contain("Full");
                vm.ApplySettings(new OutputSettings(ColorMetadata: new(ColorMetadataMode.Custom, Range: "tv")));
                range.SelectedItem.Should().Be("tv");
                vm.UpdateSourceInfo(new TestMediaInfoBuilder().WithHdr10().Build());
                mode.IsEnabled.Should().BeFalse();
                panel.IsVisible.Should().BeFalse();
                summary.Text.Should().Contain("HDR settings");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
