using FFApp.Core.Models;

namespace FFApp.Services;

public interface IComparisonWindowService
{
    Task ShowAsync(JobContext context, Func<OutputSettings, bool> apply);
    Task<bool> CloseAllAsync();
}
