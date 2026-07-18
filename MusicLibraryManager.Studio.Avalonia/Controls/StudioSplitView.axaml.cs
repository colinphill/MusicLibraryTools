using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Studio.Services;

namespace MusicLibraryManager.Studio.Avalonia.Controls;

public partial class StudioSplitView : UserControl
{
    public static readonly StyledProperty<object?> LeftProperty =
        AvaloniaProperty.Register<StudioSplitView, object?>(nameof(Left));
    public static readonly StyledProperty<object?> RightProperty =
        AvaloniaProperty.Register<StudioSplitView, object?>(nameof(Right));
    public static readonly StyledProperty<double> InitialLeftWidthProperty =
        AvaloniaProperty.Register<StudioSplitView, double>(nameof(InitialLeftWidth), 300);
    public static readonly StyledProperty<double> MinLeftWidthProperty =
        AvaloniaProperty.Register<StudioSplitView, double>(nameof(MinLeftWidth), 180);
    public static readonly StyledProperty<double> MaxLeftWidthProperty =
        AvaloniaProperty.Register<StudioSplitView, double>(nameof(MaxLeftWidth), 700);
    public static readonly StyledProperty<double> MinRightWidthProperty =
        AvaloniaProperty.Register<StudioSplitView, double>(nameof(MinRightWidth), 160);
    public static readonly StyledProperty<string?> PersistenceKeyProperty =
        AvaloniaProperty.Register<StudioSplitView, string?>(nameof(PersistenceKey));
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<StudioSplitView, string>(nameof(Label), "Resize panes");

    public object? Left { get => GetValue(LeftProperty); set => SetValue(LeftProperty, value); }
    public object? Right { get => GetValue(RightProperty); set => SetValue(RightProperty, value); }
    public double InitialLeftWidth { get => GetValue(InitialLeftWidthProperty); set => SetValue(InitialLeftWidthProperty, value); }
    public double MinLeftWidth { get => GetValue(MinLeftWidthProperty); set => SetValue(MinLeftWidthProperty, value); }
    public double MaxLeftWidth { get => GetValue(MaxLeftWidthProperty); set => SetValue(MaxLeftWidthProperty, value); }
    public double MinRightWidth { get => GetValue(MinRightWidthProperty); set => SetValue(MinRightWidthProperty, value); }
    public string? PersistenceKey { get => GetValue(PersistenceKeyProperty); set => SetValue(PersistenceKeyProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private StudioSplitStateService? _state;
    private bool _compact;
    private bool _initialized;
    private double? _lastPersistedWidth;

    public StudioSplitView()
    {
        InitializeComponent();
        LeftPresenter.Content = Left;
        RightPresenter.Content = Right;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == LeftProperty)
                LeftPresenter.Content = Left;
            else if (args.Property == RightProperty)
                RightPresenter.Content = Right;
        };
        AttachedToVisualTree += (_, _) => InitializeWidth();
        DetachedFromVisualTree += (_, _) => Persist();
        SplitGrid.SizeChanged += (_, _) => EnsurePanesFit();
        Splitter.AddHandler(Thumb.DragCompletedEvent, OnSplitterDragCompleted,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    internal StudioSplitView(StudioSplitStateService state) : this() => _state = state;

    internal double CurrentLeftWidth => SplitGrid.ColumnDefinitions[0].Width.Value;

    internal void CommitLeftWidth(double width)
    {
        SetLeftWidth(width);
        Persist();
    }

    private void InitializeWidth()
    {
        _state ??= App.GetService<StudioSplitStateService>();
        SplitGrid.ColumnDefinitions[0].MinWidth = MinLeftWidth;
        SplitGrid.ColumnDefinitions[0].MaxWidth = MaxLeftWidth;
        SplitGrid.ColumnDefinitions[2].MinWidth = MinRightWidth;
        double width = PersistenceKey is null ? InitialLeftWidth : _state.Load(PersistenceKey) ?? InitialLeftWidth;
        SetLeftWidth(width);
        _lastPersistedWidth = SplitGrid.ColumnDefinitions[0].Width.Value;
        _initialized = true;
    }

    private void SetLeftWidth(double width)
    {
        double maximum = MaxLeftWidth;
        if (!_compact && SplitGrid.Bounds.Width > 0)
        {
            double splitterWidth = SplitGrid.ColumnDefinitions[1].ActualWidth;
            maximum = Math.Min(maximum,
                Math.Max(MinLeftWidth, SplitGrid.Bounds.Width - splitterWidth - MinRightWidth));
        }
        SplitGrid.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(width, MinLeftWidth, maximum));
    }

    private void EnsurePanesFit()
    {
        if (_compact || !SplitGrid.ColumnDefinitions[0].Width.IsAbsolute)
            return;
        SetLeftWidth(SplitGrid.ColumnDefinitions[0].Width.Value);
    }

    private void Persist()
    {
        if (!_initialized || _compact || PersistenceKey is null)
            return;
        ColumnDefinition left = SplitGrid.ColumnDefinitions[0];
        double width = left.Width.IsAbsolute ? left.Width.Value : left.ActualWidth;
        if (!double.IsFinite(width) || width <= 0 ||
            (_lastPersistedWidth is double previous && Math.Abs(previous - width) < 0.5))
            return;
        (_state ??= App.GetService<StudioSplitStateService>()).Save(PersistenceKey, width);
        _lastPersistedWidth = width;
    }

    public void SetCompact(bool compact)
    {
        if (_compact == compact)
            return;
        _compact = compact;
        Splitter.IsVisible = !compact;
        SplitGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(10);
        Grid.SetColumn(LeftPresenter, 0);
        Grid.SetColumnSpan(LeftPresenter, compact ? 3 : 1);
        Grid.SetColumn(RightPresenter, compact ? 0 : 2);
        Grid.SetColumnSpan(RightPresenter, compact ? 3 : 1);
        RightPresenter.Width = compact ? 310 : double.NaN;
        RightPresenter.HorizontalAlignment = compact
            ? global::Avalonia.Layout.HorizontalAlignment.Right
            : global::Avalonia.Layout.HorizontalAlignment.Stretch;
        RightPresenter.SetValue(Panel.ZIndexProperty, compact ? 15 : 0);
        RightPresenter.IsVisible = !compact;
    }

    public void ToggleCompactRight()
    {
        if (_compact)
            RightPresenter.IsVisible = !RightPresenter.IsVisible;
    }

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e) => Persist();

    private void OnSplitterKeyDown(object? sender, KeyEventArgs e)
    {
        double current = SplitGrid.ColumnDefinitions[0].ActualWidth;
        switch (e.Key)
        {
            case Key.Home:
                CommitLeftWidth(MinLeftWidth);
                break;
            case Key.End:
                CommitLeftWidth(MaxLeftWidth);
                break;
            case Key.Left:
                CommitLeftWidth(current - 24);
                break;
            case Key.Right:
                CommitLeftWidth(current + 24);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
