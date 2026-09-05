using System.Security.Cryptography;
using System.Text;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Backlog;

/// <summary>
/// When a file was last written and what it contained, as one value. Recorded on
/// every read so a staleness check (ABSD-504) and the save-time conflict guard
/// (ABSD-206) both answer from the same evidence rather than each inventing one.
///
/// The hash is what makes "changed" mean changed: a rewrite with identical bytes
/// moves the timestamp but not the hash, and re-reporting that as an external edit
/// would train a user to dismiss the warning that matters.
/// </summary>
public sealed record FileStamp(DateTimeOffset LastWriteTimeUtc, string ContentHash)
{
    public static FileStamp For(DateTimeOffset lastWriteUtc, string text) =>
        new(lastWriteUtc, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));

    /// <summary>
    /// The same stamp from bytes the caller already holds. Both store call sites have
    /// the exact UTF-8 the text was decoded from or encoded to, so re-encoding the
    /// string to hash it allocates a second full-file buffer for no new information —
    /// which at the project's own 500-item fixture size lands on the large object heap
    /// on every read and every save.
    /// </summary>
    public static FileStamp For(DateTimeOffset lastWriteUtc, ReadOnlySpan<byte> utf8) =>
        new(lastWriteUtc, Convert.ToHexStringLower(SHA256.HashData(utf8)));

    /// <summary>True when the bytes changed, whatever the timestamp did.</summary>
    public bool ContentDiffersFrom(FileStamp other) =>
        !string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal);
}

/// <summary>One file as the store read it, with the stamp that read produced.</summary>
public sealed record StoredFile(string Text, FileStamp Stamp);

/// <summary>
/// Every read of and write to a profile's files goes through this port (ABSD-106).
/// Before it existed the UI called <c>File.ReadAllText</c> directly, which meant
/// the one write path in the application could only be tested by touching a real
/// disk, and CONVENTIONS rule 3's "no storage in Core" held only by habit.
///
/// The atomic write is part of the contract, not of one caller: FSD NFR-7 requires
/// that a crash mid-save cannot leave a half-written backlog, and a port that only
/// promised "write" would let the next caller reintroduce the hazard. ABSD-206,
/// ABSD-401 and ABSD-402 all write through this one method.
///
/// <paramref name="codeScope"/> exists because this store serves two vocabularies:
/// the backlog Markdown reports <c>backlog.not_found</c> and the config file
/// reports <c>config.not_found</c>, and a user reading a banner should be told
/// which file is missing. One store, two prefixes, rather than two stores.
/// </summary>
public interface IBacklogFileStore
{
    /// <summary>The scope every backlog read and write reports its failures under.</summary>
    const string BacklogScope = "backlog";

    /// <summary>The scope config reads report their failures under.</summary>
    const string ConfigScope = "config";

    bool Exists(string path);

    /// <summary>
    /// Reads the whole file as UTF-8. Fails with <c>{scope}.not_found</c> when it is
    /// not there, <c>{scope}.unreadable</c> when the operating system refuses, and
    /// <c>{scope}.undecodable</c> when the bytes are not valid UTF-8. Never throws.
    /// </summary>
    Result<StoredFile> Read(string path, string codeScope = BacklogScope);

    /// <summary>
    /// Writes <paramref name="text"/> over <paramref name="path"/> atomically: a
    /// temporary file in the same directory, flushed, then renamed over the
    /// original — so a reader sees either the whole old file or the whole new one,
    /// and an abort between the two leaves the original byte-identical (FSD NFR-7).
    /// Returns the stamp the write produced, so a caller need not re-read to learn it.
    /// </summary>
    Result<StoredFile> WriteAtomic(string path, string text, string codeScope = BacklogScope);

    /// <summary>
    /// The stamp of the file as it is on disk now, without the caller keeping its
    /// text. A missing file is a failure, not a null: "gone" and "unchanged" must
    /// not look the same to a staleness check.
    ///
    /// Stated once here rather than on each adapter: every implementation was the
    /// same two lines over <see cref="Read"/>, so a new adapter inherits the
    /// behaviour instead of being trusted to copy it.
    /// </summary>
    Result<FileStamp> Stamp(string path, string codeScope = BacklogScope)
    {
        var read = Read(path, codeScope);
        return read.IsFailure ? read.Error! : read.Value.Stamp;
    }

    /// <summary>The not-found failure, so every adapter reports the same code and words.</summary>
    static Error NotFound(string path, string codeScope) =>
        Error.NotFound($"{codeScope}.not_found", $"File not found: {path}.");
}
