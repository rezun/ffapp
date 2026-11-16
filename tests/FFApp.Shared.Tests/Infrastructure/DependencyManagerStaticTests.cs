using System.Globalization;
using System.Runtime.InteropServices;

using FFApp.Infrastructure;
using FFApp.Core.Models;
using FluentAssertions;

namespace FFApp.Shared.Tests.Infrastructure;

public sealed class DependencyManagerStaticTests
{
    [Theory]
    [InlineData("ffmpeg", "ffmpeg version 9.0.2 Copyright (c) 2000-2026 the FFmpeg developers", true)]
    [InlineData("ffmpeg", "ffmpeg version n9.0.2-4-g1234 Copyright (c) 2000-2026 the FFmpeg developers", true)]
    [InlineData("ffprobe", "ffprobe version 9.0.2-full_build Copyright (c) 2007-2026 the FFmpeg developers", true)]
    [InlineData("ffmpeg", "ffmpeg version 10.0", true)]
    [InlineData("ffmpeg", "ffmpeg version 8.0.1", false)]
    [InlineData("ffprobe", "ffprobe version 6.1", false)]
    [InlineData("ffprobe", "ffmpeg version 9.0.2", false)]
    [InlineData("ffmpeg", "ffmpeg version N-123456-gabcd", false)]
    [InlineData("ffmpeg", "", false)]
    [InlineData("ffmpeg", "some other tool 9.0", false)]
    [InlineData("mkvpropedit", "mkvpropedit v97.0 ('You Don't Have a Clue') 64-bit", true)]
    [InlineData("mkvpropedit", "ffmpeg version 9.0.2", false)]
    public void VersionValidation_RequiresTheExpectedToolAndSupportedRelease(string name, string output, bool valid)
    {
        var status = DependencyManager.ValidateVersionOutput(name, "/test/tool", output);
        status.IsValid.Should().Be(valid);
        if (!valid) status.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    // Captured FFmpeg table entries; current releases use two filter flags, older ones used three.
    [InlineData(" V....D libx264 libx264 H.264 / AVC", "libx264")]
    [InlineData(" .. libplacebo V->V Apply GPU-based filtering using libplacebo", "libplacebo")]
    [InlineData(" ... zscale V->V Apply resizing, colorspace and bit depth conversion", "zscale")]
    [InlineData(" A..... libopus libopus Opus", "libopus")]
    public void ComponentTables_ParseActualEntries(string output, string name) =>
        DependencyManager.ParseComponentNames(output).Should().Equal(name);

    [Theory]
    [InlineData("Unknown filter 'libplacebo'.\nExiting with exit code 0")]
    [InlineData("Filters:\n  T.. = Timeline support\n  .S. = Slice threading\n  ------")]
    [InlineData("libplacebo is not compiled into this build")]
    public void ComponentTables_DoNotMistakeHelpOrErrorsForAvailableFilters(string output) =>
        DependencyManager.ParseComponentNames(output).Should().BeEmpty();

    [Theory]
    // Scoop writes a quoted absolute path; older Scoop releases wrote it unquoted.
    [InlineData("path = \"{0}\"", true)]
    [InlineData("path = {0}\nargs = -hide_banner", true)]
    [InlineData("args = -hide_banner", false)]
    [InlineData("path = \"{0}.missing\"", false)]
    public async Task ScoopShims_ResolveToTheRealExecutable(string shimContent, bool resolves)
    {
        using var directory = new TempDirectory();
        var shims = Directory.CreateDirectory(Path.Combine(directory.Path, "shims")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(directory.Path, "apps", "ffmpeg", "current", "bin")).FullName;
        var launcher = Path.Combine(shims, "ffmpeg.exe");
        var target = Path.Combine(bin, "ffmpeg.exe");
        await File.WriteAllTextAsync(launcher, "");
        await File.WriteAllTextAsync(target, "");
        await File.WriteAllTextAsync(Path.Combine(shims, "ffmpeg.shim"), string.Format(CultureInfo.InvariantCulture, shimContent, target));

        DependencyManager.ResolveShim(launcher).Should().Be(resolves ? target : launcher);
    }

    [Fact]
    public async Task MissingExecutables_ReturnErrorsWithoutThrowing()
    {
        using var directory = new TempDirectory();
        var missing = Path.Combine(directory.Path, "missing");
        var result = await new DependencyManager().DiscoverAsync(new ExecutablePaths(missing, missing, missing));
        result.IsReady.Should().BeFalse();
        result.RequiredToolsError.Should().Contain(missing);
        result.Capabilities.Should().BeNull();
        result.IsLibplaceboAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task CancelledDiscovery_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var discover = () => new DependencyManager().DiscoverAsync(ExecutablePaths.Empty, cancellation.Token);
        await discover.Should().ThrowAsync<OperationCanceledException>();
    }
    [Fact]
    public void FindExecutable_ReturnsNullWhenMissing()
    {
        using var tempDir = new TempDirectory();

        DependencyManager.FindExecutable("definitely-not-here", [tempDir.Path]).Should().BeNull();
    }

    [Fact]
    public void FindExecutable_ReturnsMatchFromEarliestDirectory()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tool.exe" : "tool";
        var firstPath = Path.Combine(first.Path, fileName);
        var secondPath = Path.Combine(second.Path, fileName);
        File.WriteAllText(firstPath, string.Empty);
        File.WriteAllText(secondPath, string.Empty);

        DependencyManager.FindExecutable("tool", [first.Path, second.Path]).Should().Be(firstPath);
    }

    [Fact]
    public void EnumerateSearchDirectories_ListsPathEntriesThenFallbacks_EachOnce()
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        DependencyManager.EnumerateSearchDirectories().Should().Equal(
            pathEntries.Concat(DependencyManager.GetFallbackDirectories()).Distinct(StringComparer.Ordinal));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
