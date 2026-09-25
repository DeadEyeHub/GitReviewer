using System.Text.Json;

namespace GitReviewer.Services;

// Only this dispatcher translates model arguments into Git arguments. No revision expressions or disk reads.
public sealed class GitToolSession : IDisposable
{
    public const int PageSize = 16_000;
    private const int RangeInputLimit = 512_000;
    private readonly string _repository;
    private readonly HashSet<string> _commits = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifiedFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextOffsets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _readOffsets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileStream> _snapshots = new(StringComparer.Ordinal);
    private readonly long _diskLimit = ReadDiskLimit();
    private bool _disposed;
    private static long ReadDiskLimit() => int.TryParse(Environment.GetEnvironmentVariable("GITREVIEWER_SNAPSHOT_MB"), out var mb)
        && mb is >= 1 and <= 1024 ? mb * 1024L * 1024 : 64 * 1024L * 1024;
    public void Dispose()
    {
        foreach (var snapshot in _snapshots.Values) snapshot.Dispose();
        _snapshots.Clear();
        _disposed = true;
    }
    private readonly string? _parent;
    public string Sha { get; }
    public bool DiffComplete { get; private set; }
    public bool ReadyForFinal => DiffComplete;

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

    public static readonly string[] Names = ["git_metadata", "git_changed_files", "git_diff", "git_file", "git_tree", "git_search"];

    private static Dictionary<string, object> Properties(string name)
    {
        var properties = new Dictionary<string, object>
        {
            ["commit"] = new { type = "string", description = "Only reviewed SHA or its immediate first parent; defaults to reviewed SHA." }
        };
        if (name != "git_metadata")
            properties["offset"] = new { type = "integer", minimum = 0, maximum = int.MaxValue, description = "Initially 0; follow next_offset with the same arguments." };
        if (name is "git_tree" or "git_search")
            properties["path"] = new
            {
                type = "string",
                description = name == "git_tree"
                ? "Optional exact repository-relative directory without trailing slash. Omit for repository root."
                : "Optional exact repository-relative directory or file to restrict results. Omit for repository root."
            };
        if (name == "git_tree")
            properties["recursive"] = new { type = "boolean", description = "Default false: list immediate children of path. True lists all descendants; prefer scoped directories." };
        if (name == "git_file")
        {
            properties["format"] = new { type = "string", @enum = new[] { "text", "hex" }, description = "Default text. Hex reads original Git blob bytes without text decoding." };
            properties["byte_offset"] = new { type = "integer", minimum = 0, maximum = int.MaxValue, description = "Hex only: byte offset (default 0), independent of text pagination." };
            properties["byte_count"] = new { type = "integer", minimum = 1, maximum = 4096, description = "Hex only: maximum bytes (default 256). No text offset or line bounds allowed." };
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
                "git_metadata" => "Metadata of reviewed SHA or its immediate first parent. Other SHAs mentioned in metadata do not authorize reads. Do not investigate bug age.",
                "git_changed_files" => "Page changed file names/status for reviewed SHA against first parent (root against empty tree). Start offset=0; auxiliary pages are optional.",
                "git_diff" => "Read the reviewed commit patch, including binary/mode changes. Start offset=0 and follow next_offset until null before final report.",
                "git_tree" => "Browse committed directory children, optionally recursive and scoped by path. Auxiliary pages need not all be read.",
                "git_search" => "Search literal text in an allowed commit, optionally scoped by path. Returns paths, line numbers and lines; binary files are skipped. Auxiliary pages need not all be read. No matches is a successful empty result.",
                _ => "Read committed blob, never working files. Default text supports pages/line ranges. format=hex uses byte_offset/byte_count and returns original bytes, blob size and BOM (not a guessed encoding). Symlinks return stored link bytes, not target content."
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
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        if (commit is null || !_commits.Contains(commit)) throw new ArgumentException("Only the reviewed SHA and its immediate first parent are allowed. Older history and other merge parents are prohibited.");
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
        if (offset < 0) throw new ArgumentException("Offset outside supported bounds.");
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        var recursive = args.TryGetProperty("recursive", out var r) && r.GetBoolean();
        if (name is "git_diff" or "git_changed_files" && commit != Sha)
            throw new ArgumentException("Changes are bound to the reviewed SHA.");
        if ((name == "git_file" || path is not null) && (string.IsNullOrEmpty(path) || path.Length > 2048 ||
            path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(part => part is "" or "." or "..")))
            throw new ArgumentException("Use an exact relative Git path without traversal or revision syntax.");
        if (name == "git_metadata" && offset != 0)
            throw new ArgumentException("Metadata does not support paging.");
        var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (name == "git_search" && (string.IsNullOrEmpty(query) || query.Length > 1024 || query.Any(char.IsControl)))
            throw new ArgumentException("Use a nonempty literal search query without control characters, up to 1024 characters.");
        int? startLine = args.TryGetProperty("start_line", out var s) ? s.GetInt32() : null;
        int? endLine = args.TryGetProperty("end_line", out var e) ? e.GetInt32() : null;
        var format = args.TryGetProperty("format", out var f) ? f.GetString() : "text";
        var byteOffset = args.TryGetProperty("byte_offset", out var bo) ? bo.GetInt32() : 0;
        var byteCount = args.TryGetProperty("byte_count", out var bc) ? bc.GetInt32() : 256;
        if (format is not ("text" or "hex")) throw new ArgumentException("format must be text or hex.");
        if (format == "hex" && (args.TryGetProperty("offset", out _) || startLine.HasValue || endLine.HasValue))
            throw new ArgumentException("Hex reads use byte_offset/byte_count, not text offset or line ranges.");
        if (format == "text" && (args.TryGetProperty("byte_offset", out _) || args.TryGetProperty("byte_count", out _)))
            throw new ArgumentException("Byte arguments require format=hex.");
        if (byteOffset < 0 || byteCount is < 1 or > 4096) throw new ArgumentException("Invalid byte range: offset >= 0; count 1..4096.");
        if (startLine.HasValue != endLine.HasValue || startLine is < 1 or > 2_000_000 ||
            endLine is < 1 or > 2_000_000 || endLine < startLine || endLine - startLine >= 500)
            throw new ArgumentException("Provide both start_line and end_line as an inclusive range of 1 to 500 lines.");
        var key = JsonSerializer.Serialize(new { name, commit, path, query, startLine, endLine, recursive });
        var replay = _readOffsets.TryGetValue(key, out var readOffsets) && readOffsets.Contains(offset);
        var expectedOffset = _nextOffsets.GetValueOrDefault(key);
        if (format != "hex" && !replay && offset != expectedOffset)
            throw new ArgumentException($"Offset {offset} has not been read. Expected offset: {expectedOffset}. Use that offset to continue, or repeat a previously returned page offset (including 0). Do not skip unread content.");
        if (name == "git_file" && !_verifiedFiles.Contains(commit + ":" + path))
        {
            // Inspect the committed tree, never stderr wording or working-copy existence.
            // A present tree entry with an unavailable blob must still fail when read.
            var entry = await GitService.ReadPageAsync(_repository, 0, PageSize, token, trace,
                "ls-tree", "-z", "--full-tree", commit, "--", path!);
            if (entry.ExitCode != 0 || entry.HasMore)
                throw new GitException("Cannot inspect the requested commit tree; review is incomplete. " + entry.Error);
            if (entry.Output.Length == 0)
                return JsonSerializer.Serialize(new
                {
                    status = "not_found",
                    reviewed_sha = Sha,
                    commit,
                    path,
                    message = "This path does not exist in this commit. Inspect git_tree at the reviewed SHA or its immediate first parent. Do not investigate older history or substitute working-copy content. Absence alone is not a bug."
                });
            _verifiedFiles.Add(commit + ":" + path);
        }
        if (format == "hex") return await ReadHexAsync(commit, path!, byteOffset, byteCount, token, trace);
        var command = name switch
        {
            "git_metadata" => new[] { "cat-file", "commit", commit },
            "git_changed_files" => DiffArguments(true),
            "git_diff" => DiffArguments(false),
            "git_tree" => (recursive ? new[] { "ls-tree", "-r", "--full-tree", commit, "--" } : new[] { "ls-tree", "--full-tree", commit, "--" })
                .Concat(path is null ? Array.Empty<string>() : new[] { path + "/" }).ToArray(),
            "git_search" => new[] { "grep", "--no-color", "--no-ext-grep", "--no-textconv", "-I", "-n", "-F", "-e", query!, commit, "--" }
                .Concat(path is null ? Array.Empty<string>() : new[] { path }).ToArray(),
            _ => new[] { "cat-file", "blob", commit + ":" + path }
        };
        GitResult result;
        if (name is "git_diff" or "git_changed_files" or "git_tree" or "git_search" || startLine.HasValue)
        {
            if (!_snapshots.TryGetValue(key, out var snapshot))
            {
                // Render once: live attributes/config must not alter or shorten later pages.
                snapshot = new FileStream(Path.Combine(Path.GetTempPath(), "GitReviewer-snapshot-" + Guid.NewGuid().ToString("N")),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
                try
                {
                    var available = _diskLimit - _snapshots.Values.Sum(value => value.Length);
                    var captured = await GitService.CaptureSnapshotAsync(_repository, snapshot,
                        (int)(available / 2), token, trace, command);
                    if (captured.HasMore)
                    {
                        var reason = $"Snapshot disk budget exceeded ({_diskLimit} bytes per session; {available} bytes available). Narrow path/query or request a smaller directory. Configure GITREVIEWER_SNAPSHOT_MB (1–1024, default 64) before restarting the application if needed.";
                        if (name == "git_diff") throw new GitException(reason + " Full diff is required; review incomplete.");
                        throw new GitToolQueryException(reason);
                    }
                    if (captured.ExitCode != 0 && !(name == "git_search" && captured.ExitCode == 1 && snapshot.Length == 0))
                        throw new GitException("Git snapshot unavailable; review is incomplete. " + captured.Error);
                    if (startLine.HasValue)
                    {
                        if (snapshot.Length > RangeInputLimit * 2L)
                            throw new GitToolQueryException("Line-range input exceeds 512000 characters. Use ordinary paged git_file without start_line/end_line.");
                        var source = ReadSnapshot(snapshot, 0, (int)(snapshot.Length / 2));
                        if (source.Contains('\0')) throw new GitToolQueryException("Line ranges require a text blob. Use ordinary paged git_file.");
                        var lines = source.Split('\n');
                        var lineCount = source.Length == 0 ? 0 : lines.Length - (source.EndsWith('\n') ? 1 : 0);
                        if (startLine.Value > lineCount) throw new ArgumentException("start_line is beyond the end of the file.");
                        var numbered = string.Concat(Enumerable.Range(startLine.Value, Math.Min(endLine!.Value, lineCount) - startLine.Value + 1)
                            .Select(line => $"{line}: {lines[line - 1].TrimEnd('\r')}\n"));
                        if (numbered.Length * 2L > available) throw new GitToolQueryException("Numbered range exceeds snapshot disk budget. Request fewer lines.");
                        snapshot.SetLength(0);
                        snapshot.Position = 0;
                        var bytes = System.Text.Encoding.Unicode.GetBytes(numbered);
                        await snapshot.WriteAsync(bytes, token);
                    }
                    _snapshots.Add(key, snapshot);
                }
                catch { snapshot.Dispose(); throw; }
            }
            var characters = snapshot.Length / 2;
            if (offset > characters) throw new GitException("Snapshot ended before the requested offset; review is incomplete.");
            var content = ReadSnapshot(snapshot, offset, (int)Math.Min(PageSize, characters - offset));
            if (content.Length > 0 && offset + content.Length < characters && char.IsHighSurrogate(content[^1])) content = content[..^1];
            result = new GitResult(0, content, "", offset + content.Length < characters);
        }
        else
            result = await GitService.ReadPageAsync(_repository, offset, PageSize, token, trace, command);
        if (result.ExitCode != 0) throw new GitException("Git could not read that local object/path; review is incomplete. No network fetch is permitted.");
        if (result.HasMore && name == "git_metadata")
            throw new GitException("Metadata output limit exceeded; review is incomplete.");
        var next = offset + result.Output.Length;
        // Re-reading a page must not rewind progress or reopen a completed resource.
        if (!replay)
        {
            if (name != "git_metadata")
            {
                _nextOffsets[key] = next;
                if (!_readOffsets.TryGetValue(key, out readOffsets))
                    _readOffsets[key] = readOffsets = [];
                readOffsets.Add(offset);
            }
            if (name == "git_diff") DiffComplete = !result.HasMore;
        }
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            reviewed_sha = Sha,
            commit,
            offset,
            next_offset = result.HasMore ? (int?)next : null,
            content = result.Output
        });
    }

    private async Task<string> ReadHexAsync(string commit, string path, int offset, int count, CancellationToken token, Action<GitCommandTrace>? trace)
    {
        var objectName = commit + ":" + path;
        var sizeResult = await GitService.ReadPageAsync(_repository, 0, 64, token, trace, "cat-file", "-s", objectName);
        if (sizeResult.ExitCode != 0 || sizeResult.HasMore || !long.TryParse(sizeResult.Output.Trim(), out var size) || size < 0)
            throw new GitException("Cannot determine Git blob size; review incomplete.");
        if (offset > size) throw new ArgumentException("byte_offset exceeds blob size.");
        var header = await GitService.ReadBytesAsync(_repository, 0, 4, token, trace, "cat-file", "blob", objectName);
        if (header.ExitCode != 0) throw new GitException("Cannot read Git blob bytes. " + header.Error);
        var data = await GitService.ReadBytesAsync(_repository, offset, count, token, trace, "cat-file", "blob", objectName);
        if (data.ExitCode != 0) throw new GitException("Cannot read Git blob bytes. " + data.Error);
        var expected = (int)Math.Min(count, size - offset);
        if (data.Output.Length != expected * 2) throw new GitException("Git blob byte range is incomplete.");
        var bom = header.Output.StartsWith("0000FEFF", StringComparison.Ordinal) ? "UTF-32BE"
            : header.Output.StartsWith("FFFE0000", StringComparison.Ordinal) ? "UTF-32LE"
            : header.Output.StartsWith("EFBBBF", StringComparison.Ordinal) ? "UTF-8"
            : header.Output.StartsWith("FEFF", StringComparison.Ordinal) ? "UTF-16BE"
            : header.Output.StartsWith("FFFE", StringComparison.Ordinal) ? "UTF-16LE" : "none";
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            reviewed_sha = Sha,
            commit,
            path,
            format = "hex",
            blob_size_bytes = size,
            byte_offset = offset,
            bytes_returned = expected,
            next_byte_offset = offset + (long)expected < size ? offset + (long)expected : (long?)null,
            bom,
            hex = data.Output,
            note = "Original committed bytes. BOM identifies a signature only; no BOM does not imply UTF-8. Byte offsets are not text or line offsets."
        });
    }

    private static string ReadSnapshot(FileStream snapshot, int offset, int count)
    {
        snapshot.Position = offset * 2L;
        var bytes = new byte[count * 2];
        snapshot.ReadExactly(bytes);
        var characters = new char[count];
        Buffer.BlockCopy(bytes, 0, characters, 0, bytes.Length);
        return new string(characters);
    }
}

public sealed class GitToolQueryException(string message) : Exception(message);
