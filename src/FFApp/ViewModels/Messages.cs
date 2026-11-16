using CommunityToolkit.Mvvm.Messaging.Messages;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public sealed class SourceLoadedMessage(SourceMediaInfo mediaInfo)
    : ValueChangedMessage<SourceMediaInfo>(mediaInfo);

public sealed class OutputSettingsChangedMessage(OutputSettings settings)
    : ValueChangedMessage<OutputSettings>(settings);

public sealed class DependenciesResolvedMessage(DependencyStatus status, string componentNotice = "")
    : ValueChangedMessage<DependencyStatus>(status)
{
    public string ComponentNotice { get; } = componentNotice;
}
