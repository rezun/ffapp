using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using FFApp.ViewModels;

namespace FFApp.Views;

public partial class AudioView : UserControl
{
    private IDisposable? _subscription;

    public AudioView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _subscription?.Dispose();
        if (DataContext is not AudioViewModel vm) return;

        var observer = new ActionObserver<bool>(visible =>
        {
            if (visible)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    CustomBitrateBox.Focus();
                    CustomBitrateBox.SelectAll();
                });
            }
        });
        _subscription = CustomBitrateBox.GetObservable(IsVisibleProperty).Subscribe(observer);
    }

    private sealed class ActionObserver<T>(Action<T> action) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => action(value);
    }
}
