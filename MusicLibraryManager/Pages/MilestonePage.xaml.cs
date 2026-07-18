using Microsoft.UI.Xaml.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Pages;

public sealed partial class MilestonePage : UserControl
{
    public MilestonePage(ShellDestination destination)
    {
        InitializeComponent();
        DataContext = new
        {
            Title = destination.ToString(),
            Message = destination switch
            {
                ShellDestination.Health => "Analysis, representation comparison, artwork audits, and reviewed repairs are the next migration milestone.",
                ShellDestination.Ingest => "The guided preflight, preview, apply, history, and recovery workflow is being migrated without weakening its safety model.",
                ShellDestination.Organize => "File organization will arrive as a reviewed plan with stale checks, journaling, and recovery.",
                ShellDestination.Operations => "Unified sync, device, playlist, recovery, and job history workflows remain in the existing app until parity is complete.",
                _ => "This workflow remains available in MusicLibrary.App while it moves into the new Fluent shell.",
            },
        };
    }
}
