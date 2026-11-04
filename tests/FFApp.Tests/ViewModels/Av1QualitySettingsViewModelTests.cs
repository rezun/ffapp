using FFApp.Core.Models;
using FFApp.Core.Validation;
using FFApp.ViewModels;
using FFApp.ViewModels.Quality;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class Av1QualitySettingsViewModelTests
{
    [Fact]
    public void Reset_RestoresEveryDefault()
    {
        var quality = new QualityViewModel();
        quality.UpdateForCodec(VideoCodec.Av1);
        var defaults = (quality.Av1.BuildQualityOptions(), quality.Av1.BuildEncoderOptions());
        quality.Av1.ApplyQuality(new Av1QualityOptions(Crf: 20, MaxBitrateKbps: 5000));
        quality.Av1.ApplyEncoder(new Av1EncoderOptions(Preset: 4, Tune: 1, EnableOverlays: 0, Scm: 1,
            EnableVarianceBoost: 1, AdditionalOptions: "lp=2", FilmGrain: 15, FilmGrainDenoise: 1,
            AdaptiveFilmGrain: 0, VarianceBoostStrength: 4, VarianceOctile: 8));

        quality.UpdateForCodec(VideoCodec.H264);
        quality.UpdateForCodec(VideoCodec.Av1);

        (quality.Av1.BuildQualityOptions(), quality.Av1.BuildEncoderOptions()).Should().Be(defaults);
        quality.Av1.IsFilmGrainEnabled = true;
        quality.Av1.FilmGrain.Should().Be(CompatibilityData.Av1FilmGrainDefaultStrength);
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
    public void FilmGrainToggle_DefaultsStrength_AndRestoresLastStrengthWithoutTouchingOtherControls()
    {
        var vm = new Av1QualitySettingsViewModel();

        vm.IsFilmGrainEnabled = true;
        vm.FilmGrain.Should().Be(CompatibilityData.Av1FilmGrainDefaultStrength);

        vm.FilmGrain = 15;
        vm.FilmGrainDenoiseChecked = true;
        vm.AdaptiveFilmGrainChecked = false;
        vm.IsFilmGrainEnabled = false;

        vm.IsFilmGrainEnabled.Should().BeFalse();
        ((Av1EncoderOptions)vm.BuildEncoderOptions()).FilmGrain.Should().Be(0);
        ((Av1QualityOptions)vm.BuildQualityOptions()).FilmGrain.Should().Be(0);
        vm.FilmGrainDenoise.Should().Be(1);
        vm.AdaptiveFilmGrain.Should().Be(0);

        vm.IsFilmGrainEnabled = true;
        vm.FilmGrain.Should().Be(15);
    }

    [Fact]
    public void AvailableFilmGrainStrengths_SpanEveryEnabledStrength()
    {
        var strengths = new Av1QualitySettingsViewModel().AvailableFilmGrainStrengths;

        strengths.Should().OnlyContain(s => s > CompatibilityData.Av1FilmGrainMin && s <= CompatibilityData.Av1FilmGrainMax)
            .And.OnlyHaveUniqueItems()
            .And.HaveCount(CompatibilityData.Av1FilmGrainMax - CompatibilityData.Av1FilmGrainMin)
            .And.Contain(CompatibilityData.Av1FilmGrainDefaultStrength);
    }
}
