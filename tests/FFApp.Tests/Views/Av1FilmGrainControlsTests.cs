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
    public async Task VarianceBoostAndFilmGrainControls_BindSelectionsAndVisibility()
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
            var window = new Window { Content = new ScrollViewer { Content = view }, Width = 800, Height = 1000 };
            window.Show();
            try
            {
                window.UpdateLayout();
                T Find<T>(string name) where T : Control =>
                    view.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
                var varianceEnabled = Find<CheckBox>("VarianceBoostEnabledCheckBox");
                var varianceOptions = Find<Grid>("VarianceBoostOptionsPanel");
                var varianceStrength = Find<ComboBox>("VarianceBoostStrengthComboBox");
                var octile = Find<ComboBox>("VarianceOctileComboBox");
                var grainEnabled = Find<CheckBox>("FilmGrainEnabledCheckBox");
                var grainOptions = Find<Grid>("FilmGrainOptionsPanel");
                var grainStrength = Find<ComboBox>("FilmGrainComboBox");
                var denoise = Find<CheckBox>("FilmGrainDenoiseCheckBox");
                var adaptive = Find<CheckBox>("AdaptiveFilmGrainCheckBox");

                varianceOptions.IsVisible.Should().BeFalse();
                grainOptions.IsVisible.Should().BeFalse();

                varianceEnabled.IsChecked = true;
                grainEnabled.IsChecked = true;
                window.UpdateLayout();
                quality.EnableVarianceBoostChecked.Should().BeTrue();
                quality.IsFilmGrainEnabled.Should().BeTrue();
                varianceStrength.IsEffectivelyVisible.Should().BeTrue();
                octile.IsEffectivelyVisible.Should().BeTrue();
                grainStrength.IsEffectivelyVisible.Should().BeTrue();
                denoise.IsEffectivelyVisible.Should().BeTrue();
                adaptive.IsEffectivelyVisible.Should().BeTrue();

                varianceStrength.SelectedItem = 4;
                octile.SelectedItem = 8;
                grainStrength.SelectedItem = 15;
                denoise.IsChecked = true;
                adaptive.IsChecked = false;
                quality.VarianceBoostStrength.Should().Be(4);
                quality.VarianceOctile.Should().Be(8);
                quality.FilmGrain.Should().Be(15);
                quality.FilmGrainDenoiseChecked.Should().BeTrue();
                quality.AdaptiveFilmGrainChecked.Should().BeFalse();

                quality.EnableVarianceBoostChecked = false;
                quality.IsFilmGrainEnabled = false;
                window.UpdateLayout();
                varianceEnabled.IsChecked.Should().BeFalse();
                grainEnabled.IsChecked.Should().BeFalse();
                varianceOptions.IsVisible.Should().BeFalse();
                grainOptions.IsVisible.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
