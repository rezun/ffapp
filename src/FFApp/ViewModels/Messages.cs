using CommunityToolkit.Mvvm.Messaging.Messages;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public sealed class SourceLoadedMessage(SourceMediaInfo mediaInfo)
    : ValueChangedMessage<SourceMediaInfo>(mediaInfo);

public sealed class OutputSettingsChangedMessage(OutputSettings settings)
    : ValueChangedMessage<OutputSettings>(settings);

public sealed class DependenciesResolvedMessage(DependencyStatus status)
    : ValueChangedMessage<DependencyStatus>(status);

public sealed class NavigateToDependencySetupMessage;
