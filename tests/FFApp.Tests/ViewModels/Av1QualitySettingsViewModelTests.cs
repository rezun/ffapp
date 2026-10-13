using FFApp.Core.Models;
using FFApp.ViewModels;
using FFApp.ViewModels.Quality;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class Av1QualitySettingsViewModelTests
{
    [Fact]
    public void Reset_RestoresFilmGrainDefaults()
    {
        var quality = new QualityViewModel();
        quality.UpdateForCodec(VideoCodec.Av1);
        quality.Av1.FilmGrain = 15;
        quality.Av1.FilmGrainDenoiseChecked = true;
        quality.Av1.AdaptiveFilmGrainChecked = false;

        quality.UpdateForCodec(VideoCodec.H264);
        quality.UpdateForCodec(VideoCodec.Av1);

        quality.Av1.FilmGrain.Should().Be(0);
        quality.Av1.IsFilmGrainEnabled.Should().BeFalse();
        quality.Av1.FilmGrainDenoiseChecked.Should().BeFalse();
        quality.Av1.AdaptiveFilmGrainChecked.Should().BeTrue();

        quality.Av1.IsFilmGrainEnabled = true;
        quality.Av1.FilmGrain.Should().Be(8);
    }

    [Fact]
    public void FilmGrainChanges_NotifyDependentBindingsAndParent()
    {
        var quality = new QualityViewModel();
        var notifications = new List<string?>();
        quality.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        quality.Av1.FilmGrain = 8;
        quality.Av1.FilmGrainDenoise = 1;
        quality.Av1.AdaptiveFilmGrain = 0;

        quality.Av1.IsFilmGrainEnabled.Should().BeTrue();
        quality.Av1.FilmGrainDenoiseChecked.Should().BeTrue();
        quality.Av1.AdaptiveFilmGrainChecked.Should().BeFalse();
        notifications.Should().Contain(nameof(Av1QualitySettingsViewModel.IsFilmGrainEnabled))
            .And.Contain(nameof(Av1QualitySettingsViewModel.FilmGrainDenoiseChecked))
            .And.Contain(nameof(Av1QualitySettingsViewModel.AdaptiveFilmGrainChecked));
    }

    [Fact]
    public void ApplyQualityAndEncoder_RestoreFilmGrainControls()
    {
        var vm = new Av1QualitySettingsViewModel();
        vm.ApplyQuality(new Av1QualityOptions(FilmGrain: 8, FilmGrainDenoise: 1, AdaptiveFilmGrain: 0));

        vm.FilmGrain.Should().Be(8);
        vm.FilmGrainDenoiseChecked.Should().BeTrue();
        vm.AdaptiveFilmGrainChecked.Should().BeFalse();

        vm.ApplyEncoder(new Av1EncoderOptions(FilmGrain: 15, FilmGrainDenoise: 0, AdaptiveFilmGrain: 1));

        vm.FilmGrain.Should().Be(15);
        vm.FilmGrainDenoiseChecked.Should().BeFalse();
        vm.AdaptiveFilmGrainChecked.Should().BeTrue();
    }

    [Fact]
    public void DisablingFilmGrain_PreservesToggleSelectionsForReenabling()
    {
        var vm = new Av1QualitySettingsViewModel { FilmGrain = 8 };
        vm.FilmGrainDenoiseChecked = true;
        vm.AdaptiveFilmGrainChecked = false;

        vm.IsFilmGrainEnabled = false;

        vm.FilmGrain.Should().Be(0);
        vm.IsFilmGrainEnabled.Should().BeFalse();
        vm.FilmGrainDenoise.Should().Be(1);
        vm.AdaptiveFilmGrain.Should().Be(0);

        vm.IsFilmGrainEnabled = true;
        vm.FilmGrain.Should().Be(8);
        vm.IsFilmGrainEnabled.Should().BeTrue();
        vm.FilmGrainDenoiseChecked.Should().BeTrue();
        vm.AdaptiveFilmGrainChecked.Should().BeFalse();
    }

    [Fact]
    public void EnablingFilmGrain_DefaultsToEightAndRemembersSelectedStrength()
    {
        var vm = new Av1QualitySettingsViewModel();

        vm.IsFilmGrainEnabled = true;
        vm.FilmGrain.Should().Be(8);
        vm.AvailableFilmGrainStrengths.Should().Equal(Enumerable.Range(1, 50));

        vm.FilmGrain = 15;
        vm.IsFilmGrainEnabled = true;
        vm.FilmGrain.Should().Be(15);

        vm.IsFilmGrainEnabled = false;
        ((Av1EncoderOptions)vm.BuildEncoderOptions()).FilmGrain.Should().Be(0);
        ((Av1QualityOptions)vm.BuildQualityOptions()).FilmGrain.Should().Be(0);

        vm.IsFilmGrainEnabled = true;
        vm.FilmGrain.Should().Be(15);
    }
}
