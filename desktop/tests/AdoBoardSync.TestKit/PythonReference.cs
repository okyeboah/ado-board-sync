using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AdoBoardSync.TestKit;

/// <summary>
/// Runs the CLI's Python implementation and returns what it produced.
///
/// This is the reference the .NET port is measured against. It deliberately
/// invokes the real modules through <c>parity_driver.py</c> rather than a
/// recorded snapshot, so a change to the CLI's parser or HTML conversion breaks
/// the desktop build instead of silently letting the two diverge.
/// </summary>
public static class PythonReference
{
    private static readonly Lazy<string> InterpreterValue = new(FindInterpreter);

    // No preamble: a BOM on stdin or in the captured stdout would corrupt the
    // very bytes the comparison is about.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string Interpreter => InterpreterValue.Value;

    /// <summary>Runs a text mode: stdin is the input, the driver returns a single string.</summary>
    public static string Text(string mode, string input) =>
        ReadString(Run(mode, input, []), "value");

    /// <summary>Runs <c>unbalanced</c>, returning the reported tag problems.</summary>
    public static IReadOnlyList<string> Problems(string markup) =>
        ReadStringArray(Run("unbalanced", markup, []), "problems");

    /// <summary>Runs a config-driven mode, returning the raw JSON document.</summary>
    public static JsonDocument WithConfig(string mode, string configPath) =>
        JsonDocument.Parse(Run(mode, input: null, ["--config", configPath]));

    private static string Run(string mode, string? input, string[] arguments)
    {
        var info = new ProcessStartInfo(Interpreter)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = RepoPaths.Root
        };

        info.ArgumentList.Add(RepoPaths.ParityDriver);
        info.ArgumentList.Add(mode);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start {Interpreter}.");

        if (input is not null)
        {
            // Write the bytes directly: a StreamWriter would append the platform newline
            // and change the very input whose handling is under test.
            var bytes = Utf8NoBom.GetBytes(input);
            process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
        }

        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"parity_driver.py {mode} exited {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private static string ReadString(string json, string property) =>
        JsonDocument.Parse(json).RootElement.GetProperty(property).GetString() ?? string.Empty;

    private static IReadOnlyList<string> ReadStringArray(string json, string property) =>
        [.. JsonDocument.Parse(json).RootElement.GetProperty(property)
            .EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)];

    private static string FindInterpreter()
    {
        var configured = Environment.GetEnvironmentVariable("ADO_BOARD_SYNC_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var venv = Path.Combine(RepoPaths.Root, ".venv", "bin", "python3");
        return File.Exists(venv) ? venv : "python3";
    }
}
