using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using FluentAssertions;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class ScrollbarStyleTests(QueueTestSession fixture)
{
    [Fact]
    public async Task GlobalStyle_KeepsScrollbarsExpanded()
    {
        await fixture.Session.Dispatch(() =>
        {
            var viewer = new ScrollViewer { Content = new Border { Height = 1200 } };
            var window = new Window { Content = viewer, Width = 300, Height = 200 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var bar = viewer.GetVisualDescendants().OfType<ScrollBar>()
                    .Single(bar => bar.IsVisible);

                bar.AllowAutoHide.Should().BeFalse();
                bar.IsExpanded.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
