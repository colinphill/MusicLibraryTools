using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class WorkbenchShortcutStoreTests
{
    [Fact]
    public void StoreRoundTripsCommandAndRecipeBindings()
    {
        using var temp = new TempDirectory();
        string state = Path.Combine(temp.Path, "settings.json");
        var store = new WorkbenchShortcutStore(
            new AppSettings(state));
        Guid recipeId = Guid.NewGuid();
        WorkbenchShortcutBinding[] bindings =
        [
            new(
                Guid.NewGuid(),
                "Ctrl+Shift+P",
                WorkbenchShortcutTargetKind.Command,
                WorkbenchShortcutCommand.PreviewCurrentRecipe,
                TargetLabel: "Preview current recipe"),
            new(
                Guid.NewGuid(),
                "Alt+F8",
                WorkbenchShortcutTargetKind.Recipe,
                RecipeId: recipeId,
                TargetLabel: "Normalize album"),
        ];

        store.Save(bindings);

        IReadOnlyList<WorkbenchShortcutBinding> loaded =
            new WorkbenchShortcutStore(
                new AppSettings(state)).Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(
            WorkbenchShortcutCommand.PreviewCurrentRecipe,
            loaded[0].Command);
        Assert.Equal(recipeId, loaded[1].RecipeId);
    }

    [Fact]
    public void StoreRejectsBindingsWithoutExactlyOneTarget()
    {
        using var temp = new TempDirectory();
        var store = new WorkbenchShortcutStore(
            new AppSettings(
                Path.Combine(temp.Path, "settings.json")));
        var invalid = new WorkbenchShortcutBinding(
            Guid.NewGuid(),
            "Ctrl+P",
            WorkbenchShortcutTargetKind.Command,
            RecipeId: Guid.NewGuid());

        Assert.Throws<ArgumentException>(
            () => store.Save([invalid]));
    }

    [Fact]
    public void ServiceRegistrationIncludesShortcutStore()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<WorkbenchShortcutStore>(
            provider.GetRequiredService<IWorkbenchShortcutStore>());
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mlm-shortcut-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
