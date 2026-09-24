using System.Text;

namespace GitReviewer.Services;

public sealed class PersistentLog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxTailLines;
    private readonly int _maxTailCharacters;
    private readonly Queue<string> _tail = new();
    private int _characters;
    private bool _dirty = true;
    public string? Error { get; private set; }

    public PersistentLog(
        string path,
        long maxBytes = 8 * 1024 * 1024,
        int maxTailLines = 500,
        int maxTailCharacters = 128_000)
    {
        _path = path;
        _maxBytes = maxBytes;
        _maxTailLines = maxTailLines;
        _maxTailCharacters = maxTailCharacters;
        try
        {
            // Seek a bounded suffix, discard a possibly partial UTF-8 line.
            foreach (var file in new[] { path + ".1", path })
            {
                if (!File.Exists(file)) continue;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var start = Math.Max(0, stream.Length - 256_000);
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                if (start > 0) reader.ReadLine();
                string? line;
                while ((line = reader.ReadLine()) is not null) AddTail(line + "\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Error = "Log history could not be loaded (" + e.GetType().Name + ")."; }
    }

    private void AddTail(string line)
    {
        if (line.Length > 100_000) line = "[UI tail truncated] " + line[^99_000..];
        _tail.Enqueue(line);
        _characters += line.Length;
        while (_tail.Count > _maxTailLines || _characters > _maxTailCharacters)
            _characters -= _tail.Dequeue().Length;
        _dirty = true;
    }

    public void Append(string message)
    {
        lock (_gate)
        {
            var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
            foreach (var source in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                // Bound even a single huge entry, without dropping its persisted content.
                for (var offset = 0; offset < Math.Max(1, source.Length); offset += 2048)
                {
                    var line = stamp + (offset == 0 ? " | " : " | [continued] ") + source.Substring(offset, Math.Min(2048, source.Length - offset)) + "\n";
                    AddTail(line);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                        var bytes = Encoding.UTF8.GetBytes(line);
                        if (File.Exists(_path) && new FileInfo(_path).Length + bytes.Length > _maxBytes)
                            File.Move(_path, _path + ".1", true);
                        using var file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                        file.Write(bytes);
                        // Disposing flushes each append, including canceled partial responses.
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { Error = "Log persistence unavailable (" + e.GetType().Name + "). Review continues; visible history is memory-only."; }
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            // Remove the rotated history too, otherwise it returns on restart.
            File.Delete(_path + ".1");
            File.Delete(_path);
            _tail.Clear();
            _characters = 0;
            Error = null;
            _dirty = true;
        }
    }

    public string? Snapshot(bool force = false)
    {
        lock (_gate)
        {
            if (!_dirty && !force) return null;
            _dirty = false;
            return (Error is null ? "" : Error + "\n") + string.Concat(_tail);
        }
    }
}
