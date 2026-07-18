using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLibrary.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace MusicLibraryManager.Pages;

public sealed partial class IngestPage : UserControl
{
    private readonly IngestViewModel _viewModel;

    public IngestPage()
    {
        InitializeComponent();
        _viewModel = App.GetService<IngestViewModel>();
        DataContext = _viewModel;
    }

    private void IngestPage_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void IngestPage_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            if (items.FirstOrDefault() is { } item)
                _viewModel.SetDroppedSource(item.Path);
        }
        e.Handled = true;
    }
}
