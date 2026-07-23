using System.Collections.Immutable;
using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IOperationRecipeStore
{
    IReadOnlyList<OperationRecipe> Recipes { get; }
    event EventHandler? Changed;
    void Save(OperationRecipe recipe);
    bool Delete(Guid id);
}

public sealed class OperationRecipeStore(IAppSettings settings) : IOperationRecipeStore
{
    private const string Preference = "manager.metadata.recipes.v1";
    private const int MaximumRecipes = 100;
    private readonly List<OperationRecipe> _recipes = Load(settings);

    public IReadOnlyList<OperationRecipe> Recipes => _recipes;
    public event EventHandler? Changed;

    public void Save(OperationRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (string.IsNullOrWhiteSpace(recipe.Name))
            throw new ArgumentException("A recipe name is required.", nameof(recipe));
        int index = _recipes.FindIndex(item => item.Id == recipe.Id);
        if (index >= 0)
            _recipes[index] = recipe;
        else
            _recipes.Insert(0, recipe);
        if (_recipes.Count > MaximumRecipes)
            _recipes.RemoveRange(MaximumRecipes, _recipes.Count - MaximumRecipes);
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Delete(Guid id)
    {
        int removed = _recipes.RemoveAll(recipe => recipe.Id == id);
        if (removed == 0)
            return false;
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static List<OperationRecipe> Load(IAppSettings settings)
    {
        try
        {
            string? json = settings.GetPreference(Preference);
            if (string.IsNullOrWhiteSpace(json))
                return [];
            StoredRecipes? stored = JsonSerializer.Deserialize<StoredRecipes>(json);
            return stored?.Version == 1 ? stored.Recipes.ToList() : [];
        }
        catch
        {
            return [];
        }
    }

    private void Persist() => settings.SetPreference(
        Preference,
        JsonSerializer.Serialize(new StoredRecipes(1, [.. _recipes])));

    private sealed record StoredRecipes(
        int Version,
        ImmutableArray<OperationRecipe> Recipes);
}
