using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Threading;
using MusicLibraryManager.Studio.Services;

namespace MusicLibraryManager.Studio.Avalonia.Views;

public partial class StudioDialogHost : UserControl
{
    private readonly StudioDialogService _dialogs;

    public StudioDialogHost()
    {
        InitializeComponent();
        _dialogs = App.GetService<StudioDialogService>();
        _dialogs.Changed += DialogChanged;
        DetachedFromVisualTree += (_, _) => _dialogs.Changed -= DialogChanged;
    }

    private void DialogChanged() => Dispatcher.UIThread.Post(RenderDialog);

    private void RenderDialog()
    {
        StudioDialogRequest? request = _dialogs.Current;
        IsVisible = request is not null;
        if (request is null)
            return;
        DialogTitle.Text = request.Title;
        DialogButtons.Children.Clear();
        DialogCard.Width = request is StudioFieldsRequest ? 980 : 560;
        switch (request)
        {
            case StudioFieldsRequest fields:
                DialogContent.Content = new FieldsEditorView(fields.ViewModel);
                break;
            case StudioMessageRequest message:
                DialogContent.Content = Message(message.Message);
                DialogButtons.Children.Add(ActionButton("OK", true, primary: true));
                break;
            case StudioConfirmRequest confirm:
                DialogContent.Content = Message(confirm.Message);
                DialogButtons.Children.Add(ActionButton("Cancel", false));
                DialogButtons.Children.Add(ActionButton(confirm.PrimaryText, true, primary: true));
                break;
        }
        Focus();
    }

    private static Control Message(string text) => new ScrollViewer
    {
        MaxHeight = 560,
        Content = new TextBlock
        {
            Text = text,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Classes = { "muted" },
        },
    };

    private Button ActionButton(string text, bool result, bool primary = false)
    {
        var button = new Button { Content = text };
        button.Classes.Add("studio");
        if (primary)
            button.Classes.Add("primary");
        button.Click += (_, _) => _dialogs.Complete(result);
        return button;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => _dialogs.Complete(false);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _dialogs.Complete(false);
            e.Handled = true;
        }
    }
}
