namespace FFApp.Core.Models;

public sealed record JobFailure(
    string Title,
    string Message,
    string Suggestion,
    string Context,
    string TechnicalDetails);
