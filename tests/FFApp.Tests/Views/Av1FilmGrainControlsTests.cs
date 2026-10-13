using Avalonia.Controls;
using Avalonia.VisualTree;
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
public sealed class Av1FilmGrainControlsTests(QueueTestSession fixture)
{
    [Fact]
    public async Task FilmGrainControls_CollapseWhenOffAndRestoreDropdownSelectionWhenReenabled()
    {
        await fixture.Session.Dispatch(() =>
        {
            var validation = Substitute.For<IValidationService>();
            validation.Validate(Arg.Any<OutputSettings>(), Arg.Any<SourceMediaInfo>())
                .Returns(new ValidationResult());
            var vm = new OutputSettingsViewModel(validation, Substitute.For<IMessenger>(),
                Substitute.For<IFfmpegCapabilityService>(), new ExecutablePathsConfig());
            vm.ApplySettings(new TestOutputSettingsBuilder().WithAv1().Build());
            var view = new OutputSettingsView { DataContext = vm };
            var window = new Window { Content = new ScrollViewer { Content = view }, Width = 800, Height = 900 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var enabled = view.GetVisualDescendants().OfType<CheckBox>()
                    .Single(c => c.Name == "FilmGrainEnabledCheckBox");
                var options = view.GetVisualDescendants().OfType<Grid>()
                    .Single(c => c.Name == "FilmGrainOptionsPanel");
                var strength = view.GetVisualDescendants().OfType<ComboBox>()
                    .Single(c => c.Name == "FilmGrainComboBox");
                var denoise = view.GetVisualDescendants().OfType<CheckBox>()
                    .Single(c => c.Name == "FilmGrainDenoiseCheckBox");
                var adaptive = view.GetVisualDescendants().OfType<CheckBox>()
                    .Single(c => c.Name == "AdaptiveFilmGrainCheckBox");

                enabled.IsChecked.Should().BeFalse();
                options.IsVisible.Should().BeFalse();
                strength.IsEffectivelyVisible.Should().BeFalse();
                denoise.IsEffectivelyVisible.Should().BeFalse();
                adaptive.IsEffectivelyVisible.Should().BeFalse();
                strength.Items.Cast<int>().Should().Equal(Enumerable.Range(1, 50));
                denoise.IsChecked.Should().BeFalse();
                adaptive.IsChecked.Should().BeTrue();
                var collapsedHeight = view.DesiredSize.Height;

                enabled.IsChecked = true;
                window.UpdateLayout();
                options.IsVisible.Should().BeTrue();
                strength.IsEffectivelyVisible.Should().BeTrue();
                denoise.IsEffectivelyVisible.Should().BeTrue();
                adaptive.IsEffectivelyVisible.Should().BeTrue();
                strength.SelectedItem.Should().Be(8);
                view.DesiredSize.Height.Should().BeGreaterThan(collapsedHeight);

                strength.SelectedItem = 15;
                denoise.IsChecked = true;
                adaptive.IsChecked = false;

                var encoder = vm.BuildSettings().VideoEncoderOptions
                    .Should().BeOfType<Av1EncoderOptions>().Subject;
                encoder.FilmGrain.Should().Be(15);
                encoder.FilmGrainDenoise.Should().Be(1);
                encoder.AdaptiveFilmGrain.Should().Be(0);

                enabled.IsChecked = false;
                window.UpdateLayout();
                options.IsVisible.Should().BeFalse();
                view.DesiredSize.Height.Should().Be(collapsedHeight);
                ((Av1EncoderOptions)vm.BuildSettings().VideoEncoderOptions!).FilmGrain.Should().Be(0);
                denoise.IsChecked.Should().BeTrue();
                adaptive.IsChecked.Should().BeFalse();

                enabled.IsChecked = true;
                strength.SelectedItem.Should().Be(15);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
