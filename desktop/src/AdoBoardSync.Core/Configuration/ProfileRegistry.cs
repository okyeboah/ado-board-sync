using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Configuration;

/// <summary>
/// One known Board profile: where its <c>board.config.json</c> lives, which board
/// that config points at, and what to call it in the switcher.
///
/// It holds no token and no backlog content. The registry is a list of places to
/// look, not a cache of what was found there — so a stolen registry file names
/// boards, and nothing else.
/// </summary>
public sealed record ProfileEntry(string ConfigPath, string Org, string Project, string DisplayName)
{
    /// <summary>
    /// How two config paths are compared. Case-insensitively everywhere but Linux,
    /// because on Windows and macOS <c>/x/Board.json</c> and <c>/x/board.json</c>
    /// are one file, and treating them as two would put one board in the list
    /// twice — the duplicate this record exists to prevent.
    /// </summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>What the switcher shows. Falls back to the board rather than to an
    /// empty row: an unnamed profile is still identifiable by the board it opens.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName)
        ? $"{Org}/{Project}"
        : DisplayName.Trim();

    public string BoardDisplay => $"{Org}/{Project}";
}

/// <summary>
/// The known profiles and which one is active (ABSD-502).
///
/// A value, not a service: adding, removing and switching are pure transformations
/// of this record, so the rules that matter — one entry per config path, and a
/// defined active profile after every operation — are testable without a disk and
/// cannot differ between the file the store wrote and the list the shell shows.
/// Persistence is <see cref="IProfileRegistryStore"/>'s job.
///
/// Identity here is the config path, not the org/project pair. Two configs can
/// point at one board — a second checkout, a copy with different iterations — and
/// collapsing them would silently drop one of the user's backlog files. They still
/// share a history key: see <c>ProfileKey</c>, which derives that from the board.
/// </summary>
public sealed record ProfileRegistry
{
    public static ProfileRegistry Empty { get; } = new();

    public IReadOnlyList<ProfileEntry> Profiles { get; init; } = [];

    public string? ActiveConfigPath { get; init; }

    public ProfileEntry? Active => Find(ActiveConfigPath);

    public bool IsEmpty => Profiles.Count == 0;

    public ProfileEntry? Find(string? configPath) => string.IsNullOrWhiteSpace(configPath)
        ? null
        : Profiles.FirstOrDefault(p => ProfileEntry.PathComparer.Equals(p.ConfigPath, Normalise(configPath)));

    /// <summary>
    /// Adds a profile, or updates the one already registered at that path.
    ///
    /// Re-adding a known config path replaces it in place rather than appending: a
    /// user who opens the same file twice has one profile, and a second row for it
    /// would be a switcher offering the same board under two names.
    /// </summary>
    public Result<ProfileRegistry> Add(ProfileEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ConfigPath))
        {
            return Error.Validation(
                "profile.no_path",
                "A profile with no config file has nothing to reopen. Save the profile before registering it.");
        }

        if (string.IsNullOrWhiteSpace(entry.Org) || string.IsNullOrWhiteSpace(entry.Project))
        {
            return Error.Validation(
                "profile.no_board",
                "A registry entry must name its organisation and project, or its history and its "
                + "board reads cannot be scoped to it.");
        }

        var normalised = entry with
        {
            ConfigPath = Normalise(entry.ConfigPath),
            Org = entry.Org.Trim(),
            Project = entry.Project.Trim(),
            DisplayName = entry.DisplayName.Trim(),
        };

        var profiles = Profiles.ToList();
        var existing = profiles.FindIndex(
            p => ProfileEntry.PathComparer.Equals(p.ConfigPath, normalised.ConfigPath));

        if (existing >= 0)
        {
            profiles[existing] = normalised;
        }
        else
        {
            profiles.Add(normalised);
        }

        return new ProfileRegistry
        {
            Profiles = profiles,

            // The first profile a machine learns about becomes the active one.
            // A registry with entries and no active profile is a switcher that
            // opens on nothing, which is a state no user ever asked for.
            ActiveConfigPath = Active is null ? normalised.ConfigPath : ActiveConfigPath,
        };
    }

    /// <summary>
    /// Forgets a profile. Removing the active one hands the active slot to the
    /// first profile left, and to nothing when none is — never to a path that is
    /// no longer in the list.
    ///
    /// Removing a path that is not registered succeeds and changes nothing. The
    /// caller's intent — that this profile is not in the list — already holds, and
    /// a second click on Remove is not an error worth a dialog.
    /// </summary>
    public Result<ProfileRegistry> Remove(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return Error.Validation(
                "profile.no_path", "Name the profile to remove.");
        }

        var target = Normalise(configPath);
        var profiles = Profiles
            .Where(p => !ProfileEntry.PathComparer.Equals(p.ConfigPath, target))
            .ToList();

        var active = ActiveConfigPath;
        if (active is not null && ProfileEntry.PathComparer.Equals(Normalise(active), target))
        {
            active = profiles.Count > 0 ? profiles[0].ConfigPath : null;
        }

        return new ProfileRegistry { Profiles = profiles, ActiveConfigPath = active };
    }

    /// <summary>Makes a registered profile the active one.</summary>
    public Result<ProfileRegistry> SetActive(string configPath)
    {
        if (Find(configPath) is not { } entry)
        {
            return Error.NotFound(
                "profile.unknown",
                $"{configPath} is not a registered profile, so it cannot be made the active one. "
                + "Add it first.");
        }

        return this with { ActiveConfigPath = entry.ConfigPath };
    }

    /// <summary>
    /// Absolute and free of "." and "..", so the same file reached by two spellings
    /// is one entry. Left as typed when it cannot be resolved — an unusable path is
    /// better refused by <see cref="Add"/> with its own message than swallowed here.
    /// </summary>
    private static string Normalise(string configPath)
    {
        try
        {
            return Path.GetFullPath(configPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return configPath.Trim();
        }
    }
}
