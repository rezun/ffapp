using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IPresetService
{
    Task<IReadOnlyList<PresetDefinition>> LoadAllAsync(CancellationToken ct = default);
    Task SaveAsync(PresetDefinition preset, CancellationToken ct = default);
    Task<PresetDefinition> RenameAsync(string currentName, string newName, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
}
