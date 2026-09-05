using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure;

/// <summary>
/// Picks the credential store this machine actually has (ABSD-103).
///
/// macOS and Linux drive the platform's own in-box tool — <c>security</c> and
/// <c>secret-tool</c> — rather than its native API, so the application gains no
/// native dependency and nothing that fails to load on a platform it was not built
/// for. The cost is one short-lived child process per read, once per board action.
///
/// On those two the secret never reaches a command line: it goes to the child on
/// stdin, because arguments are visible to every other process through <c>ps</c>.
///
/// Windows is the exception and uses <c>advapi32</c> directly, because it ships no
/// in-box tool that will print a stored secret back — <c>cmdkey</c> deliberately
/// refuses — so the CLI route cannot read there at all.
/// </summary>
public static class OsCredentialStore
{
    /// <summary>The store for the running platform, or an unavailable one.</summary>
    public static ICredentialStore ForThisPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacOsKeychainCredentialStore();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialManagerStore();
        }

        if (OperatingSystem.IsLinux())
        {
            return SecretToolCredentialStore.IsInstalled()
                ? new SecretToolCredentialStore()
                : new UnavailableCredentialStore(
                    "libsecret's secret-tool is not installed, so this machine has no desktop secret service");
        }

        return new UnavailableCredentialStore($"no credential store is known for {Environment.OSVersion.Platform}");
    }
}

/// <summary>
/// Runs one credential tool and reports what it said. Shared by the adapters so the
/// timeout, the stdin discipline and the "never throw" contract are stated once
/// rather than once per platform.
/// </summary>
internal static class CredentialProcess
{
    // A store that is not answering must not hang a board action behind it. Long
    // enough for a keychain prompt the user is looking at, short enough that an
    // unattended run fails over to pat_env instead of stalling.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    internal static Result<CredentialOutput> Run(string fileName, IReadOnlyList<string> arguments, string? stdin = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return Error.SourceFailure("credential.unavailable", $"Could not start {fileName}.");
            }

            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(Limit))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the timeout and the kill. Nothing to do.
                }

                return Error.SourceFailure(
                    "credential.timeout", $"{fileName} did not answer within {Limit.TotalSeconds:0} seconds.");
            }

            return new CredentialOutput(process.ExitCode, output, error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or
                                       InvalidOperationException or PlatformNotSupportedException)
        {
            // The tool is missing or the platform refuses to launch it. That is a
            // store that is not there, not a crash the user should see.
            return Error.SourceFailure("credential.unavailable", $"Could not run {fileName}: {ex.Message}");
        }
    }
}

internal sealed record CredentialOutput(int ExitCode, string StandardOutput, string StandardError)
{
    internal bool Succeeded => ExitCode == 0;

    /// <summary>The secret the tool printed, or null when it printed nothing.</summary>
    internal string? Token =>
        StandardOutput.TrimEnd('\n', '\r') is { Length: > 0 } token ? token : null;

    /// <summary>What to put in an error. Stderr when the tool said something, else the code.</summary>
    internal string Trouble =>
        StandardError.Trim() is { Length: > 0 } detail ? detail : $"exit code {ExitCode}";
}

/// <summary>
/// The shape both tool-driven stores share: probe availability once, run the
/// platform's tool, and separate "no such entry" from "the store refused".
///
/// It exists because the macOS and Linux adapters were the same five steps written
/// twice and had already drifted — one mapped a failed delete to a typed error and
/// the other silently reported success. A subclass now supplies only what actually
/// differs: the tool, its arguments, and how it signals a miss.
/// </summary>
public abstract class ProcessCredentialStore(bool isAvailable) : ICredentialStore
{
    public abstract string Name { get; }

    public bool IsAvailable { get; } = isAvailable;

    /// <summary>The executable to run. Absolute where the platform guarantees one.</summary>
    protected abstract string Tool { get; }

    protected abstract IReadOnlyList<string> ReadArguments(string key);

    protected abstract IReadOnlyList<string> WriteArguments(string key);

    protected abstract IReadOnlyList<string> DeleteArguments(string key);

    /// <summary>What the tool wants on stdin to store <paramref name="secret"/>.</summary>
    protected abstract string StandardInputFor(string secret);

    /// <summary>
    /// True when a non-zero exit means "there is no such entry" rather than "this
    /// store would not answer". The two must not collapse: a miss falls through to
    /// the next credential source, a refusal is reported on the badge.
    /// </summary>
    protected abstract bool IsMissing(int exitCode, string standardError);

    public Result<string?> TryRead(string key)
    {
        if (!IsAvailable)
        {
            return (string?)null;
        }

        var run = CredentialProcess.Run(Tool, ReadArguments(key));
        if (run.IsFailure)
        {
            return run.Error!;
        }

        var output = run.Value;
        if (output.Succeeded)
        {
            return output.Token;
        }

        return IsMissing(output.ExitCode, output.StandardError)
            ? (string?)null
            : Error.SourceFailure(
                "credential.unreadable", $"{Name} refused to read {key}: {output.Trouble}");
    }

    public Result<bool> Write(string key, string secret)
    {
        if (!IsAvailable)
        {
            return Error.SourceFailure("credential.unavailable", $"This machine has no {Name}.");
        }

        var run = CredentialProcess.Run(Tool, WriteArguments(key), StandardInputFor(secret));
        if (run.IsFailure)
        {
            return run.Error!;
        }

        return run.Value.Succeeded
            ? true
            : Error.SourceFailure(
                "credential.unwritable", $"{Name} refused to store {key}: {run.Value.Trouble}");
    }

    public Result<bool> Delete(string key)
    {
        if (!IsAvailable)
        {
            return true;
        }

        var run = CredentialProcess.Run(Tool, DeleteArguments(key));
        if (run.IsFailure)
        {
            return run.Error!;
        }

        // Removing what is not there is the state the caller asked for.
        return run.Value.Succeeded || IsMissing(run.Value.ExitCode, run.Value.StandardError)
            ? true
            : Error.SourceFailure(
                "credential.undeletable", $"{Name} refused to remove {key}: {run.Value.Trouble}");
    }
}

/// <summary>
/// The macOS login keychain, through <c>security</c>. The entry is a generic
/// password whose service is the key, so it is visible and revocable in Keychain
/// Access under a name the user can recognise.
/// </summary>
public sealed class MacOsKeychainCredentialStore()
    : ProcessCredentialStore(OperatingSystem.IsMacOS() && File.Exists(ToolPath))
{
    private const string ToolPath = "/usr/bin/security";

    // `security`'s "the specified item could not be found in the keychain".
    private const int ItemNotFound = 44;

    public override string Name => "the macOS keychain";

    protected override string Tool => ToolPath;

    protected override IReadOnlyList<string> ReadArguments(string key) =>
        ["find-generic-password", "-s", key, "-w"];

    // -U updates an existing entry rather than failing on it; -w without a value
    // makes `security` read the secret from stdin, which is what keeps the token
    // out of the process list.
    protected override IReadOnlyList<string> WriteArguments(string key) =>
        ["add-generic-password", "-a", Environment.UserName, "-s", key, "-U", "-w"];

    protected override IReadOnlyList<string> DeleteArguments(string key) =>
        ["delete-generic-password", "-s", key];

    protected override string StandardInputFor(string secret) => secret + "\n";

    protected override bool IsMissing(int exitCode, string standardError) => exitCode == ItemNotFound;
}

/// <summary>
/// The Linux desktop secret service (GNOME Keyring, KWallet) through libsecret's
/// <c>secret-tool</c>. Absent on a headless server, which is why
/// <see cref="IsInstalled"/> is asked before this adapter is ever chosen.
/// </summary>
public sealed class SecretToolCredentialStore()
    : ProcessCredentialStore(OperatingSystem.IsLinux() && IsInstalled())
{
    private const string ToolName = "secret-tool";
    private const string Attribute = "ado-board-sync-key";

    public override string Name => "the desktop secret service (libsecret)";

    protected override string Tool => ToolName;

    protected override IReadOnlyList<string> ReadArguments(string key) => ["lookup", Attribute, key];

    protected override IReadOnlyList<string> WriteArguments(string key) =>
        ["store", "--label", $"ADO Board Sync — {key}", Attribute, key];

    protected override IReadOnlyList<string> DeleteArguments(string key) => ["clear", Attribute, key];

    protected override string StandardInputFor(string secret) => secret;

    // secret-tool exits non-zero both for "no such secret" and for a locked
    // keyring, and only the latter writes to stderr — so stderr is what separates
    // "empty" from "refused".
    protected override bool IsMissing(int exitCode, string standardError) => standardError.Trim().Length == 0;

    /// <summary>True when <c>secret-tool</c> is on PATH.</summary>
    public static bool IsInstalled()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.Split(Path.PathSeparator)
            .Any(directory => directory.Length > 0 && File.Exists(Path.Combine(directory, ToolName)));
    }
}

/// <summary>
/// Windows Credential Manager, as a generic credential under the key's own name.
///
/// This one is P/Invoke rather than a child process, because Windows has no in-box
/// tool that will print a stored secret back — <c>cmdkey</c> deliberately refuses —
/// so the CLI route the other two adapters take cannot read. <c>advapi32</c> ships
/// with the operating system, so this still adds no dependency.
///
/// The credential is written with <c>CRED_PERSIST_LOCAL_MACHINE</c>, which is
/// per-user despite the name: it survives a sign-out on this machine and does not
/// roam to another. A roaming credential would put the token on a domain
/// controller, which ARCHITECTURE §6 does not allow.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerStore : ICredentialStore
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;   // ERROR_NOT_FOUND

    public string Name => "Windows Credential Manager";

    public bool IsAvailable => true;

    public Result<string?> TryRead(string key)
    {
        if (!CredRead(key, GenericCredential, 0, out var handle))
        {
            var code = Marshal.GetLastWin32Error();
            return code == NotFound
                ? (string?)null
                : Error.SourceFailure(
                    "credential.unreadable",
                    $"Windows Credential Manager refused to read {key} (error {code}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(handle);

            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return (string?)null;
            }

            // Written as UTF-16 by this adapter; the blob length is in bytes.
            var token = Marshal.PtrToStringUni(
                credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);

            return string.IsNullOrWhiteSpace(token) ? (string?)null : token.Trim();
        }
        finally
        {
            CredFree(handle);
        }
    }

    public Result<bool> Write(string key, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobHandle = Marshal.AllocHGlobal(blob.Length);
        var targetHandle = Marshal.StringToHGlobalUni(key);
        var userHandle = Marshal.StringToHGlobalUni(Environment.UserName);

        try
        {
            Marshal.Copy(blob, 0, blobHandle, blob.Length);

            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetHandle,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobHandle,
                Persist = PersistLocalMachine,
                UserName = userHandle,
            };

            if (CredWrite(ref credential, 0))
            {
                return true;
            }

            var code = Marshal.GetLastWin32Error();
            return Error.SourceFailure(
                "credential.unwritable",
                $"Windows Credential Manager refused to store {key} (error {code}).");
        }
        finally
        {
            // Zeroed before release: the secret was copied into unmanaged memory,
            // which no garbage collector is going to clear for us.
            Marshal.Copy(new byte[blob.Length], 0, blobHandle, blob.Length);
            Marshal.FreeHGlobal(blobHandle);
            Marshal.FreeHGlobal(targetHandle);
            Marshal.FreeHGlobal(userHandle);
        }
    }

    public Result<bool> Delete(string key)
    {
        if (CredDelete(key, GenericCredential, 0))
        {
            return true;
        }

        var code = Marshal.GetLastWin32Error();

        // Removing what is not there is the state the caller asked for.
        return code == NotFound
            ? true
            : Error.SourceFailure(
                "credential.undeletable",
                $"Windows Credential Manager refused to remove {key} (error {code}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    // DllImport rather than the source-generated LibraryImport on purpose:
    // LibraryImport requires AllowUnsafeBlocks and DisableRuntimeMarshalling on the
    // whole assembly, and turning unsafe code on project-wide to reach one Windows
    // API is a far larger change than the runtime marshaller this struct needs.
#pragma warning disable SYSLIB1054
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
#pragma warning restore SYSLIB1054
}
