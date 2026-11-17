using FFApp.Remote.Contracts;

namespace FFApp.Agent;

/// <summary>A request the agent rejects; mapped to an <see cref="ErrorResponse"/> with the given status.</summary>
public sealed class AgentException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    public static AgentException NotFound(string what) =>
        new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"{what} was not found.");

    public static AgentException Invalid(string message) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, message);

    public static AgentException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);
}
