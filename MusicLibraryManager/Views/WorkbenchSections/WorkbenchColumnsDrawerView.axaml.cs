using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using System.Globalization;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchColumnsDrawerView : UserControl
{
    private WorkbenchSessionSectionView? _session;
    private readonly ILocalizationService _localization;
    private string? _selectedBuiltInKey;
    private bool _listeningForCulture;

    public WorkbenchColumnsDrawerView()
    {
        InitializeComponent();
        _localization = App.GetService<ILocalizationService>();
        AttachedToVisualTree += (_, _) =>
            StartListeningForCulture();
        DetachedFromVisualTree += (_, _) =>
            StopListeningForCulture();
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
        string query =
            ColumnSearchBox.Text?.Trim() ?? "";
        foreach (AppGridColumnDefinition definition in
                 _session.ColumnDefinitions)
        {
            string label =
                definition.HeaderResourceKey is
                    { Length: > 0 } key
                    ? _localization.Get(key)
                    : definition.Header;
            if (query.Length > 0 &&
                !label.Contains(
                    query,
                    StringComparison.CurrentCultureIgnoreCase) &&
                !definition.Key.Contains(
                    query,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var check = new CheckBox
            {
                Content = label,
                IsChecked = definition.Visible,
                Tag = definition.Key,
                VerticalAlignment =
                    VerticalAlignment.Center,
            };
            check.IsCheckedChanged +=
                OnColumnChecked;
            check.Click += OnColumnSelected;

            var moveUp = new Button
            {
                Classes =
                {
                    "app",
                    "icon",
                },
                Content = new AppVectorIcon
                {
                    Kind = AppVectorIconKind.MoveUp,
                },
                Width = 36,
                Height = 36,
                Padding = new(0),
                Tag = new ColumnMoveRequest(
                    definition.Key,
                    -1),
            };
            AutomationProperties.SetName(
                moveUp,
                _localization.Get(
                    "Workbench.Action.MoveUp"));
            moveUp.Click += OnMoveColumn;

            var moveDown = new Button
            {
                Classes =
                {
                    "app",
                    "icon",
                },
                Content = new AppVectorIcon
                {
                    Kind = AppVectorIconKind.MoveDown,
                },
                Width = 36,
                Height = 36,
                Padding = new(0),
                Tag = new ColumnMoveRequest(
                    definition.Key,
                    1),
            };
            AutomationProperties.SetName(
                moveDown,
                _localization.Get(
                    "Workbench.Action.MoveDown"));
            moveDown.Click += OnMoveColumn;

            var actions = new StackPanel
            {
                Orientation =
                    Orientation.Horizontal,
                Spacing = 4,
            };
            actions.Children.Add(moveUp);
            actions.Children.Add(moveDown);

            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new(
                        1,
                        GridUnitType.Star),
                    new(
                        GridLength.Auto),
                },
                ColumnSpacing = 8,
            };
            row.Children.Add(check);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            var card = new Border
            {
                Classes =
                {
                    "card",
                },
                Padding = new(8),
                Child = row,
            };
            if (string.Equals(
                    definition.Key,
                    _selectedBuiltInKey,
                    StringComparison.Ordinal))
                card.Classes.Add("selected");
            WorkbenchColumnOptions.Children.Add(card);
        }
        UpdateBuiltInDetails();
    }

    private void OnColumnSelected(
        object? sender,
        global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not CheckBox
            {
                Tag: string key,
            })
            return;
        _selectedBuiltInKey = key;
        BuildOptions();
    }

    private void UpdateBuiltInDetails()
    {
        AppGridColumnDefinition? definition =
            _session?.ColumnDefinitions.FirstOrDefault(
                column => string.Equals(
                    column.Key,
                    _selectedBuiltInKey,
                    StringComparison.Ordinal));
        BuiltInColumnDetails.IsVisible =
            definition is not null;
        if (definition is null)
            return;
        BuiltInColumnTitle.Text =
            definition.HeaderResourceKey is
                { Length: > 0 } key
                ? _localization.Get(key)
                : definition.Header;
        BuiltInColumnWidth.Text =
            definition.Width.ToString(
                "N0",
                CultureInfo.CurrentCulture);
        BuiltInColumnEditable.Text =
            _localization.Get(
                definition.Editable
                    ? "Common.Yes"
                    : "Common.No");
        BuiltInColumnEditingHelp.IsVisible =
            !definition.Editable;
    }

    private void StartListeningForCulture()
    {
        if (_listeningForCulture)
            return;
        _localization.CultureChanged +=
            OnCultureChanged;
        _listeningForCulture = true;
        BuildOptions();
    }

    private void StopListeningForCulture()
    {
        if (!_listeningForCulture)
            return;
        _localization.CultureChanged -=
            OnCultureChanged;
        _listeningForCulture = false;
    }

    private void OnCultureChanged(
        object? sender,
        EventArgs e) =>
        BuildOptions();

    private void OnColumnSearchChanged(
        object? sender,
        TextChangedEventArgs e) =>
        BuildOptions();

    private void OnMoveColumn(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e)
    {
        if (_session is null ||
            sender is not Button
            {
                Tag: ColumnMoveRequest request,
            })
            return;
        _session.MoveColumn(
            request.Key,
            request.Offset);
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

    private sealed record ColumnMoveRequest(
        string Key,
        int Offset);
}
