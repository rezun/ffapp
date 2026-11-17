using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Remote;
using FFApp.Remote.Contracts;

namespace FFApp.ViewModels;

/// <summary>Settings → Server agent: address, token, connection test, defaults and agent updates.</summary>
public sealed partial class RemoteAgentSettingsViewModel : ObservableObject, IDisposable
{
    private readonly RemoteAgentConnection _connection;
    private readonly ISecretStore _secrets;
    private readonly Func<RemoteAgentSettings, Task> _save;
    private readonly bool _hasSavedToken;

    public RemoteAgentSettingsViewModel(
        RemoteAgentConnection connection,
        ISecretStore secrets,
        RemoteAgentSettings current,
        Func<RemoteAgentSettings, Task> save)
    {
        ArgumentNullException.ThrowIfNull(current);
        _connection = connection;
        _secrets = secrets;
        _save = save;
        _baseUrl = current.BaseUrl ?? "";
        try
        {
            _hasSavedToken = !string.IsNullOrEmpty(secrets.Read(SecretNames.AgentToken));
        }
        catch (InvalidOperationException)
        {
            _hasSavedToken = false;
        }
        _defaultToServer = current.DefaultTarget == ExecutionTarget.Server;
        _defaultResultMode = current.DefaultResultMode;
        _defaultServerOutputRoot = current.DefaultServerOutputRoot;
        TokenPlaceholder = _hasSavedToken ? "Saved; enter a new token to replace it" : "Token from ffapp-agent client add";
        ShowStatus(connection.Status);
        connection.StatusChanged += OnStatusChanged;
        connection.UpdateChanged += OnUpdateChanged;
    }

    public string InstallCommand { get; } = AgentInstallCommand.Latest;

    public string TokenPlaceholder { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _baseUrl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _token = "";

    [ObservableProperty]
    private bool _defaultToServer;

    [ObservableProperty]
    private ResultMode _defaultResultMode;

    [ObservableProperty]
    private string? _defaultServerOutputRoot;

    public ObservableCollection<ResultOption> ResultOptions { get; } = [];

    [ObservableProperty]
    private ResultOption? _selectedDefaultResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _detailsText = "";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAgentCommand))]
    private bool _offersUpdate;

    [ObservableProperty]
    private string _updateStatusText = "";

    public string UpdateAgentLabel => $"Update agent to {AppVersion.Current}";

    /// <summary>Raised after saving, so the window can close.</summary>
    public event Action? Saved;

    private bool CanTest => !IsBusy && AgentEndpoint.ParseBaseUrl(BaseUrl) is not null
        && (Token.Trim().Length > 0 || _hasSavedToken);

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (Endpoint() is not { } endpoint)
            return;
        IsBusy = true;
        StatusText = "Connecting…";
        DetailsText = "";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var status = await _connection.TestAsync(endpoint, timeout.Token);
            ShowStatus(status);
            if (status.Info is not null)
            {
                try
                {
                    var load = await _connection.Client.GetQueueAsync(endpoint, timeout.Token);
                    DetailsText += Environment.NewLine + JobListPanelViewModel.DescribeLoad(load, DateTimeOffset.UtcNow);
                }
                catch (RemoteAgentException)
                {
                    // Load information is optional in the test result.
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave => string.IsNullOrWhiteSpace(BaseUrl) || AgentEndpoint.ParseBaseUrl(BaseUrl) is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var url = AgentEndpoint.ParseBaseUrl(BaseUrl);
        try
        {
            if (url is null)
                _secrets.Delete(SecretNames.AgentToken);
            else if (Token.Trim() is { Length: > 0 } token)
                _secrets.Write(SecretNames.AgentToken, token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save the token: {ex.Message}";
            return;
        }

        var option = SelectedDefaultResult;
        await _save(new RemoteAgentSettings(
            url?.ToString(),
            DefaultToServer ? ExecutionTarget.Server : ExecutionTarget.Local,
            option?.Mode ?? ResultMode.Download,
            option?.Root));
        Saved?.Invoke();
    }

    private bool CanUpdateAgent => OffersUpdate;

    [RelayCommand(CanExecute = nameof(CanUpdateAgent))]
    private async Task UpdateAgentAsync()
    {
        try
        {
            var status = await _connection.RequestUpdateAsync(CancellationToken.None);
            ShowUpdate(status);
        }
        catch (RemoteAgentException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            UpdateStatusText = "This agent is too old to update from the app. Run this on the server:"
                + Environment.NewLine + AgentInstallCommand.For(AppVersion.Current);
        }
        catch (RemoteAgentException ex)
        {
            UpdateStatusText = $"Agent update failed: {ex.Message}";
        }
    }

    private AgentEndpoint? Endpoint()
    {
        if (AgentEndpoint.ParseBaseUrl(BaseUrl) is not { } url)
            return null;
        var token = Token.Trim();
        if (token.Length == 0)
        {
            try
            {
                token = _secrets.Read(SecretNames.AgentToken) ?? "";
            }
            catch (InvalidOperationException)
            {
                token = "";
            }
        }
        return token.Length == 0 ? null : new AgentEndpoint(url, token);
    }

    private void OnStatusChanged(AgentConnectionStatus status) => Dispatcher.UIThread.Post(() =>
    {
        if (!IsBusy)
            ShowStatus(status);
    });

    private void OnUpdateChanged(UpdateStatus status) => Dispatcher.UIThread.Post(() => ShowUpdate(status));

    private void ShowStatus(AgentConnectionStatus status)
    {
        StatusText = status.Message;
        IsConnected = status.Info is not null && status.State != AgentConnectionState.Unreachable;
        OffersUpdate = status.OffersUpdate;
        DetailsText = status.Info is { } info ? Describe(info) : "";

        var options = new List<ResultOption> { new(OperatingSystem.IsMacOS() ? "To this Mac" : "To this computer", ResultMode.Download, null) };
        if (status.Info is { } agent)
            options.AddRange(agent.OutputRoots.Select(root => new ResultOption($"On the server: {root}", ResultMode.KeepOnServer, root)));
        else if (DefaultResultMode == ResultMode.KeepOnServer && DefaultServerOutputRoot is { } saved)
            options.Add(new ResultOption($"On the server: {saved}", ResultMode.KeepOnServer, saved));
        if (!options.SequenceEqual(ResultOptions))
        {
            ResultOptions.Clear();
            foreach (var option in options)
                ResultOptions.Add(option);
        }
        SelectedDefaultResult = ResultOptions.FirstOrDefault(option => option.Mode == DefaultResultMode
            && (option.Mode == ResultMode.Download || string.Equals(option.Root, DefaultServerOutputRoot, StringComparison.Ordinal)))
            ?? ResultOptions.FirstOrDefault();
    }

    partial void OnSelectedDefaultResultChanged(ResultOption? value)
    {
        if (value is null)
            return;
        DefaultResultMode = value.Mode;
        DefaultServerOutputRoot = value.Root;
    }

    private void ShowUpdate(UpdateStatus status) => UpdateStatusText = status.State switch
    {
        UpdateStateKind.Idle => status.Message ?? "",
        UpdateStateKind.WaitingForJobs => $"The agent installs {status.TargetVersion} once the running job has finished.",
        UpdateStateKind.Downloading => $"The agent is downloading {status.TargetVersion}…",
        UpdateStateKind.Restarting => $"The agent is restarting with {status.TargetVersion}…",
        UpdateStateKind.Failed => $"Agent update failed: {status.Message}",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status.State, "Unknown update state."),
    };

    internal static string Describe(AgentInfo info)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.CurrentCulture, $"Agent {info.AgentVersion} · connected as \"{info.ClientName}\"");
        text.AppendLine(info.FfmpegVersion ?? "FFmpeg not found on the server");
        if (info.Capabilities is { } capabilities)
        {
            var encoders = new[] { "libsvtav1", "libx265", "libx264", "libvpx-vp9", "libopus", "aac" }
                .Where(capabilities.Encoders.Contains);
            text.AppendLine(CultureInfo.CurrentCulture, $"Encoders: {string.Join(", ", encoders)}");
        }
        text.Append(CultureInfo.CurrentCulture,
            $"Free space: {FormatHelpers.FormatStorageSize(info.FreeBytes)} · quota used: {FormatHelpers.FormatStorageSize(info.UsedBytes)} of {FormatHelpers.FormatStorageSize(info.QuotaBytes)}");
        foreach (var warning in info.Warnings)
            text.Append(Environment.NewLine).Append("Warning: ").Append(warning);
        return text.ToString();
    }

    public void Dispose()
    {
        _connection.StatusChanged -= OnStatusChanged;
        _connection.UpdateChanged -= OnUpdateChanged;
    }
}
