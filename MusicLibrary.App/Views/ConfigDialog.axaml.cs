using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MusicLibrary.App.ViewModels;

namespace MusicLibrary.App.Views;

public partial class ConfigDialog : Window
{
    public ConfigDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConfigDialogViewModel vm)
                vm.CloseRequested += path => Close(path);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
