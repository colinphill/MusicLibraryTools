using System.Collections.ObjectModel;

namespace MusicLibraryManager.Presentation;

public sealed class AppActivityService : IActivityService
{
    private readonly List<AppActivity> _state = [];
    private readonly ObservableCollection<AppActivity> _activities = [];
    private readonly Dictionary<Guid, Action> _cancellations = [];
    private readonly ILocalizationService? _localization;
    private readonly SynchronizationContext? _notificationContext;
    private readonly object _gate = new();
    private readonly object _publishGate = new();
    private bool _publishScheduled;

    public AppActivityService(
        ILocalizationService? localization = null,
        SynchronizationContext? notificationContext = null)
    {
        _localization = localization;
        _notificationContext = notificationContext;
        Activities = new ReadOnlyObservableCollection<AppActivity>(_activities);
    }

    public ReadOnlyObservableCollection<AppActivity> Activities { get; }
    public AppActivity? Current
    {
        get
        {
            lock (_publishGate)
                return _activities.FirstOrDefault(
                    activity =>
                        activity.State ==
                        AppActivityState.Running);
        }
    }
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
        lock (_gate)
        {
            if (cancel is not null)
                _cancellations[id] = cancel;
            _state.Insert(0, activity);
            TrimLocked();
        }
        SchedulePublish();
        return id;
    }

    public void Report(Guid id, string message, double? progress = null)
        => Replace(
            id,
            activity =>
                activity.State !=
                    AppActivityState.Running
                    ? activity
                    : activity with
                    {
                        Message = message,
                        Progress = progress,
                    });

    public void Finish(Guid id, string message, AppActivityState state = AppActivityState.Completed)
    {
        lock (_gate)
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
        Action? cancel;
        lock (_gate)
        {
            if (!_cancellations.Remove(
                    id,
                    out cancel))
                return false;
        }
        cancel!();
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
        lock (_gate)
        {
            int index = _state
                .FindIndex(
                    activity =>
                        activity.Id == id);
            if (index < 0 ||
                _state[index].State ==
                AppActivityState.Running)
                return;
            _cancellations.Remove(id);
            _state.RemoveAt(index);
        }
        SchedulePublish();
    }

    private void Replace(Guid id, Func<AppActivity, AppActivity> update)
    {
        lock (_gate)
        {
            int index = _state
                .FindIndex(
                    activity =>
                        activity.Id == id);
            if (index < 0)
                return;
            AppActivity current =
                _state[index];
            AppActivity updated =
                update(current);
            if (ReferenceEquals(
                    current,
                    updated))
                return;
            _state[index] = updated;
            TrimLocked();
        }
        SchedulePublish();
    }

    private void TrimLocked()
    {
        while (_state.Count > 25)
        {
            int index = -1;
            for (int candidate = _state.Count - 1; candidate >= 0; candidate--)
            {
                if (_state[candidate].State != AppActivityState.Running)
                {
                    index = candidate;
                    break;
                }
            }
            if (index < 0)
                break;
            Guid id = _state[index].Id;
            _state.RemoveAt(index);
            _cancellations.Remove(id);
        }
    }

    private void SchedulePublish()
    {
        SynchronizationContext? context =
            _notificationContext;
        if (context is null ||
            ReferenceEquals(
                SynchronizationContext.Current,
                context))
        {
            PublishSnapshot();
            return;
        }

        lock (_gate)
        {
            if (_publishScheduled)
                return;
            _publishScheduled = true;
        }
        context.Post(
            static state =>
                ((AppActivityService)state!)
                    .PublishSnapshot(),
            this);
    }

    private void PublishSnapshot()
    {
        lock (_publishGate)
        {
            AppActivity[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _state];
                _publishScheduled = false;
            }
            ApplySnapshot(snapshot);
        }
        Changed?.Invoke();
    }

    private void ApplySnapshot(
        IReadOnlyList<AppActivity> snapshot)
    {
        for (int index = 0;
             index < snapshot.Count;
             index++)
        {
            AppActivity expected =
                snapshot[index];
            if (index < _activities.Count &&
                _activities[index].Id ==
                expected.Id)
            {
                if (_activities[index] !=
                    expected)
                    _activities[index] =
                        expected;
                continue;
            }

            int existingIndex = -1;
            for (int candidate = index + 1;
                 candidate < _activities.Count;
                 candidate++)
            {
                if (_activities[candidate].Id ==
                    expected.Id)
                {
                    existingIndex = candidate;
                    break;
                }
            }
            if (existingIndex >= 0)
            {
                _activities.Move(
                    existingIndex,
                    index);
                if (_activities[index] !=
                    expected)
                    _activities[index] =
                        expected;
            }
            else
            {
                _activities.Insert(
                    index,
                    expected);
            }
        }

        while (_activities.Count >
               snapshot.Count)
            _activities.RemoveAt(
                _activities.Count - 1);
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
