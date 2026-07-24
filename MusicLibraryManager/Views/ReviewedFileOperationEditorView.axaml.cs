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
                ReviewedFileOperationKind,
                0);
            Grid.SetRow(
                ReviewedFileOperationKind,
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
                ReviewedFileNameTemplate,
                0);
            Grid.SetColumnSpan(
                ReviewedFileNameTemplate,
                3);
            Grid.SetRow(
                ReviewedFileNameTemplate,
                0);
            Grid.SetColumn(
                ReviewedCollisionPolicy,
                0);
            Grid.SetRow(
                ReviewedCollisionPolicy,
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
            Grid.SetColumn(
                PreviewReviewedFileOperationButton,
                1);
            Grid.SetRow(
                PreviewReviewedFileOperationButton,
                2);
            Grid.SetColumn(
                ApplyReviewedFileOperationButton,
                2);
            Grid.SetRow(
                ApplyReviewedFileOperationButton,
                2);
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
                ReviewedFileOperationKind,
                0);
            Grid.SetRow(
                ReviewedFileOperationKind,
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
            for (int index = 0; index < 3; index++)
                TemplateOptionsLayout
                    .ColumnDefinitions.Add(
                        new ColumnDefinition(
                            GridLength.Auto));
            TemplateOptionsLayout
                .RowDefinitions.Add(
                    new RowDefinition(
                        GridLength.Auto));
            Grid.SetColumn(
                ReviewedFileNameTemplate,
                0);
            Grid.SetColumnSpan(
                ReviewedFileNameTemplate,
                1);
            Grid.SetRow(
                ReviewedFileNameTemplate,
                0);
            Grid.SetColumn(
                ReviewedCollisionPolicy,
                1);
            Grid.SetRow(
                ReviewedCollisionPolicy,
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
            Grid.SetColumn(
                PreviewReviewedFileOperationButton,
                3);
            Grid.SetRow(
                PreviewReviewedFileOperationButton,
                0);
            Grid.SetColumn(
                ApplyReviewedFileOperationButton,
                4);
            Grid.SetRow(
                ApplyReviewedFileOperationButton,
                0);
        }

        TargetSummaryText.IsVisible =
            !compactHeight;
        EditorLayout.RowSpacing =
            compactHeight
                ? 7
                : 10;
    }
}
