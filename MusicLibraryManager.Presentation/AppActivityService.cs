using System.Collections.ObjectModel;

namespace MusicLibraryManager.Presentation;

public sealed class AppActivityService : IActivityService
{
    private readonly ObservableCollection<AppActivity> _activities = [];

    public AppActivityService()
    {
        Activities = new ReadOnlyObservableCollection<AppActivity>(_activities);
    }

    public ReadOnlyObservableCollection<AppActivity> Activities { get; }
    public AppActivity? Current => _activities.FirstOrDefault(activity => activity.State == AppActivityState.Running);
    public event Action? Changed;

    public Guid Start(string title, string message)
    {
        var activity = new AppActivity(Guid.NewGuid(), title, message, AppActivityState.Running, null, DateTimeOffset.Now);
        _activities.Insert(0, activity);
        Trim();
        Changed?.Invoke();
        return activity.Id;
    }

    public void Report(Guid id, string message, double? progress = null)
        => Replace(id, activity => activity with { Message = message, Progress = progress });

    public void Finish(Guid id, string message, AppActivityState state = AppActivityState.Completed)
        => Replace(id, activity => activity with
        {
            Message = message,
            State = state,
            Progress = state == AppActivityState.Completed ? 1 : activity.Progress,
            FinishedAt = DateTimeOffset.Now,
        });

    private void Replace(Guid id, Func<AppActivity, AppActivity> update)
    {
        int index = _activities.ToList().FindIndex(activity => activity.Id == id);
        if (index < 0)
            return;
        _activities[index] = update(_activities[index]);
        Changed?.Invoke();
    }

    private void Trim()
    {
        while (_activities.Count > 25)
            _activities.RemoveAt(_activities.Count - 1);
    }
}

public sealed class NavigationService : INavigationService
{
    public ShellDestination Current { get; private set; } = ShellDestination.Home;
    public event Action<ShellDestination>? NavigationRequested;

    public void Navigate(ShellDestination destination)
    {
        Current = destination;
        NavigationRequested?.Invoke(destination);
    }
}
