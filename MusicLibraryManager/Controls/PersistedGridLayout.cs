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
                Visible = saved.Visible,
            });
        }
        return restored;
    }

    internal static IReadOnlyList<LibraryColumnState> CaptureSnapshotColumns(
        IReadOnlyList<AppGridColumnDefinition> definitions,
        IReadOnlyList<LibraryColumnState> visibleLayout)
    {
        Dictionary<string, LibraryColumnState> visibleByKey =
            visibleLayout.ToDictionary(
                column => column.Key,
                StringComparer.OrdinalIgnoreCase);
        LibraryColumnState[] reorderedVisible =
            visibleLayout
                .Where(column =>
                    definitions.Any(definition =>
                        definition.Key.Equals(
                            column.Key,
                            StringComparison.OrdinalIgnoreCase)))
                .OrderBy(column => column.DisplayIndex)
                .ToArray();
        int visibleIndex = 0;
        var result =
            new List<LibraryColumnState>(
                definitions.Count);

        for (int displayIndex = 0;
             displayIndex < definitions.Count;
             displayIndex++)
        {
            AppGridColumnDefinition definition =
                definitions[displayIndex];
            if (visibleByKey.ContainsKey(
                    definition.Key) &&
                visibleIndex < reorderedVisible.Length)
            {
                result.Add(
                    reorderedVisible[visibleIndex++] with
                    {
                        DisplayIndex = displayIndex,
                        Visible = true,
                    });
                continue;
            }

            result.Add(
                new LibraryColumnState(
                    definition.Key,
                    definition.Width,
                    displayIndex,
                    false));
        }

        return result;
    }
}
