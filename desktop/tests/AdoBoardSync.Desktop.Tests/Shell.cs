using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.Infrastructure;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Builds the shell the way the composition root does (ABSD-106), so a test names
/// the store it wants instead of restating the wiring. <see cref="OnDisk" /> is for
/// tests that are genuinely about files; <see cref="InMemory" /> is for the ones
/// that are not, and which therefore should not touch a disk to run.
/// </summary>
internal static class Shell
{
    public static MainWindowViewModel OnDisk()
    {
        var store = new FileSystemBacklogFileStore();
        return new MainWindowViewModel(new ProfileLoader(store), store);
    }

    public static MainWindowViewModel InMemory(IBacklogFileStore store) =>
        new(new ProfileLoader(store), store);

    public static ProfileLoader Loader() => new(new FileSystemBacklogFileStore());

    public static OnboardingViewModel Onboarding(IBacklogFileStore? store = null)
    {
        store ??= new FileSystemBacklogFileStore();
        return new OnboardingViewModel(store, new ProfileLoader(store));
    }

    /// <summary>Opens a workspace from a real config file, for a test that needs one.</summary>
    public static async Task<BacklogWorkspace> WorkspaceAsync(string configPath)
    {
        var loaded = await Loader().LoadAsync(configPath);
        Assert.True(loaded.IsSuccess, loaded.Error?.SafeMessage);
        return loaded.Value;
    }

    /// <summary>The same, keeping the failure so a test can assert on it.</summary>
    public static Task<Result<BacklogWorkspace>> TryWorkspaceAsync(string configPath) =>
        Loader().LoadAsync(configPath);
}
