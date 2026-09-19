using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Versioning;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure;

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
