using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using FFApp.Tests.Builders;
using FFApp.Views;
using FluentAssertions;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class ComparisonWindowIntegrationTests(QueueTestSession fixture)
{
    [Fact]
    public async Task DeleteComparison_ClosesWindowWithoutRecreatingSavedComparison()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var harness = new ComparisonTestHarness();
            await harness.LoadAsync();
            var comparison = new ComparisonWindow { DataContext = harness.ViewModel };
            comparison.Show();
            try
            {
                await harness.ViewModel.RunAllCommand.ExecuteAsync(null);
                await harness.ViewModel.DeleteComparisonCommand.ExecuteAsync(null);
                comparison.IsVisible.Should().BeFalse();
                Directory.GetFiles(Path.Combine(harness.DirectoryPath, "comparison"), "*.json").Should().BeEmpty();
            }
            finally { if (comparison.IsVisible) await comparison.CloseSavedAsync(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StorageSettingsWindow_BindsUsageAndCacheLimit()
    {
        await fixture.Session.Dispatch(() =>
        {
            var comparisons = Substitute.For<IComparisonService>();
            comparisons.GetCacheUsage().Returns(new FFApp.Core.Models.ComparisonCacheUsage(4096, 0, 1024));
            var vm = new MainWindowViewModel(Substitute.For<ISettingsService>(), Substitute.For<IDependencyService>(),
                new ExecutablePathsConfig(), new WeakReferenceMessenger(),
                () => throw new InvalidOperationException("Not needed."),
                Substitute.For<IServiceScopeFactory>(), Substitute.For<IFfmpegCapabilityService>(),
                new TranscodingPreferences(), comparisonService: comparisons);
            vm.RefreshComparisonStorageCommand.Execute(null);
            var window = new ComparisonStorageWindow { DataContext = vm };
            window.Show();
            try
            {
                window.UpdateLayout();
                var usage = window.FindControl<TextBlock>("StorageUsage") ?? throw new InvalidOperationException();
                usage.Text.Should().Contain($"{4d:F1} KiB");
                vm.DeleteAllClipsLabel.Should().Contain($"{3d:F1} KiB");
                var input = window.FindControl<FFApp.Controls.NoWheelNumericUpDown>("CacheLimitInput") ?? throw new InvalidOperationException();
                input.Value = 8;
                vm.ComparisonCacheLimitGiB.Should().Be(8);
                vm.ComparisonStorageStatus = "Limit saved. Oldest unused clips are removed first. Open comparisons may temporarily exceed the limit.";
                window.UpdateLayout();
                var status = window.FindControl<TextBlock>("StorageStatus") ?? throw new InvalidOperationException();
                var position = status.TranslatePoint(new Point(0, status.Bounds.Height), window);
                position.Should().NotBeNull();
                position?.Y.Should().BeLessThan(window.ClientSize.Height);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ComparisonButton_IsRightAlignedBesidePresets_WithoutEllipsis()
    {
        await fixture.Session.Dispatch(() =>
        {
            var workspace = new WorkspaceView();
            var button = workspace.FindControl<Button>("OpenComparisonButton")
                ?? throw new InvalidOperationException("Missing comparison button.");
            var actions = workspace.FindControl<Grid>("PresetActions")
                ?? throw new InvalidOperationException("Missing preset actions.");
            actions.Children.Should().Contain(button);
            actions.Children.Should().ContainSingle(c => c is PresetBarView);
            button.Content.Should().Be("Compare encodes");
            actions.Measure(new Size(1000, 100));
            actions.Arrange(new Rect(0, 0, 1000, actions.DesiredSize.Height));
            button.Bounds.Right.Should().BeApproximately(actions.Bounds.Width, 0.5,
                "the comparison button should sit at the right edge of the preset row");
            // The outer dock panel now reserves vertical space only for running-job status.
            var root = (DockPanel)(workspace.Content ?? throw new InvalidOperationException("Missing workspace content."));
            root.Children.OfType<Border>().Should().OnlyContain(b => DockPanel.GetDock(b) == Dock.Bottom);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeQuit_UsesMainSavePipeline_AndHonorsComparisonCloseFailure(bool canClose)
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var harness = new ComparisonTestHarness();
            await harness.LoadAsync();
            var main = new MainWindow();
            main.Show();
            var comparison = new ComparisonWindow { DataContext = harness.ViewModel };
            comparison.Show(main);
            var windows = Substitute.For<IComparisonWindowService>();
            var settings = Substitute.For<ISettingsService>();
            var vm = new MainWindowViewModel(settings, Substitute.For<IDependencyService>(),
                new ExecutablePathsConfig(), new WeakReferenceMessenger(),
                () => throw new InvalidOperationException("Not needed."),
                Substitute.For<IServiceScopeFactory>(), Substitute.For<IFfmpegCapabilityService>(),
                new TranscodingPreferences(), windows);
            main.DataContext = vm;
            windows.CloseAllAsync().Returns(async _ => canClose && await comparison.CloseSavedAsync());
            try
            {
                var request = new ShutdownRequestedEventArgs();
                await App.HandleShutdownRequestedAsync(main, request);
                request.Cancel.Should().BeTrue();
                await windows.Received(1).CloseAllAsync();
                main.IsVisible.Should().Be(!canClose);
                comparison.IsVisible.Should().Be(!canClose);
                if (canClose)
                {
                    await settings.Received(1).SaveAsync(Arg.Any<FFApp.Core.Models.AppSettings>());
                    // OnMainWindowClose requests lifetime shutdown again. It must be
                    // allowed through after persistence, otherwise the process stays alive.
                    var finalRequest = new ShutdownRequestedEventArgs();
                    await App.HandleShutdownRequestedAsync(main, finalRequest);
                    finalRequest.Cancel.Should().BeFalse();
                    await windows.Received(1).CloseAllAsync();
                }
                else
                    await settings.DidNotReceive().SaveAsync(Arg.Any<FFApp.Core.Models.AppSettings>());
            }
            finally
            {
                if (comparison.IsVisible) await comparison.CloseSavedAsync();
                main.DataContext = null;
                main.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CloseWindowMenu_UsesCommandWAndSavesOnlyComparison()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var harness = new ComparisonTestHarness();
            await harness.LoadAsync();
            var main = new Window();
            main.Show();
            var comparison = new ComparisonWindow { DataContext = harness.ViewModel };
            comparison.Show(main);
            try
            {
                var menu = NativeMenu.GetMenu(comparison)
                    ?? throw new InvalidOperationException("Missing comparison menu.");
                var file = menu.Items.OfType<NativeMenuItem>().Single().Menu
                    ?? throw new InvalidOperationException("Missing File menu.");
                var close = file.Items.OfType<NativeMenuItem>().Single();
                close.Gesture.Should().Be(new KeyGesture(Key.W,
                    OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control));
                var closed = new TaskCompletionSource();
                comparison.Closed += (_, _) => closed.SetResult();
                harness.ViewModel.Samples[0].Name = "Saved on close";
                ((INativeMenuItemExporterEventsImplBridge)close).RaiseClicked();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                comparison.IsVisible.Should().BeFalse();
                main.IsVisible.Should().BeTrue();
                var saved = await harness.Service.LoadAsync(harness.Context);
                saved.Samples[0].Name.Should().Be("Saved on close");
            }
            finally
            {
                if (comparison.IsVisible) await comparison.CloseSavedAsync();
                main.Close();
            }
            return true;
        }, CancellationToken.None);
    }
}
