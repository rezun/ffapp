using FFApp.Core.Models;
using FFApp.Services;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public sealed class OutputPathViewModelTests
{
    private static readonly string FirstSource = Path.Combine("/rips", "Interstellar UHD", "Interstellar UHD.mp4");
    private static readonly string SecondSource = Path.Combine("/rips", "John Wick 3", "John Wick Kapitel 3_t1.mp4");

    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly OutputPathViewModel _sut;

    public OutputPathViewModelTests()
    {
        _sut = new OutputPathViewModel(_dialogs);
        _sut.UpdateContainer(OutputContainer.Mkv);
    }

    private static SourceMediaInfo Media(string path) =>
        new TestMediaInfoBuilder().WithSourcePath(path).Build();

    [Fact]
    public void DefaultMode_WritesNextToEachSelectedSource()
    {
        _sut.LocationMode.Should().Be(OutputLocationMode.SourceFolder);

        _sut.UpdateFromSource(Media(FirstSource));
        _sut.UpdateFromSource(Media(SecondSource));

        _sut.GetEffectiveDirectory().Should().BeNull();
        _sut.ResolvedPath.Should().Be(
            Path.Combine("/rips", "John Wick 3", "John Wick Kapitel 3_t1.mkv"));
    }

    [Fact]
    public void CustomFolder_IsUsedForEverySource()
    {
        _sut.CustomFolder = "/encodes";
        _sut.LocationMode = OutputLocationMode.CustomFolder;

        _sut.UpdateFromSource(Media(FirstSource));
        _sut.ResolvedPath.Should().Be(Path.Combine("/encodes", "Interstellar UHD.mkv"));

        _sut.UpdateFromSource(Media(SecondSource));
        _sut.ResolvedPath.Should().Be(Path.Combine("/encodes", "John Wick Kapitel 3_t1.mkv"));
        _sut.GetEffectiveDirectory().Should().Be("/encodes");
    }

    [Fact]
    public void CustomModeWithoutFolder_FallsBackToSourceFolder()
    {
        _sut.LocationMode = OutputLocationMode.CustomFolder;
        _sut.UpdateFromSource(Media(FirstSource));

        _sut.IsCustomFolder.Should().BeTrue();
        _sut.GetEffectiveDirectory().Should().BeNull();
        _sut.ResolvedPath.Should().Be(Path.Combine("/rips", "Interstellar UHD", "Interstellar UHD.mkv"));
    }

    [Fact]
    public void PresetName_IsAppendedAndAnnounced()
    {
        _sut.UpdateFromSource(Media(FirstSource));
        var changed = new List<string?>();
        _sut.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _sut.UpdatePresetName("AV1 CRF24");

        changed.Should().Contain(nameof(OutputPathViewModel.PresetName));
        _sut.ResolvedPath.Should().Be(
            Path.Combine("/rips", "Interstellar UHD", "Interstellar UHD - AV1 CRF24.mkv"));
    }

    [Fact]
    public async Task BrowseDirectory_SwitchesToCustomFolder()
    {
        _dialogs.OpenFolderAsync(Arg.Any<string>()).Returns("/encodes");

        await _sut.BrowseDirectoryCommand.ExecuteAsync(null);

        _sut.LocationMode.Should().Be(OutputLocationMode.CustomFolder);
        _sut.CustomFolder.Should().Be("/encodes");
    }

    [Fact]
    public async Task BrowseDirectory_Canceled_KeepsCurrentMode()
    {
        _dialogs.OpenFolderAsync(Arg.Any<string>()).Returns((string?)null);

        await _sut.BrowseDirectoryCommand.ExecuteAsync(null);

        _sut.LocationMode.Should().Be(OutputLocationMode.SourceFolder);
        _sut.CustomFolder.Should().BeEmpty();
    }

    [Theory]
    [InlineData(OutputLocationMode.SourceFolder, null)]
    [InlineData(OutputLocationMode.SourceFolder, "/kept/for/later")]
    [InlineData(OutputLocationMode.CustomFolder, "/encodes")]
    public void RestoreLocation_RoundTripsThroughLocation(OutputLocationMode mode, string? folder)
    {
        var location = new OutputLocation(mode, folder);

        _sut.RestoreLocation(location);

        _sut.Location.Should().Be(location);
    }
}
