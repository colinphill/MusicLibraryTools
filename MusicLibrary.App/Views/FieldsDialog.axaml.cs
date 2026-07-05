using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MusicLibrary.App.ViewModels;

namespace MusicLibrary.App.Views;

public partial class FieldsDialog : Window
{
    public FieldsDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FieldsDialogViewModel vm)
                vm.CloseRequested += result => Close(result);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
