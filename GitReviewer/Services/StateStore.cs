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

        await using var stream = File.OpenRead(AppPaths.StateFile);
        var savedState = await JsonSerializer.DeserializeAsync<RepositoryState>(
            stream, JsonOptions, cancellationToken) ?? new RepositoryState();
        return new RepositoryState
        {
            LastReviewedCommits = new Dictionary<string, string>(
                savedState.LastReviewedCommits,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    public async Task SaveAsync(RepositoryState state, CancellationToken cancellationToken)
    {
        var temporaryPath = AppPaths.StateFile + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        File.Move(temporaryPath, AppPaths.StateFile, true);
    }
}
