using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Controls;

internal static class PersistedGridLayout
{
    public static void Configure(
        AppDataGrid grid,
        GridStateService state,
        string persistenceKey,
        IReadOnlyList<AppGridColumnDefinition> definitions)
    {
        grid.ConfigureColumns(ApplySnapshot(definitions, state.Load(persistenceKey)));
        grid.LayoutChanged += (_, _) => state.Save(persistenceKey,
            new GridSnapshot(grid.CaptureColumnLayout(), Sort: null));
    }

    internal static IReadOnlyList<AppGridColumnDefinition> ApplySnapshot(
        IReadOnlyList<AppGridColumnDefinition> definitions,
        GridSnapshot? snapshot)
    {
        var restored = definitions.ToList();
        if (snapshot is null)
            return restored;

        foreach (LibraryColumnState saved in snapshot.Columns.OrderBy(column => column.DisplayIndex))
        {
            int currentIndex = restored.FindIndex(column =>
                column.Key.Equals(saved.Key, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0)
                continue;
            AppGridColumnDefinition definition = restored[currentIndex];
            restored.RemoveAt(currentIndex);
            restored.Insert(Math.Clamp(saved.DisplayIndex, 0, restored.Count), definition with
            {
                Width = saved.Width is > 0 ? saved.Width.Value : definition.Width,
            });
        }
        return restored;
    }
}
