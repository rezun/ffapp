using System.Reflection;
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
            var (window, vm) = CreateWindow(preferences);
            var item = Submenu(window, "Transcoding").Items.OfType<NativeMenuItem>().Single();

            item.IsChecked.Should().BeFalse();
            vm.UseLowPriorityTranscoding = true;
            item.IsChecked.Should().BeTrue();
            var nativeItem = (INativeMenuItemExporterEventsImplBridge)item;
            nativeItem.RaiseClicked();
            item.IsChecked.Should().BeFalse();
            preferences.UseLowPriority.Should().BeFalse();
            (vm.LoadedSettings?.UseLowPriorityTranscoding).Should().BeFalse();

            nativeItem.RaiseClicked();
            item.IsChecked.Should().BeTrue();
            preferences.UseLowPriority.Should().BeTrue();
            (vm.LoadedSettings?.UseLowPriorityTranscoding).Should().BeTrue();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EnabledMenuItems_AllHaveACommandOrClickHandler()
    {
        await fixture.Session.Dispatch(() =>
        {
            var (window, _) = CreateWindow(new TranscodingPreferences());
            // Avalonia's private API (stripped from the reference assembly) that the exporter checks.
            var hasClickHandlers = typeof(NativeMenuItem).GetProperty("HasClickHandlers",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("NativeMenuItem.HasClickHandlers no longer exists.");
            var root = NativeMenu.GetMenu(window) ?? throw new InvalidOperationException("Missing main menu.");
            var leaves = Leaves(root).Where(item => item is not NativeMenuItemSeparator && item.IsEnabled).ToList();

            // The macOS exporter disables items without a command or click handler,
            // even when IsEnabled is true.
            leaves.Select(item => item.Header).Should().Contain(["Low CPU priority", "Open logs folder"]);
            foreach (var item in leaves)
                (item.Command is not null || hasClickHandlers.GetValue(item) is true)
                    .Should().BeTrue($"menu item '{item.Header}' must do something when chosen");
        }, CancellationToken.None);
    }

    private static (MainWindow Window, MainWindowViewModel ViewModel) CreateWindow(TranscodingPreferences preferences)
    {
        var vm = new MainWindowViewModel(
            Substitute.For<ISettingsService>(), Substitute.For<IDependencyService>(),
            new ExecutablePathsConfig(), Substitute.For<IMessenger>(),
            () => throw new InvalidOperationException("Not needed in this test."),
            Substitute.For<IServiceScopeFactory>(), Substitute.For<IFfmpegCapabilityService>(),
            preferences);
        return (new MainWindow { DataContext = vm }, vm);
    }

    private static NativeMenu Submenu(MainWindow window, string header) =>
        (NativeMenu.GetMenu(window) ?? throw new InvalidOperationException("Missing main menu."))
            .Items.OfType<NativeMenuItem>().Single(i => Equals(i.Header, header)).Menu
        ?? throw new InvalidOperationException($"Missing {header} menu.");

    private static IEnumerable<NativeMenuItem> Leaves(NativeMenu menu) =>
        menu.Items.OfType<NativeMenuItem>()
            .SelectMany(item => item.Menu is { } submenu ? Leaves(submenu) : [item]);
}
