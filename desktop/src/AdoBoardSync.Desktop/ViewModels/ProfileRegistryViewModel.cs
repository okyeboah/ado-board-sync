using System.Collections.ObjectModel;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// One row in the profile switcher.
/// </summary>
public sealed partial class ProfileRowViewModel(ProfileEntry entry) : ObservableObject
{
    [ObservableProperty] private bool _isActive;

    public ProfileEntry Entry { get; } = entry;

    public string ConfigPath => Entry.ConfigPath;

    public string Label => Entry.Label;

    public string BoardDisplay => Entry.BoardDisplay;

    /// <summary>
    /// What this profile's runs are filed under. Two rows can share it — two config
    /// files can point at one board — and that is the point of showing it: they
    /// share a history, and a reader who does not know that will misread the
    /// timeline.
    /// </summary>
    public string ProfileKey =>
        AdoBoardSync.Infrastructure.Operations.ProfileKey.For(Entry.Org, Entry.Project);
}

/// <summary>
/// Handles a change of active profile. Returns a Task so the switch can wait for
/// the new profile to be on screen: a switcher that returned while the previous
/// profile's Plan was still visible would be the bug ABSD-502 exists to prevent.
/// </summary>
public delegate Task ActiveProfileChangedHandler(ProfileEntry? profile, CancellationToken cancellationToken);

/// <summary>
/// The known Board profiles and which one is open (ABSD-502).
///
/// It owns the list and the active choice, persists both through
/// <see cref="IProfileRegistryStore"/>, and raises exactly one event —
/// <see cref="ActiveProfileChanged"/> — when the active profile changes. One event,
/// because every surface that holds per-profile state has to be told at the same
/// moment; a second notification path is how one of them gets missed and shows the
/// previous board's numbers.
/// </summary>
public sealed partial class ProfileRegistryViewModel : ObservableObject
{
    private readonly IProfileRegistryStore _store;

    private ProfileRegistry _registry = ProfileRegistry.Empty;

    /// <summary>
    /// The active profile the rest of the app has been told about. Held separately
    /// from <see cref="ProfileRegistry.ActiveConfigPath"/> so the event fires on a
    /// real change and not on every list edit — a spurious switch would clear a
    /// Plan the user is halfway through reading.
    /// </summary>
    private string? _announced;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveProfile))]
    private ProfileRowViewModel? _activeProfile;

    [ObservableProperty] private bool _isSwitching;

    [ObservableProperty] private string _statusText = "No board profiles registered yet.";

    public ProfileRegistryViewModel(IProfileRegistryStore store)
    {
        _store = store;
    }

    /// <summary>Raised after the active profile has changed, and never for a
    /// re-selection of the profile already open.</summary>
    public event ActiveProfileChangedHandler? ActiveProfileChanged;

    public ObservableCollection<ProfileRowViewModel> Profiles { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasActiveProfile => ActiveProfile is not null;

    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>
    /// Reads the registry from disk and opens the profile that was active when the
    /// app last closed. The event fires here too: at start-up "no profile" is the
    /// previous state, so opening one is a change like any other, and the surfaces
    /// load through the same path they use for every later switch.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var read = _store.Read();
        if (read.IsFailure)
        {
            ErrorText = $"{read.Error!.SafeMessage} ({read.Error.Code})";
            StatusText = "Could not read the profile registry.";
            return;
        }

        Republish(read.Value);
        ErrorText = null;

        await RaiseAsync(cancellationToken);
    }

    /// <summary>
    /// Registers the profile a workspace was opened from, and makes it the active
    /// one. A profile with no config file is refused rather than invented: the
    /// registry's whole job is to reopen a file later.
    /// </summary>
    public Task AddAsync(BacklogWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (workspace.ConfigPath is not { Length: > 0 } path)
        {
            ErrorText =
                "This profile has no board.config.json on disk yet, so there is nothing to reopen. "
                + "Save it first. (profile.no_path)";
            return Task.CompletedTask;
        }

        return AddAsync(
            new ProfileEntry(path, workspace.Config.Org, workspace.Config.Project, workspace.ProfileName),
            cancellationToken);
    }

    public async Task AddAsync(ProfileEntry entry, CancellationToken cancellationToken = default)
    {
        var added = _registry.Add(entry);
        if (added.IsFailure)
        {
            ErrorText = $"{added.Error!.SafeMessage} ({added.Error.Code})";
            return;
        }

        ErrorText = null;
        Persist(added.Value);
        Republish(added.Value);

        await RaiseAsync(cancellationToken);
    }

    /// <summary>
    /// Forgets a profile. Removing the one that is open switches to whatever is
    /// left, or to nothing — the registry never leaves an active path pointing at a
    /// profile it no longer holds.
    /// </summary>
    public async Task RemoveAsync(string configPath, CancellationToken cancellationToken = default)
    {
        var removed = _registry.Remove(configPath);
        if (removed.IsFailure)
        {
            ErrorText = $"{removed.Error!.SafeMessage} ({removed.Error.Code})";
            return;
        }

        ErrorText = null;
        Persist(removed.Value);
        Republish(removed.Value);

        await RaiseAsync(cancellationToken);
    }

    public async Task SetActiveAsync(string configPath, CancellationToken cancellationToken = default)
    {
        var activated = _registry.SetActive(configPath);
        if (activated.IsFailure)
        {
            ErrorText = $"{activated.Error!.SafeMessage} ({activated.Error.Code})";
            return;
        }

        ErrorText = null;
        Persist(activated.Value);
        Republish(activated.Value);

        await RaiseAsync(cancellationToken);
    }

    private async Task RaiseAsync(CancellationToken cancellationToken)
    {
        var active = _registry.Active;
        if (ProfileEntry.PathComparer.Equals(active?.ConfigPath ?? string.Empty, _announced ?? string.Empty))
        {
            return;
        }

        _announced = active?.ConfigPath;

        if (ActiveProfileChanged is not { } subscribers)
        {
            return;
        }

        IsSwitching = true;
        try
        {
            // Awaited one at a time rather than fired and forgotten, so this method
            // completing means every surface has finished reloading. Handlers touch
            // observable collections bound to the UI thread, and running them
            // concurrently would mutate those collections from two contexts at once.
            foreach (var subscriber in subscribers.GetInvocationList().Cast<ActiveProfileChangedHandler>())
            {
                await subscriber(active, cancellationToken).ConfigureAwait(true);
            }
        }
        finally
        {
            IsSwitching = false;
        }
    }

    private void Persist(ProfileRegistry registry)
    {
        var written = _store.Write(registry);
        if (written.IsFailure)
        {
            // The switch still happens. A preferences file that cannot be written is
            // a reason to warn, not a reason to refuse the profile the user asked for.
            ErrorText =
                $"{written.Error!.SafeMessage} ({written.Error.Code}) "
                + "The profile is open, but this machine will not remember it.";
        }
    }

    private void Republish(ProfileRegistry registry)
    {
        _registry = registry;

        Profiles.Clear();
        foreach (var entry in registry.Profiles)
        {
            Profiles.Add(new ProfileRowViewModel(entry)
            {
                IsActive = ProfileEntry.PathComparer.Equals(entry.ConfigPath, registry.ActiveConfigPath ?? string.Empty),
            });
        }

        ActiveProfile = Profiles.FirstOrDefault(row => row.IsActive);

        StatusText = Profiles.Count switch
        {
            0 => "No board profiles registered yet.",
            1 => "1 board profile",
            var n => $"{n} board profiles · {ActiveProfile?.Label ?? "none open"} active",
        };

        OnPropertyChanged(nameof(HasProfiles));
    }
}

