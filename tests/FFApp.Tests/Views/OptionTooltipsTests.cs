using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class OptionTooltipsTests(QueueTestSession fixture)
{
    // One row per codec quality template (H.264 and H.265 share one) and per tone-map engine.
    // The strategy panels are standalone views, covered by SupportingSettingsViews below.
    [Theory]
    [InlineData(VideoCodec.H264, EncodingStrategy.TargetQuality, ToneMapEngine.Standard)]
    [InlineData(VideoCodec.Av1, EncodingStrategy.TargetQuality, ToneMapEngine.Standard)]
    [InlineData(VideoCodec.Vp9, EncodingStrategy.TargetQuality, ToneMapEngine.Standard)]
    [InlineData(VideoCodec.H264, EncodingStrategy.TargetBitrate, ToneMapEngine.Libplacebo)]
    public async Task OutputOptions_HaveHelpInEachDistinctSettingsPanel(
        VideoCodec codec, EncodingStrategy strategy, ToneMapEngine engine)
    {
        await fixture.Session.Dispatch(() =>
        {
            var validation = Substitute.For<IValidationService>();
            validation.Validate(Arg.Any<OutputSettings>(), Arg.Any<SourceMediaInfo>())
                .Returns(new ValidationResult());
            var vm = new OutputSettingsViewModel(validation, Substitute.For<IMessenger>(),
                Substitute.For<IFfmpegCapabilityService>(), new ExecutablePathsConfig());
            var builder = codec switch
            {
                VideoCodec.H264 => new TestOutputSettingsBuilder(),
                VideoCodec.Av1 => new TestOutputSettingsBuilder().WithAv1().WithAv1FilmGrain(8),
                VideoCodec.Vp9 => new TestOutputSettingsBuilder().WithVp9(),
                _ => throw new ArgumentOutOfRangeException(nameof(codec)),
            };
            vm.UpdateSourceInfo(new TestMediaInfoBuilder().WithHdr10().Build());
            vm.ApplySettings(builder.WithContainer(OutputContainer.Mkv).Build());
            vm.Audio.SelectedAudioMode = AudioMode.Reencode;
            vm.Audio.SelectedBitrateOption = -1;
            vm.Audio.AllAudioTracks = false;
            vm.Hdr.IsLibplaceboAvailable = true;
            vm.Hdr.SelectedHdrMode = HdrMode.TonemapToSdr;
            vm.Hdr.SelectedEngine = engine;
            vm.Hdr.PeakDetect = false;
            vm.Strategy.SelectedStrategy = strategy;
            vm.Strategy.Quality.Av1.EnableVarianceBoostChecked = true;
            var view = new OutputSettingsView { DataContext = vm };
            WithWindow(view, _ => AssertOptionHelp(view).Should().BeGreaterThan(10));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SupportingSettingsViews_HaveHelpForAllOptionsAndActions()
    {
        await fixture.Session.Dispatch(() =>
        {
            var views = new Control[]
            {
                new OutputPathView { DataContext = new OutputPathViewModel(Substitute.For<IDialogService>()) { LocationMode = OutputLocationMode.CustomFolder } },
                new PresetBarView { DataContext = new PresetViewModel(Substitute.For<IPresetService>(), Substitute.For<IDialogService>()) },
                new DependencySetupView { DataContext = new DependencySetupViewModel(
                    Substitute.For<IDependencyService>(), Substitute.For<ISettingsService>(),
                    Substitute.For<IDialogService>(), Substitute.For<IMessenger>()) },
                new FormatView { DataContext = new FormatViewModel() },
                new StreamHandlingView { DataContext = new StreamHandlingViewModel() },
                new BitrateView { DataContext = new BitrateViewModel() },
                new FileSizeView { DataContext = new FileSizeViewModel() },
            };
            foreach (var view in views)
                WithWindow(view, _ => AssertOptionHelp(view).Should().BeGreaterThan(0));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LongTooltip_WrapsWithinAReadableWidth()
    {
        await fixture.Session.Dispatch(() =>
        {
            var tooltip = new ToolTip
            {
                Content = string.Join(' ', Enumerable.Repeat("Detailed option guidance with units and tradeoffs.", 8)),
            };
            var option = new CheckBox { Content = "Disabled option", IsEnabled = false };
            ToolTip.SetTip(option, tooltip);
            WithWindow(option, window =>
            {
                ToolTip.GetShowOnDisabled(option).Should().BeTrue();
                ToolTip.SetIsOpen(option, true);
                try
                {
                    tooltip.UpdateLayout();
                    tooltip.Bounds.Width.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(420);
                    tooltip.GetVisualDescendants().OfType<ContentPresenter>()
                        .Should().Contain(p => p.TextWrapping == TextWrapping.Wrap);
                }
                finally
                {
                    ToolTip.SetIsOpen(option, false);
                }
            });
        }, CancellationToken.None);
    }

    private static int AssertOptionHelp(Control view)
    {
        var options = view.GetVisualDescendants().OfType<Control>().Prepend(view)
            .Where(c => c.TemplatedParent is null && c.IsEffectivelyVisible)
            .Where(c => c is ComboBox or Slider or CheckBox or RadioButton
                or TextBox { IsReadOnly: false } or Button { Command: not null }).ToList();
        foreach (var option in options)
        {
            ToolTip.GetTip(option).Should().BeOfType<string>()
                .Which.Should().NotBeNullOrWhiteSpace($"{view.GetType().Name}: {option.GetType().Name} {option.Name} needs option help");
            ToolTip.GetShowOnDisabled(option).Should().BeTrue();
        }
        return options.Count;
    }

    private static void WithWindow(Control content, Action<Window> verify)
    {
        var window = new Window { Content = new ScrollViewer { Content = content }, Width = 1000, Height = 1000 };
        window.Show();
        try
        {
            window.UpdateLayout();
            verify(window);
        }
        finally
        {
            window.Close();
        }
    }
}
