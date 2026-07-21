using MusicLibrary.Core.Models;

namespace MusicLibraryManager.Presentation;

/// <summary>
/// Resolves the single filesystem path represented by a Health result row. Aggregate results that
/// represent several paths are intentionally unsupported so context actions never imply that an
/// arbitrary file will be used.
/// </summary>
public static class HealthResultPathResolver
{
    public static bool TryGetPath(object? result, out string path)
    {
        string? candidate = result switch
        {
            AnalysisFindingViewModel finding => finding.Path,
            AnalysisRepairItemViewModel repair => repair.Path,
            RepresentationRepairActionItemViewModel repair => repair.SourcePath,
            ItlMetadataRepairItemViewModel repair => repair.Path,
            AnalysisConflictGroupViewModel conflict => conflict.Directory,
            TrackRecord track => track.Path,
            AlbumMetadataRow row => row.Path,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(candidate))
        {
            path = string.Empty;
            return false;
        }

        path = candidate;
        return true;
    }
}
