namespace FFApp.Services;

public interface IUpdateHost
{
    Task OpenDownloadsAsync();
    Task<bool> RestartAsync(Action applyUpdate);
}
