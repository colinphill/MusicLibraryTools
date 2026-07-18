using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.AspNetCore.Components;

namespace MusicLibraryManager.Studio.Components;

public abstract class ReactiveComponentBase : ComponentBase, IDisposable
{
    private readonly List<Action> _unsubscribe = [];
    private bool _disposed;

    protected void Observe(INotifyPropertyChanged source)
    {
        PropertyChangedEventHandler handler = (_, _) => Refresh();
        source.PropertyChanged += handler;
        _unsubscribe.Add(() => source.PropertyChanged -= handler);
    }

    protected void Observe(INotifyCollectionChanged source)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => Refresh();
        source.CollectionChanged += handler;
        _unsubscribe.Add(() => source.CollectionChanged -= handler);
    }

    protected void Observe<T>(T source)
        where T : INotifyPropertyChanged, INotifyCollectionChanged
    {
        Observe((INotifyPropertyChanged)source);
        Observe((INotifyCollectionChanged)source);
    }

    protected void Observe(Action<Action> subscribe, Action<Action> unsubscribe)
    {
        Action handler = Refresh;
        subscribe(handler);
        _unsubscribe.Add(() => unsubscribe(handler));
    }

    protected void Refresh()
    {
        if (!_disposed)
            _ = InvokeAsync(StateHasChanged);
    }

    public virtual void Dispose()
    {
        _disposed = true;
        foreach (Action unsubscribe in _unsubscribe)
            unsubscribe();
        _unsubscribe.Clear();
        GC.SuppressFinalize(this);
    }
}
