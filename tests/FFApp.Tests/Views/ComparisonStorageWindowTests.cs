using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Controls;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class ComparisonStorageWindowTests(QueueTestSession fixture)
{
    [Fact]
    public async Task BindsUsageAndCacheLimit_AndKeepsLongStatusInsideWindow()
    {
        await fixture.Session.Dispatch(() =>
        {
            var comparisons = Substitute.For<IComparisonService>();
            comparisons.GetCacheUsage().Returns(new ComparisonCacheUsage(4096, 0, 1024));
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
                usage.Text.Should().NotBeNullOrEmpty().And.Be(vm.ComparisonStorageUsage);
                var input = window.FindControl<NoWheelNumericUpDown>("CacheLimitInput") ?? throw new InvalidOperationException();
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
}
