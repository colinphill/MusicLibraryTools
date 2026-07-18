using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Pages;

public sealed partial class HealthPage : UserControl
{
    private readonly AnalyzerViewModel _viewModel;
    private readonly List<AnalysisTreeItem> _treeItems = [];

    public HealthPage()
    {
        InitializeComponent();
        _viewModel = App.GetService<AnalyzerViewModel>();
        DataContext = _viewModel;
        Loaded += HealthPage_Loaded;
        Unloaded += HealthPage_Unloaded;
    }

    private void HealthPage_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        RebuildTrees();
    }

    private void HealthPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ClearTreeItems();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SelectedRun raises FindingGroups first; all three immutable hierarchies are ready then.
        if (e.PropertyName == nameof(AnalyzerViewModel.FindingGroups))
            RebuildTrees();
    }

    private void RebuildTrees()
    {
        ClearTreeItems();
        FindingTree.RootNodes.Clear();
        RepairTree.RootNodes.Clear();
        RepresentationTree.RootNodes.Clear();

        foreach (AnalysisProblemGroupViewModel problem in _viewModel.FindingGroups)
        {
            TreeViewNode problemNode = CreateNode(problem, problem.Problem, problem.Count);
            foreach (AnalysisArtistGroupViewModel artist in problem.Artists)
            {
                TreeViewNode artistNode = CreateNode(artist, artist.Artist, artist.Count);
                foreach (AnalysisAlbumGroupViewModel album in artist.Albums)
                    artistNode.Children.Add(CreateNode(album, album.Album, album.Count));
                problemNode.Children.Add(artistNode);
            }
            FindingTree.RootNodes.Add(problemNode);
        }

        foreach (AnalysisRepairCategoryGroupViewModel category in _viewModel.RepairGroups)
        {
            TreeViewNode categoryNode = CreateNode(category, category.Category, category.Count);
            foreach (AnalysisRepairArtistGroupViewModel artist in category.Artists)
            {
                TreeViewNode artistNode = CreateNode(artist, artist.Artist, artist.Count);
                foreach (AnalysisRepairAlbumGroupViewModel album in artist.Albums)
                    artistNode.Children.Add(CreateNode(album, album.Album, album.Count));
                categoryNode.Children.Add(artistNode);
            }
            RepairTree.RootNodes.Add(categoryNode);
        }

        foreach (RepresentationRepairCategoryGroupViewModel category in _viewModel.RepresentationActionGroups)
        {
            TreeViewNode categoryNode = CreateNode(category, category.Category, category.Count);
            foreach (RepresentationRepairArtistGroupViewModel artist in category.Artists)
            {
                TreeViewNode artistNode = CreateNode(artist, artist.Artist, artist.Count);
                foreach (RepresentationRepairAlbumGroupViewModel album in artist.Albums)
                    artistNode.Children.Add(CreateNode(album, album.Album, album.Count));
                categoryNode.Children.Add(artistNode);
            }
            RepresentationTree.RootNodes.Add(categoryNode);
        }
    }

    private TreeViewNode CreateNode(object model, string label, int count)
    {
        var item = new AnalysisTreeItem(model, label, count);
        _treeItems.Add(item);
        return new TreeViewNode { Content = item };
    }

    private void ClearTreeItems()
    {
        foreach (AnalysisTreeItem item in _treeItems)
            item.Dispose();
        _treeItems.Clear();
    }

    private void FindingTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
        => _viewModel.SelectedFindingNode = (sender.SelectedNode?.Content as AnalysisTreeItem)?.Model;

    private void RepairTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
        => _viewModel.SelectedRepairNode = (sender.SelectedNode?.Content as AnalysisTreeItem)?.Model;

    private void RepresentationTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
        => _viewModel.SelectedRepresentationNode = (sender.SelectedNode?.Content as AnalysisTreeItem)?.Model;
}

public sealed class AnalysisTreeItem : INotifyPropertyChanged, IDisposable
{
    private readonly INotifyPropertyChanged? _observable;

    public AnalysisTreeItem(object model, string label, int count)
    {
        Model = model;
        Label = label;
        Count = count;
        _observable = model as INotifyPropertyChanged;
        if (_observable is not null)
            _observable.PropertyChanged += Model_PropertyChanged;
    }

    public object Model { get; }
    public string Label { get; }
    public int Count { get; }
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisRepairDisposition>();

    public bool HasAutomatedRepair => Model switch
    {
        AnalysisRepairCategoryGroupViewModel value => value.Artists.Any(ContainsAutomatedRepair),
        AnalysisRepairArtistGroupViewModel value => value.Albums.Any(ContainsAutomatedRepair),
        AnalysisRepairAlbumGroupViewModel value => value.Items.Any(item => item.CanChangeDisposition),
        RepresentationRepairCategoryGroupViewModel value => value.Artists.Any(ContainsAutomatedRepair),
        RepresentationRepairArtistGroupViewModel value => value.Albums.Any(ContainsAutomatedRepair),
        RepresentationRepairAlbumGroupViewModel value =>
            value.Items.Any(item => item.CanChangeDisposition),
        _ => false,
    };

    public AnalysisRepairDisposition Disposition
    {
        get => Model switch
        {
            AnalysisProblemGroupViewModel value => value.Disposition,
            AnalysisArtistGroupViewModel value => value.Disposition,
            AnalysisAlbumGroupViewModel value => value.Disposition,
            AnalysisRepairCategoryGroupViewModel value => value.Disposition,
            AnalysisRepairArtistGroupViewModel value => value.Disposition,
            AnalysisRepairAlbumGroupViewModel value => value.Disposition,
            RepresentationRepairCategoryGroupViewModel value => value.Disposition,
            RepresentationRepairArtistGroupViewModel value => value.Disposition,
            RepresentationRepairAlbumGroupViewModel value => value.Disposition,
            _ => AnalysisRepairDisposition.Mixed,
        };
        set
        {
            switch (Model)
            {
                case AnalysisProblemGroupViewModel model: model.Disposition = value; break;
                case AnalysisArtistGroupViewModel model: model.Disposition = value; break;
                case AnalysisAlbumGroupViewModel model: model.Disposition = value; break;
                case AnalysisRepairCategoryGroupViewModel model: model.Disposition = value; break;
                case AnalysisRepairArtistGroupViewModel model: model.Disposition = value; break;
                case AnalysisRepairAlbumGroupViewModel model: model.Disposition = value; break;
                case RepresentationRepairCategoryGroupViewModel model: model.Disposition = value; break;
                case RepresentationRepairArtistGroupViewModel model: model.Disposition = value; break;
                case RepresentationRepairAlbumGroupViewModel model: model.Disposition = value; break;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Disposition))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Disposition)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAutomatedRepair)));
    }

    private static bool ContainsAutomatedRepair(AnalysisRepairArtistGroupViewModel artist) =>
        artist.Albums.Any(ContainsAutomatedRepair);

    private static bool ContainsAutomatedRepair(AnalysisRepairAlbumGroupViewModel album) =>
        album.Items.Any(item => item.CanChangeDisposition);

    private static bool ContainsAutomatedRepair(RepresentationRepairArtistGroupViewModel artist) =>
        artist.Albums.Any(ContainsAutomatedRepair);

    private static bool ContainsAutomatedRepair(RepresentationRepairAlbumGroupViewModel album) =>
        album.Items.Any(item => item.CanChangeDisposition);

    public void Dispose()
    {
        if (_observable is not null)
            _observable.PropertyChanged -= Model_PropertyChanged;
    }
}
