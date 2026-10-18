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
    public async Task VarianceBoostControls_BindSelectionsAndVisibility()
    {
        await fixture.Session.Dispatch(() =>
        {
            var validation = Substitute.For<IValidationService>();
            var vm = new OutputSettingsViewModel(validation, Substitute.For<IMessenger>(),
                Substitute.For<IFfmpegCapabilityService>(), new ExecutablePathsConfig());
            vm.ApplySettings(new TestOutputSettingsBuilder().WithAv1().Build());
            var quality = vm.Strategy.Quality.Av1;
            var view = new OutputSettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 800, Height = 1000 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var enabled = view.GetVisualDescendants().OfType<CheckBox>()
                    .Single(c => c.Name == "VarianceBoostEnabledCheckBox");
                var options = view.GetVisualDescendants().OfType<Grid>()
                    .Single(c => c.Name == "VarianceBoostOptionsPanel");
                var strength = view.GetVisualDescendants().OfType<ComboBox>()
                    .Single(c => c.Name == "VarianceBoostStrengthComboBox");
                var octile = view.GetVisualDescendants().OfType<ComboBox>()
                    .Single(c => c.Name == "VarianceOctileComboBox");

                options.IsVisible.Should().BeFalse();
                enabled.IsChecked = true;
                window.UpdateLayout();
                strength.IsEffectivelyVisible.Should().BeTrue();
                octile.IsEffectivelyVisible.Should().BeTrue();
                strength.SelectedItem = 4;
                octile.SelectedItem = 8;
                quality.VarianceBoostStrength.Should().Be(4);
                quality.VarianceOctile.Should().Be(8);
                quality.EnableVarianceBoostChecked = false;
                enabled.IsChecked.Should().BeFalse();
                options.IsVisible.Should().BeFalse();
                quality.VarianceBoostStrength = 1;
                quality.VarianceOctile = 3;
                strength.SelectedItem.Should().Be(1);
                octile.SelectedItem.Should().Be(3);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FilmGrainControls_BindSelectionsAndCollapseWhenOff()
    {
        await fixture.Session.Dispatch(() =>
        {
            var validation = Substitute.For<IValidationService>();
            validation.Validate(Arg.Any<OutputSettings>(), Arg.Any<SourceMediaInfo>())
                .Returns(new ValidationResult());
            var vm = new OutputSettingsViewModel(validation, Substitute.For<IMessenger>(),
                Substitute.For<IFfmpegCapabilityService>(), new ExecutablePathsConfig());
            vm.ApplySettings(new TestOutputSettingsBuilder().WithAv1().Build());
            var quality = vm.Strategy.Quality.Av1;
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

                options.IsVisible.Should().BeFalse();
                var collapsedHeight = view.DesiredSize.Height;

                enabled.IsChecked = true;
                window.UpdateLayout();
                strength.IsEffectivelyVisible.Should().BeTrue();
                denoise.IsEffectivelyVisible.Should().BeTrue();
                adaptive.IsEffectivelyVisible.Should().BeTrue();
                view.DesiredSize.Height.Should().BeGreaterThan(collapsedHeight);

                strength.SelectedItem = 15;
                denoise.IsChecked = true;
                adaptive.IsChecked = false;

                quality.FilmGrain.Should().Be(15);
                quality.FilmGrainDenoiseChecked.Should().BeTrue();
                quality.AdaptiveFilmGrainChecked.Should().BeFalse();

                quality.IsFilmGrainEnabled = false;
                window.UpdateLayout();
                enabled.IsChecked.Should().BeFalse();
                options.IsVisible.Should().BeFalse();
                view.DesiredSize.Height.Should().Be(collapsedHeight);
                quality.FilmGrain = 8;
                quality.FilmGrainDenoiseChecked = false;
                quality.AdaptiveFilmGrainChecked = true;
                strength.SelectedItem.Should().Be(8);
                denoise.IsChecked.Should().BeFalse();
                adaptive.IsChecked.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
