using Avalonia.Controls;
using System.ComponentModel;
using MusicLibrary.Core.Models;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchTranscodeDrawerView : UserControl
{
    private TranscodeEditorViewModel? _editor;

    public WorkbenchTranscodeDrawerView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
            AttachEditor();
        DetachedFromVisualTree += (_, _) =>
            DetachEditor();
    }

    public event EventHandler? CloseRequested;

    public Control InitialFocus =>
        WorkbenchTranscodeCloseButton;

    private void OnClose(
        object? sender,
        global::Avalonia.Interactivity.RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void AttachEditor()
    {
        TranscodeEditorViewModel? editor =
            App.GetService<WorkbenchViewModel>()
                .TranscodeEditor;
        if (ReferenceEquals(
                editor,
                _editor))
            return;
        DetachEditor();
        _editor = editor;
        if (_editor is not null)
            _editor.PropertyChanged +=
                OnEditorPropertyChanged;
        ApplyContextualOptions();
    }

    private void DetachEditor()
    {
        if (_editor is not null)
            _editor.PropertyChanged -=
                OnEditorPropertyChanged;
        _editor = null;
    }

    private void OnEditorPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(TranscodeEditorViewModel.SelectedFormatId) or
            nameof(TranscodeEditorViewModel.SelectedRateMode))
            ApplyContextualOptions();
    }

    private void ApplyContextualOptions()
    {
        CreateCorrectionFileCheckBox.IsVisible =
            _editor?.SelectedFormatId ==
                AudioTranscodeFormatIds.WavPack &&
            _editor.SelectedRateMode is
                AudioTranscodeRateMode.HybridBitrate or
                AudioTranscodeRateMode.HybridQuality;
    }
}
