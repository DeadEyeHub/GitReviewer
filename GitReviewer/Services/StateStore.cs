using System.Text.Json;
using GitReviewer.Models;

namespace GitReviewer.Services;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<RepositoryState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AppPaths.StateFile))
            return new RepositoryState();

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(AppPaths.StateFile, cancellationToken));
        var version = document.RootElement.TryGetProperty("SchemaVersion", out var schemaVersion)
            ? schemaVersion.GetInt32() : 0;
        if (version != 0 && version != RepositoryState.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported state schema version: {version}.");
        var savedState = document.RootElement.Deserialize<RepositoryState>(JsonOptions)
            ?? throw new InvalidDataException("Invalid repository state.");
        var state = new RepositoryState
        {
            LastReviewedCommits = new Dictionary<string, string>(
                savedState.LastReviewedCommits,
                StringComparer.Ordinal)
        };
        if (version == 0)
        {
            // Every v0 suffix is a short local name, even one beginning with refs/heads/.
            // Convert the entire namespace once, not according to the selected branch.
            state.LastReviewedCommits.Clear();
            foreach (var (key, sha) in savedState.LastReviewedCommits)
            {
                var separator = key.IndexOf('|');
                if (separator < 0)
                    throw new InvalidDataException("Invalid legacy repository state key.");
                state.LastReviewedCommits.Add(key.Insert(separator + 1, "refs/heads/"), sha);
            }
            await SaveAsync(state, cancellationToken);
        }
        return state;
    }

    public async Task SaveAsync(RepositoryState state, CancellationToken cancellationToken)
    {
        if (state.SchemaVersion != RepositoryState.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported state schema version: {state.SchemaVersion}.");
        var temporaryPath = AppPaths.StateFile + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        File.Move(temporaryPath, AppPaths.StateFile, true);
    }
}
