using global::Avalonia;
using global::Avalonia.Automation;
using global::Avalonia.Controls;
using global::Avalonia.LogicalTree;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Threading;

namespace MusicLibraryManager.Controls;

public partial class PageHeader : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Title), "");
    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Subtitle), "");
    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(Actions));
    public static readonly StyledProperty<object?> PrimaryActionProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(PrimaryAction));
    public static readonly StyledProperty<object?> SecondaryActionsProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(SecondaryActions));
    public static readonly StyledProperty<object?> MoreActionProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(MoreAction));
    public static readonly StyledProperty<bool> SecondaryOverflowEnabledProperty =
        AvaloniaProperty.Register<PageHeader, bool>(
            nameof(SecondaryOverflowEnabled));
    public static readonly StyledProperty<string> SecondaryOverflowLabelProperty =
        AvaloniaProperty.Register<PageHeader, string>(
            nameof(SecondaryOverflowLabel),
            "");
    public static readonly StyledProperty<string> SecondaryOverflowAutomationNameProperty =
        AvaloniaProperty.Register<PageHeader, string>(
            nameof(SecondaryOverflowAutomationName),
            "");
    public static readonly AttachedProperty<string?> OverflowHeaderProperty =
        AvaloniaProperty.RegisterAttached<
            PageHeader,
            Button,
            string?>("OverflowHeader");

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public object? PrimaryAction { get => GetValue(PrimaryActionProperty); set => SetValue(PrimaryActionProperty, value); }
    public object? SecondaryActions { get => GetValue(SecondaryActionsProperty); set => SetValue(SecondaryActionsProperty, value); }
    public object? MoreAction { get => GetValue(MoreActionProperty); set => SetValue(MoreActionProperty, value); }
    public bool SecondaryOverflowEnabled
    {
        get => GetValue(SecondaryOverflowEnabledProperty);
        set => SetValue(SecondaryOverflowEnabledProperty, value);
    }
    public string SecondaryOverflowLabel
    {
        get => GetValue(SecondaryOverflowLabelProperty);
        set => SetValue(SecondaryOverflowLabelProperty, value);
    }
    public string SecondaryOverflowAutomationName
    {
        get => GetValue(SecondaryOverflowAutomationNameProperty);
        set => SetValue(
            SecondaryOverflowAutomationNameProperty,
            value);
    }

    public static string? GetOverflowHeader(
        Button button) =>
        button.GetValue(OverflowHeaderProperty);

    public static void SetOverflowHeader(
        Button button,
        string? value) =>
        button.SetValue(
            OverflowHeaderProperty,
            value);

    private readonly MenuFlyout
        _secondaryOverflowFlyout = new();
    private readonly List<Button>
        _secondaryOverflowSources = [];
    private readonly Dictionary<Button, MenuItem>
        _secondaryOverflowItems = [];
    private bool _responsiveUpdatePending;
    private bool _secondaryOverflowActive;
    private Button? _lastFocusedOverflowSource;
    private bool _restoreOverflowSourceFocus;

    public PageHeader()
    {
        InitializeComponent();
        SecondaryOverflowButton.Flyout =
            _secondaryOverflowFlyout;
        SizeChanged += (_, _) =>
            ScheduleResponsiveLayout();
        TitleStack.SizeChanged +=
            (_, _) => ScheduleResponsiveLayout();
        ActionsPresenter.SizeChanged +=
            (_, _) => ScheduleResponsiveLayout();
        PrimaryActionPresenter.SizeChanged +=
            (_, _) => ScheduleResponsiveLayout();
        SecondaryActionsPresenter.SizeChanged +=
            (_, _) => ScheduleResponsiveLayout();
        MoreActionPresenter.SizeChanged +=
            (_, _) => ScheduleResponsiveLayout();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == ActionsProperty ||
                change.Property == PrimaryActionProperty ||
                change.Property == SecondaryActionsProperty ||
                change.Property == MoreActionProperty ||
                change.Property ==
                    SecondaryOverflowEnabledProperty ||
                change.Property ==
                    SecondaryOverflowLabelProperty ||
                change.Property ==
                    SecondaryOverflowAutomationNameProperty)
            {
                ScheduleResponsiveLayout();
            }
        };
        AttachedToVisualTree += (_, _) =>
            ScheduleResponsiveLayout();
        DetachedFromVisualTree += (_, _) =>
            ReplaceOverflowSources([]);
    }

    private void ScheduleResponsiveLayout()
    {
        if (_responsiveUpdatePending)
            return;
        _responsiveUpdatePending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _responsiveUpdatePending = false;
                ApplyResponsiveLayout();
            },
            DispatcherPriority.Loaded);
    }

    private void ApplyResponsiveLayout()
    {
        RefreshOverflowSources();
        double width = Math.Max(0, Bounds.Width);
        bool usesCommandBar =
            PrimaryAction is not null ||
            SecondaryActions is not null ||
            MoreAction is not null;
        ActionsPresenter.IsVisible =
            !usesCommandBar &&
            Actions is not null;
        CommandBar.IsVisible =
            usesCommandBar;

        if (!usesCommandBar)
        {
            ApplyLegacyLayout(width);
            Classes.Set(
                "compact-actions",
                false);
            return;
        }

        Grid.SetRow(CommandBar, 0);
        Grid.SetColumn(CommandBar, 1);
        Grid.SetColumnSpan(CommandBar, 1);
        CommandBar.HorizontalAlignment =
            global::Avalonia.Layout.HorizontalAlignment.Right;

        bool primaryVisible =
            IsContentVisible(PrimaryAction);
        bool secondaryVisible =
            IsContentVisible(SecondaryActions);
        bool moreVisible =
            IsContentVisible(MoreAction);
        bool hasVisibleOverflowCommand =
            HasVisibleOverflowCommand();
        bool canOverflowSecondary =
            SecondaryOverflowEnabled &&
            hasVisibleOverflowCommand &&
            !string.IsNullOrWhiteSpace(
                SecondaryOverflowLabel) &&
            !string.IsNullOrWhiteSpace(
                SecondaryOverflowAutomationName);

        PrimaryActionPresenter.IsVisible =
            PrimaryAction is not null;
        SecondaryActionsPresenter.IsVisible =
            SecondaryActions is not null;
        MoreActionPresenter.IsVisible =
            MoreAction is not null;

        double primaryWidth =
            MeasureContent(PrimaryAction);
        double secondaryWidth =
            MeasureContent(SecondaryActions);
        double moreWidth =
            MeasureContent(MoreAction);
        double actionWidth =
            SumCommandWidth(
                primaryVisible
                    ? primaryWidth
                    : 0,
                secondaryVisible
                    ? secondaryWidth
                    : 0,
                moreVisible
                    ? moreWidth
                    : 0);
        double inlineActionAllowance =
            CalculateInlineActionAllowance(width);
        bool exceedsInlineActionAllowance =
            actionWidth >
            inlineActionAllowance;
        bool contractCompact =
            secondaryVisible &&
            canOverflowSecondary &&
            exceedsInlineActionAllowance;
        bool legacyCompact =
            secondaryVisible &&
            !canOverflowSecondary &&
            moreVisible &&
            exceedsInlineActionAllowance;
        bool compact =
            contractCompact ||
            legacyCompact;
        object? focused =
            TopLevel.GetTopLevel(this)?
                .FocusManager?
                .GetFocusedElement();
        bool secondaryHadFocus =
            focused is Button focusedButton &&
            _secondaryOverflowSources
                .Contains(focusedButton);
        bool overflowHadFocus =
            ReferenceEquals(
                focused,
                SecondaryOverflowButton);
        bool overflowItemHadFocus =
            focused is MenuItem focusedItem &&
            _secondaryOverflowItems.Values
                .Contains(focusedItem);
        bool overflowFlyoutWasOpen =
            _secondaryOverflowFlyout.IsOpen;
        bool focusedInlineSourceBecameUnavailable =
            _restoreOverflowSourceFocus &&
            _lastFocusedOverflowSource is
                { IsVisible: false } hiddenSource &&
            (focused is null ||
             ReferenceEquals(
                 focused,
                 hiddenSource));

        SecondaryActionsPresenter.IsVisible =
            SecondaryActions is not null &&
            !compact;
        SecondaryOverflowButton.IsVisible =
            contractCompact;
        if (!contractCompact &&
            overflowFlyoutWasOpen)
        {
            _restoreOverflowSourceFocus = true;
            _secondaryOverflowFlyout.Hide();
        }
        CommandBar.MaxWidth =
            double.PositiveInfinity;
        Classes.Set(
            "compact-actions",
            compact);
        Classes.Set(
            "stacked-actions",
            false);

        if (contractCompact &&
            !_secondaryOverflowActive &&
            (secondaryHadFocus ||
             (focused is null &&
              _lastFocusedOverflowSource
                  is not null)))
        {
            _restoreOverflowSourceFocus =
                true;
            SecondaryOverflowButton.Focus();
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (TopLevel
                            .GetTopLevel(this)?
                            .FocusManager?
                            .GetFocusedElement()
                        is null)
                    {
                        SecondaryOverflowButton
                            .Focus();
                    }
                },
                DispatcherPriority.Input);
        }
        else if (!contractCompact &&
                 ((_secondaryOverflowActive &&
                   (overflowHadFocus ||
                    overflowItemHadFocus ||
                    overflowFlyoutWasOpen ||
                    (focused is null &&
                     _restoreOverflowSourceFocus))) ||
                  focusedInlineSourceBecameUnavailable))
        {
            Button? firstVisible =
                _lastFocusedOverflowSource
                    is { IsVisible: true }
                    ? _lastFocusedOverflowSource
                    : _secondaryOverflowSources
                        .FirstOrDefault(
                            button =>
                                button.IsVisible) ??
                      FindFirstFocusableButton(
                          PrimaryAction) ??
                      FindFirstFocusableButton(
                          MoreAction);
            if (firstVisible is not null)
            {
                firstVisible.Focus();
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (TopLevel
                                .GetTopLevel(this)?
                                .FocusManager?
                                .GetFocusedElement()
                            is null)
                        {
                            firstVisible.Focus();
                        }
                    },
                    DispatcherPriority.Input);
            }
            _restoreOverflowSourceFocus =
                false;
        }
        _secondaryOverflowActive =
            contractCompact;
    }

    private void ApplyLegacyLayout(
        double width)
    {
        double actionWidth =
            MeasureContent(Actions);
        double inlineActionAllowance =
            CalculateInlineActionAllowance(width);
        bool compact =
            actionWidth >
            inlineActionAllowance;
        Grid.SetRow(ActionsPresenter, compact ? 1 : 0);
        Grid.SetColumn(ActionsPresenter, compact ? 0 : 1);
        Grid.SetColumnSpan(ActionsPresenter, compact ? 2 : 1);
        ActionsPresenter.HorizontalAlignment = compact
            ? global::Avalonia.Layout.HorizontalAlignment.Left
            : global::Avalonia.Layout.HorizontalAlignment.Right;
        ActionsPresenter.MaxWidth = compact
            ? width
            : inlineActionAllowance;
        Classes.Set("stacked-actions", compact);
    }

    private double CalculateInlineActionAllowance(
        double width)
    {
        double desiredTitleWidth = Math.Max(
            TitleBlock.DesiredSize.Width,
            SubtitleBlock.DesiredSize.Width);
        // Retain a proportional title lane even before the localized title
        // has completed its first measure. This prevents a large command set
        // from claiming the entire row during the initial layout pass.
        double titleAllowance = Math.Min(
            width * .54,
            Math.Max(
                desiredTitleWidth,
                width * .46));
        double inlineActionAllowance = Math.Max(
            0,
            width -
            titleAllowance -
            HeaderGrid.ColumnSpacing);
        return inlineActionAllowance;
    }

    private static double MeasureContent(
        object? content)
    {
        if (content is not Control control ||
            !control.IsVisible)
        {
            return 0;
        }

        control.Measure(
            new Size(
                double.PositiveInfinity,
                double.PositiveInfinity));
        return control.DesiredSize.Width;
    }

    private static bool IsContentVisible(
        object? content) =>
        content is Control control &&
        control.IsVisible;

    private static Button?
        FindFirstFocusableButton(
            object? content)
    {
        if (content is not Control control)
            return null;
        if (control is Button button &&
            button.IsVisible &&
            button.IsEnabled)
        {
            return button;
        }

        return control
            .GetLogicalDescendants()
            .OfType<Button>()
            .FirstOrDefault(
                candidate =>
                    candidate.IsVisible &&
                    candidate.IsEnabled);
    }

    private void RefreshOverflowSources()
    {
        Button[] sources =
            SecondaryActions is Control control
                ? EnumerateOverflowSources(
                        control)
                    .Distinct()
                    .ToArray()
                : [];
        if (!_secondaryOverflowSources
                .SequenceEqual(sources))
        {
            ReplaceOverflowSources(
                sources);
        }
        foreach (Button source in
                 _secondaryOverflowSources)
        {
            SynchronizeOverflowItem(
                source);
        }
    }

    private static IEnumerable<Button>
        EnumerateOverflowSources(
            Control root)
    {
        if (root is Button rootButton &&
            !string.IsNullOrWhiteSpace(
                GetOverflowHeader(
                    rootButton)))
        {
            yield return rootButton;
        }

        foreach (Button button in
                 root.GetLogicalDescendants()
                     .OfType<Button>())
        {
            if (!string.IsNullOrWhiteSpace(
                    GetOverflowHeader(button)))
            {
                yield return button;
            }
        }
    }

    private void ReplaceOverflowSources(
        IReadOnlyCollection<Button> sources)
    {
        foreach (Button source in
                 _secondaryOverflowSources)
        {
            source.PropertyChanged -=
                OnOverflowSourcePropertyChanged;
        }
        _secondaryOverflowSources.Clear();
        _secondaryOverflowItems.Clear();
        _secondaryOverflowFlyout.Items
            .Clear();
        if (_lastFocusedOverflowSource
                is not null &&
            !sources.Contains(
                _lastFocusedOverflowSource))
        {
            _lastFocusedOverflowSource =
                null;
            _restoreOverflowSourceFocus =
                false;
        }

        foreach (Button source in sources)
        {
            var item = new MenuItem();
            source.PropertyChanged +=
                OnOverflowSourcePropertyChanged;
            _secondaryOverflowSources.Add(
                source);
            _secondaryOverflowItems.Add(
                source,
                item);
            _secondaryOverflowFlyout.Items
                .Add(item);
            SynchronizeOverflowItem(
                source);
        }
    }

    private void OnOverflowSourcePropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not Button source)
            return;
        if (e.Property ==
                IsVisibleProperty &&
            !source.IsVisible &&
            ReferenceEquals(
                source,
                _lastFocusedOverflowSource))
        {
            object? focused =
                TopLevel.GetTopLevel(this)?
                    .FocusManager?
                    .GetFocusedElement();
            if (source.IsFocused ||
                focused is null ||
                ReferenceEquals(
                    focused,
                    source))
            {
                _restoreOverflowSourceFocus =
                    true;
            }
        }
        if (e.Property ==
                IsFocusedProperty &&
            source.IsFocused)
        {
            _lastFocusedOverflowSource =
                source;
        }
        if ((e.Property ==
                 IsVisibleProperty ||
             e.Property ==
                 OverflowHeaderProperty) &&
            _secondaryOverflowFlyout.IsOpen &&
            !HasVisibleOverflowCommand())
        {
            _restoreOverflowSourceFocus =
                true;
            _secondaryOverflowFlyout.Hide();
        }
        if (e.Property ==
            OverflowHeaderProperty)
        {
            ScheduleResponsiveLayout();
            return;
        }
        SynchronizeOverflowItem(
            source);
        if (e.Property ==
                IsVisibleProperty ||
            e.Property ==
                IsEnabledProperty ||
            e.Property ==
                BoundsProperty)
        {
            ScheduleResponsiveLayout();
        }
    }

    private bool HasVisibleOverflowCommand() =>
        _secondaryOverflowSources.Any(
            button =>
                button.IsVisible &&
                !string.IsNullOrWhiteSpace(
                    GetOverflowHeader(button)));

    private void SynchronizeOverflowItem(
        Button source)
    {
        if (!_secondaryOverflowItems
                .TryGetValue(
                    source,
                    out MenuItem? item))
        {
            return;
        }

        string header =
            GetOverflowHeader(source) ??
            "";
        item.Header = header;
        item.Command = source.Command;
        item.CommandParameter =
            source.CommandParameter;
        item.IsEnabled =
            source.IsEnabled;
        item.IsVisible =
            source.IsVisible;
        AutomationProperties.SetName(
            item,
            string.IsNullOrWhiteSpace(
                AutomationProperties
                    .GetName(source))
                ? header
                : AutomationProperties
                    .GetName(source));
    }

    private double SumCommandWidth(
        params double[] widths)
    {
        double total = 0;
        int visibleCount = 0;
        foreach (double commandWidth in widths)
        {
            if (commandWidth <= 0)
                continue;
            total += commandWidth;
            visibleCount++;
        }

        if (visibleCount > 1)
        {
            total +=
                CommandBar.ColumnSpacing *
                (visibleCount - 1);
        }
        return total;
    }
}
