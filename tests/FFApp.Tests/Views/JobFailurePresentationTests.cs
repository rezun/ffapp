using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.Tests.ViewModels;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class JobFailurePresentationTests(QueueTestSession fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishedCallback_UpdatesFailureDetailsAndQueueCount(bool withoutResult)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var queue = Substitute.For<IQueueService>();
            var dialogs = Substitute.For<IDialogService>();
            var sources = new SourceListPanelViewModel(
                Substitute.For<IMediaProbeService>(), dialogs, Substitute.For<IMessenger>());
            var panel = new JobListPanelViewModel(queue,
                Substitute.For<ICommandBuilder>(), Substitute.For<IValidationService>(), dialogs,
                sources, new VideoPreviewViewModel(null), new OutputPathViewModel(dialogs));
            var item = JobFailureViewModelTests.CreateItem();
            panel.Jobs.Add(item);
            panel.SelectedJob = item;
            var finished = new QueueItem(item.Id, item.QueuedJob, JobState.Failed,
                Result: withoutResult ? null : JobFailureViewModelTests.Result(item, JobState.Failed),
                FailureMessage: withoutResult ? "Could not start FFmpeg" : null);
            queue.ExecuteAllAsync(Arg.Any<Action<QueueItem>?>(), Arg.Any<Action<QueueItem>?>(),
                    Arg.Any<Action<QueueItem>?>(), Arg.Any<Action<QueueItem, string>?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    call.ArgAt<Action<QueueItem>?>(2)?.Invoke(finished);
                    return Task.FromResult<IReadOnlyList<QueueItem>>([finished]);
                });

            await panel.StartCommand.ExecuteAsync(null);
            // Completion is posted by the execution callback; process it after Start's finally block.
            Dispatcher.UIThread.RunJobs();

            item.State.Should().Be(JobState.Failed);
            panel.Details.HasFailure.Should().BeTrue();
            panel.Details.Failure?.Message.Should().Contain(withoutResult ? "Could not start FFmpeg" : "44.1 kHz");
            panel.SummaryText.Should().Be("1 jobs: 0 pending, 0 done, 1 failed");
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureCauseAndSuggestion_AreVisibleWithoutExpandingLogOrCommand(bool dark)
    {
        await fixture.Session.Dispatch(() =>
        {
            var item = JobFailureViewModelTests.CreateItem();
            var details = new JobItemDetailsViewModel();
            details.UpdateFromItem(item);
            var view = new JobItemDetailsView { DataContext = details };
            var window = new Window
            {
                Content = view, Width = 300, Height = 220,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            };
            window.Show();
            try
            {
                item.UpdateResult(JobFailureViewModelTests.Result(item, JobState.Failed));
                window.UpdateLayout();

                var panel = view.FindControl<Border>("FailurePanel")
                    ?? throw new InvalidOperationException("Failure panel missing.");
                var message = view.FindControl<SelectableTextBlock>("FailureMessage")
                    ?? throw new InvalidOperationException("Failure message missing.");
                var hint = view.FindControl<SelectableTextBlock>("FailureSuggestion")
                    ?? throw new InvalidOperationException("Failure suggestion missing.");
                var command = view.FindControl<Expander>("CommandExpander")
                    ?? throw new InvalidOperationException("Command expander missing.");

                panel.IsVisible.Should().BeTrue();
                message.Text.Should().Contain("44.1 kHz");
                hint.Text.Should().Contain("48 kHz");
                command.IsExpanded.Should().BeFalse();
                details.IsLogExpanded.Should().BeFalse();
                var hintPosition = hint.TranslatePoint(default, window)
                    ?? throw new InvalidOperationException("Suggestion was not laid out.");
                hintPosition.Y.Should().BeGreaterThanOrEqualTo(0);
                (hintPosition.Y + hint.Bounds.Height).Should().BeLessThanOrEqualTo(window.Bounds.Height,
                    "the suggestion must be readable in a compact details panel without scrolling");

                item.ResetToPending();
                window.UpdateLayout();
                panel.IsVisible.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
