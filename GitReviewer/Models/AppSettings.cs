namespace GitReviewer.Models;

public sealed class ProjectCatalog
{
    public int Version { get; set; } = 1;
    public int ActiveIndex { get; set; }
    public List<AppSettings> Projects { get; set; } = [];
}

public sealed class AppSettings
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string BranchRef { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 60;
    public bool PullEnabled { get; set; } = true;
    public bool FetchSubmodules { get; set; }
    public string Language { get; set; } = "en";
    public string GitAuthenticationMode { get; set; } = "auto";
    public string SshPrivateKeyPath { get; set; } = string.Empty;
    public string PlinkPath { get; set; } = string.Empty;
}
