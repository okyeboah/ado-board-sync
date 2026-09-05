using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Configuration;

/// <summary>
/// The operating system's own secret store — Keychain on macOS, Credential Manager
/// on Windows, the Secret Service on Linux (ABSD-103). Declared in Core so the
/// resolver can prefer it without Core knowing any platform API; the adapters live
/// in Infrastructure.
///
/// <see cref="IsAvailable"/> is a first-class answer rather than an exception
/// because "this machine has no secret service running" is an ordinary state on a
/// headless Linux box, and the app must fall back to the CLI's environment
/// variable and token file rather than fail.
/// </summary>
public interface ICredentialStore
{
    /// <summary>A short name safe to show in an error. Never a secret.</summary>
    string Name { get; }

    /// <summary>False when this platform has no usable store; every call then no-ops.</summary>
    bool IsAvailable { get; }

    /// <summary>The stored secret for <paramref name="key"/>, or null when there is none.</summary>
    Result<string?> TryRead(string key);

    /// <summary>Stores <paramref name="secret"/> under <paramref name="key"/>, replacing any existing value.</summary>
    Result<bool> Write(string key, string secret);

    /// <summary>Removes the entry. Deleting an entry that is not there succeeds.</summary>
    Result<bool> Delete(string key);
}

/// <summary>
/// A store on a platform that has none. Every read misses, so the resolver falls
/// straight through to the environment variable and the token file.
/// </summary>
public sealed class UnavailableCredentialStore(string reason) : ICredentialStore
{
    public string Name => $"OS credential store (unavailable: {reason})";

    public bool IsAvailable => false;

    public Result<string?> TryRead(string key) => (string?)null;

    public Result<bool> Write(string key, string secret) =>
        Error.SourceFailure("credential.unavailable", $"No OS credential store here: {reason}.");

    public Result<bool> Delete(string key) => true;
}

/// <summary>Reads the PAT from the operating system's credential store.</summary>
public sealed class CredentialStorePatSource(ICredentialStore store, string key) : IPatSource
{
    public string Name => store.Name;

    public Result<string?> TryRead() => store.IsAvailable ? store.TryRead(key) : (string?)null;
}
