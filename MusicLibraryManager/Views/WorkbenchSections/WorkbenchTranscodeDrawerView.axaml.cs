using Avalonia.Controls;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchTranscodeDrawerView : UserControl
{
    public WorkbenchTranscodeDrawerView()
    {
        InitializeComponent();
    }

    public event EventHandler? CloseRequested;

    public Control InitialFocus =>
        WorkbenchTranscodeCloseButton;

    private void OnClose(
        object? sender,
        global::Avalonia.Interactivity.RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
