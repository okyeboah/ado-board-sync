using System.Collections.Concurrent;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.TestKit;

/// <summary>
/// A file store that lives in a dictionary (ABSD-107). It is what proves the seam
/// is real: a view-model test that drives the whole load path through this one
/// touches no disk, so it cannot pass because of a file some earlier test left
/// behind, and it cannot be slow because of one.
///
/// Behaviour matches <c>FileSystemBacklogFileStore</c> where a test can tell the
/// difference: the same error codes under the same scope, a stamp on every read,
/// and a write that returns the stamp it produced.
/// </summary>
public sealed class InMemoryBacklogFileStore : IBacklogFileStore
{
    private readonly ConcurrentDictionary<string, Entry> _files = new(StringComparer.Ordinal);

    /// <summary>Advanced by one tick per write, so two writes never share a timestamp.</summary>
    private DateTimeOffset _clock = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every path read, in call order — for asserting what a load touched.</summary>
    public List<string> Reads { get; } = [];

    /// <summary>Every path written, in call order.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Set to make the next read of this path fail, whatever it holds.</summary>
    public Dictionary<string, Error> ReadErrors { get; } = new(StringComparer.Ordinal);

    /// <summary>Set to make every write fail.</summary>
    public Error? WriteError { get; set; }

    /// <summary>Runs before each read — the seam a test uses to race an external edit in.</summary>
    public Action<string>? BeforeRead { get; set; }

    /// <summary>Seeds a file. Returns this, so a fixture reads as one expression.</summary>
    public InMemoryBacklogFileStore With(string path, string text)
    {
        _clock = _clock.AddSeconds(1);
        _files[path] = new Entry(text, _clock);
        return this;
    }

    /// <summary>Replaces a file as an external editor would, moving its stamp.</summary>
    public void ChangeExternally(string path, string text) => With(path, text);

    public string? TextAt(string path) => _files.TryGetValue(path, out var entry) ? entry.Text : null;

    public bool Exists(string path) => _files.ContainsKey(path);

    public Result<StoredFile> Read(string path, string codeScope = IBacklogFileStore.BacklogScope)
    {
        BeforeRead?.Invoke(path);
        Reads.Add(path);

        if (ReadErrors.TryGetValue(path, out var failure))
        {
            return failure;
        }

        if (!_files.TryGetValue(path, out var entry))
        {
            return Error.NotFound($"{codeScope}.not_found", $"File not found: {path}.");
        }

        return new StoredFile(entry.Text, FileStamp.For(entry.LastWriteUtc, entry.Text));
    }

    public Result<StoredFile> WriteAtomic(
        string path, string text, string codeScope = IBacklogFileStore.BacklogScope)
    {
        if (WriteError is { } failure)
        {
            return failure;
        }

        Writes.Add(path);
        With(path, text);
        return new StoredFile(text, FileStamp.For(_files[path].LastWriteUtc, text));
    }

    public Result<FileStamp> Stamp(string path, string codeScope = IBacklogFileStore.BacklogScope)
    {
        var read = Read(path, codeScope);
        return read.IsFailure ? read.Error! : read.Value.Stamp;
    }

    private sealed record Entry(string Text, DateTimeOffset LastWriteUtc);
}
