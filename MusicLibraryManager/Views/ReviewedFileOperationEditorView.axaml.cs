using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MusicLibraryManager.Views;

public partial class ReviewedFileOperationEditorView :
    UserControl
{
    public ReviewedFileOperationEditorView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
            ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        bool narrow =
            Bounds.Width > 0 &&
            Bounds.Width < 880;
        bool compactHeight =
            Bounds.Height > 0 &&
            Bounds.Height <= 700;

        SourceOptionsLayout.ColumnDefinitions.Clear();
        SourceOptionsLayout.RowDefinitions.Clear();
        TemplateOptionsLayout.ColumnDefinitions.Clear();
        TemplateOptionsLayout.RowDefinitions.Clear();
        if (narrow)
        {
            SourceOptionsLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    GridLength.Star));
            SourceOptionsLayout.RowDefinitions.Add(
                new RowDefinition(
                    GridLength.Auto));
            SourceOptionsLayout.RowDefinitions.Add(
                new RowDefinition(
                    GridLength.Auto));
            SourceOptionsLayout.RowDefinitions.Add(
                new RowDefinition(
                    GridLength.Auto));
            Grid.SetColumn(
                ReviewedFileOperationKindField,
                0);
            Grid.SetRow(
                ReviewedFileOperationKindField,
                0);
            Grid.SetColumn(
                DestinationLayout,
                0);
            Grid.SetRow(
                DestinationLayout,
                1);
            Grid.SetColumn(
                TargetSummaryText,
                0);
            Grid.SetRow(
                TargetSummaryText,
                2);

            TemplateOptionsLayout
                .ColumnDefinitions.Add(
                    new ColumnDefinition(
                        GridLength.Star));
            TemplateOptionsLayout
                .ColumnDefinitions.Add(
                    new ColumnDefinition(
                        GridLength.Auto));
            TemplateOptionsLayout
                .ColumnDefinitions.Add(
                    new ColumnDefinition(
                        GridLength.Auto));
            for (int index = 0; index < 3; index++)
                TemplateOptionsLayout
                    .RowDefinitions.Add(
                        new RowDefinition(
                            GridLength.Auto));
            Grid.SetColumn(
                ReviewedFileNameTemplateField,
                0);
            Grid.SetColumnSpan(
                ReviewedFileNameTemplateField,
                3);
            Grid.SetRow(
                ReviewedFileNameTemplateField,
                0);
            Grid.SetColumn(
                ReviewedCollisionPolicyField,
                0);
            Grid.SetRow(
                ReviewedCollisionPolicyField,
                1);
            Grid.SetColumn(
                PreserveRelativeFoldersCheckBox,
                1);
            Grid.SetColumnSpan(
                PreserveRelativeFoldersCheckBox,
                2);
            Grid.SetRow(
                PreserveRelativeFoldersCheckBox,
                1);
        }
        else
        {
            SourceOptionsLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(170)));
            SourceOptionsLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    GridLength.Star));
            SourceOptionsLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    GridLength.Auto));
            SourceOptionsLayout.RowDefinitions.Add(
                new RowDefinition(
                    GridLength.Auto));
            Grid.SetColumn(
                ReviewedFileOperationKindField,
                0);
            Grid.SetRow(
                ReviewedFileOperationKindField,
                0);
            Grid.SetColumn(
                DestinationLayout,
                1);
            Grid.SetRow(
                DestinationLayout,
                0);
            Grid.SetColumn(
                TargetSummaryText,
                2);
            Grid.SetRow(
                TargetSummaryText,
                0);

            TemplateOptionsLayout
                .ColumnDefinitions.Add(
                    new ColumnDefinition(
                        GridLength.Star));
            TemplateOptionsLayout
                .ColumnDefinitions.Add(
                    new ColumnDefinition(
                        new GridLength(170)));
            TemplateOptionsLayout
                .ColumnDefinitions.Add(
                    new ColumnDefinition(
                        GridLength.Auto));
            TemplateOptionsLayout
                .RowDefinitions.Add(
                    new RowDefinition(
                        GridLength.Auto));
            Grid.SetColumn(
                ReviewedFileNameTemplateField,
                0);
            Grid.SetColumnSpan(
                ReviewedFileNameTemplateField,
                1);
            Grid.SetRow(
                ReviewedFileNameTemplateField,
                0);
            Grid.SetColumn(
                ReviewedCollisionPolicyField,
                1);
            Grid.SetRow(
                ReviewedCollisionPolicyField,
                0);
            Grid.SetColumn(
                PreserveRelativeFoldersCheckBox,
                2);
            Grid.SetColumnSpan(
                PreserveRelativeFoldersCheckBox,
                1);
            Grid.SetRow(
                PreserveRelativeFoldersCheckBox,
                0);
        }

        SourceOptionsLayout.RowSpacing =
            narrow ? 8 : 0;
        TemplateOptionsLayout.RowSpacing =
            narrow ? 8 : 0;
        EditorLayout.RowSpacing =
            compactHeight
                ? 7
                : 10;
    }
}
