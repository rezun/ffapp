using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public class JobListPanelViewModelTests
{
    private readonly IQueueService _queueService = Substitute.For<IQueueService>();
    private readonly ICommandBuilder _commandBuilder = Substitute.For<ICommandBuilder>();
    private readonly IValidationService _validationService = Substitute.For<IValidationService>();
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();
    private readonly JobListPanelViewModel _sut;

    public JobListPanelViewModelTests()
    {
        _sut = new JobListPanelViewModel(
            _queueService, _commandBuilder, _validationService, _dialogService);
    }

    private static JobContext MakeContext() =>
        new(new TestMediaInfoBuilder().Build(), new OutputSettings());

    [Fact]
    public void Default_IsRunning_IsFalse()
    {
        _sut.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Default_SummaryText_IsNoJobs()
    {
        _sut.SummaryText.Should().Be("No jobs.");
    }

    [Fact]
    public void Default_HasValidationErrors_IsFalse()
    {
        _sut.HasValidationErrors.Should().BeFalse();
    }

    [Fact]
    public void UpdateHasValidationErrors_SetsProperty()
    {
        _sut.UpdateHasValidationErrors(true);
        _sut.HasValidationErrors.Should().BeTrue();

        _sut.UpdateHasValidationErrors(false);
        _sut.HasValidationErrors.Should().BeFalse();
    }

    [Fact]
    public void UpdateJobContext_Null_IsAccepted()
    {
        _sut.UpdateJobContext(MakeContext());
        _sut.UpdateJobContext(null);
        // Should not throw
    }

    [Fact]
    public void Default_Jobs_IsEmpty()
    {
        _sut.Jobs.Should().BeEmpty();
    }

    [Fact]
    public void Default_IsPaused_IsFalse()
    {
        _sut.IsPaused.Should().BeFalse();
    }
}
