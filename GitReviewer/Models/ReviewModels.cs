namespace GitReviewer.Models;

public sealed record CommitInfo(
    string Sha,
    string Author,
    DateTimeOffset Date,
    string Subject);

public sealed record Finding(
    string File,
    int? Line,
    string Side,
    string Description);

public sealed class ReviewResult
{
    public List<Finding> Findings { get; } = [];
    public string? UnstructuredResponse { get; set; }
    public bool EmptyDiff { get; set; }
}

public sealed class RepositoryState
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Dictionary<string, RepositoryReviewState> Repositories { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RepositoryReviewState
{
    public Dictionary<string, string> LastReviewedCommits { get; set; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, string> PendingStartCommits { get; set; } =
        new(StringComparer.Ordinal);
}

public enum ReviewStage { Started, PreparingDiff, Request, Waiting, Response, Parsing, Report, SavingCursor, Completed, Failed, Canceled, Tool }

public sealed record ReviewProgress(ReviewStage Stage, string Model, string Commit, string Detail = "");

public sealed record CommitReviewed(string RepositoryPath, string BranchRef, string Sha,
    int FindingCount, bool HasUnstructuredResponse, bool EmptyDiff, bool Manual);
