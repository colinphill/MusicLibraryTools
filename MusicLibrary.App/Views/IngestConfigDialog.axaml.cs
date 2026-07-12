using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MusicLibrary.App.ViewModels;

namespace MusicLibrary.App.Views;

public partial class IngestConfigDialog : Window
{
    public IngestConfigDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is IngestConfigDialogViewModel vm)
                vm.CloseRequested += path => Close(path);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
