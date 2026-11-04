using Avalonia;
using Avalonia.Controls;
using FFApp.Views;
using FluentAssertions;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class WorkspaceViewTests(QueueTestSession fixture)
{
    [Fact]
    public async Task ComparisonButton_IsRightAlignedBesidePresets()
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
            actions.Measure(new Size(1000, 100));
            actions.Arrange(new Rect(0, 0, 1000, actions.DesiredSize.Height));
            button.Bounds.Right.Should().BeApproximately(actions.Bounds.Width, 0.5,
                "the comparison button should sit at the right edge of the preset row");
        }, CancellationToken.None);
    }
}
