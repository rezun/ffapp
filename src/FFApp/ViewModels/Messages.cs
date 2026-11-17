using CommunityToolkit.Mvvm.Messaging.Messages;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public sealed class SourceLoadedMessage(SourceMediaInfo mediaInfo)
    : ValueChangedMessage<SourceMediaInfo>(mediaInfo);

public sealed class OutputSettingsChangedMessage(OutputSettings settings)
    : ValueChangedMessage<OutputSettings>(settings);

public sealed class DependenciesResolvedMessage(DependencyStatus status)
    : ValueChangedMessage<DependencyStatus>(status);

/// <summary>
/// The queue's target changed: validation must use these FFmpeg capabilities. <see cref="IsServer"/>
/// false means the local FFmpeg build.
/// </summary>
public sealed record ValidationTargetChangedMessage(bool IsServer, FfmpegCapabilities? Capabilities, bool HasMkvpropedit = false);
