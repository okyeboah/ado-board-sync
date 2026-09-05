using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AdoBoardSync.Core.Diagnostics;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure.Diagnostics;

/// <summary>What the bundle should contain and what it is allowed to name (ABSD-507).</summary>
public sealed record DiagnosticsBundleRequest
{
    /// <summary>The .zip the user chose to attach to a support conversation.</summary>
    public required string DestinationPath { get; init; }

    public string LogDirectory { get; init; } = DiagnosticsPaths.DefaultDirectory;

    /// <summary>
    /// The board's organization and project, included only when the caller passes
    /// them. They are not secrets, but they name a customer's internal projects to
    /// whoever reads the bundle, so the decision is the caller's and the summary
    /// states which way it went.
    /// </summary>
    public string? Org { get; init; }

    public string? Project { get; init; }

    public bool CredentialStoreAvailable { get; init; }

    /// <summary>The store's own <c>Name</c>, which is documented never to be a secret.</summary>
    public string? CredentialStoreName { get; init; }

    /// <summary>Events the sink could not write, so a short log is not read as a quiet one.</summary>
    public int FailedWrites { get; init; }
}

/// <summary>
/// Packs the current log files and a plain-text environment summary into one zip a
/// user can attach to a support conversation (ARCHITECTURE.md §7).
///
/// Everything written here goes through <see cref="DiagnosticRedaction"/> first,
/// including the summary. The logs were redacted when they were written, and are
/// redacted again on the way in: this is the file that leaves the machine, and one
/// pass that was skipped by a sink added later would not be noticed until it had.
/// </summary>
public static class DiagnosticsBundle
{
    public const string SummaryEntryName = "environment.txt";

    public const string LogEntryDirectory = "logs";

    /// <summary>
    /// Writes the bundle and returns the path it landed at.
    /// <c>diagnostics.unwritten</c> follows the shape FSD §5.1 already uses for
    /// <c>csv.unwritten</c>; exporting a bundle is an operation the table does not
    /// cover yet, and inventing a different shape for it would be the parallel
    /// vocabulary this ticket exists to avoid.
    /// </summary>
    public static Result<string> Write(DiagnosticsBundleRequest request, DiagnosticRedaction redaction)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(redaction);

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath));
        if (string.IsNullOrEmpty(directory))
        {
            return Error.Validation(
                "diagnostics.unwritten",
                $"Cannot write a diagnostics bundle to {request.DestinationPath}: it names no directory.");
        }

        var logs = FindLogs(request.LogDirectory);

        // Built into a temporary file in the destination directory and renamed, the
        // same way a backlog save is: a half-written zip left where the user just
        // chose to save one is a file they would attach without knowing.
        var tempPath = Path.Combine(directory, $".diagnostics-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteText(archive, SummaryEntryName, redaction.Redact(Summarize(request, logs)));

                foreach (var log in logs)
                {
                    CopyLog(archive, log, redaction);
                }
            }

            File.Move(tempPath, request.DestinationPath, overwrite: true);
            return request.DestinationPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(tempPath);
            return Error.SourceFailure(
                "diagnostics.unwritten",
                $"Could not write the diagnostics bundle to {request.DestinationPath}: {ex.Message}");
        }
    }

    private static IReadOnlyList<string> FindLogs(string logDirectory)
    {
        try
        {
            if (!Directory.Exists(logDirectory))
            {
                return [];
            }

            var files = Directory.GetFiles(logDirectory, DiagnosticsPaths.LogFileSearchPattern);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A bundle with a summary and no logs is still worth sending: it says
            // which build and which platform, which is often the whole answer.
            return [];
        }
    }

    private static string Summarize(DiagnosticsBundleRequest request, IReadOnlyList<string> logs)
    {
        var summary = new StringBuilder();
        summary.AppendLine("ADO Board Sync — diagnostics bundle");
        summary.AppendLine();
        Line(summary, "Created (UTC)", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Line(summary, "Application", ApplicationVersion());
        Line(summary, "Runtime", RuntimeInformation.FrameworkDescription);
        Line(summary, "Operating system", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Line(summary, "Credential store", DescribeCredentialStore(request));
        Line(summary, "Board identity", DescribeBoardIdentity(request));
        Line(summary, "Log files", logs.Count.ToString(CultureInfo.InvariantCulture));
        Line(summary, "Dropped events", request.FailedWrites.ToString(CultureInfo.InvariantCulture));
        summary.AppendLine();
        summary.AppendLine(
            "No personal access token, password or other credential is in this bundle: every");
        summary.AppendLine(
            "line was passed through the redactor before it was written, and again on the way");
        summary.AppendLine(
            "in here. The logs do contain file paths from this machine and the issue codes of");
        summary.AppendLine(
            "the work items each operation touched.");
        return summary.ToString();
    }

    private static string DescribeCredentialStore(DiagnosticsBundleRequest request)
    {
        var name = string.IsNullOrWhiteSpace(request.CredentialStoreName)
            ? "not reported"
            : request.CredentialStoreName;

        return request.CredentialStoreAvailable
            ? $"{name} (available)"
            : $"{name} (unavailable — the PAT came from the environment, a token file, or this session)";
    }

    private static string DescribeBoardIdentity(DiagnosticsBundleRequest request)
    {
        var org = request.Org;
        var project = request.Project;

        return string.IsNullOrWhiteSpace(org) && string.IsNullOrWhiteSpace(project)
            ? "omitted — this bundle does not name your Azure DevOps organization or project"
            : $"{org}/{project} — this bundle names your Azure DevOps organization and project";
    }

    private static string ApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(DiagnosticsBundle).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static void WriteText(ZipArchive archive, string entryName, string text)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    private static void CopyLog(ZipArchive archive, string path, DiagnosticRedaction redaction)
    {
        string text;
        try
        {
            // FileShare.ReadWrite because the sink appends to the current file while
            // this runs; a bundle exported during an Apply must not fail on a lock.
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(source);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = $"This log could not be read when the bundle was written: {ex.Message}\n";
        }

        WriteText(archive, $"{LogEntryDirectory}/{Path.GetFileName(path)}", redaction.Redact(text));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The write already failed and its error is on its way back to the user.
            // A leftover temp file is not worth turning that into a second failure.
        }
    }

    private static void Line(StringBuilder summary, string label, string value) =>
        summary.AppendLine(CultureInfo.InvariantCulture, $"{label,-18}{value}");
}
