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
}

public sealed class RepositoryState
{
    public Dictionary<string, string> LastReviewedCommits { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
