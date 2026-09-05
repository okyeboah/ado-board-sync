using System.Text;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure;

/// <summary>
/// The only place in the application that touches the filesystem on a profile's
/// behalf (ABSD-106). Every failure comes back as a typed <see cref="Error"/>;
/// nothing here throws.
///
/// Bytes are handled directly rather than through <c>File.ReadAllText</c>, which
/// detects and strips a byte-order mark. The CLI does not: <c>parser.py</c> opens
/// the backlog with <c>encoding="utf-8"</c>, which yields a leading U+FEFF as an
/// ordinary character. Stripping it here would make the desktop app parse a
/// BOM-prefixed backlog differently from the CLI — a parity break invisible until
/// someone saved a backlog from Notepad.
/// </summary>
public sealed class FileSystemBacklogFileStore : IBacklogFileStore
{
    // Throws on malformed bytes rather than substituting U+FFFD, so "this file is
    // not UTF-8" is reported as itself instead of silently becoming replacement
    // characters that would then be written back over the user's file.
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public bool Exists(string path) => File.Exists(path);

    public Result<StoredFile> Read(string path, string codeScope = IBacklogFileStore.BacklogScope)
    {
        if (!File.Exists(path))
        {
            return IBacklogFileStore.NotFound(path, codeScope);
        }

        byte[] bytes;
        DateTimeOffset lastWrite;
        try
        {
            bytes = File.ReadAllBytes(path);
            lastWrite = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.SourceFailure($"{codeScope}.unreadable", $"Could not read {path}: {ex.Message}");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Error.Validation(
                $"{codeScope}.undecodable",
                $"{path} is not valid UTF-8 text. Re-save it as UTF-8 and open it again.");
        }

        // Hashed from the bytes just read rather than by re-encoding the string:
        // identical digest, one fewer full-file allocation.
        return new StoredFile(text, FileStamp.For(lastWrite, bytes));
    }

    public Result<StoredFile> WriteAtomic(
        string path, string text, string codeScope = IBacklogFileStore.BacklogScope)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
        {
            return Error.Validation($"{codeScope}.unsaved", $"Cannot write to {path}: it names no directory.");
        }

        // The temporary file lives in the destination directory on purpose: a
        // rename is only atomic within one filesystem, and a temp directory can
        // easily be on another volume.
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        var bytes = StrictUtf8.GetBytes(text);
        try
        {
            Directory.CreateDirectory(directory);

            // Flushed to the device before the rename: without this the rename can
            // be durable while the bytes it points at are not, which is the same
            // torn file NFR-7 exists to prevent.
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryRemove(tempPath);
            return Error.SourceFailure($"{codeScope}.unsaved", $"Could not save {path}: {ex.Message}");
        }

        // The write already succeeded, so a failure to read the timestamp back must
        // not be reported as a failed save — the caller would keep an editor buffer
        // it believes is unsaved over a file that is. Fall back to now: the stamp's
        // load-bearing half is the content hash, which is exact either way.
        DateTimeOffset lastWrite;
        try
        {
            lastWrite = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lastWrite = DateTimeOffset.UtcNow;
        }

        return new StoredFile(text, FileStamp.For(lastWrite, bytes));
    }

    private static void TryRemove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp file is better than masking the save failure that
            // brought us here — the caller needs to hear about that one.
        }
    }
}
