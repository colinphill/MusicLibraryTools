using Microsoft.AspNetCore.Components;

namespace MusicLibraryManager.Studio.Components;

public sealed class StudioGridColumn<TItem>
{
    public required string Key { get; init; }
    public required string Header { get; init; }
    public required Func<TItem, object?> Value { get; init; }
    public RenderFragment<TItem>? Template { get; init; }
    public double Width { get; set; } = 160;
    public double MinWidth { get; init; } = 64;
    public bool Visible { get; set; } = true;
    public bool Sortable { get; init; } = true;
}
