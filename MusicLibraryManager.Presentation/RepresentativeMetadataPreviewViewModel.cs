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
    private readonly ILocalizationService? _localization;
    private CancellationTokenSource? _cancellation;
    private int _generation;
    private string? _statusKey;
    private object?[] _statusArguments = [];
    private long? _statusCount;

    public RepresentativeMetadataPreviewViewModel(
        IMetadataOperationService operations,
        ILocalizationService? localization = null)
    {
        _operations = operations;
        _localization = localization;
        SetStatus(
            "Workbench.Representative.Status.ChooseFile");
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasPreview;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasStatusDiagnosticDetail))]
    private string? _statusDiagnosticDetail;

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
            SetStatus(
                "Workbench.Representative.Status.ChooseFile");
            return;
        }

        var cancellation =
            new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        SetStatus(
            "Workbench.Representative.Status.Updating",
            Path.GetFileName(path));
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
                    L(
                        "Workbench.Representative.Error.NoFile"));
            OperationIssue? blocker = file.Issues
                .FirstOrDefault(issue =>
                    issue.Severity ==
                    OperationIssueSeverity.Blocker);
            if (blocker is not null)
            {
                HasPreview = false;
                BeforeText = null;
                AfterText = null;
                SetFailure(
                    "Workbench.Representative.Status.Blocked",
                    blocker.Message);
                return;
            }
            if (file.Differences.Length == 0)
            {
                HasPreview = false;
                BeforeText = null;
                AfterText = null;
                SetStatus(
                    "Workbench.Representative.Status.NoChanges",
                    Path.GetFileName(path));
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
            SetCountStatus(
                "Workbench.Representative.Status.Ready",
                file.Differences.Length,
                Path.GetFileName(path));
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
            SetFailure(
                "Workbench.Representative.Status.Unavailable",
                error.Message);
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
            ? LocalizedText.Get(
                "Workbench.Representative.Missing")
            : string.Join(
                LocalizedText.Get(
                    "Workbench.Representative.ValueSeparator"),
                values);

    public bool HasStatusDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(StatusDiagnosticDetail);

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);

    private string LC(
        string key,
        long count,
        params object?[] arguments) =>
        _localization?.FormatCount(
            key,
            count,
            arguments) ??
        LocalizedText.FormatCount(
            key,
            count,
            arguments);

    private void SetStatus(
        string key,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusCount = null;
        Status = LF(key, arguments);
        StatusDiagnosticDetail = null;
    }

    private void SetCountStatus(
        string key,
        long count,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusCount = count;
        Status = LC(key, count, arguments);
        StatusDiagnosticDetail = null;
    }

    private void SetFailure(
        string key,
        string? diagnosticDetail)
    {
        SetStatus(key);
        StatusDiagnosticDetail =
            diagnosticDetail;
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        if (_statusKey is null)
            return;
        Status = _statusCount is { } count
            ? LC(
                _statusKey,
                count,
                _statusArguments)
            : LF(
                _statusKey,
                _statusArguments);
    }
}
