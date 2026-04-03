using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public class ExecutionViewModelTests
{
    private readonly IJobExecutionService _jobService = Substitute.For<IJobExecutionService>();
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();
    private readonly ExecutionViewModel _sut;

    public ExecutionViewModelTests()
    {
        _sut = new ExecutionViewModel(_jobService, _dialogService);
    }

    private static JobContext MakeContext() =>
        new(new TestMediaInfoBuilder().Build(), new OutputSettings());

    [Fact]
    public void CanStart_NullContext_ReturnsFalse()
    {
        _sut.CanStart.Should().BeFalse();
    }

    [Fact]
    public void CanStart_WithValidContext_ReturnsTrue()
    {
        _sut.UpdateJobContext(MakeContext());

        _sut.CanStart.Should().BeTrue();
    }

    [Fact]
    public void CanStart_WithValidationErrors_ReturnsFalse()
    {
        _sut.UpdateJobContext(MakeContext());
        _sut.UpdateHasValidationErrors(true);

        _sut.CanStart.Should().BeFalse();
    }

    [Fact]
    public void UpdateJobContext_TransitionsCanStart()
    {
        _sut.CanStart.Should().BeFalse();

        _sut.UpdateJobContext(MakeContext());

        _sut.CanStart.Should().BeTrue();
    }

    [Fact]
    public void UpdateJobContext_Null_ClearsCanStart()
    {
        _sut.UpdateJobContext(MakeContext());
        _sut.CanStart.Should().BeTrue();

        _sut.UpdateJobContext(null);

        _sut.CanStart.Should().BeFalse();
    }

    [Fact]
    public void Default_IsRunning_IsFalse()
    {
        _sut.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Default_StateText_IsReady()
    {
        _sut.StateText.Should().Be("Ready");
    }
}
