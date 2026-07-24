using Avalonia.Controls;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchColumnsDrawerView : UserControl
{
    private WorkbenchSessionSectionView? _session;
    private readonly ILocalizationService _localization;

    public WorkbenchColumnsDrawerView()
    {
        InitializeComponent();
        _localization = App.GetService<ILocalizationService>();
    }

    public event EventHandler? CloseRequested;

    public Control InitialFocus =>
        WorkbenchColumnsCloseButton;

    public void Attach(
        WorkbenchSessionSectionView session)
    {
        if (ReferenceEquals(_session, session))
            return;
        if (_session is not null)
            _session.ColumnDefinitionsChanged -=
                BuildOptions;
        _session = session;
        _session.ColumnDefinitionsChanged +=
            BuildOptions;
        BuildOptions();
    }

    private void BuildOptions()
    {
        WorkbenchColumnOptions.Children.Clear();
        if (_session is null)
            return;
        foreach (AppGridColumnDefinition definition in
                 _session.ColumnDefinitions)
        {
            var check = new CheckBox
            {
                Content =
                    definition.HeaderResourceKey is
                        { Length: > 0 } key
                        ? _localization.Get(key)
                        : definition.Header,
                IsChecked = definition.Visible,
                Tag = definition.Key,
                Margin = new(
                    0,
                    0,
                    12,
                    8),
            };
            check.IsCheckedChanged +=
                OnColumnChecked;
            WorkbenchColumnOptions.Children.Add(check);
        }
    }

    private void OnColumnChecked(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e)
    {
        if (_session is null ||
            sender is not CheckBox
            {
                Tag: string key,
                IsChecked: bool visible,
            } check)
            return;
        if (!visible &&
            _session.ColumnDefinitions.Count(
                column => column.Visible) == 1)
        {
            check.IsChecked = true;
            return;
        }
        _session.SetColumnVisibility(
            key,
            visible);
    }

    private void OnClose(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e) =>
        CloseRequested?.Invoke(
            this,
            EventArgs.Empty);
}
