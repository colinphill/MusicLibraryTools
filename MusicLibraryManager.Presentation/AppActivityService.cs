using System.Collections.ObjectModel;

namespace MusicLibraryManager.Presentation;

public sealed class AppActivityService : IActivityService
{
    private readonly ObservableCollection<AppActivity> _activities = [];
    private readonly Dictionary<Guid, Action> _cancellations = [];
    private readonly ILocalizationService? _localization;

    public AppActivityService(
        ILocalizationService? localization = null)
    {
        _localization = localization;
        Activities = new ReadOnlyObservableCollection<AppActivity>(_activities);
    }

    public ReadOnlyObservableCollection<AppActivity> Activities { get; }
    public AppActivity? Current => _activities.FirstOrDefault(activity => activity.State == AppActivityState.Running);
    public event Action? Changed;

    public Guid Start(
        string title,
        string message,
        ShellDestination? destination = null,
        Action? cancel = null)
    {
        Guid id = Guid.NewGuid();
        var activity = new AppActivity(
            id,
            title,
            message,
            AppActivityState.Running,
            null,
            DateTimeOffset.Now,
            Destination: destination,
            CanCancel: cancel is not null);
        if (cancel is not null)
            _cancellations[id] = cancel;
        _activities.Insert(0, activity);
        Trim();
        Changed?.Invoke();
        return id;
    }

    public void Report(Guid id, string message, double? progress = null)
        => Replace(id, activity => activity with { Message = message, Progress = progress });

    public void Finish(Guid id, string message, AppActivityState state = AppActivityState.Completed)
    {
        _cancellations.Remove(id);
        Replace(id, activity => activity with
        {
            Message = message,
            State = state,
            Progress = state == AppActivityState.Completed ? 1 : activity.Progress,
            FinishedAt = DateTimeOffset.Now,
            CanCancel = false,
        });
    }

    public bool Cancel(Guid id)
    {
        if (!_cancellations.Remove(id, out Action? cancel))
            return false;
        cancel();
        Replace(id, activity => activity with
        {
            Message = _localization?.Get(
                "Activity.Cancelling") ??
                LocalizedText.Get(
                    "Activity.Cancelling"),
            CanCancel = false,
        });
        return true;
    }

    public void Dismiss(Guid id)
    {
        int index = _activities.ToList().FindIndex(activity => activity.Id == id);
        if (index < 0 || _activities[index].State == AppActivityState.Running)
            return;
        _cancellations.Remove(id);
        _activities.RemoveAt(index);
        Changed?.Invoke();
    }

    private void Replace(Guid id, Func<AppActivity, AppActivity> update)
    {
        int index = _activities.ToList().FindIndex(activity => activity.Id == id);
        if (index < 0)
            return;
        _activities[index] = update(_activities[index]);
        Trim();
        Changed?.Invoke();
    }

    private void Trim()
    {
        while (_activities.Count > 25)
        {
            int index = -1;
            for (int candidate = _activities.Count - 1; candidate >= 0; candidate--)
            {
                if (_activities[candidate].State != AppActivityState.Running)
                {
                    index = candidate;
                    break;
                }
            }
            if (index < 0)
                break;
            Guid id = _activities[index].Id;
            _activities.RemoveAt(index);
            _cancellations.Remove(id);
        }
    }
}

public sealed class NavigationService : INavigationService
{
    private long _requestVersion;
    private Task _latestNavigation = Task.CompletedTask;

    public ShellDestination Current { get; private set; } = ShellDestination.Home;
    public event Action<ShellDestination>? NavigationRequested;
    public event Action<Exception>? NavigationFailed;
    public Func<ShellDestination, Task<bool>>? Guard { get; set; }
    public Exception? LastError { get; private set; }
    public Task LatestNavigation => Volatile.Read(ref _latestNavigation);

    public void Navigate(ShellDestination destination) => _ = NavigateAsync(destination);

    public Task NavigateAsync(ShellDestination destination)
    {
        long version = Interlocked.Increment(ref _requestVersion);
        Task navigation = NavigateCoreAsync(destination, version);
        Volatile.Write(ref _latestNavigation, navigation);
        return navigation;
    }

    private async Task NavigateCoreAsync(ShellDestination destination, long version)
    {
        ShellDestination previous = Current;
        try
        {
            Func<ShellDestination, Task<bool>>? guard = Guard;
            if (guard is not null && !await guard(destination))
                return;
            if (version != Volatile.Read(ref _requestVersion))
                return;

            LastError = null;
            Current = destination;
            NavigationRequested?.Invoke(destination);
        }
        catch (Exception error)
        {
            if (version == Volatile.Read(ref _requestVersion))
            {
                Current = previous;
                LastError = error;
            }
            try
            {
                NavigationFailed?.Invoke(error);
            }
            catch
            {
                // A diagnostic subscriber must not turn a contained navigation failure
                // back into an unobserved task exception.
            }
        }
    }
}
