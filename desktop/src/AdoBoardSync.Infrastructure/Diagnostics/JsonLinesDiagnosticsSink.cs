using System.Buffers;
using System.Globalization;
using System.Text.Json;
using AdoBoardSync.Core.Diagnostics;

namespace AdoBoardSync.Infrastructure.Diagnostics;

/// <summary>
/// Writes each event as one JSON object on one line (ABSD-507), rotating the file
/// once it reaches a cap. One object per line is the whole point of the format: a
/// support bundle is filtered with <c>grep board.unauthorized</c> or
/// <c>jq 'select(.level=="error")'</c> rather than read top to bottom.
///
/// <para>
/// Redaction is a constructor argument, not an option. A sink that persists is the
/// last place a secret can be caught, so it must not be possible to build one that
/// skips the pass.
/// </para>
///
/// <para>
/// Nothing here throws. A diagnostics failure that surfaced as an exception would
/// replace the failure the user was actually trying to understand, which is the
/// exact inversion this class exists to prevent; a failed write is counted in
/// <see cref="FailedWrites"/> and otherwise dropped.
/// </para>
/// </summary>
public sealed class JsonLinesDiagnosticsSink : IDiagnostics
{
    public const long DefaultMaximumFileBytes = 512 * 1024;

    /// <summary>The current file plus the archives kept beside it.</summary>
    public const int DefaultMaximumFiles = 5;

    private readonly Lock _gate = new();
    private readonly DiagnosticRedaction _redaction;
    private readonly long _maximumFileBytes;
    private readonly int _maximumFiles;
    private int _failedWrites;

    public JsonLinesDiagnosticsSink(
        string directory,
        DiagnosticRedaction redaction,
        long maximumFileBytes = DefaultMaximumFileBytes,
        int maximumFiles = DefaultMaximumFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(redaction);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFiles, 1);

        DirectoryPath = directory;
        _redaction = redaction;
        _maximumFileBytes = maximumFileBytes;
        _maximumFiles = maximumFiles;
    }

    public string DirectoryPath { get; }

    public string CurrentFilePath => Path.Combine(DirectoryPath, DiagnosticsPaths.LogFileName);

    /// <summary>
    /// How many events could not be written. Reported in the bundle summary, because
    /// "the log is short" and "the log could not be written" look identical
    /// otherwise, and they lead a support conversation in opposite directions.
    /// </summary>
    public int FailedWrites
    {
        get
        {
            lock (_gate)
            {
                return _failedWrites;
            }
        }
    }

    public void Write(DiagnosticEvent diagnosticEvent)
    {
        try
        {
            var line = Serialize(_redaction.Apply(diagnosticEvent));

            lock (_gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfFull(line.Length);
                using var stream = new FileStream(
                    CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(line);
            }
        }
        catch (Exception)
        {
            // Deliberately every exception: an unwritable directory, a full disk, a
            // path the operating system rejects and a serializer failure are all the
            // same answer here — the app carries on and the user's real error is
            // still the one they see.
            lock (_gate)
            {
                _failedWrites++;
            }
        }
    }

    /// <summary>
    /// Rotates before the write that would cross the cap, never after, so a file
    /// never exceeds the size the caller asked for. An empty current file is never
    /// rotated: a single event larger than the cap would otherwise rotate on every
    /// write and push every archive out with nothing in it.
    /// </summary>
    private void RotateIfFull(int incomingBytes)
    {
        var current = new FileInfo(CurrentFilePath);
        if (!current.Exists || current.Length == 0 || current.Length + incomingBytes <= _maximumFileBytes)
        {
            return;
        }

        if (_maximumFiles == 1)
        {
            File.Delete(CurrentFilePath);
            return;
        }

        var oldest = Path.Combine(DirectoryPath, DiagnosticsPaths.ArchiveFileName(_maximumFiles - 1));
        File.Delete(oldest);

        for (var index = _maximumFiles - 2; index >= 1; index--)
        {
            var from = Path.Combine(DirectoryPath, DiagnosticsPaths.ArchiveFileName(index));
            if (File.Exists(from))
            {
                File.Move(from, Path.Combine(DirectoryPath, DiagnosticsPaths.ArchiveFileName(index + 1)), overwrite: true);
            }
        }

        File.Move(CurrentFilePath, Path.Combine(DirectoryPath, DiagnosticsPaths.ArchiveFileName(1)), overwrite: true);
    }

    /// <summary>
    /// The JSON is written field by field rather than reflected off the record. The
    /// key order is then fixed and the shape cannot drift when a property is added
    /// to <see cref="DiagnosticEvent"/>, which is what lets a filter written against
    /// one release keep working against the next.
    /// </summary>
    private static byte[] Serialize(DiagnosticEvent diagnosticEvent)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        // The default encoder, not the relaxed one: it escapes every control
        // character, so a message containing a newline stays one line and the
        // one-object-per-line promise holds for text the user typed.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "ts", diagnosticEvent.Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            writer.WriteString("level", diagnosticEvent.Level.ToString().ToLowerInvariant());
            writer.WriteString("category", diagnosticEvent.Category);

            if (diagnosticEvent.Code is { } code)
            {
                writer.WriteString("code", code);
            }

            writer.WriteString("message", diagnosticEvent.Message);

            if (diagnosticEvent.Data.Count > 0)
            {
                writer.WriteStartObject("data");
                foreach (var entry in diagnosticEvent.Data)
                {
                    writer.WriteString(entry.Key, entry.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        var json = buffer.WrittenSpan;
        var line = new byte[json.Length + 1];
        json.CopyTo(line);
        line[^1] = (byte)'\n';
        return line;
    }
}
