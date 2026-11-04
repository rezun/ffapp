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
    public async Task DeleteComparison_ClosesWindow()
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
            }
            finally { if (comparison.IsVisible) await comparison.CloseSavedAsync(); }
            return true;
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
