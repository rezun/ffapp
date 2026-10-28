namespace FFApp.Core.Services;

/// <summary>
/// ViewModels that require asynchronous initialization after construction implement this interface.
/// The navigation/hosting infrastructure calls <see cref="InitializeAsync"/> after the ViewModel
/// is resolved from DI and before the view is presented.
/// </summary>
public interface IAsyncInitializable
{
    Task InitializeAsync();
}
