using global::Avalonia.Controls;
using global::Avalonia.Automation;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Views;

public partial class DialogHost : UserControl
{
    private readonly DialogService _dialogs;
    private readonly ILocalizationService _localization;
    private DialogRequest? _renderedRequest;
    private IInputElement? _priorFocus;
    private Button? _cancelButton;
    private Button? _primaryButton;

    public DialogHost()
    {
        InitializeComponent();
        _dialogs = App.GetService<DialogService>();
        _localization = App.GetService<ILocalizationService>();
        _dialogs.Changed += DialogChanged;
        DetachedFromVisualTree += (_, _) => _dialogs.Changed -= DialogChanged;
    }

    private void DialogChanged() => Dispatcher.UIThread.Post(RenderDialog);

    private void RenderDialog()
    {
        DialogRequest? request = _dialogs.Current;
        if (request is null)
        {
            HideDialog();
            return;
        }

        if (_renderedRequest is null)
            _priorFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

        _renderedRequest = request;
        IsVisible = true;
        DialogTitle.Text = request.Title;
        AutomationProperties.SetName(DialogCard, request.Title);
        DialogButtons.Children.Clear();
        DialogCard.MaxWidth = request is FieldsRequest ? 980 : 560;
        DialogCloseButton.IsVisible = request.DismissalPolicy.ShowCloseButton;
        _cancelButton = null;
        _primaryButton = null;
        switch (request)
        {
            case FieldsRequest fields:
                DialogContent.Content = new FieldsEditorView(fields.ViewModel);
                break;
            case MessageRequest message:
                DialogContent.Content = Message(message.Message, request.Tone);
                _primaryButton = ActionButton(
                    _localization.Get("Common.Ok"),
                    true,
                    request,
                    primary: true);
                DialogButtons.Children.Add(_primaryButton);
                break;
            case ConfirmRequest confirm:
                DialogContent.Content = Message(confirm.Message, request.Tone);
                _cancelButton = ActionButton(
                    _localization.Get("Common.Cancel"),
                    false,
                    request);
                _primaryButton = ActionButton(confirm.PrimaryText, true, request, primary: true);
                DialogButtons.Children.Add(_cancelButton);
                DialogButtons.Children.Add(_primaryButton);
                break;
        }

        Dispatcher.UIThread.Post(
            () => FocusDefaultAction(request),
            DispatcherPriority.Input);
    }

    private Control Message(string text, DialogTone tone)
    {
        var icon = new TextBlock
        {
            Text = ToneIcon(tone),
            Classes = { "status-icon" },
        };
        AutomationProperties.SetName(icon, ToneName(tone));

        var message = new TextBlock
        {
            Text = text,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
        };
        Grid.SetColumn(message, 1);

        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 10,
            Children = { icon, message },
        };
        var banner = new Border { Child = layout };
        banner.Classes.Add("status-banner");
        banner.Classes.Add(ToneClass(tone));
        AutomationProperties.SetLiveSetting(
            banner,
            tone is DialogTone.Error or DialogTone.Danger
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);

        return new ScrollViewer
        {
            MaxHeight = 560,
            Content = banner,
        };
    }

    private Button ActionButton(
        string text,
        bool result,
        DialogRequest request,
        bool primary = false)
    {
        var button = new Button { Content = text };
        button.Classes.Add("app");
        if (primary)
            button.Classes.Add("primary");
        if (primary &&
            request.PrimaryActionRole ==
                DialogActionRole.Destructive)
            button.Classes.Add("danger");
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => _dialogs.Complete(result);
        return button;
    }

    private void HideDialog()
    {
        if (_renderedRequest is null)
        {
            IsVisible = false;
            return;
        }

        IInputElement? priorFocus = _priorFocus;
        _renderedRequest = null;
        _priorFocus = null;
        _cancelButton = null;
        _primaryButton = null;
        DialogContent.Content = null;
        DialogButtons.Children.Clear();
        IsVisible = false;

        if (priorFocus is not null)
        {
            Dispatcher.UIThread.Post(
                () => priorFocus.Focus(NavigationMethod.Unspecified),
                DispatcherPriority.Input);
        }
    }

    private void FocusDefaultAction(DialogRequest request)
    {
        if (!ReferenceEquals(_dialogs.Current, request) || !IsVisible)
            return;

        IInputElement? target = request.DefaultAction switch
        {
            DialogDefaultAction.Cancel => _cancelButton,
            DialogDefaultAction.Primary => _primaryButton,
            _ => null,
        };
        target ??= DialogContent.GetVisualDescendants()
            .OfType<IInputElement>()
            .FirstOrDefault(element =>
                element.Focusable && element.IsEffectivelyEnabled && element.IsEffectivelyVisible);
        target ??= DialogCloseButton.IsVisible ? DialogCloseButton : null;
        target?.Focus(NavigationMethod.Tab);
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse &&
            !e.GetCurrentPoint(DialogScrim).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            return;
        }

        DialogRequest? request = _dialogs.Current;
        if (request?.DismissalPolicy.CanDismissFromScrim == true)
            _dialogs.Complete(false);
        else if (request is not null &&
                 !RouteThroughEditorCancellation(request))
            FocusDefaultAction(request);
        e.Handled = true;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        DialogRequest? request = _dialogs.Current;
        if (request?.DismissalPolicy.CanDismissFromCloseButton == true)
            _dialogs.Complete(false);
        else if (request is not null &&
                 !RouteThroughEditorCancellation(request))
            FocusDefaultAction(request);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        DialogRequest? request = _dialogs.Current;
        if (request is null)
            return;

        if (e.Key == Key.Escape)
        {
            if (request.DismissalPolicy.CanEscape)
                _dialogs.Complete(false);
            else if (!RouteThroughEditorCancellation(
                         request))
                FocusDefaultAction(request);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || e.Source is Button ||
            e.Source is TextBox { AcceptsReturn: true })
            return;

        switch (request.DefaultAction)
        {
            case DialogDefaultAction.Cancel when _cancelButton?.IsEffectivelyEnabled == true:
                _dialogs.Complete(false);
                e.Handled = true;
                break;
            case DialogDefaultAction.Primary when _primaryButton?.IsEffectivelyEnabled == true:
                _dialogs.Complete(true);
                e.Handled = true;
                break;
        }
    }

    private static bool RouteThroughEditorCancellation(
        DialogRequest request)
    {
        if (request is not FieldsRequest fields ||
            !fields.ViewModel.CancelCommand.CanExecute(
                null))
        {
            return false;
        }

        fields.ViewModel.CancelCommand.Execute(null);
        return true;
    }

    private static string ToneClass(DialogTone tone) => tone switch
    {
        DialogTone.Success => "success",
        DialogTone.Warning => "warning",
        DialogTone.Error or DialogTone.Danger => "error",
        _ => "info",
    };

    private static string ToneIcon(DialogTone tone) => tone switch
    {
        DialogTone.Success => "\u2713",
        DialogTone.Warning => "\u26A0",
        DialogTone.Error or DialogTone.Danger => "!",
        _ => "i",
    };

    private string ToneName(DialogTone tone) =>
        _localization.Get(tone switch
    {
        DialogTone.Success => "Dialog.Tone.Success",
        DialogTone.Warning => "Dialog.Tone.Warning",
        DialogTone.Error => "Dialog.Tone.Error",
        DialogTone.Danger => "Dialog.Tone.Danger",
        _ => "Dialog.Tone.Information",
    });
}
