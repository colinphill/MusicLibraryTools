using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.App.ViewModels;
using MusicLibrary.App.Views;
using MusicLibrary.Core.Services;
using MusicLibrary.Core.Models;

namespace MusicLibrary.App.Services;

/// <inheritdoc cref="IDialogService"/>
public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _services;

    /// <summary>Set by the main window at startup; dialogs are shown modally over it.</summary>
    public Window? Owner { get; set; }

    public DialogService(IServiceProvider services) => _services = services;

    public async Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths)
    {
        if (Owner is null || paths.Count == 0)
            return false;

        var vm = new FieldsDialogViewModel(
            _services.GetRequiredService<IMediaFileService>(),
            _services.GetRequiredService<ITagWriteService>(),
            paths);
        var dialog = new FieldsDialog { DataContext = vm };
        return await dialog.ShowDialog<bool>(Owner);
    }

    public async Task<string?> ShowConfigEditorAsync(string? existingPath)
    {
        if (Owner is null)
            return null;

        var vm = new ConfigDialogViewModel(_services.GetRequiredService<IFileDialogService>(), existingPath);
        var dialog = new ConfigDialog { DataContext = vm };
        return await dialog.ShowDialog<string?>(Owner);
    }

    public async Task<string?> ShowIngestConfigEditorAsync(string? existingPath)
    {
        if (Owner is null) return null;
        var vm = new IngestConfigDialogViewModel(_services.GetRequiredService<IFileDialogService>(), existingPath);
        var dialog = new IngestConfigDialog { DataContext = vm };
        return await dialog.ShowDialog<string?>(Owner);
    }

    public async Task<bool> ConfirmCdDerivationAsync(IngestApprovalItem item)
    {
        if (Owner is null) return false;
        var yes = new Button { Content = "Derive CD files", Classes = { "accent" } };
        var no = new Button { Content = "Cancel entire run" };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(yes); buttons.Children.Add(no);
        var panel = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = $"CD-quality FLAC files are missing for {item.AlbumDisplay}.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "You may have forgotten to obtain them. Derive these tracks from high-resolution files?", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(new ScrollViewer { Content = new ItemsControl { ItemsSource = item.MissingTracks }, MaxHeight = 300 });
        panel.Children.Add(buttons);
        var dialog = new Window { Title = "Confirm CD-quality derivation", Width = 620, SizeToContent = SizeToContent.Height,
            Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(Owner);
    }

    public async Task<bool> ConfirmRestoreAsync(OperationRestorePlan plan)
    {
        if (Owner is null || !plan.CanApply) return false;
        var restore = new Button { Content = $"Restore {plan.Actions.Count:N0} item(s)", Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel" };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(restore); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Restore {plan.Actions.Count:N0} selected item(s) to their original paths?",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = plan.CollisionCount == 0
                ? "No destination collisions were present when this preview was created."
                : $"{plan.CollisionCount:N0} existing destination(s) will be preserved in the restore rollback area.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Every source and destination will be revalidated before the first move. A failure rolls back completed restore actions.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Confirm restore",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        restore.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(Owner);
    }

    public async Task<bool> ConfirmPurgeAsync(OperationPurgePlan plan)
    {
        if (Owner is null || !plan.CanApply) return false;
        var purge = new Button { Content = $"Permanently purge {plan.Runs.Count:N0} run(s)", Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel" };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(purge); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Permanently delete {plan.Runs.Count:N0} operation run(s) older than " +
                $"{plan.RetentionDays:N0} day(s)?",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"This removes {plan.FileCount:N0} file(s), including " +
                $"{plan.RestoreBackupFileCount:N0} restore-collision backup file(s). Interrupted runs are protected.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "This cannot be undone. Every reviewed run will be revalidated before the first run is staged for deletion.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Confirm permanent purge",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        purge.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(Owner);
    }
}
