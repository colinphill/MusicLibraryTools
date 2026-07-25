using global::Avalonia;
using global::Avalonia.Controls;
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

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public object? PrimaryAction { get => GetValue(PrimaryActionProperty); set => SetValue(PrimaryActionProperty, value); }
    public object? SecondaryActions { get => GetValue(SecondaryActionsProperty); set => SetValue(SecondaryActionsProperty, value); }
    public object? MoreAction { get => GetValue(MoreActionProperty); set => SetValue(MoreActionProperty, value); }

    public PageHeader()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        TitleStack.SizeChanged +=
            (_, _) => ApplyResponsiveLayout();
        ActionsPresenter.SizeChanged +=
            (_, _) => ApplyResponsiveLayout();
        PrimaryActionPresenter.SizeChanged +=
            (_, _) => ApplyResponsiveLayout();
        SecondaryActionsPresenter.SizeChanged +=
            (_, _) => ApplyResponsiveLayout();
        MoreActionPresenter.SizeChanged +=
            (_, _) => ApplyResponsiveLayout();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == ActionsProperty ||
                change.Property == PrimaryActionProperty ||
                change.Property == SecondaryActionsProperty ||
                change.Property == MoreActionProperty)
            {
                Dispatcher.UIThread.Post(
                    ApplyResponsiveLayout,
                    DispatcherPriority.Loaded);
            }
        };
        AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(
                ApplyResponsiveLayout,
                DispatcherPriority.Loaded);
    }

    private void ApplyResponsiveLayout()
    {
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
        bool compact =
            secondaryVisible &&
            moreVisible &&
            actionWidth >
            inlineActionAllowance;

        SecondaryActionsPresenter.IsVisible =
            SecondaryActions is not null &&
            !compact;
        CommandBar.MaxWidth =
            double.PositiveInfinity;
        Classes.Set(
            "compact-actions",
            compact);
        Classes.Set(
            "stacked-actions",
            false);
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
