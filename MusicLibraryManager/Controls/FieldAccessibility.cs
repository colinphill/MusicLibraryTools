using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MusicLibraryManager.Controls;

/// <summary>
/// Connects a shared field's visible label and supporting text to its primary
/// input without changing focus or tab behavior.
/// </summary>
public sealed class FieldAccessibility : AvaloniaObject
{
    private static readonly ConditionalWeakTable<Control, AssociationState>
        States = new();

    public static readonly AttachedProperty<bool> AssociateProperty =
        AvaloniaProperty.RegisterAttached<
            FieldAccessibility,
            Control,
            bool>("Associate");

    static FieldAccessibility()
    {
        AssociateProperty.Changed.AddClassHandler<Control>(
            static (control, _) =>
            {
                if (GetAssociate(control))
                    Enable(control);
                else
                    Disable(control);
            });
    }

    private FieldAccessibility()
    {
    }

    public static bool GetAssociate(Control control) =>
        control.GetValue(AssociateProperty);

    public static void SetAssociate(Control control, bool value) =>
        control.SetValue(AssociateProperty, value);

    internal static long RefreshBatchCount =>
        RefreshScheduler.CompletedBatchCount;

    internal static int GetRefreshCount(Control field) =>
        States.TryGetValue(
            field,
            out AssociationState? state)
            ? state.RefreshCount
            : 0;

    internal static void Invalidate(Control field)
    {
        if (States.TryGetValue(
                field,
                out AssociationState? state))
        {
            state.InvalidateTopology();
        }
    }

    private static void Enable(Control control)
    {
        AssociationState state = States.GetValue(
            control,
            static field => new AssociationState(field));
        state.Attach();
    }

    private static void Disable(Control control)
    {
        if (!States.TryGetValue(
                control,
                out AssociationState? state))
        {
            return;
        }

        state.Detach();
        States.Remove(control);
    }

    private sealed class AssociationState
    {
        private readonly Control _field;
        private Control[] _controls = [];
        private Visual[] _visibilityChain = [];
        private Control? _input;
        private Control? _automaticLabel;
        private string? _automaticHelpText;
        private IDisposable? _automaticLabelValue;
        private IDisposable? _automaticHelpValue;
        private bool _attached;
        private bool _active;
        private bool _topologyDirty = true;

        public AssociationState(Control field) =>
            _field = field;

        public int RefreshCount { get; private set; }

        public void Attach()
        {
            if (_attached)
                return;

            _attached = true;
            _field.AttachedToVisualTree +=
                OnFieldAttachedToVisualTree;
            _field.DetachedFromVisualTree +=
                OnFieldDetachedFromVisualTree;
            if (_field.IsAttachedToVisualTree())
                Activate();
        }

        public void Detach()
        {
            if (!_attached)
                return;

            _attached = false;
            _field.AttachedToVisualTree -=
                OnFieldAttachedToVisualTree;
            _field.DetachedFromVisualTree -=
                OnFieldDetachedFromVisualTree;
            Deactivate();
        }

        public void InvalidateTopology()
        {
            if (!_active)
                return;

            _topologyDirty = true;
            RefreshScheduler.Request(this);
        }

        public void Refresh()
        {
            if (!_active)
                return;

            RefreshCount++;
            if (_topologyDirty)
                DiscoverControls();

            TextBlock? label = _controls
                .OfType<TextBlock>()
                .FirstOrDefault(IsVisibleFieldLabel);
            Control? input = _controls
                .FirstOrDefault(IsVisibleFieldInput);
            if (label is null ||
                input is null)
            {
                ReleaseAutomaticValues();
                _input = null;
                return;
            }

            if (!ReferenceEquals(
                    input,
                    _input))
            {
                ReleaseAutomaticValues();
                _input = input;
            }

            ApplyAutomaticLabel(
                label);
            ApplyAutomaticHelp(
                string.Join(
                    Environment.NewLine,
                    _controls
                        .OfType<TextBlock>()
                        .Where(IsVisibleSupportingText)
                        .Select(static text =>
                            text.Text?.Trim())
                        .Where(static text =>
                            !string.IsNullOrWhiteSpace(
                                text))
                        .Distinct(StringComparer.Ordinal)));
        }

        private void Activate()
        {
            if (_active)
                return;

            _active = true;
            _topologyDirty = true;
            SubscribeVisibilityChain();
            Refresh();
        }

        private void Deactivate()
        {
            if (!_active)
                return;

            _active = false;
            UnsubscribeVisibilityChain();
            UnsubscribeControls();
            ReleaseAutomaticValues();
            _input = null;
            _controls = [];
            _topologyDirty = true;
        }

        private void DiscoverControls()
        {
            UnsubscribeControls();
            _controls =
            [
                .. EnumerateFieldControls(
                    _field),
            ];
            foreach (Control control in
                     _controls)
            {
                control.PropertyChanged +=
                    OnDescendantPropertyChanged;
                control.AttachedToVisualTree +=
                    OnDescendantTreeChanged;
                control.DetachedFromVisualTree +=
                    OnDescendantTreeChanged;
            }

            _topologyDirty = false;
        }

        private void UnsubscribeControls()
        {
            foreach (Control control in
                     _controls)
            {
                control.PropertyChanged -=
                    OnDescendantPropertyChanged;
                control.AttachedToVisualTree -=
                    OnDescendantTreeChanged;
                control.DetachedFromVisualTree -=
                    OnDescendantTreeChanged;
            }
        }

        private void SubscribeVisibilityChain()
        {
            _visibilityChain =
            [
                _field,
                .. _field.GetVisualAncestors(),
            ];
            foreach (Visual visual in
                     _visibilityChain)
            {
                visual.PropertyChanged +=
                    OnVisibilityChainPropertyChanged;
            }
        }

        private void UnsubscribeVisibilityChain()
        {
            foreach (Visual visual in
                     _visibilityChain)
            {
                visual.PropertyChanged -=
                    OnVisibilityChainPropertyChanged;
            }

            _visibilityChain = [];
        }

        private void OnFieldAttachedToVisualTree(
            object? sender,
            VisualTreeAttachmentEventArgs e) =>
            Activate();

        private void OnFieldDetachedFromVisualTree(
            object? sender,
            VisualTreeAttachmentEventArgs e) =>
            Deactivate();

        private void OnVisibilityChainPropertyChanged(
            object? sender,
            AvaloniaPropertyChangedEventArgs e)
        {
            if (_active &&
                e.Property ==
                Visual.IsVisibleProperty)
            {
                RefreshScheduler.Request(this);
            }
        }

        private void OnDescendantTreeChanged(
            object? sender,
            VisualTreeAttachmentEventArgs e) =>
            InvalidateTopology();

        private void OnDescendantPropertyChanged(
            object? sender,
            AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBlock.TextProperty ||
                e.Property == Visual.IsVisibleProperty)
            {
                RefreshScheduler.Request(this);
            }
        }

        private void ApplyAutomaticLabel(
            Control label)
        {
            if (_input is null ||
                ReferenceEquals(
                    _automaticLabel,
                    label))
            {
                return;
            }

            _automaticLabelValue?.Dispose();
            _automaticLabelValue =
                _input.SetValue(
                    AutomationProperties
                        .LabeledByProperty,
                    label,
                    BindingPriority.Style);
            _automaticLabel = label;
        }

        private void ApplyAutomaticHelp(
            string helpText)
        {
            if (_input is null)
                return;

            string? normalized =
                string.IsNullOrWhiteSpace(
                    helpText)
                    ? null
                    : helpText;
            if (string.Equals(
                    _automaticHelpText,
                    normalized,
                    StringComparison.Ordinal))
            {
                return;
            }

            _automaticHelpValue?.Dispose();
            _automaticHelpValue = null;
            _automaticHelpText = normalized;
            if (normalized is not null)
            {
                _automaticHelpValue =
                    _input.SetValue(
                        AutomationProperties
                            .HelpTextProperty,
                        normalized,
                        BindingPriority.Style);
            }
        }

        private void ReleaseAutomaticValues()
        {
            _automaticLabelValue?.Dispose();
            _automaticHelpValue?.Dispose();
            _automaticLabelValue = null;
            _automaticHelpValue = null;
            _automaticLabel = null;
            _automaticHelpText = null;
        }

        private static bool IsVisibleFieldLabel(
            TextBlock text) =>
            text.Classes.Contains(
                "field-label") &&
            text.IsEffectivelyVisible &&
            !string.IsNullOrWhiteSpace(
                text.Text);

        private static bool IsVisibleSupportingText(
            TextBlock text) =>
            text.IsEffectivelyVisible &&
            !text.Classes.Contains(
                "field-label") &&
            (text.Classes.Contains(
                 "field-help") ||
             text.Classes.Contains(
                 "warning-text") ||
             text.Classes.Contains(
                 "warning") ||
             text.Classes.Contains(
                 "error"));

        private static bool IsVisibleFieldInput(
            Control control) =>
            control.IsEffectivelyVisible &&
            control is (
                TextBox or
                ComboBox or
                NumericUpDown or
                Slider or
                ToggleButton or
                ToggleSwitch or
                DatePicker or
                TimePicker);

        private static IEnumerable<Control>
            EnumerateFieldControls(
                Visual parent)
        {
            foreach (Visual child in
                     parent.GetVisualChildren())
            {
                if (child is
                        Control nestedControl &&
                    (GetAssociate(
                         nestedControl) ||
                     nestedControl.Classes.Contains(
                         "field")))
                {
                    continue;
                }

                if (child is Control control)
                    yield return control;

                foreach (Control descendant in
                         EnumerateFieldControls(
                             child))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static class RefreshScheduler
    {
        private static readonly HashSet<AssociationState>
            Pending = [];
        private static bool _scheduled;

        public static long CompletedBatchCount
        {
            get;
            private set;
        }

        public static void Request(
            AssociationState state)
        {
            Pending.Add(state);
            if (_scheduled)
                return;

            _scheduled = true;
            Dispatcher.UIThread.Post(
                Process,
                DispatcherPriority.Background);
        }

        private static void Process()
        {
            AssociationState[] batch =
            [
                .. Pending,
            ];
            Pending.Clear();
            _scheduled = false;
            CompletedBatchCount++;
            foreach (AssociationState state in
                     batch)
            {
                state.Refresh();
            }
        }
    }
}
