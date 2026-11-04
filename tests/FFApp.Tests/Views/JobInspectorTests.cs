using Avalonia.Controls;
using FFApp.Tests.ViewModels;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class JobInspectorTests(QueueTestSession fixture)
{
    [Theory]
    [InlineData(300, 220)]
    [InlineData(460, 600)]
    public async Task DiagnosticTabs_FillAvailableSpace_WithConsistentAppearance(int width, int height)
    {
        await fixture.Session.Dispatch(() =>
        {
            var item = JobItemDetailsViewModelTests.CreateItem();
            var details = new JobItemDetailsViewModel();
            details.UpdateFromItem(item);
            var view = new JobItemDetailsView { DataContext = details };
            var window = new Window { Content = view, Width = width, Height = height };
            window.Show();
            try
            {
                var tabs = view.FindControl<TabControl>("InspectorTabs")
                    ?? throw new InvalidOperationException("Inspector tabs missing.");
                tabs.SelectedIndex = 1;
                window.UpdateLayout();
                details.SelectedTabIndex.Should().Be(1);
                var command = view.FindControl<TextBox>("CommandTextBox")
                    ?? throw new InvalidOperationException("Command text box missing.");
                var commandBounds = command.Bounds;
                commandBounds.Height.Should().BeGreaterThan(height - 80);
                commandBounds.Width.Should().BeLessThan(width);
                command.IsReadOnly.Should().BeTrue();
                command.AcceptsReturn.Should().BeTrue();

                // The log tab is only laid out if the view follows the view model's tab.
                details.SelectedTabIndex = 2;
                window.UpdateLayout();
                var log = view.FindControl<TextBox>("LogTextBox")
                    ?? throw new InvalidOperationException("Log text box missing.");
                log.Bounds.Size.Should().Be(commandBounds.Size);
                log.FontFamily.Should().Be(command.FontFamily);
                log.FontSize.Should().Be(command.FontSize);
                log.Padding.Should().Be(command.Padding);
                log.IsReadOnly.Should().BeTrue();

                item.AppendLog("Live encoding output");
                log.Text.Should().Contain("Live encoding output");

                details.UpdateFromItem(null);
                window.UpdateLayout();
                tabs.IsVisible.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
