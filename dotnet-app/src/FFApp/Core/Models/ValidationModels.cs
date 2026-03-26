using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message,
    string? FieldName = null);

public sealed record ValidationResult
{
    public ImmutableArray<ValidationIssue> Issues { get; init; } = [];

    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == ValidationSeverity.Warning);

    public ImmutableArray<ValidationIssue> Errors =>
        [.. Issues.Where(i => i.Severity == ValidationSeverity.Error)];

    public ImmutableArray<ValidationIssue> Warnings =>
        [.. Issues.Where(i => i.Severity == ValidationSeverity.Warning)];
}
