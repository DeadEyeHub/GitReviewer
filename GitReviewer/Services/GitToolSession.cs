using System.Text.Json;

namespace GitReviewer.Services;

// Only this dispatcher translates model arguments into Git arguments. No revision expressions or disk reads.
public sealed class GitToolSession
{
    public const int PageSize = 16_000;
    public const int SnapshotLimit = 512_000;
    private readonly string _repository;
    private readonly HashSet<string> _commits = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextOffsets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _snapshots = new(StringComparer.Ordinal);
    private readonly string? _parent;
    public string Sha { get; }
    public bool DiffComplete { get; private set; }
    public bool ReadyForFinal => DiffComplete && _pending.Count == 0;

    private GitToolSession(string repository, string sha, string? parent)
    {
        _repository = repository;
        Sha = sha;
        _parent = parent;
        _commits.Add(sha);
        if (parent is not null) _commits.Add(parent);
    }

    public static async Task<GitToolSession> CreateAsync(string repository, string sha, CancellationToken token)
    {
        repository = Path.GetFullPath(repository);
        var root = await GitService.ReadPageAsync(repository, 0, PageSize, token, "rev-parse", "--show-toplevel");
        if (root.ExitCode != 0 || root.HasMore) throw new GitException("Select a valid Git working repository.");
        repository = root.Output.EndsWith('\n') ? root.Output[..^1] : root.Output;
        if (OperatingSystem.IsWindows()) repository = repository.TrimEnd('\r');
        if (repository.Length == 0 || repository.Any(char.IsControl))
            throw new GitException("Repository roots containing control characters are not supported.");
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new GitException("A full immutable commit SHA is required.");
        var type = await GitService.ReadPageAsync(repository, 0, 32, token, "cat-file", "-t", sha);
        if (type.ExitCode != 0 || type.HasMore || type.Output.Trim() != "commit")
            throw new GitException("Reviewed SHA must identify a locally available commit object.");
        // rev-list hides parents at shallow boundaries; raw headers preserve the actual comparison base.
        var metadata = await GitService.ReadPageAsync(repository, 0, PageSize, token, "cat-file", "commit", sha);
        var headerEnd = metadata.Output.IndexOf("\n\n", StringComparison.Ordinal);
        if (metadata.ExitCode != 0 || headerEnd < 0) throw new GitException("Cannot read complete commit headers locally.");
        var parents = metadata.Output[..headerEnd].Split('\n').Where(line => line.StartsWith("parent ", StringComparison.Ordinal))
            .Select(line => line[7..]).ToArray();
        if (parents.Any(id => id.Length != 40 || !id.All(Uri.IsHexDigit))) throw new GitException("Invalid commit parent header.");
        var session = new GitToolSession(repository, sha, parents.FirstOrDefault());
        foreach (var id in parents) session._commits.Add(id);
        return session;
    }

    private string[] DiffArguments(bool names) => _parent is null
        ? ["diff-tree", "--root", "--no-commit-id", "-r", "--no-renames", "--no-ext-diff", "--no-textconv", "--no-color", "--ignore-submodules=none", "--submodule=short",
            names ? "--name-status" : "--patch", names ? "--no-color" : "--unified=5", Sha, "--"]
        : ["diff", "--no-renames", "--no-ext-diff", "--no-textconv", "--no-color", "--ignore-submodules=none", "--submodule=short",
            names ? "--name-status" : "--patch", names ? "--no-color" : "--unified=5", _parent, Sha, "--"];

    public async Task<bool> IsEmptyAsync(CancellationToken token)
    {
        var result = await GitService.ReadPageAsync(_repository, 0, 1, token, DiffArguments(true));
        if (result.ExitCode != 0) throw new GitException("Cannot inspect changed files locally.");
        return result.Output.Length == 0 && !result.HasMore;
    }

    public static readonly string[] Names = ["git_metadata", "git_history", "git_changed_files", "git_diff", "git_file", "git_tree", "git_search"];

    private static Dictionary<string, object> Properties(string name)
    {
        var properties = new Dictionary<string, object>
        {
            ["commit"] = new { type = "string", description = "Discovered full SHA; defaults to reviewed SHA." }
        };
        if (name is not ("git_metadata" or "git_history"))
            properties["offset"] = new { type = "integer", minimum = 0, maximum = 2_000_000, description = "Initially 0; follow next_offset with the same arguments." };
        if (name == "git_file")
        {
            properties["path"] = new { type = "string", description = "Exact repository-relative file path." };
            properties["start_line"] = new { type = "integer", minimum = 1, maximum = 2_000_000, description = "Optional inclusive 1-based range start; requires end_line. Returns numbered lines." };
            properties["end_line"] = new { type = "integer", minimum = 1, maximum = 2_000_000, description = "Inclusive range end, at most 500 lines. Range reads support blobs up to 512000 characters." };
        }
        if (name == "git_search")
            properties["query"] = new { type = "string", minLength = 1, maxLength = 1024, description = "Literal case-sensitive text, not a regular expression. Search committed text files only." };
        return properties;
    }
    public static object[] Definitions => Names.Select(name => (object)new
    {
        type = "function",
        function = new
        {
            name,
            description = name switch
            {
                "git_metadata" => "Commit metadata and parents. commit defaults to reviewed SHA; only discovered full SHAs allowed.",
                "git_history" => "Discover up to 20 ancestors and their parents from an allowed commit. Bounded local history only.",
                "git_changed_files" => "Page changed file names/status for reviewed SHA against first parent (root against empty tree). Start offset=0 and follow next_offset until null.",
                "git_diff" => "Read the reviewed commit patch, including binary/mode changes. Start offset=0 and follow next_offset until null before final report.",
                "git_tree" => "List all committed files recursively, including modes and object IDs. Follow every next_offset until null.",
                "git_search" => "Search literal text in an allowed commit. Returns matching paths, line numbers and lines; binary files are skipped. Follow every next_offset until null. No matches is a successful empty result.",
                _ => "Page committed blob content at path and allowed commit, never working files. Start offset=0 and follow next_offset until null. Symlinks return link text, not their target."
            },
            parameters = new
            {
                type = "object",
                properties = Properties(name),
                required = name == "git_file" ? new[] { "path" } : name == "git_search" ? new[] { "query" } : Array.Empty<string>(),
                additionalProperties = false
            }
        }
    }).ToArray();

    public Task<string> ExecuteAsync(string name, string arguments, CancellationToken token) =>
        ExecuteAsync(name, arguments, token, null);

    internal async Task<string> ExecuteAsync(
        string name,
        string arguments,
        CancellationToken token,
        Action<GitCommandTrace>? trace)
    {
        token.ThrowIfCancellationRequested();
        if (!Names.Contains(name)) throw new ArgumentException("Unsupported tool. Use an advertised Git tool.");
        if (arguments.Length > 4096) throw new ArgumentException("Arguments exceed 4096 characters.");
        using var document = JsonDocument.Parse(arguments);
        var args = document.RootElement;
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Arguments must be a JSON object.");
        var seen = new HashSet<string>();
        foreach (var property in args.EnumerateObject())
            if (!seen.Add(property.Name) || !Properties(name).ContainsKey(property.Name))
                throw new ArgumentException("Unknown or duplicate argument.");
        var commit = args.TryGetProperty("commit", out var c) ? c.GetString() : Sha;
        if (commit is null || !_commits.Contains(commit)) throw new ArgumentException("Use the reviewed SHA or a full SHA returned by metadata/history.");
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
        if (offset is < 0 or > 2_000_000) throw new ArgumentException("Offset outside supported bounds.");
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (name != "git_file" && path is not null) throw new ArgumentException("path is only supported by git_file.");
        if (name is "git_diff" or "git_changed_files" && commit != Sha)
            throw new ArgumentException("Changes are bound to the reviewed SHA.");
        if (name == "git_file" && (string.IsNullOrEmpty(path) || path.Length > 2048 ||
            path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(part => part is "" or "." or "..")))
            throw new ArgumentException("Use an exact relative Git path without traversal or revision syntax.");
        if (name is "git_metadata" or "git_history" && offset != 0)
            throw new ArgumentException("Metadata/history do not support paging; explore a returned ancestor SHA instead.");
        var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (name == "git_search" && (string.IsNullOrEmpty(query) || query.Length > 1024 || query.Any(char.IsControl)))
            throw new ArgumentException("Use a nonempty literal search query without control characters, up to 1024 characters.");
        int? startLine = args.TryGetProperty("start_line", out var s) ? s.GetInt32() : null;
        int? endLine = args.TryGetProperty("end_line", out var e) ? e.GetInt32() : null;
        if (startLine.HasValue != endLine.HasValue || startLine is < 1 or > 2_000_000 ||
            endLine is < 1 or > 2_000_000 || endLine < startLine || endLine - startLine >= 500)
            throw new ArgumentException("Provide both start_line and end_line as an inclusive range of 1 to 500 lines.");
        var key = JsonSerializer.Serialize(new { name, commit, path, query, startLine, endLine });
        if (offset != _nextOffsets.GetValueOrDefault(key))
            throw new ArgumentException("Read each resource sequentially from offset 0 using its next_offset.");
        var command = name switch
        {
            "git_metadata" => new[] { "cat-file", "commit", commit },
            "git_history" => new[] { "log", "--no-show-signature", "--format=%H %P", "-n", "20", commit, "--" },
            "git_changed_files" => DiffArguments(true),
            "git_diff" => DiffArguments(false),
            "git_tree" => new[] { "ls-tree", "-r", "--full-tree", commit, "--" },
            "git_search" => new[] { "grep", "--no-color", "--no-ext-grep", "--no-textconv", "-I", "-n", "-F", "-e", query!, commit, "--" },
            _ => new[] { "cat-file", "blob", commit + ":" + path }
        };
        GitResult result;
        if (name is "git_diff" or "git_changed_files" or "git_tree" or "git_search" || startLine.HasValue)
        {
            if (!_snapshots.TryGetValue(key, out var snapshot))
            {
                // Render once: live attributes/config must not alter or shorten later pages.
                var captured = await GitService.ReadPageAsync(_repository, 0,
                    SnapshotLimit - _snapshots.Values.Sum(value => value.Length), token, trace, command);
                if ((captured.ExitCode != 0 && !(name == "git_search" && captured.ExitCode == 1 && captured.Output.Length == 0)) || captured.HasMore)
                    throw new GitException("Git snapshot unavailable or exceeds the 512000-character session limit; review is incomplete.");
                snapshot = captured.Output;
                if (startLine.HasValue)
                {
                    if (snapshot.Contains('\0')) throw new GitException("Line ranges require a text blob.");
                    var lines = snapshot.Split('\n');
                    var lineCount = snapshot.Length == 0 ? 0 : lines.Length - (snapshot.EndsWith('\n') ? 1 : 0);
                    if (startLine.Value > lineCount) throw new ArgumentException("start_line is beyond the end of the file.");
                    snapshot = string.Concat(Enumerable.Range(startLine.Value, Math.Min(endLine!.Value, lineCount) - startLine.Value + 1)
                        .Select(line => $"{line}: {lines[line - 1].TrimEnd('\r')}\n"));
                    if (snapshot.Length > SnapshotLimit - _snapshots.Values.Sum(value => value.Length))
                        throw new GitException("Numbered range exceeds the session snapshot limit.");
                }
                _snapshots.Add(key, snapshot);
            }
            if (offset > snapshot.Length) throw new GitException("Snapshot ended before the requested offset; review is incomplete.");
            var length = Math.Min(PageSize, snapshot.Length - offset);
            if (length > 0 && offset + length < snapshot.Length && char.IsHighSurrogate(snapshot[offset + length - 1])) length--;
            result = new GitResult(0, snapshot.Substring(offset, length), "", offset + length < snapshot.Length);
        }
        else
            result = await GitService.ReadPageAsync(_repository, offset, PageSize, token, trace, command);
        if (result.ExitCode != 0) throw new GitException("Git could not read that local object/path; review is incomplete. No network fetch is permitted.");
        if (result.HasMore && name is "git_metadata" or "git_history")
            throw new GitException("Metadata output limit exceeded; review is incomplete.");
        if (name is "git_metadata" or "git_history")
        {
            var lines = result.Output.Split('\n');
            var ids = name == "git_metadata"
                ? lines.TakeWhile(line => line.Length > 0).Where(line => line.StartsWith("parent ", StringComparison.Ordinal)).Select(line => line[7..])
                : lines.AsEnumerable();
            foreach (var id in ids.SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                if (id.Length == 40 && id.All(Uri.IsHexDigit) && _commits.Count < 1024) _commits.Add(id);
        }
        var next = offset + result.Output.Length;
        if (name is not ("git_metadata" or "git_history")) _nextOffsets[key] = next;
        if (result.HasMore) _pending.Add(key);
        else _pending.Remove(key);
        if (name == "git_diff") DiffComplete = !result.HasMore;
        return JsonSerializer.Serialize(new { status = "ok", reviewed_sha = Sha, commit, offset,
            next_offset = result.HasMore ? (int?)next : null, content = result.Output });
    }
}
