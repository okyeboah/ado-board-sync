using System.Text;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Infrastructure;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The ABSD-106 store against a real disk. The in-memory fake proves what callers
/// do with the port; only a filesystem can prove what the port promises about one
/// — that no failure arrives as an exception, that a byte-order mark survives a
/// round trip, and above all that FSD NFR-7 holds: an abort between the temporary
/// write and the rename leaves the original backlog byte-identical.
/// </summary>
public class BacklogFileStoreTests
{
    // Matches the store's own encoder, so a byte-for-byte comparison is against the
    // bytes the store would have written rather than File.WriteAllText's guess.
    private static readonly UTF8Encoding Utf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    [Fact]
    public void AMissingBacklogIsReportedAsNotFoundRatherThanThrowing()
    {
        InTempDirectory(directory =>
        {
            var store = new FileSystemBacklogFileStore();
            var path = Path.Combine(directory, "absent.md");

            var read = store.Read(path);

            Assert.False(store.Exists(path));
            Assert.True(read.IsFailure);
            Assert.Equal("backlog.not_found", read.Error!.Code);
            Assert.Equal(ErrorKind.NotFound, read.Error.Kind);
        });
    }

    [Fact]
    public void AFileTheOperatingSystemWillNotOpenIsReportedAsUnreadable()
    {
        InTempDirectory(directory =>
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var path = Path.Combine(directory, "backlog.md");
            File.WriteAllBytes(path, Utf8NoBom.GetBytes("## Epic 1 — Locked away\n"));
            File.SetUnixFileMode(path, UnixFileMode.None);
            try
            {
                if (CanRead(path))
                {
                    // Root, or a filesystem that ignores the mode bits: this
                    // environment cannot express "unreadable", so there is nothing
                    // to assert here rather than something to assert loosely.
                    return;
                }

                var read = new FileSystemBacklogFileStore().Read(path);

                Assert.True(read.IsFailure);
                Assert.Equal("backlog.unreadable", read.Error!.Code);
            }
            finally
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        });
    }

    [Fact]
    public void BytesThatAreNotValidUtf8AreReportedAsUndecodable()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "backlog.md");
            File.WriteAllBytes(path, [0x48, 0xFF, 0xFE, 0x49]);

            var read = new FileSystemBacklogFileStore().Read(path);

            Assert.True(read.IsFailure);
            Assert.Equal("backlog.undecodable", read.Error!.Code);
            Assert.Equal(ErrorKind.Validation, read.Error.Kind);
        });
    }

    [Fact]
    public void TheCodeScopeChoosesWhichFileTheFailureNames()
    {
        InTempDirectory(directory =>
        {
            var store = new FileSystemBacklogFileStore();
            var path = Path.Combine(directory, "board.config.json");

            var read = store.Read(path, IBacklogFileStore.ConfigScope);

            Assert.True(read.IsFailure);
            Assert.Equal("config.not_found", read.Error!.Code);
            Assert.Equal(ErrorKind.NotFound, read.Error.Kind);

            // One store, two vocabularies: the same missing file under the default
            // scope must tell the user the backlog is gone, not the config.
            Assert.Equal("backlog.not_found", store.Read(path).Error!.Code);
        });
    }

    [Fact]
    public void AnAbortBetweenTheTemporaryWriteAndTheRenameLeavesTheOriginalIntact()
    {
        InTempDirectory(directory =>
        {
            var store = new FileSystemBacklogFileStore();
            var path = Path.Combine(directory, "backlog.md");
            const string rewritten = "## Epic 1 — Rewritten\n\n### PROJ-101 · Saved\n";
            File.WriteAllBytes(
                path,
                Utf8NoBom.GetBytes("## Epic 1 — Seeded\n\n### PROJ-101 · Original\n"));

            var written = store.WriteAtomic(path, rewritten);

            Assert.True(written.IsSuccess, written.Error?.SafeMessage);
            Assert.Empty(TempFilesIn(directory));

            var beforeTheAbort = File.ReadAllBytes(path);
            if (TryDenyNewFiles(directory))
            {
                try
                {
                    // The store cannot create its temporary file, which is where a
                    // crashed save dies too: whatever the cause, the rename never
                    // happens and the original must come through untouched.
                    var refused = store.WriteAtomic(path, "## Epic 1 — Never lands\n");

                    Assert.True(refused.IsFailure);
                    Assert.Equal("backlog.unsaved", refused.Error!.Code);
                    Assert.Equal(beforeTheAbort, File.ReadAllBytes(path));
                    Assert.Empty(TempFilesIn(directory));
                }
                finally
                {
                    RestoreWriting(directory);
                }
            }

            // A leftover from somebody else's aborted save is a temp file, not the
            // backlog: the store reads the path it was given and nothing beside it.
            File.WriteAllBytes(
                Path.Combine(directory, ".backlog.md.tmp-0123456789abcdef"),
                Utf8NoBom.GetBytes("## Epic 1 — Half-written rub"));

            var read = store.Read(path);

            Assert.True(read.IsSuccess, read.Error?.SafeMessage);
            Assert.Equal(rewritten, read.Value.Text);
        });
    }

    [Fact]
    public void ACrlfBacklogWithABomRoundTripsByteForByteAndKeepsItsBom()
    {
        InTempDirectory(directory =>
        {
            var store = new FileSystemBacklogFileStore();
            var path = Path.Combine(directory, "backlog.md");
            var original = Utf8NoBom.GetBytes(
                "\uFEFF## Epic 1 — Saved from Notepad\r\n\r\n### PROJ-101 · CRLF\r\n");
            File.WriteAllBytes(path, original);

            var read = store.Read(path);
            Assert.True(read.IsSuccess, read.Error?.SafeMessage);

            // parser.py opens the backlog with encoding="utf-8", which hands the BOM
            // back as an ordinary character. Stripping it here would make the desktop
            // app parse a BOM-prefixed backlog differently from the CLI.
            Assert.Equal('\uFEFF', read.Value.Text[0]);

            var written = store.WriteAtomic(path, read.Value.Text);

            Assert.True(written.IsSuccess, written.Error?.SafeMessage);
            Assert.Equal(original, File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void AnLfBacklogWithNoTrailingNewlineRoundTripsByteForByte()
    {
        InTempDirectory(directory =>
        {
            var store = new FileSystemBacklogFileStore();
            var path = Path.Combine(directory, "backlog.md");
            var original = Utf8NoBom.GetBytes(
                "## Epic 1 — Unix\n\n### PROJ-101 · No newline at end of file");
            File.WriteAllBytes(path, original);

            var read = store.Read(path);
            Assert.True(read.IsSuccess, read.Error?.SafeMessage);

            var written = store.WriteAtomic(path, read.Value.Text);

            Assert.True(written.IsSuccess, written.Error?.SafeMessage);
            Assert.Equal(original, File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void RewritingIdenticalContentIsNotReportedAsAnExternalEdit()
    {
        InTempDirectory(directory =>
        {
            IBacklogFileStore store = new FileSystemBacklogFileStore();
            var path = Path.Combine(directory, "backlog.md");
            const string text = "## Epic 1 — Stable\n\n### PROJ-101 · Unchanged\n";

            var first = store.WriteAtomic(path, text);
            Assert.True(first.IsSuccess, first.Error?.SafeMessage);

            // Aged by hand rather than by waiting: two writes in a row can land
            // inside one timestamp tick, which would prove nothing about the hash.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            var aged = store.Stamp(path);
            Assert.True(aged.IsSuccess, aged.Error?.SafeMessage);

            var second = store.WriteAtomic(path, text);
            Assert.True(second.IsSuccess, second.Error?.SafeMessage);

            Assert.NotEqual(aged.Value.LastWriteTimeUtc, second.Value.Stamp.LastWriteTimeUtc);
            Assert.Equal(first.Value.Stamp.ContentHash, second.Value.Stamp.ContentHash);
            Assert.Equal(aged.Value.ContentHash, second.Value.Stamp.ContentHash);

            // ABSD-504 asks the stamp, not the clock: a moved timestamp over identical
            // bytes is not an external edit, and reporting one would train the user to
            // dismiss the warning that matters.
            Assert.False(second.Value.Stamp.ContentDiffersFrom(aged.Value));

            var edited = store.WriteAtomic(
                path,
                text.Replace("PROJ-101", "PROJ-102", StringComparison.Ordinal));

            Assert.True(edited.IsSuccess, edited.Error?.SafeMessage);
            Assert.True(edited.Value.Stamp.ContentDiffersFrom(second.Value.Stamp));
        });
    }

    [Fact]
    public void WriteAtomicCreatesTheDirectoriesLeadingToTheFile()
    {
        InTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "docs", "planning", "backlog.md");
            const string text = "## Epic 1 — Nested\n";

            var written = new FileSystemBacklogFileStore().WriteAtomic(path, text);

            Assert.True(written.IsSuccess, written.Error?.SafeMessage);
            Assert.Equal(Utf8NoBom.GetBytes(text), File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void StampingAMissingFileIsAFailureRatherThanAnEmptyStamp()
    {
        InTempDirectory(directory =>
        {
            var stamp = ((IBacklogFileStore)new FileSystemBacklogFileStore()).Stamp(Path.Combine(directory, "absent.md"));

            Assert.True(stamp.IsFailure);
            Assert.Equal("backlog.not_found", stamp.Error!.Code);
            Assert.Equal(ErrorKind.NotFound, stamp.Error.Kind);
        });
    }

    /// <summary>
    /// Every test gets its own directory: some of these strip permission bits, and a
    /// shared directory would carry that state into whatever ran next.
    /// </summary>
    private static void InTempDirectory(Action<string> test)
    {
        var directory = Directory.CreateTempSubdirectory("abs-filestore-").FullName;
        try
        {
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The store's temp files, by its own convention of <c>.{name}.tmp-{guid}</c>
    /// beside the destination. Hidden files are deliberately not skipped: the leading
    /// dot marks the file hidden on Unix, and a remnant this assertion cannot see is
    /// exactly the torn save NFR-7 exists to prevent.
    /// </summary>
    private static string[] TempFilesIn(string directory) =>
        Directory.GetFiles(
            directory,
            ".*.tmp-*",
            new EnumerationOptions { AttributesToSkip = FileAttributes.None });

    private static bool CanRead(string path)
    {
        try
        {
            File.ReadAllBytes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stops the store from creating its temporary file, the closest a test can get
    /// to the process dying before the rename. Returns false where the environment
    /// cannot express that — Windows has no Unix mode, root ignores it — so the
    /// caller skips instead of asserting something it is not actually testing.
    /// </summary>
    private static bool TryDenyNewFiles(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        new DirectoryInfo(directory).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;

        var probe = Path.Combine(directory, ".permission-probe");
        try
        {
            File.WriteAllBytes(probe, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        File.Delete(probe);
        return false;
    }

    private static void RestoreWriting(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        new DirectoryInfo(directory).UnixFileMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    }
}
