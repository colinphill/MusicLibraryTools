using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MusicLibraryManager.Controls;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class FieldAccessibilityTests
{
    [AvaloniaFact]
    public void Shared_field_style_associates_visible_label_and_help_with_primary_input()
    {
        TextBlock label = new()
        {
            Text = "Output folder",
        };
        label.Classes.Add("field-label");

        Button browse = new()
        {
            Content = "Browse",
        };
        TextBox input = new();
        Grid composite = new()
        {
            ColumnDefinitions = new("Auto,*"),
            ColumnSpacing = 8,
        };
        composite.Children.Add(browse);
        composite.Children.Add(input);
        Grid.SetColumn(input, 1);

        TextBlock help = new()
        {
            Text = "Choose where generated files are written.",
        };
        help.Classes.Add("field-help");
        TextBlock validation = new()
        {
            Text = "The selected folder is unavailable.",
            IsVisible = false,
        };
        validation.Classes.Add("error");
        validation.Classes.Add("field-help");

        StackPanel field = new();
        field.Classes.Add("field");
        field.Children.Add(label);
        field.Children.Add(composite);
        field.Children.Add(help);
        field.Children.Add(validation);

        Window window = new()
        {
            Width = 500,
            Height = 240,
            Content = field,
        };

        bool inputFocusable = input.Focusable;
        bool browseFocusable = browse.Focusable;
        try
        {
            window.Show();
            window.Activate();
            Render();

            Assert.True(FieldAccessibility.GetAssociate(field));
            Assert.Same(
                label,
                AutomationProperties.GetLabeledBy(input));
            Assert.Null(AutomationProperties.GetLabeledBy(browse));
            Assert.Equal(
                help.Text,
                AutomationProperties.GetHelpText(input));
            Assert.Equal(inputFocusable, input.Focusable);
            Assert.Equal(browseFocusable, browse.Focusable);

            Assert.True(input.Focus());
            Assert.Same(
                input,
                window.FocusManager?.GetFocusedElement());

            label.Text = "Localized output folder";
            help.Text = "Localized supporting guidance.";
            validation.IsVisible = true;
            Render();

            Assert.Same(
                label,
                AutomationProperties.GetLabeledBy(input));
            Assert.Equal(
                string.Join(
                    Environment.NewLine,
                    help.Text,
                    validation.Text),
                AutomationProperties.GetHelpText(input));
            Assert.Same(
                input,
                window.FocusManager?.GetFocusedElement());
            Assert.Equal(inputFocusable, input.Focusable);

            FieldAccessibility.SetAssociate(field, false);

            Assert.Null(AutomationProperties.GetLabeledBy(input));
            Assert.Null(AutomationProperties.GetHelpText(input));
            Assert.Same(
                input,
                window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Explicit_accessibility_associations_are_preserved()
    {
        TextBlock visibleLabel = new()
        {
            Text = "Encoder",
        };
        visibleLabel.Classes.Add("field-label");
        TextBlock explicitLabel = new()
        {
            Text = "Explicit encoder label",
        };
        ComboBox input = new();
        AutomationProperties.SetLabeledBy(input, explicitLabel);
        AutomationProperties.SetHelpText(
            input,
            "Explicit encoder guidance.");
        TextBlock help = new()
        {
            Text = "Displayed field guidance.",
        };
        help.Classes.Add("field-help");

        StackPanel field = new();
        field.Classes.Add("field");
        field.Children.Add(visibleLabel);
        field.Children.Add(input);
        field.Children.Add(help);

        Window window = new()
        {
            Width = 400,
            Height = 200,
            Content = field,
        };

        try
        {
            window.Show();
            Render();

            Assert.Same(
                explicitLabel,
                AutomationProperties.GetLabeledBy(input));
            Assert.Equal(
                "Explicit encoder guidance.",
                AutomationProperties.GetHelpText(input));

            help.Text = "Changed displayed guidance.";
            Render();

            Assert.Same(
                explicitLabel,
                AutomationProperties.GetLabeledBy(input));
            Assert.Equal(
                "Explicit encoder guidance.",
                AutomationProperties.GetHelpText(input));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Late_explicit_values_override_matching_automatic_values_and_survive_detach()
    {
        TextBlock automaticLabel = new()
        {
            Text = "Destination",
        };
        automaticLabel.Classes.Add("field-label");
        TextBlock alternateLabel = new()
        {
            Text = "Localized destination",
            IsVisible = false,
        };
        alternateLabel.Classes.Add("field-label");
        TextBlock help = new()
        {
            Text = "Choose a destination.",
        };
        help.Classes.Add("field-help");
        TextBox input = new();

        StackPanel field = new();
        field.Classes.Add("field");
        field.Children.Add(automaticLabel);
        field.Children.Add(alternateLabel);
        field.Children.Add(input);
        field.Children.Add(help);
        Window window = new()
        {
            Width = 400,
            Height = 220,
            Content = field,
        };

        try
        {
            window.Show();
            Render();
            Assert.Same(
                automaticLabel,
                AutomationProperties.GetLabeledBy(input));
            Assert.Equal(
                help.Text,
                AutomationProperties.GetHelpText(input));

            // Install explicit local values after the automatic style values,
            // deliberately using the same effective values.
            AutomationProperties.SetLabeledBy(
                input,
                automaticLabel);
            AutomationProperties.SetHelpText(
                input,
                help.Text);

            automaticLabel.IsVisible = false;
            alternateLabel.IsVisible = true;
            help.Text = "Changed automatic guidance.";
            Render();

            Assert.Same(
                automaticLabel,
                AutomationProperties.GetLabeledBy(input));
            Assert.Equal(
                "Choose a destination.",
                AutomationProperties.GetHelpText(input));

            FieldAccessibility.SetAssociate(
                field,
                false);
            Assert.Same(
                automaticLabel,
                AutomationProperties.GetLabeledBy(input));
            Assert.Equal(
                "Choose a destination.",
                AutomationProperties.GetHelpText(input));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Conditional_labels_update_and_associations_survive_visual_tree_recycling()
    {
        TextBlock firstLabel = new()
        {
            Text = "First label",
        };
        firstLabel.Classes.Add("field-label");
        TextBlock secondLabel = new()
        {
            Text = "Second label",
            IsVisible = false,
        };
        secondLabel.Classes.Add("field-label");
        TextBox input = new();
        StackPanel field = new();
        FieldAccessibility.SetAssociate(
            field,
            true);
        field.Children.Add(firstLabel);
        field.Children.Add(secondLabel);
        field.Children.Add(input);

        Border host = new()
        {
            Child = field,
        };
        Window window = new()
        {
            Width = 400,
            Height = 220,
            Content = host,
        };

        try
        {
            window.Show();
            Render();
            Assert.Same(
                firstLabel,
                AutomationProperties.GetLabeledBy(input));

            firstLabel.IsVisible = false;
            secondLabel.IsVisible = true;
            Render();
            Assert.Same(
                secondLabel,
                AutomationProperties.GetLabeledBy(input));

            host.Child = null;
            Render();
            Assert.Null(
                AutomationProperties.GetLabeledBy(input));

            host.Child = field;
            Render();
            Assert.Same(
                secondLabel,
                AutomationProperties.GetLabeledBy(input));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Hidden_field_reassociates_when_effective_visibility_changes()
    {
        TextBlock label = new()
        {
            Text = "Conditional destination",
        };
        label.Classes.Add("field-label");
        TextBox input = new();
        StackPanel field = new()
        {
            IsVisible = false,
        };
        FieldAccessibility.SetAssociate(
            field,
            true);
        field.Children.Add(label);
        field.Children.Add(input);

        Border ancestor = new()
        {
            Child = field,
        };
        Window window = new()
        {
            Width = 400,
            Height = 220,
            Content = ancestor,
        };

        try
        {
            window.Show();
            Render();
            Assert.Null(
                AutomationProperties.GetLabeledBy(input));

            field.IsVisible = true;
            Render();
            Assert.Same(
                label,
                AutomationProperties.GetLabeledBy(input));

            ancestor.IsVisible = false;
            Render();
            Assert.Null(
                AutomationProperties.GetLabeledBy(input));

            ancestor.IsVisible = true;
            Render();
            Assert.Same(
                label,
                AutomationProperties.GetLabeledBy(input));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Repeated_field_invalidations_are_coalesced_into_one_refresh()
    {
        TextBlock label = new()
        {
            Text = "Title",
        };
        label.Classes.Add("field-label");
        TextBlock help = new()
        {
            Text = "Initial help.",
        };
        help.Classes.Add("field-help");
        TextBox input = new();
        StackPanel field = new();
        FieldAccessibility.SetAssociate(
            field,
            true);
        field.Children.Add(label);
        field.Children.Add(input);
        field.Children.Add(help);
        Window window = new()
        {
            Width = 400,
            Height = 220,
            Content = field,
        };

        try
        {
            window.Show();
            Render();
            int refreshesBefore =
                FieldAccessibility.GetRefreshCount(
                    field);
            long batchesBefore =
                FieldAccessibility.RefreshBatchCount;

            for (int index = 0;
                 index < 100;
                 index++)
            {
                help.Text =
                    $"Updated help {index}.";
                FieldAccessibility.Invalidate(
                    field);
            }

            Render();
            Assert.Equal(
                refreshesBefore + 1,
                FieldAccessibility.GetRefreshCount(
                    field));
            Assert.Equal(
                batchesBefore + 1,
                FieldAccessibility.RefreshBatchCount);
            Assert.Equal(
                "Updated help 99.",
                AutomationProperties.GetHelpText(input));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Outer_field_skips_an_explicit_nested_grid_field()
    {
        TextBlock outerLabel = new()
        {
            Text = "Outer value",
        };
        outerLabel.Classes.Add("field-label");
        TextBlock innerLabel = new()
        {
            Text = "Inner value",
        };
        innerLabel.Classes.Add("field-label");
        TextBox innerInput = new();
        Grid innerField = new()
        {
            RowDefinitions = new("Auto,Auto"),
        };
        innerField.Children.Add(innerLabel);
        innerField.Children.Add(innerInput);
        Grid.SetRow(innerInput, 1);
        FieldAccessibility.SetAssociate(innerField, true);

        TextBox outerInput = new();
        StackPanel outerField = new();
        outerField.Classes.Add("field");
        outerField.Children.Add(outerLabel);
        outerField.Children.Add(innerField);
        outerField.Children.Add(outerInput);

        Window window = new()
        {
            Width = 400,
            Height = 240,
            Content = outerField,
        };

        try
        {
            window.Show();
            Render();

            Assert.Same(
                innerLabel,
                AutomationProperties.GetLabeledBy(innerInput));
            Assert.Same(
                outerLabel,
                AutomationProperties.GetLabeledBy(outerInput));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}
