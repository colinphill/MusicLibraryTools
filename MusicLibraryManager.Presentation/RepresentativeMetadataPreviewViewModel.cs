using CommunityToolkit.Mvvm.ComponentModel;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

/// <summary>
/// Builds a small, non-applyable preview for one representative file while a
/// metadata recipe is being edited.
/// </summary>
public partial class RepresentativeMetadataPreviewViewModel :
    ObservableObject
{
    private readonly IMetadataOperationService _operations;
    private CancellationTokenSource? _cancellation;
    private int _generation;

    public RepresentativeMetadataPreviewViewModel(
        IMetadataOperationService operations)
    {
        _operations = operations;
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasPreview;

    [ObservableProperty]
    private string _status =
        "Choose a representative file and edit an operation.";

    [ObservableProperty]
    private string? _beforeText;

    [ObservableProperty]
    private string? _afterText;

    public void Schedule(
        string? path,
        Func<OperationRecipe> recipeFactory)
    {
        int generation = ++_generation;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            IsBusy = false;
            HasPreview = false;
            BeforeText = null;
            AfterText = null;
            Status =
                "Choose a representative file and edit an operation.";
            return;
        }

        var cancellation =
            new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        Status =
            $"Updating draft preview for {Path.GetFileName(path)}…";
        _ = RunAsync(
            generation,
            path,
            recipeFactory,
            cancellation);
    }

    private async Task RunAsync(
        int generation,
        string path,
        Func<OperationRecipe> recipeFactory,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(250),
                cancellation.Token);
            OperationRecipe recipe =
                recipeFactory();
            MetadataOperationPlan plan =
                await _operations.PreviewAsync(
                    [path],
                    recipe,
                    cancellation.Token);
            if (generation != _generation)
                return;
            MetadataFilePlan file =
                plan.Files.FirstOrDefault() ??
                throw new InvalidOperationException(
                    "The representative preview returned no file.");
            OperationIssue? blocker = file.Issues
                .FirstOrDefault(issue =>
                    issue.Severity ==
                    OperationIssueSeverity.Blocker);
            if (blocker is not null)
            {
                HasPreview = false;
                BeforeText = null;
                AfterText = null;
                Status =
                    $"Draft preview blocked: {blocker.Message}";
                return;
            }
            if (file.Differences.Length == 0)
            {
                HasPreview = false;
                BeforeText = null;
                AfterText = null;
                Status =
                    $"{Path.GetFileName(path)}: no metadata changes.";
                return;
            }

            BeforeText = string.Join(
                Environment.NewLine,
                file.Differences.Select(difference =>
                    $"{difference.Field.DisplayName}: " +
                    FormatValues(difference.Before)));
            AfterText = string.Join(
                Environment.NewLine,
                file.Differences.Select(difference =>
                    $"{difference.Field.DisplayName}: " +
                    FormatValues(difference.After)));
            HasPreview = true;
            Status =
                $"{Path.GetFileName(path)} · " +
                $"{file.Differences.Length:N0} proposed " +
                $"{(file.Differences.Length == 1 ? "field change" : "field changes")}";
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (generation != _generation)
                return;
            HasPreview = false;
            BeforeText = null;
            AfterText = null;
            Status =
                $"Draft preview unavailable: {error.Message}";
        }
        finally
        {
            if (generation == _generation)
            {
                IsBusy = false;
                _cancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private static string FormatValues(
        IReadOnlyList<string> values) =>
        values.Count == 0
            ? "(missing)"
            : string.Join(" · ", values);
}
