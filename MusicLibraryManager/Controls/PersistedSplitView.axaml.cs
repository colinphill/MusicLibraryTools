using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Styling;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Controls;

public partial class PersistedSplitView : UserControl
{
    public static readonly StyledProperty<object?> LeftProperty =
        AvaloniaProperty.Register<PersistedSplitView, object?>(nameof(Left));
    public static readonly StyledProperty<object?> RightProperty =
        AvaloniaProperty.Register<PersistedSplitView, object?>(nameof(Right));
    public static readonly StyledProperty<double> InitialLeftWidthProperty =
        AvaloniaProperty.Register<PersistedSplitView, double>(nameof(InitialLeftWidth), 300);
    public static readonly StyledProperty<double> MinLeftWidthProperty =
        AvaloniaProperty.Register<PersistedSplitView, double>(nameof(MinLeftWidth), 180);
    public static readonly StyledProperty<double> MaxLeftWidthProperty =
        AvaloniaProperty.Register<PersistedSplitView, double>(nameof(MaxLeftWidth), 700);
    public static readonly StyledProperty<double> MinRightWidthProperty =
        AvaloniaProperty.Register<PersistedSplitView, double>(nameof(MinRightWidth), 160);
    public static readonly StyledProperty<string?> PersistenceKeyProperty =
        AvaloniaProperty.Register<PersistedSplitView, string?>(nameof(PersistenceKey));
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<PersistedSplitView, string>(
            nameof(Label),
            string.Empty);

    public object? Left { get => GetValue(LeftProperty); set => SetValue(LeftProperty, value); }
    public object? Right { get => GetValue(RightProperty); set => SetValue(RightProperty, value); }
    public double InitialLeftWidth { get => GetValue(InitialLeftWidthProperty); set => SetValue(InitialLeftWidthProperty, value); }
    public double MinLeftWidth { get => GetValue(MinLeftWidthProperty); set => SetValue(MinLeftWidthProperty, value); }
    public double MaxLeftWidth { get => GetValue(MaxLeftWidthProperty); set => SetValue(MaxLeftWidthProperty, value); }
    public double MinRightWidth { get => GetValue(MinRightWidthProperty); set => SetValue(MinRightWidthProperty, value); }
    public string? PersistenceKey { get => GetValue(PersistenceKeyProperty); set => SetValue(PersistenceKeyProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private SplitStateService? _state;
    private bool _compact;
    private bool _initialized;
    private bool _usesDefaultLabel;
    private double? _lastPersistedWidth;
    private double _expandedLeftWidth;
    private double
        _responsiveMinimumLeftWidth;

    public PersistedSplitView()
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
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        SplitGrid.SizeChanged += (_, _) => EnsurePanesFit();
        Splitter.AddHandler(Thumb.DragCompletedEvent, OnSplitterDragCompleted,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    internal PersistedSplitView(SplitStateService state) : this() => _state = state;

    internal double CurrentLeftWidth => SplitGrid.ColumnDefinitions[0].Width.Value;
    internal double PreferredLeftWidth =>
        _expandedLeftWidth;
    internal double
        EffectiveMinimumLeftWidth =>
        Math.Max(
            MinLeftWidth,
            _responsiveMinimumLeftWidth);

    private void OnAttachedToVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs e)
    {
        _usesDefaultLabel =
            string.IsNullOrWhiteSpace(Label);
        if (_usesDefaultLabel)
            ApplyDefaultLabel();
        AvaloniaLocalizationResourceBridge.ResourcesApplied +=
            OnLocalizationResourcesApplied;
        InitializeWidth();
    }

    private void OnDetachedFromVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs e)
    {
        AvaloniaLocalizationResourceBridge.ResourcesApplied -=
            OnLocalizationResourcesApplied;
        Persist();
    }

    private void OnLocalizationResourcesApplied(
        object? sender,
        EventArgs e)
    {
        if (_usesDefaultLabel)
            ApplyDefaultLabel();
    }

    private void ApplyDefaultLabel()
    {
        if (Application.Current is not { } application)
            return;
        if (application.TryGetResource(
                AvaloniaLocalizationResourceBridge.ResourcePrefix +
                "Common.ResizePanes",
                ThemeVariant.Default,
                out object? value) &&
            value is string localized)
            Label = localized;
    }

    internal void CommitLeftWidth(double width)
    {
        _expandedLeftWidth =
            Math.Clamp(
                width,
                MinLeftWidth,
                MaxLeftWidth);
        SetLeftWidth(_expandedLeftWidth);
        Persist();
    }

    private void InitializeWidth()
    {
        _state ??= App.GetService<SplitStateService>();
        SplitGrid.ColumnDefinitions[0].MinWidth =
            EffectiveMinimumLeftWidth;
        SplitGrid.ColumnDefinitions[0].MaxWidth = MaxLeftWidth;
        SplitGrid.ColumnDefinitions[2].MinWidth = MinRightWidth;
        double width = PersistenceKey is null ? InitialLeftWidth : _state.Load(PersistenceKey) ?? InitialLeftWidth;
        _expandedLeftWidth = Math.Clamp(width, MinLeftWidth, MaxLeftWidth);
        if (_compact)
            ApplyCompactColumns();
        else
            SetLeftWidth(_expandedLeftWidth);
        _lastPersistedWidth = _expandedLeftWidth;
        _initialized = true;
    }

    private void SetLeftWidth(double width)
    {
        double minimum =
            EffectiveMinimumLeftWidth;
        double maximum = MaxLeftWidth;
        if (!_compact && SplitGrid.Bounds.Width > 0)
        {
            double splitterWidth = SplitGrid.ColumnDefinitions[1].ActualWidth;
            maximum = Math.Min(maximum,
                Math.Max(
                    minimum,
                    SplitGrid.Bounds.Width -
                    splitterWidth -
                    MinRightWidth));
        }
        SplitGrid.ColumnDefinitions[0].Width =
            new GridLength(
                Math.Clamp(
                    width,
                    minimum,
                    maximum));
    }

    private void EnsurePanesFit()
    {
        if (_compact || !SplitGrid.ColumnDefinitions[0].Width.IsAbsolute)
            return;
        SetLeftWidth(
            _expandedLeftWidth > 0
                ? _expandedLeftWidth
                : SplitGrid.ColumnDefinitions[0]
                    .Width.Value);
    }

    private void Persist()
    {
        if (!_initialized || _compact || PersistenceKey is null)
            return;
        double width = _expandedLeftWidth;
        if (!double.IsFinite(width) || width <= 0 ||
            (_lastPersistedWidth is double previous && Math.Abs(previous - width) < 0.5))
            return;
        (_state ??= App.GetService<SplitStateService>()).Save(PersistenceKey, width);
        _lastPersistedWidth = width;
    }

    internal void
        SetResponsiveMinimumLeftWidth(
            double minimum)
    {
        if (!double.IsFinite(minimum) ||
            minimum < 0 ||
            minimum > MaxLeftWidth)
            throw new
                ArgumentOutOfRangeException(
                    nameof(minimum));

        _responsiveMinimumLeftWidth =
            minimum;
        if (!_initialized ||
            _compact)
            return;

        SplitGrid.ColumnDefinitions[0]
            .MinWidth =
            EffectiveMinimumLeftWidth;
        SetLeftWidth(
            _expandedLeftWidth > 0
                ? _expandedLeftWidth
                : InitialLeftWidth);
    }

    public void SetCompact(bool compact)
    {
        if (_compact == compact)
            return;
        if (compact)
        {
            Persist();
        }
        _compact = compact;
        Splitter.IsVisible = !compact;
        if (compact)
            ApplyCompactColumns();
        else
            ApplyExpandedColumns();
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

    private void ApplyCompactColumns()
    {
        ColumnDefinition left = SplitGrid.ColumnDefinitions[0];
        ColumnDefinition divider = SplitGrid.ColumnDefinitions[1];
        ColumnDefinition right = SplitGrid.ColumnDefinitions[2];
        left.MinWidth = 0;
        left.MaxWidth = double.PositiveInfinity;
        left.Width = new GridLength(1, GridUnitType.Star);
        divider.Width = new GridLength(0);
        right.MinWidth = 0;
        right.Width = new GridLength(0);
    }

    private void ApplyExpandedColumns()
    {
        ColumnDefinition left = SplitGrid.ColumnDefinitions[0];
        ColumnDefinition divider = SplitGrid.ColumnDefinitions[1];
        ColumnDefinition right = SplitGrid.ColumnDefinitions[2];
        left.MinWidth =
            EffectiveMinimumLeftWidth;
        left.MaxWidth = MaxLeftWidth;
        divider.Width = new GridLength(10);
        right.MinWidth = MinRightWidth;
        right.Width = new GridLength(1, GridUnitType.Star);
        SetLeftWidth(_expandedLeftWidth > 0 ? _expandedLeftWidth : InitialLeftWidth);
    }

    public void ToggleCompactRight()
    {
        if (_compact)
            RightPresenter.IsVisible = !RightPresenter.IsVisible;
    }

    private void OnSplitterDragCompleted(
        object? sender,
        VectorEventArgs e)
    {
        if (!_compact)
        {
            ColumnDefinition left =
                SplitGrid.ColumnDefinitions[0];
            double width = left.Width.IsAbsolute
                ? left.Width.Value
                : left.ActualWidth;
            if (double.IsFinite(width) &&
                width > 0)
            {
                _expandedLeftWidth =
                    Math.Clamp(
                        width,
                        MinLeftWidth,
                        MaxLeftWidth);
            }
        }
        Persist();
    }

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
