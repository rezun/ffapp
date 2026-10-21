using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Services;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class TranscodingMenuTests(QueueTestSession fixture)
{
    [Fact]
    public async Task PriorityMenu_ReflectsPreferenceAndUpdatesItWhenToggled()
    {
        await fixture.Session.Dispatch(() =>
        {
            var preferences = new TranscodingPreferences();
            var vm = new MainWindowViewModel(
                Substitute.For<ISettingsService>(), Substitute.For<IDependencyService>(),
                new ExecutablePathsConfig(), Substitute.For<IMessenger>(),
                () => throw new InvalidOperationException("Not needed in this test."),
                Substitute.For<IServiceScopeFactory>(), Substitute.For<IFfmpegCapabilityService>(),
                preferences);
            var window = new MainWindow { DataContext = vm };
            var menu = NativeMenu.GetMenu(window)
                ?? throw new InvalidOperationException("Missing main menu.");
            var submenu = menu.Items.OfType<NativeMenuItem>().Single(i => Equals(i.Header, "Transcoding")).Menu
                ?? throw new InvalidOperationException("Missing transcoding menu.");
            var item = submenu.Items.OfType<NativeMenuItem>().Single();

            // The macOS exporter disables items without a command or click handler,
            // even when IsEnabled is true and IsChecked is bound.
            item.IsEnabled.Should().BeTrue();
            item.Command.Should().NotBeNull();
            item.Command?.CanExecute(item.CommandParameter).Should().BeTrue();
            item.IsChecked.Should().BeFalse();
            vm.UseLowPriorityTranscoding = true;
            item.IsChecked.Should().BeTrue();
            var nativeItem = (INativeMenuItemExporterEventsImplBridge)item;
            nativeItem.RaiseClicked();
            item.IsChecked.Should().BeFalse();
            preferences.UseLowPriority.Should().BeFalse();
            vm.LoadedSettings?.UseLowPriorityTranscoding.Should().BeFalse();

            nativeItem.RaiseClicked();
            item.IsChecked.Should().BeTrue();
            preferences.UseLowPriority.Should().BeTrue();
            vm.LoadedSettings?.UseLowPriorityTranscoding.Should().BeTrue();
        }, CancellationToken.None);
    }
}
