using global::Avalonia.Controls;
using global::Avalonia.Layout;
using global::Avalonia.Media;

namespace MusicLibraryManager.Views;

public sealed class PlaceholderView : UserControl
{
    public PlaceholderView(string title)
    {
        Content = new StackPanel
        {
            Margin = new global::Avalonia.Thickness(26, 22),
            Spacing = 5,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 27,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = "Native Avalonia view migration in progress.",
                    Classes = { "muted" },
                },
            },
        };
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }
}
