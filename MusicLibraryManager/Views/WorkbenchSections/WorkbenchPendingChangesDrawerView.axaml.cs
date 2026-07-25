using Avalonia.Controls;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchPendingChangesDrawerView :
    UserControl
{
    public WorkbenchPendingChangesDrawerView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            bool compactHeight =
                Bounds.Height > 0 &&
                Bounds.Height < 430;
            SupportingText.IsVisible =
                !compactHeight;
            StatusText.IsVisible =
                !compactHeight;
        };
    }

    public event EventHandler? CloseRequested;

    public Control InitialFocus =>
        WorkbenchPendingChangesCloseButton;

    private void OnClose(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e) =>
        CloseRequested?.Invoke(
            this,
            EventArgs.Empty);
}
