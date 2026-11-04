using FFApp.Core.Models;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class EncodingStrategyViewModelTests
{
    private readonly EncodingStrategyViewModel _sut = new();

    [Theory]
    [InlineData(EncodingStrategy.TargetQuality, EncodingStrategy.TargetQuality, typeof(QualityViewModel))]
    [InlineData(EncodingStrategy.TargetBitrate, EncodingStrategy.TargetQuality, typeof(QualityViewModel))]
    [InlineData(EncodingStrategy.TargetQuality, EncodingStrategy.TargetBitrate, typeof(BitrateViewModel))]
    [InlineData(EncodingStrategy.TargetQuality, EncodingStrategy.TargetFileSize, typeof(FileSizeViewModel))]
    public void SelectedStrategy_ActivatesItsViewModel(EncodingStrategy from, EncodingStrategy to, Type expected)
    {
        _sut.SelectedStrategy = from;
        _sut.SelectedStrategy = to;

        _sut.ActiveStrategyViewModel.Should().BeOfType(expected);
    }
}
