using System.Windows.Input;
using System.ComponentModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Studio.Components;
using MusicLibraryManager.Studio.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.Studio.Tests;

public sealed class StudioComponentTests
{
    [Fact]
    public void Page_header_renders_studio_heading_and_actions()
    {
        using var context = new BunitContext();
        IRenderedComponent<PageHeader> cut = context.Render<PageHeader>(parameters => parameters
            .Add(component => component.Title, "Mastering queue")
            .Add(component => component.Subtitle, "Review the signal chain")
            .Add(component => component.Actions, builder => builder.AddMarkupContent(0, "<button>Render</button>")));

        Assert.Equal("Mastering queue", cut.Find("h1").TextContent);
        Assert.Contains("signal chain", cut.Markup);
        Assert.Equal("Render", cut.Find("button").TextContent);
    }

    [Fact]
    public void Command_button_tracks_can_execute_changes()
    {
        using var context = new BunitContext();
        var command = new MutableCommand();
        IRenderedComponent<StudioCommandButton> cut = context.Render<StudioCommandButton>(parameters => parameters
            .Add(component => component.Command, command)
            .AddChildContent("Apply"));

        Assert.True(cut.Find("button").HasAttribute("disabled"));
        command.Enabled = true;
        command.Notify();
        cut.WaitForAssertion(() => Assert.False(cut.Find("button").HasAttribute("disabled")));
        cut.Find("button").Click();
        Assert.Equal(1, command.ExecutionCount);
    }

    [Fact]
    public void Data_grid_sorts_typed_values_and_supports_selection()
    {
        using var context = new BunitContext();
        context.JSInterop.SetupVoid("studio.initializeGrid", _ => true);
        var rows = new[] { new Row("Second", 2), new Row("First", 1) };
        IList<StudioGridColumn<Row>> columns =
        [
            new() { Key = "name", Header = "Name", Value = row => row.Name },
            new() { Key = "number", Header = "Number", Value = row => row.Number },
        ];
        IReadOnlyList<Row> selected = [];
        int layoutChanges = 0;
        IRenderedComponent<StudioDataGrid<Row>> cut = context.Render<StudioDataGrid<Row>>(parameters => parameters
            .Add(component => component.Items, rows)
            .Add(component => component.Columns, columns)
            .Add(component => component.KeySelector, row => row.Name)
            .Add(component => component.OnLayoutChanged, EventCallback.Factory.Create(this, () => layoutChanges++))
            .Add(component => component.SelectionChanged, EventCallback.Factory.Create<IReadOnlyList<Row>>(this, value => selected = value)));

        cut.FindAll(".grid-sort")[1].Click();
        Assert.True(cut.FindAll(".grid-cell")[1].TextContent.Contains('1'));
        cut.Find(".grid-row").Click();
        Assert.Single(selected);
        Assert.Equal("First", selected[0].Name);
        Assert.All(cut.FindAll(".grid-cell-text"), cell => Assert.False(string.IsNullOrWhiteSpace(cell.TextContent)));

        cut.InvokeAsync(() => cut.Instance.ResizeColumn("number", 240));
        cut.WaitForAssertion(() => Assert.Contains("240px", cut.Find(".grid-row").GetAttribute("style")));
        int changesBeforeCommit = layoutChanges;
        cut.InvokeAsync(cut.Instance.CommitGridLayout);
        Assert.Equal(changesBeforeCommit + 1, layoutChanges);
        int changesBeforeReorder = layoutChanges;
        cut.FindAll(".column-drag-handle")[1].TriggerEvent("onpointerdown", new PointerEventArgs());
        cut.FindAll(".grid-header-cell")[0].TriggerEvent("onpointerenter", new PointerEventArgs());
        cut.FindAll(".grid-header-cell")[0].TriggerEvent("onpointerup", new PointerEventArgs());
        cut.WaitForAssertion(() => Assert.Equal("Number", cut.Find(".grid-sort span").TextContent));
        Assert.Equal(changesBeforeReorder + 1, layoutChanges);
    }

    [Fact]
    public void Split_pane_supports_pointer_and_keyboard_width_updates()
    {
        using var context = new BunitContext();
        var settings = new ComponentFakeSettings();
        var splitState = new StudioSplitStateService(settings);
        splitState.Save("test", 360);
        context.Services.AddSingleton(splitState);
        context.JSInterop.SetupVoid("studio.initializeSplit", _ => true);
        IRenderedComponent<ResizableSplitPane> cut = context.Render<ResizableSplitPane>(parameters => parameters
            .Add(component => component.InitialLeftWidth, 300)
            .Add(component => component.MinLeftWidth, 200)
            .Add(component => component.MaxLeftWidth, 500)
            .Add(component => component.PersistenceKey, "test")
            .Add(component => component.Left, builder => builder.AddContent(0, "Tree"))
            .Add(component => component.Right, builder => builder.AddContent(0, "Grid")));

        Assert.Contains("360px", cut.Find(".resizable-split").GetAttribute("style"));
        cut.InvokeAsync(() => cut.Instance.ResizeSplit(420));
        cut.WaitForAssertion(() => Assert.Contains("420px", cut.Find(".resizable-split").GetAttribute("style")));
        cut.InvokeAsync(cut.Instance.CommitSplitWidth);
        Assert.Equal(420, splitState.Load("test"));
        cut.Find(".splitter").KeyDown("ArrowLeft");
        cut.WaitForAssertion(() => Assert.Contains("396px", cut.Find(".resizable-split").GetAttribute("style")));
        Assert.Equal(396, splitState.Load("test"));
    }

    [Fact]
    public void Reactive_component_rerenders_for_external_view_model_notifications()
    {
        using var context = new BunitContext();
        var notifier = new TestNotifier { Value = "Queued" };
        IRenderedComponent<ReactiveProbe> cut = context.Render<ReactiveProbe>(parameters =>
            parameters.Add(component => component.Notifier, notifier));

        notifier.Value = "Rendered";
        notifier.Notify();

        cut.WaitForAssertion(() => Assert.Equal("Rendered", cut.Find("output").TextContent));
    }

    [Fact]
    public void Health_branch_disposition_propagates_and_virtualized_grid_reflects_leaf_change()
    {
        const string firstPath = @"C:\Music\Artist\Album\One.flac";
        const string secondPath = @"C:\Music\Artist\Album\Two.flac";
        AnalysisRepairItemViewModel[] items =
        [
            new(new AnalysisTagRepair(firstPath, TagFields.Title, "Old", "One", "Normalize", 1, DateTime.UtcNow)),
            new(new AnalysisTagRepair(secondPath, TagFields.Title, "Old", "Two", "Normalize", 1, DateTime.UtcNow)),
        ];
        IReadOnlyList<AnalysisRepairCategoryGroupViewModel> groups = AnalysisRepairCategoryGroupViewModel.Build(
            items,
            [new TrackRecord { Path=firstPath, Artist="Artist", Album="Album" }, new TrackRecord { Path=secondPath, Artist="Artist", Album="Album" }]);

        using var context = new BunitContext();
        context.JSInterop.SetupVoid("studio.initializeGrid", _ => true);
        IList<StudioGridColumn<AnalysisRepairItemViewModel>> columns =
        [new() { Key="Disposition", Header="Disposition", Value=item=>item.Disposition }];
        IRenderedComponent<StudioDataGrid<AnalysisRepairItemViewModel>> cut = context.Render<StudioDataGrid<AnalysisRepairItemViewModel>>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.Columns, columns)
            .Add(component => component.KeySelector, item => item.Path));

        groups[0].Disposition = AnalysisRepairDisposition.Deferred;

        Assert.All(items, item => Assert.Equal(AnalysisRepairDisposition.Deferred, item.Disposition));
        cut.WaitForAssertion(() => Assert.All(cut.FindAll(".grid-cell-text"), cell => Assert.Equal("Deferred", cell.TextContent)));
    }

    private sealed record Row(string Name, int Number);

    private sealed class MutableCommand : ICommand
    {
        public bool Enabled { get; set; }
        public int ExecutionCount { get; private set; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => Enabled;
        public void Execute(object? parameter) => ExecutionCount++;
        public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ComponentFakeSettings : IAppSettings
    {
        private readonly Dictionary<string, string> Preferences = [];
        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration => null;
        public event EventHandler? ConfigurationChanged;
        public AppConfigurationSnapshot GetSnapshot() => new(null, null, 0);
        public void LoadConfig(string path) => ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        public string? GetRememberedConfigPath() => null;
        public IReadOnlyList<string> RecentConfigPaths => [];
        public void ClearRecentConfigs() { }
        public string? GetPreference(string key) => Preferences.GetValueOrDefault(key);
        public void SetPreference(string key, string? value)
        {
            if (value is null) Preferences.Remove(key); else Preferences[key] = value;
        }
    }
}

public sealed class TestNotifier : INotifyPropertyChanged
{
    public string Value { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}

public sealed class ReactiveProbe : ReactiveComponentBase
{
    [Parameter, EditorRequired] public TestNotifier Notifier { get; set; } = null!;
    protected override void OnInitialized() => Observe(Notifier);
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "output");
        builder.AddContent(1, Notifier.Value);
        builder.CloseElement();
    }
}
