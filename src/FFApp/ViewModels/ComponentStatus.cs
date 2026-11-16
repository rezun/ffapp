namespace FFApp.ViewModels;

public enum ComponentState
{
    /// <summary>Neutral progress or information; no icon.</summary>
    Info,
    Ok,
    /// <summary>Optional component missing or limited; encoding still works.</summary>
    Warning,
    /// <summary>Blocks encoding.</summary>
    Error,
}

/// <summary>One status line in the tools dialog: an optional title, a detail sentence and a state icon.</summary>
public sealed record ComponentStatus(string Title, string Detail, ComponentState State)
{
    public static ComponentStatus Empty { get; } = new("", "", ComponentState.Info);

    public bool HasTitle => Title.Length > 0;
    public bool IsOk => State == ComponentState.Ok;
    public bool IsWarning => State == ComponentState.Warning;
    public bool IsError => State == ComponentState.Error;
}
