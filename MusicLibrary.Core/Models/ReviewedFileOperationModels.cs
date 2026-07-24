namespace MusicLibrary.Core.Models;

public enum ReviewedFileOperationKind
{
    Copy,
    Move,
    Rename,
    Quarantine,
}

public enum ReviewedFileCollisionPolicy
{
    Stop,
    Suffix,
}

public sealed record ReviewedFileOperationRequest(
    IReadOnlyList<string> SourcePaths,
    ReviewedFileOperationKind Kind,
    string? DestinationDirectory = null,
    string FileNameTemplate = "{Name}{Extension}",
    bool PreserveRelativeLayout = false,
    ReviewedFileCollisionPolicy CollisionPolicy =
        ReviewedFileCollisionPolicy.Stop);

public sealed record ReviewedFileOperationItem(
    string SourcePath,
    string? DestinationPath,
    FileMutationKind MutationKind,
    IReadOnlyList<OperationIssue> Issues)
{
    public bool CanApply =>
        DestinationPath is not null &&
        Issues.All(issue =>
            issue.Severity != OperationIssueSeverity.Blocker);

    public string Operation => MutationKind.ToString();

    public string Status =>
        Issues.FirstOrDefault(issue =>
            issue.Severity == OperationIssueSeverity.Blocker)?.Message ??
        Issues.FirstOrDefault()?.Message ??
        "Ready";
}

public sealed record ReviewedFileOperationPlan(
    ReviewedFileOperationRequest Request,
    IReadOnlyList<ReviewedFileOperationItem> Items,
    FileMutationPlan MutationPlan)
{
    public bool CanApply => MutationPlan.CanApply;
}
