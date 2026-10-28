using System.Buffers;
using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record GeneratedCommand(
    string ExecutablePath,
    ImmutableArray<string> Arguments,
    string DisplayString,
    ProcessProtocol Protocol = ProcessProtocol.Ffmpeg)
{
    private static readonly SearchValues<char> ShellMetaCharacters =
        SearchValues.Create(" \t\"'\\()&|;<>$`!{}");

    public ImmutableArray<string> TemporaryFiles { get; init; } = [];

    /// <summary>Creates a command whose display string can be pasted into a POSIX shell.</summary>
    public static GeneratedCommand Create(
        string executable,
        IReadOnlyList<string> arguments,
        ProcessProtocol protocol = ProcessProtocol.Ffmpeg)
    {
        var displayParts = new List<string>(arguments.Count + 1) { QuoteArgument(executable) };
        foreach (var arg in arguments)
            displayParts.Add(QuoteArgument(arg));

        return new GeneratedCommand(
            ExecutablePath: executable,
            Arguments: [.. arguments],
            DisplayString: string.Join(' ', displayParts),
            Protocol: protocol);
    }

    private static string QuoteArgument(string arg)
    {
        if (arg.Length == 0)
            return "''";
        if (arg.AsSpan().ContainsAny(ShellMetaCharacters))
            return "'" + arg.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        return arg;
    }
}

public sealed record ProcessProgress(
    double ElapsedSeconds,
    double? ProcessedSeconds = null,
    double? Percentage = null,
    double? Speed = null);

public sealed record ProcessExecutionResult(
    GeneratedCommand Command,
    JobState State,
    int ReturnCode,
    double ElapsedSeconds,
    string StderrOutput);
