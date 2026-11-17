using System.Collections.Immutable;

using FFApp.Remote.Contracts;

namespace FFApp.Core.Models;

public sealed record SplitterLayout
{
    public ImmutableArray<int> MainSplitterSizes { get; init; } = [];
    public ImmutableArray<int> QueueSplitterSizes { get; init; } = [];
    public int PreviewHeight { get; init; }
}

public sealed record WindowGeometry(
    int? X = null,
    int? Y = null,
    int? Width = null,
    int? Height = null,
    bool IsMaximized = false);

public enum ThemePreference
{
    System,
    Dark,
    Light,
}

public sealed record AppSettings
{
    public ExecutablePaths ExecutablePaths { get; init; } = ExecutablePaths.Empty;
    public OutputSettings LastUsedSettings { get; init; } = new();
    public string? LastUsedPresetName { get; init; }
    public OutputLocation OutputLocation { get; init; } = new();
    public SplitterLayout SplitterLayout { get; init; } = new();
    public WindowGeometry WindowGeometry { get; init; } = new();
    public ThemePreference ThemePreference { get; init; } = ThemePreference.System;
    public bool UseLowPriorityTranscoding { get; init; }
    public int ComparisonCacheLimitGiB { get; init; } = 5;
    public RemoteAgentSettings RemoteAgent { get; init; } = new();
    public int SchemaVersion { get; init; } = 6;
}

public enum ExecutionTarget
{
    Local,
    Server,
}

/// <summary>
/// Connection to the server agent. The token is not part of the settings file; it lives in the
/// platform's secret store.
/// </summary>
public sealed record RemoteAgentSettings(
    string? BaseUrl = null,
    ExecutionTarget DefaultTarget = ExecutionTarget.Local,
    ResultMode DefaultResultMode = ResultMode.Download,
    string? DefaultServerOutputRoot = null)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
