using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure.Serialization;

namespace FFApp.Infrastructure;

public sealed class ComparisonEncodingException(string message) : InvalidOperationException(message);

public sealed class ComparisonService(
    ICommandBuilder commandBuilder,
    IJobExecutionService execution,
    IValidationService validation,
    JsonSerializerOptions jsonOptions,
    string? directory = null) : IComparisonService
{
    private readonly string _directory = directory ?? Path.Combine(AppPaths.AppDataDirectory(), "comparisons");
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string Fingerprint(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The comparison source is no longer available.", path);
        return Hash($"{Path.GetFullPath(path)}|{file.Length}|{new DateTimeOffset(file.LastWriteTimeUtc).UtcTicks}");
    }

    private string SessionPath(string path) => Path.Combine(_directory, Hash(Path.GetFullPath(path)) + ".json");

    public async Task<ComparisonSession> LoadAsync(JobContext context, CancellationToken ct = default)
    {
        var media = context.MediaInfo;
        if (media.PrimaryVideoStream is null || media.DurationSeconds is not > 0
            || !double.IsFinite(media.DurationSeconds.Value))
            throw new InvalidOperationException("A video with a known duration is required for comparison.");
        var fingerprint = Fingerprint(media.SourcePath);
        var path = SessionPath(media.SourcePath);
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            var saved = ComparisonSerializer.Deserialize(document.RootElement, jsonOptions);
            if (string.Equals(saved.SourceFingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(saved.SourcePath, media.SourcePath, StringComparison.Ordinal))
            {
                ValidateSession(saved, media.DurationSeconds.Value);
                return saved;
            }
        }

        var start = context.TrimStartSeconds ?? 0;
        var end = context.TrimEndSeconds ?? media.DurationSeconds.Value;
        var session = new ComparisonSession(media.SourcePath, fingerprint, context.Settings,
            start, end, context.Crop, ComparisonPlan.Distribute(start, end),
            [new ComparisonVariant(Guid.NewGuid().ToString("N"), "Baseline", context.Settings)], [], context.SelectedAudioStreamIndices);
        ValidateSession(session, media.DurationSeconds.Value);
        return session;
    }

    internal static void ValidateSession(ComparisonSession session, double duration)
    {
        if (!double.IsFinite(session.RangeStart) || !double.IsFinite(session.RangeEnd)
            || session.RangeStart < 0 || session.RangeEnd > duration || session.RangeEnd <= session.RangeStart
            || session.Samples.IsDefaultOrEmpty || session.Variants.IsDefaultOrEmpty || session.Results.IsDefault
            || session.Samples.Length > ComparisonPlan.MaximumSampleCount
            || session.Samples.Any(s => !ComparisonPlan.IsValid(s, session.RangeStart, session.RangeEnd))
            || session.Samples.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != session.Samples.Length
            || session.Variants.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() != session.Variants.Length)
            throw new SettingsDecodeException("The saved comparison contains invalid samples or variants.");
    }

    /// <summary>
    /// Writes a replacement manifest and atomically renames it. Cancellation before
    /// the rename removes the temporary manifest and preserves the previous session.
    /// </summary>
    public async Task SaveAsync(ComparisonSession session, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_directory);
        var target = SessionPath(session.SourcePath);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, ComparisonSerializer.Serialize(session, jsonOptions).ToJsonString(jsonOptions), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public string ClipPath(string cacheKey)
    {
        var stem = Path.GetFileNameWithoutExtension(cacheKey);
        var extension = Path.GetExtension(cacheKey);
        if (stem.Length != 64 || stem.Any(c => !char.IsAsciiHexDigit(c))
            || !string.Equals(Path.GetFileName(cacheKey), cacheKey, StringComparison.Ordinal)
            || extension is not (".mp4" or ".mkv" or ".webm"))
            throw new ArgumentException("Invalid comparison cache key.", nameof(cacheKey));
        return Path.Combine(_directory, "clips", cacheKey);
    }

    public string CacheKey(ComparisonSession session, SourceMediaInfo media,
        ComparisonSample sample, ComparisonVariant variant)
    {
        var extension = DefaultCommandBuilder.ContainerExtension(variant.Settings.Container);
        var job = ComparisonPlan.BuildJob(session, media, sample, variant, Path.Combine(_directory, "signature" + extension));
        var commands = commandBuilder.Build(job, media);
        var signatures = commands.Select(c => new
        {
            c.ExecutablePath, c.Arguments,
            Binary = File.Exists(c.ExecutablePath) ? Fingerprint(c.ExecutablePath) : c.ExecutablePath,
        });
        return Hash("comparison-v1|" + session.SourceFingerprint + "|"
            + JsonSerializer.Serialize(signatures, jsonOptions)) + extension;
    }

    /// <inheritdoc />
    public async Task<ComparisonClip> EncodeAsync(ComparisonSession session, SourceMediaInfo media,
        ComparisonSample sample, ComparisonVariant variant,
        Action<JobProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(Fingerprint(session.SourcePath), session.SourceFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The source has changed. Reopen the comparison to start a new session.");
        var key = CacheKey(session, media, sample, variant);
        var output = ClipPath(key);
        var offset = sample.StartSeconds - Math.Max(session.RangeStart, sample.StartSeconds - ComparisonPlan.PreRollSeconds);
        var previous = session.Results.FirstOrDefault(r => string.Equals(r.CacheKey, key, StringComparison.Ordinal));
        var cached = new FileInfo(output);
        if (previous is not null && cached.Exists && cached.Length == previous.SizeBytes && cached.Length > 0)
            return new ComparisonClip(output, offset, sample.DurationSeconds, cached.Length, previous.ElapsedSeconds, key, true);

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? _directory);
        var temporary = Path.Combine(Path.GetDirectoryName(output) ?? _directory,
            Guid.NewGuid().ToString("N") + ".partial" + Path.GetExtension(output));
        try
        {
            var job = ComparisonPlan.BuildJob(session, media, sample, variant, temporary);
            var issues = validation.Validate(ComparisonPlan.VideoOnlySettings(variant.Settings), media,
                sample.EndSeconds - (job.TrimStartSeconds ?? 0));
            if (issues.HasErrors)
                throw new ComparisonEncodingException(string.Join(Environment.NewLine, issues.Errors.Select(i => i.Message)));
            var result = await execution.ExecuteAsync(job, media, progressCallback: progress, ct: ct);
            if (result.State == JobState.Canceled)
                throw new OperationCanceledException(ct);
            if (result.State != JobState.Completed)
                throw new ComparisonEncodingException(result.LogOutput);
            ct.ThrowIfCancellationRequested();
            var size = new FileInfo(temporary).Length;
            if (size == 0)
                throw new ComparisonEncodingException("The encoder produced an empty clip.");
            File.Move(temporary, output, overwrite: true);
            return new ComparisonClip(output, offset, sample.DurationSeconds, size, result.ElapsedSeconds, key, false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
