using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Diagnostics;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Infrastructure.Diagnostics;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The ABSD-507 sinks against a real disk. Two promises need a filesystem to prove:
/// that the log rotates instead of growing without bound, and that a diagnostics
/// failure stays a diagnostics failure — a sink that threw would replace the error
/// the user was actually trying to understand with one about logging.
/// </summary>
public class DiagnosticsSinkTests
{
    private const string Secret = "Swordfish-Passphrase-9f3a";

    [Fact]
    public void TheLogRotatesOnceTheCapIsReached()
    {
        InTempDirectory(directory =>
        {
            var sink = new JsonLinesDiagnosticsSink(
                directory, new DiagnosticRedaction(), maximumFileBytes: 400, maximumFiles: 3);

            for (var i = 0; i < 20; i++)
            {
                sink.Write(Info($"event number {i}"));
            }

            Assert.True(File.Exists(sink.CurrentFilePath));
            Assert.True(File.Exists(Path.Combine(directory, DiagnosticsPaths.ArchiveFileName(1))));

            foreach (var file in Directory.GetFiles(directory, DiagnosticsPaths.LogFileSearchPattern))
            {
                Assert.True(
                    new FileInfo(file).Length <= 400,
                    $"{Path.GetFileName(file)} grew past the cap it was given.");
            }

            Assert.Equal(0, sink.FailedWrites);
        });
    }

    [Fact]
    public void RotationKeepsOnlyAsManyFilesAsItWasAskedFor()
    {
        InTempDirectory(directory =>
        {
            var sink = new JsonLinesDiagnosticsSink(
                directory, new DiagnosticRedaction(), maximumFileBytes: 200, maximumFiles: 3);

            for (var i = 0; i < 200; i++)
            {
                sink.Write(Info($"event number {i}"));
            }

            var files = Directory.GetFiles(directory, DiagnosticsPaths.LogFileSearchPattern);

            // Three files total: the current one and two archives. A fourth would
            // mean the oldest is never dropped and the cap is not a cap.
            Assert.Equal(3, files.Length);
            Assert.False(File.Exists(Path.Combine(directory, DiagnosticsPaths.ArchiveFileName(3))));
        });
    }

    [Fact]
    public void AWriteToAnUnwritableDirectoryDoesNotThrow()
    {
        InTempDirectory(directory =>
        {
            // A file where the directory should be. The operating system cannot
            // create the log directory over it on any platform, which is the point:
            // this is a genuinely impossible write, and it still has to be silent.
            var blocked = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(blocked, "occupied");

            var sink = new JsonLinesDiagnosticsSink(blocked, new DiagnosticRedaction());

            var failure = Record.Exception(() => sink.Write(Info("this cannot be written anywhere")));

            Assert.Null(failure);
            Assert.Equal(1, sink.FailedWrites);
        });
    }

    [Fact]
    public void AWriteToADirectoryTheOperatingSystemWillNotOpenDoesNotThrow()
    {
        InTempDirectory(directory =>
        {
            var target = Path.Combine(directory, "logs");
            Directory.CreateDirectory(target);
            if (!TryDenyNewFiles(target))
            {
                // Windows has no Unix mode, and root ignores it. Nothing to assert
                // here rather than something asserted loosely.
                return;
            }

            try
            {
                var sink = new JsonLinesDiagnosticsSink(target, new DiagnosticRedaction());

                Assert.Null(Record.Exception(() => sink.Write(Info("permission denied"))));
                Assert.Equal(1, sink.FailedWrites);
            }
            finally
            {
                RestoreWriting(target);
            }
        });
    }

    [Fact]
    public void ANullEventIsDroppedRatherThanThrown()
    {
        InTempDirectory(directory =>
        {
            var sink = new JsonLinesDiagnosticsSink(directory, new DiagnosticRedaction());

            Assert.Null(Record.Exception(() => sink.Write(null!)));
            Assert.Equal(1, sink.FailedWrites);
        });
    }

    [Fact]
    public void EveryEventIsOneLineOfJsonEvenWhenTheMessageSpansLines()
    {
        InTempDirectory(directory =>
        {
            var sink = new JsonLinesDiagnosticsSink(directory, new DiagnosticRedaction());

            sink.Write(Info("first"));
            // A markup problem quotes the user's own text, which wraps. If that
            // reached the file raw, one event would become several lines and every
            // grep or jq filter over the bundle would come apart.
            sink.Write(Info("second\nwith a newline\r\nand a carriage return"));
            sink.Write(Info("third"));

            var lines = File.ReadAllLines(sink.CurrentFilePath);

            Assert.Equal(3, lines.Length);
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                Assert.Equal("info", document.RootElement.GetProperty("level").GetString());
                Assert.Equal("plan", document.RootElement.GetProperty("category").GetString());
            }
        });
    }

    [Fact]
    public void AFailureLineCarriesTheCodeTheStatusBarShowedTheUser()
    {
        InTempDirectory(directory =>
        {
            var sink = new JsonLinesDiagnosticsSink(directory, new DiagnosticRedaction());

            sink.OperationFailed(
                "apply",
                Error.Conflict("plan.stale_backlog", "The backlog file changed after this Plan was generated."));

            using var document = JsonDocument.Parse(File.ReadAllLines(sink.CurrentFilePath)[0]);

            Assert.Equal("plan.stale_backlog", document.RootElement.GetProperty("code").GetString());
            Assert.Equal("error", document.RootElement.GetProperty("level").GetString());
            Assert.Equal("Conflict", document.RootElement.GetProperty("data").GetProperty("kind").GetString());
        });
    }

    [Fact]
    public void APlanAndItsApplyAreRecordedWithTheirCountsAndDurations()
    {
        InTempDirectory(directory =>
        {
            var sink = new JsonLinesDiagnosticsSink(directory, new DiagnosticRedaction());
            var plan = SamplePlan();

            sink.PlanGenerated(plan, TimeSpan.FromMilliseconds(120));
            sink.ApplyStarted(plan);
            sink.ApplyFinished(plan, new ApplyReport([Failed(plan.Rows[0]), Succeeded(plan.Rows[1])]),
                TimeSpan.FromSeconds(3));
            sink.FileWritten("backlog", Path.Combine(directory, "backlog.md"), 4096);

            var lines = File.ReadAllLines(sink.CurrentFilePath);
            Assert.Equal(4, lines.Length);

            var generated = Data(lines[0]);
            Assert.Equal("Import", generated["command"]);
            Assert.Equal("2", generated["rows"]);
            Assert.Equal("120", generated["duration_ms"]);

            var finished = JsonDocument.Parse(lines[2]).RootElement;
            // Warning, not Info: a run with a failed row is the run a support
            // conversation is about, and it has to be findable without reading
            // every line of the bundle.
            Assert.Equal("warning", finished.GetProperty("level").GetString());
            Assert.Equal("PROJ-1", finished.GetProperty("data").GetProperty("failed_codes").GetString());

            Assert.Equal("4096", Data(lines[3])["bytes"]);
        });
    }

    [Fact]
    public void ARegisteredSecretNeverReachesTheFile()
    {
        InTempDirectory(directory =>
        {
            var redaction = new DiagnosticRedaction();
            redaction.Register(Secret);
            var sink = new JsonLinesDiagnosticsSink(directory, redaction);

            sink.Write(Info($"authenticating with {Secret}"));

            Assert.DoesNotContain(
                Secret, File.ReadAllText(sink.CurrentFilePath), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheBundleCarriesTheLogsAndAnEnvironmentSummary()
    {
        InTempDirectory(directory =>
        {
            var logs = Path.Combine(directory, "logs");
            var sink = new JsonLinesDiagnosticsSink(logs, new DiagnosticRedaction());
            sink.Write(Info("something happened"));

            var destination = Path.Combine(directory, "bundle.zip");
            var written = DiagnosticsBundle.Write(
                new DiagnosticsBundleRequest
                {
                    DestinationPath = destination,
                    LogDirectory = logs,
                    CredentialStoreAvailable = true,
                    CredentialStoreName = "macOS Keychain",
                },
                new DiagnosticRedaction());

            Assert.True(written.IsSuccess, written.Error?.SafeMessage);

            using var archive = ZipFile.OpenRead(destination);
            var summary = ReadEntry(archive, DiagnosticsBundle.SummaryEntryName);

            Assert.Contains("macOS Keychain (available)", summary, StringComparison.Ordinal);
            Assert.Contains("Runtime", summary, StringComparison.Ordinal);
            Assert.Contains("Operating system", summary, StringComparison.Ordinal);
            Assert.Contains(
                "something happened",
                ReadEntry(archive, $"{DiagnosticsBundle.LogEntryDirectory}/{DiagnosticsPaths.LogFileName}"),
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheBundleSaysWhetherItNamesTheOrganizationAndProject()
    {
        InTempDirectory(directory =>
        {
            var named = Path.Combine(directory, "named.zip");
            var anonymous = Path.Combine(directory, "anonymous.zip");
            var redaction = new DiagnosticRedaction();

            DiagnosticsBundle.Write(
                new DiagnosticsBundleRequest
                {
                    DestinationPath = named,
                    LogDirectory = Path.Combine(directory, "logs"),
                    Org = "contoso",
                    Project = "Payments",
                },
                redaction);

            DiagnosticsBundle.Write(
                new DiagnosticsBundleRequest
                {
                    DestinationPath = anonymous,
                    LogDirectory = Path.Combine(directory, "logs"),
                },
                redaction);

            using var withIdentity = ZipFile.OpenRead(named);
            using var withoutIdentity = ZipFile.OpenRead(anonymous);

            var stated = ReadEntry(withIdentity, DiagnosticsBundle.SummaryEntryName);
            Assert.Contains("contoso/Payments", stated, StringComparison.Ordinal);
            Assert.Contains("names your Azure DevOps organization", stated, StringComparison.Ordinal);

            var omitted = ReadEntry(withoutIdentity, DiagnosticsBundle.SummaryEntryName);
            Assert.DoesNotContain("contoso", omitted, StringComparison.Ordinal);
            Assert.Contains("omitted", omitted, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheBundleRedactsALogWrittenBeforeTheSecretWasKnown()
    {
        InTempDirectory(directory =>
        {
            var logs = Path.Combine(directory, "logs");
            Directory.CreateDirectory(logs);

            // Written by an earlier session, before this one registered anything.
            // The bundle is the file that leaves the machine, so it redacts again on
            // the way in rather than trusting that every past sink did its job.
            File.WriteAllText(
                Path.Combine(logs, DiagnosticsPaths.LogFileName),
                $$"""{"ts":"2026-09-03T00:00:00Z","level":"error","category":"apply","message":"used {{Secret}}"}""");

            var redaction = new DiagnosticRedaction();
            redaction.Register(Secret);

            var destination = Path.Combine(directory, "bundle.zip");
            Assert.True(
                DiagnosticsBundle.Write(
                    new DiagnosticsBundleRequest { DestinationPath = destination, LogDirectory = logs },
                    redaction).IsSuccess);

            using var archive = ZipFile.OpenRead(destination);
            Assert.DoesNotContain(
                Secret,
                ReadEntry(archive, $"{DiagnosticsBundle.LogEntryDirectory}/{DiagnosticsPaths.LogFileName}"),
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheBundleReportsAnUnwritableDestinationAsATypedError()
    {
        InTempDirectory(directory =>
        {
            // The destination's own parent is a file, so the zip can never be created.
            var blocked = Path.Combine(directory, "occupied");
            File.WriteAllText(blocked, "occupied");

            var written = DiagnosticsBundle.Write(
                new DiagnosticsBundleRequest
                {
                    DestinationPath = Path.Combine(blocked, "bundle.zip"),
                    LogDirectory = Path.Combine(directory, "logs"),
                },
                new DiagnosticRedaction());

            Assert.True(written.IsFailure);
            Assert.Equal("diagnostics.unwritten", written.Error!.Code);
            Assert.Equal(ErrorKind.SourceFailure, written.Error.Kind);
        });
    }

    private static DiagnosticEvent Info(string message) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Level = DiagnosticLevel.Info,
        Category = "plan",
        Message = message,
    };

    private static Plan SamplePlan() => new()
    {
        Command = PlanCommand.Import,
        Rows =
        [
            new PlanRow
            {
                Operation = PlanOperation.Create,
                Level = BacklogLevel.Issue,
                Title = "A title that has no business being in a support bundle",
                Code = "PROJ-1",
            },
            new PlanRow
            {
                Operation = PlanOperation.Create,
                Level = BacklogLevel.Issue,
                Title = "Another",
                Code = "PROJ-2",
            },
        ],
        BacklogFingerprint = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
        BoardFingerprint = "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752",
    };

    private static ApplyOutcome Failed(PlanRow row) => new(row, Succeeded: false, BoardId: null, "rejected");

    private static ApplyOutcome Succeeded(PlanRow row) => new(row, Succeeded: true, BoardId: 7, "created");

    private static Dictionary<string, string> Data(string line) =>
        JsonDocument.Parse(line).RootElement.GetProperty("data").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty,
                StringComparer.Ordinal);

    private static string ReadEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"absd-507-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            test(directory);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A test that denied itself write permission may not be able to
                // clean up. The temp directory is the operating system's problem
                // then, not a reason to fail a test that already made its point.
            }
        }
    }

    /// <summary>
    /// Stops the sink from creating a file, the closest a test can get to a log
    /// directory the user cannot write. Returns false where the environment cannot
    /// express that, so the caller skips rather than asserting nothing.
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
