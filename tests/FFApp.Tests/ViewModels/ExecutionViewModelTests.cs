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

    [Fact]
    public void CanStart_NoMediaInfo_ReturnsFalse()
    {
        _sut.UpdateOutputSettings(new OutputSettings());

        _sut.CanStart.Should().BeFalse();
    }

    [Fact]
    public void CanStart_NoSettings_ReturnsFalse()
    {
        var mediaInfo = new TestMediaInfoBuilder().Build();
        _sut.UpdateSourceInfo(mediaInfo);

        _sut.CanStart.Should().BeFalse();
    }

    [Fact]
    public void CanStart_BothPresent_ReturnsTrue()
    {
        var mediaInfo = new TestMediaInfoBuilder().Build();
        _sut.UpdateSourceInfo(mediaInfo);
        _sut.UpdateOutputSettings(new OutputSettings());

        _sut.CanStart.Should().BeTrue();
    }

    [Fact]
    public void UpdateSourceInfo_WhenSettingsAlreadySet_SetsCanStart()
    {
        _sut.UpdateOutputSettings(new OutputSettings());
        _sut.CanStart.Should().BeFalse();

        var mediaInfo = new TestMediaInfoBuilder().Build();
        _sut.UpdateSourceInfo(mediaInfo);

        _sut.CanStart.Should().BeTrue();
    }

    [Fact]
    public void UpdateOutputSettings_WhenMediaInfoAlreadySet_SetsCanStart()
    {
        var mediaInfo = new TestMediaInfoBuilder().Build();
        _sut.UpdateSourceInfo(mediaInfo);
        _sut.CanStart.Should().BeFalse();

        _sut.UpdateOutputSettings(new OutputSettings());

        _sut.CanStart.Should().BeTrue();
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
