using Microsoft.UI.Xaml.Controls;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Dialogs;

public sealed partial class FieldsDialog : ContentDialog
{
    private readonly FieldsDialogViewModel _viewModel;
    private bool _saved;

    public FieldsDialog(FieldsDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += CloseRequested;
        Closed += FieldsDialog_Closed;
    }

    public async Task<bool> ShowEditorAsync()
    {
        await ShowAsync();
        return _saved;
    }

    private void CloseRequested(bool saved)
    {
        _saved = saved;
        Hide();
    }

    private void FieldsDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        => _viewModel.CloseRequested -= CloseRequested;
}
