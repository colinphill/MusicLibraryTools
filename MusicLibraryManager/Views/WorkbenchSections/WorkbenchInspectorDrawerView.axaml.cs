using Avalonia.Controls;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchInspectorDrawerView :
    UserControl
{
    public WorkbenchInspectorDrawerView()
    {
        InitializeComponent();
        WorkbenchInspectorView.CloseRequested +=
            (_, _) =>
                CloseRequested?.Invoke(
                    this,
                    EventArgs.Empty);
    }

    public event EventHandler? CloseRequested;

    public Control InitialFocus =>
        WorkbenchInspectorView.CloseButton;
}
