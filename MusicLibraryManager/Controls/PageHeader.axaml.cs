using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;

namespace MusicLibraryManager.Controls;

public partial class PageHeader : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Title), "");
    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Subtitle), "");
    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(Actions));

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public PageHeader()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        bool compact = Bounds.Width < 760;
        Grid.SetRow(ActionsPresenter, compact ? 1 : 0);
        Grid.SetColumn(ActionsPresenter, compact ? 0 : 1);
        Grid.SetColumnSpan(ActionsPresenter, compact ? 2 : 1);
        ActionsPresenter.HorizontalAlignment = compact
            ? global::Avalonia.Layout.HorizontalAlignment.Left
            : global::Avalonia.Layout.HorizontalAlignment.Right;
    }
}
