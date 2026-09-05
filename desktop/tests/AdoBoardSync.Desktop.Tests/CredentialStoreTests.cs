using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Infrastructure;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The three OS credential stores (ABSD-103) — the last path to a real secret with
/// no test, and the one High gap that code alone could close.
///
/// They are driven through the process seam rather than the real tools. Spawning
/// <c>security</c> for real would prompt for a keychain unlock, edit the
/// developer's own secrets, and fail on every machine that is not a Mac; the
/// alternative was what we had, which is nothing. What the seam preserves is
/// everything that actually matters here: which tool is invoked, which arguments
/// it gets, what reaches its stdin, and how each exit code is classified.
///
/// The security property with teeth is the last of those. A miss must return null
/// so <see cref="PatResolver" /> falls through to <c>pat_env</c>; a refusal must
/// return a typed error so the badge says the store would not answer. Collapsing
/// them either hides a locked keyring or turns an empty one into a hard failure.
/// </summary>
public class CredentialStoreTests
{
    private const string Key = "ado-board-sync:contoso:board";
    private const string Secret = "a-personal-access-token";

    /// <summary>One recorded invocation of a credential tool.</summary>
    private sealed record Invocation(string Tool, IReadOnlyList<string> Arguments, string? StandardInput);

    /// <summary>
    /// Stands in for the child process. It records what it was asked to run and
    /// answers with whatever the test decided the tool would have said.
    /// </summary>
    private sealed class FakeTool(int exitCode = 0, string standardOutput = "", string standardError = "")
    {
        public List<Invocation> Invocations { get; } = [];

        /// <summary>Set to model the tool being absent, or refusing to launch.</summary>
        public Error? LaunchFailure { get; set; }

        public CredentialProcess.Runner Runner => (tool, arguments, stdin) =>
        {
            Invocations.Add(new Invocation(tool, [.. arguments], stdin));

            return LaunchFailure is { } failure
                ? failure
                : new CredentialOutput(exitCode, standardOutput, standardError);
        };

        public Invocation Only => Assert.Single(Invocations);
    }

    // ------------------------------------------------------------ macOS

    private static (MacOsKeychainCredentialStore Store, FakeTool Tool) Keychain(
        int exitCode = 0, string standardOutput = "", string standardError = "", bool available = true)
    {
        var tool = new FakeTool(exitCode, standardOutput, standardError);
        return (new MacOsKeychainCredentialStore(tool.Runner, available), tool);
    }

    [Fact]
    public void TheKeychainReadsTheSecretBackWithoutItsTrailingNewline()
    {
        // `security -w` prints the secret followed by a newline. Returning that
        // newline would send it to Azure DevOps inside the Authorization header.
        var (store, tool) = Keychain(standardOutput: Secret + "\n");

        var read = store.TryRead(Key);

        Assert.True(read.IsSuccess);
        Assert.Equal(Secret, read.Value);
        Assert.Equal("/usr/bin/security", tool.Only.Tool);
        Assert.Equal(["find-generic-password", "-s", Key, "-w"], tool.Only.Arguments);
    }

    [Fact]
    public void TheKeychainToolIsRunByAbsolutePath()
    {
        // Not "security" resolved through PATH. A credential tool found by PATH is
        // a credential tool an attacker can put earlier on PATH.
        var (store, tool) = Keychain(standardOutput: Secret);
        store.TryRead(Key);

        Assert.StartsWith("/", tool.Only.Tool, StringComparison.Ordinal);
        Assert.Equal("/usr/bin/security", tool.Only.Tool);
    }

    [Fact]
    public void AKeychainMissIsNoTokenRatherThanAFailure()
    {
        // Exit 44 is `security`'s "the specified item could not be found". It has
        // to read as an empty source so PatResolver moves on to pat_env; an error
        // here would stop resolution at a store that simply holds nothing.
        var (store, _) = Keychain(exitCode: 44);

        var read = store.TryRead(Key);

        Assert.True(read.IsSuccess);
        Assert.Null(read.Value);
    }

    [Fact]
    public void AKeychainThatRefusesIsReportedRatherThanTreatedAsEmpty()
    {
        // A locked keychain, or one the user denied. Reporting this as "no token"
        // would send the user to check pat_env when the real answer is on screen.
        var (store, _) = Keychain(exitCode: 1, standardError: "User interaction is not allowed.");

        var read = store.TryRead(Key);

        Assert.True(read.IsFailure);
        Assert.Equal("credential.unreadable", read.Error!.Code);
        Assert.Contains("User interaction is not allowed", read.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSecretReachesTheKeychainOnStdinAndNeverInAnArgument()
    {
        // The reason these adapters shell out at all rather than take the simpler
        // route: every argument of every process on the machine is readable through
        // `ps`. This is the assertion that keeps that true.
        var (store, tool) = Keychain();

        var written = store.Write(Key, Secret);

        Assert.True(written.IsSuccess);
        Assert.Equal(Secret + "\n", tool.Only.StandardInput);
        Assert.DoesNotContain(tool.Only.Arguments, argument => argument.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void WritingToTheKeychainUpdatesAnExistingEntryRatherThanFailingOnIt()
    {
        // Without -U, `security` refuses when the entry exists, so the second time
        // a user saved a token it would fail for no reason they could act on.
        var (store, tool) = Keychain();

        store.Write(Key, Secret);

        Assert.Contains("-U", tool.Only.Arguments);
        Assert.Equal("add-generic-password", tool.Only.Arguments[0]);
    }

    [Fact]
    public void AKeychainThatRefusesToStoreSaysSo()
    {
        var (store, _) = Keychain(exitCode: 1, standardError: "write permission denied");

        var written = store.Write(Key, Secret);

        Assert.True(written.IsFailure);
        Assert.Equal("credential.unwritable", written.Error!.Code);
    }

    [Fact]
    public void DeletingAnEntryTheKeychainDoesNotHaveSucceeds()
    {
        // The caller asked for "no entry under this key". There is none.
        var (store, _) = Keychain(exitCode: 44);

        var deleted = store.Delete(Key);

        Assert.True(deleted.IsSuccess);
        Assert.True(deleted.Value);
    }

    [Fact]
    public void AKeychainThatRefusesToDeleteSaysSo()
    {
        var (store, _) = Keychain(exitCode: 1, standardError: "keychain is locked");

        var deleted = store.Delete(Key);

        Assert.True(deleted.IsFailure);
        Assert.Equal("credential.undeletable", deleted.Error!.Code);
    }

    [Fact]
    public void AToolThatWillNotLaunchIsAStoreThatIsNotThere()
    {
        var (store, tool) = Keychain();
        tool.LaunchFailure = Error.SourceFailure("credential.unavailable", "Could not run /usr/bin/security.");

        var read = store.TryRead(Key);

        Assert.True(read.IsFailure);
        Assert.Equal("credential.unavailable", read.Error!.Code);
    }

    // ------------------------------------------------------------ Linux

    private static (SecretToolCredentialStore Store, FakeTool Tool) SecretTool(
        int exitCode = 0, string standardOutput = "", string standardError = "", bool available = true)
    {
        var tool = new FakeTool(exitCode, standardOutput, standardError);
        return (new SecretToolCredentialStore(tool.Runner, available), tool);
    }

    [Fact]
    public void SecretToolLooksTheKeyUpByTheApplicationsOwnAttribute()
    {
        // Attribute-scoped, so this app's entries are its own and a lookup cannot
        // collide with another application's key of the same name.
        var (store, tool) = SecretTool(standardOutput: Secret + "\n");

        var read = store.TryRead(Key);

        Assert.Equal(Secret, read.Value);
        Assert.Equal("secret-tool", tool.Only.Tool);
        Assert.Equal(["lookup", "ado-board-sync-key", Key], tool.Only.Arguments);
    }

    [Fact]
    public void ASecretToolMissIsSilentAndReadsAsNoToken()
    {
        // secret-tool exits non-zero for a miss and says nothing. Silence is the
        // only thing separating that from a locked keyring.
        var (store, _) = SecretTool(exitCode: 1);

        var read = store.TryRead(Key);

        Assert.True(read.IsSuccess);
        Assert.Null(read.Value);
    }

    [Fact]
    public void ALockedKeyringIsReportedBecauseItSaidSomething()
    {
        // Same exit code as a miss; different because stderr is not empty. Getting
        // this backwards would make a locked keyring look like an empty one and
        // silently fall through to a token source the user did not intend.
        var (store, _) = SecretTool(
            exitCode: 1, standardError: "Cannot autolaunch D-Bus without X11 $DISPLAY");

        var read = store.TryRead(Key);

        Assert.True(read.IsFailure);
        Assert.Equal("credential.unreadable", read.Error!.Code);
    }

    [Fact]
    public void TheSecretReachesSecretToolOnStdinAndNeverInAnArgument()
    {
        var (store, tool) = SecretTool();

        store.Write(Key, Secret);

        Assert.Equal(Secret, tool.Only.StandardInput);
        Assert.DoesNotContain(tool.Only.Arguments, argument => argument.Contains(Secret, StringComparison.Ordinal));
        Assert.Equal("store", tool.Only.Arguments[0]);
    }

    [Fact]
    public void SecretToolEntriesCarryALabelAUserCanRecogniseInASecretsManager()
    {
        // The entry is revocable by hand, which only helps if its name says what
        // it is and which key it belongs to.
        var (store, tool) = SecretTool();

        store.Write(Key, Secret);

        Assert.Contains("--label", tool.Only.Arguments);
        Assert.Contains(tool.Only.Arguments, argument => argument.Contains("ADO Board Sync", StringComparison.Ordinal));
        Assert.Contains(tool.Only.Arguments, argument => argument.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public void ClearingASecretThatIsNotThereSucceeds()
    {
        var (store, tool) = SecretTool(exitCode: 1);

        var deleted = store.Delete(Key);

        Assert.True(deleted.IsSuccess);
        Assert.Equal(["clear", "ado-board-sync-key", Key], tool.Only.Arguments);
    }

    // ------------------------------------------- a store that is not there

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnavailableStoreNeverRunsAnythingAndNeverFailsARead(bool macOs)
    {
        // A headless Linux box, or a Mac without /usr/bin/security. The store has
        // to behave as an empty source rather than an error, or every board action
        // on such a machine would be blocked by a store it does not have.
        var (store, tool) = macOs
            ? Keychain(available: false)
            : ((ProcessCredentialStore, FakeTool))SecretTool(available: false);

        var read = store.TryRead(Key);
        var deleted = store.Delete(Key);

        Assert.True(read.IsSuccess);
        Assert.Null(read.Value);
        Assert.True(deleted.IsSuccess);
        Assert.Empty(tool.Invocations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnavailableStoreRefusesAWriteRatherThanPretendingItSaved(bool macOs)
    {
        // A read may fall through; a write may not. Reporting success would tell
        // the user their token is stored when nothing holds it.
        var (store, tool) = macOs
            ? Keychain(available: false)
            : ((ProcessCredentialStore, FakeTool))SecretTool(available: false);

        var written = store.Write(Key, Secret);

        Assert.True(written.IsFailure);
        Assert.Equal("credential.unavailable", written.Error!.Code);
        Assert.Empty(tool.Invocations);
    }

    // ------------------------------------------------------- the selection

    [Fact]
    public void ThisPlatformGetsTheStoreItActuallyHas()
    {
        var store = OsCredentialStore.ForThisPlatform();

        Assert.NotNull(store);
        Assert.NotEmpty(store.Name);

        if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacOsKeychainCredentialStore>(store);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsCredentialManagerStore>(store);
        }
        else if (OperatingSystem.IsLinux())
        {
            // Either one, depending on whether libsecret is installed on the
            // runner — but never a throw, and never a store from another platform.
            Assert.True(
                store is SecretToolCredentialStore or UnavailableCredentialStore,
                $"Linux selected {store.GetType().Name}.");
        }
    }

    [Fact]
    public void AStoreNameIsSafeToShowAndNamesNoSecret()
    {
        // The name reaches the credential badge and the diagnostics log.
        foreach (var name in new[]
                 {
                     new MacOsKeychainCredentialStore().Name,
                     new SecretToolCredentialStore().Name,
                     new UnavailableCredentialStore("no store here").Name,
                 })
        {
            Assert.NotEmpty(name);
            Assert.DoesNotContain(Secret, name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RunningAToolThatIsNotOnThisMachineIsAnUnavailableStoreNotACrash()
    {
        // The real CredentialProcess, deliberately: the fake cannot prove that a
        // missing binary is caught rather than thrown, and that is the one branch
        // every machine without the tool takes.
        var run = CredentialProcess.Run(
            Path.Combine(Path.GetTempPath(), $"absd-no-such-tool-{Guid.NewGuid():N}"), ["--version"]);

        Assert.True(run.IsFailure);
        Assert.Equal("credential.unavailable", run.Error!.Code);
    }
}
