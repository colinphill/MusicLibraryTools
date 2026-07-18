using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;

namespace MusicLibraryManager.Studio.Avalonia.Controls;

public partial class StudioPageHeader : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<StudioPageHeader, string>(nameof(Title), "");
    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<StudioPageHeader, string>(nameof(Subtitle), "");
    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<StudioPageHeader, object?>(nameof(Actions));

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public StudioPageHeader() => InitializeComponent();
}
