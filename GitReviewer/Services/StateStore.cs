using System.Text.Json;
using GitReviewer.Models;

namespace GitReviewer.Services;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<RepositoryState> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await LoadCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(RepositoryState state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await SaveCoreAsync(state, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<bool> RegisterRepositoryAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        repositoryPath = NormalizeRepositoryPath(repositoryPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadCoreAsync(cancellationToken);
            if (state.Repositories.ContainsKey(repositoryPath)) return true;
            state.Repositories.Add(repositoryPath, new RepositoryReviewState());
            await SaveCoreAsync(state, cancellationToken);
            return false;
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetCursorAsync(
        string repositoryPath,
        string branch,
        CancellationToken cancellationToken)
    {
        repositoryPath = NormalizeRepositoryPath(repositoryPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadCoreAsync(cancellationToken);
            return state.Repositories.TryGetValue(repositoryPath, out var repository) &&
                repository.LastReviewedCommits.TryGetValue(branch, out var sha)
                ? sha
                : null;
        }
        finally { _gate.Release(); }
    }

    public async Task SetCursorAsync(
        string repositoryPath,
        string branch,
        string sha,
        CancellationToken cancellationToken)
    {
        repositoryPath = NormalizeRepositoryPath(repositoryPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadCoreAsync(cancellationToken);
            if (!state.Repositories.TryGetValue(repositoryPath, out var repository))
            {
                repository = new RepositoryReviewState();
                state.Repositories.Add(repositoryPath, repository);
            }
            repository.LastReviewedCommits[branch] = sha;
            await SaveCoreAsync(state, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<RepositoryState> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AppPaths.StateFile))
            return new RepositoryState();

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(AppPaths.StateFile, cancellationToken));
        var version = document.RootElement.TryGetProperty("SchemaVersion", out var schemaVersion)
            ? schemaVersion.GetInt32() : 0;
        if (version is < 0 or > RepositoryState.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported state schema version: {version}.");

        if (version == RepositoryState.CurrentSchemaVersion)
            return Normalize(document.RootElement.Deserialize<RepositoryState>(JsonOptions)
                ?? throw new InvalidDataException("Invalid repository state."));

        var legacy = document.RootElement.Deserialize<LegacyRepositoryState>(JsonOptions)
            ?? throw new InvalidDataException("Invalid legacy repository state.");
        var state = new RepositoryState();
        foreach (var (key, sha) in legacy.LastReviewedCommits)
        {
            var separator = key.IndexOf('|');
            if (separator < 1 || separator == key.Length - 1)
                throw new InvalidDataException("Invalid legacy repository state key.");
            var repositoryPath = NormalizeRepositoryPath(key[..separator]);
            var branch = key[(separator + 1)..];
            if (version == 0) branch = "refs/heads/" + branch;
            if (!state.Repositories.TryGetValue(repositoryPath, out var repository))
            {
                repository = new RepositoryReviewState();
                state.Repositories.Add(repositoryPath, repository);
            }
            if (!repository.LastReviewedCommits.TryAdd(branch, sha) &&
                !repository.LastReviewedCommits[branch].Equals(sha, StringComparison.Ordinal))
                throw new InvalidDataException("Legacy state contains conflicting repository cursors.");
        }
        await SaveCoreAsync(state, cancellationToken);
        return state;
    }

    private static RepositoryState Normalize(RepositoryState savedState)
    {
        if (savedState.Repositories is null)
            throw new InvalidDataException("Invalid repository state.");
        var state = new RepositoryState();
        foreach (var (path, savedRepository) in savedState.Repositories)
        {
            if (savedRepository?.LastReviewedCommits is null)
                throw new InvalidDataException("Invalid repository state entry.");
            var normalizedPath = NormalizeRepositoryPath(path);
            if (!state.Repositories.TryAdd(normalizedPath, new RepositoryReviewState
            {
                LastReviewedCommits = new Dictionary<string, string>(
                    savedRepository.LastReviewedCommits,
                    StringComparer.Ordinal)
            }))
                throw new InvalidDataException("State contains duplicate repository paths.");
        }
        return state;
    }

    private static async Task SaveCoreAsync(RepositoryState state, CancellationToken cancellationToken)
    {
        if (state.SchemaVersion != RepositoryState.CurrentSchemaVersion || state.Repositories is null)
            throw new InvalidDataException($"Unsupported state schema version: {state.SchemaVersion}.");
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.StateFile)!);
        var temporaryPath = AppPaths.StateFile + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        File.Move(temporaryPath, AppPaths.StateFile, true);
    }

    private static string NormalizeRepositoryPath(string repositoryPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));

    private sealed class LegacyRepositoryState
    {
        public Dictionary<string, string> LastReviewedCommits { get; set; } = [];
    }
}
