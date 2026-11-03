using FFApp.Infrastructure;
using FFApp.Infrastructure.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFApp.Shared.Tests.Infrastructure;

public sealed class JobLogStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 14, 22, 5, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ffapp-joblogs-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private JobLogStore CreateSut(string? directory = null) => new(
        new JobLogOptions(directory ?? _directory, TimeSpan.FromDays(30), "FFApp 1.2.3"),
        new FixedTimeProvider(Now),
        NullLogger<JobLogStore>.Instance);

    [Fact]
    public void Start_WritesHeaderAndLinesToANewFile()
    {
        string path;
        using (var log = CreateSut().Start("Movie (2026).mkv"))
        {
            log.WriteLine("frame=  100 fps=50");
            path = log.FilePath!;
        }

        Path.GetFileName(path).Should().Be("2026-11-03_14-22-05_Movie (2026).mkv.log");
        File.ReadAllLines(path).Should().Equal(
            "# FFApp 1.2.3 job log",
            "Job:       Movie (2026).mkv",
            "Started:   2026-11-03 14:22:05 +00:00",
            "frame=  100 fps=50");
    }

    [Fact]
    public void Start_SameNameInTheSameSecond_CreatesSeparateFiles()
    {
        var sut = CreateSut();

        using var first = sut.Start("out.mp4");
        using var second = sut.Start("out.mp4");

        second.FilePath.Should().NotBe(first.FilePath);
        Path.GetFileName(second.FilePath).Should().Be("2026-11-03_14-22-05_out.mp4-2.log");
    }

    [Fact]
    public void Start_FileIsReadableWhileTheJobRuns()
    {
        using var log = CreateSut().Start("running.mkv");
        log.WriteLine("still encoding");

        File.ReadAllText(log.FilePath!).Should().Contain("still encoding");
    }

    [Fact]
    public void Start_DeletesExpiredLogsOnce()
    {
        Directory.CreateDirectory(_directory);
        var expired = Path.Combine(_directory, "old.log");
        var recent = Path.Combine(_directory, "recent.log");
        var other = Path.Combine(_directory, "notes.txt");
        foreach (var file in new[] { expired, recent, other })
            File.WriteAllText(file, "");
        File.SetLastWriteTimeUtc(expired, Now.UtcDateTime.AddDays(-31));
        File.SetLastWriteTimeUtc(recent, Now.UtcDateTime.AddDays(-29));
        File.SetLastWriteTimeUtc(other, Now.UtcDateTime.AddDays(-90));

        using var _ = CreateSut().Start("job.mkv");

        File.Exists(expired).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
        File.Exists(other).Should().BeTrue("only job logs are pruned");
    }

    [Fact]
    public void Start_WhenTheDirectoryCannotBeCreated_ReturnsADiscardingLog()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "file-not-folder");
        File.WriteAllText(blocker, "");

        using var log = CreateSut(Path.Combine(blocker, "jobs")).Start("job.mkv");

        log.FilePath.Should().BeNull();
        log.Invoking(l => l.WriteLine("ignored")).Should().NotThrow();
    }

    [Theory]
    [InlineData("a/b\\c:d.mkv", "a_b_c_d.mkv")]
    [InlineData("   ", "job")]
    [InlineData("tab\there", "tab_here")]
    public void SafeName_ReplacesCharactersThatBreakFileNames(string name, string expected) =>
        JobLogStore.SafeName(name).Should().Be(expected);

    [Fact]
    public void SafeName_LimitsTheLength() =>
        JobLogStore.SafeName(new string('x', 300)).Should().HaveLength(80);

    [Fact]
    public void WriteLine_IsSafeFromSeveralThreads()
    {
        string path;
        using (var log = CreateSut().Start("parallel.mkv"))
        {
            Parallel.For(0, 200, i => log.WriteLine($"line {i}"));
            path = log.FilePath!;
        }

        File.ReadAllLines(path).Count(line => line.StartsWith("line ", StringComparison.Ordinal)).Should().Be(200);
    }

    [Fact]
    public void LogDirectory_SeparatesAppsAndIsRooted()
    {
        var ffapp = AppPaths.LogDirectory(new AppIdentity("FFApp", "ffapp"));
        var ffrux = AppPaths.LogDirectory(new AppIdentity("FFRux", "ffrux"));

        Path.IsPathRooted(ffapp).Should().BeTrue();
        ffrux.Should().NotBe(ffapp);
        ffapp.Should().NotBe(AppPaths.AppDataDirectory(new AppIdentity("FFApp", "ffapp")));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
