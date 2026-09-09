namespace GitReviewer.Models;

public sealed class AppSettings
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string BranchRef { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 60;
    public bool PullEnabled { get; set; } = true;
    public string Language { get; set; } = "en";
    public string GitAuthenticationMode { get; set; } = "auto";
    public string SshPrivateKeyPath { get; set; } = string.Empty;
    public string PlinkPath { get; set; } = string.Empty;
}
