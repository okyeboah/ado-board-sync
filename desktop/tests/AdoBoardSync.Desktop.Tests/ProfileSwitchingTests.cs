using System.Text.Json;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.Infrastructure.Configuration;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The multi-profile registry end to end (ABSD-502): the JSON file it persists to,
/// the switcher that edits it, and the one edge between the switcher and the shell.
///
/// Two properties matter more than the rest. The file must never hold a credential
/// — it names every board a person works on and lives outside any repository, so a
/// token in it would be the worst of both. And switching must be idempotent at the
/// edges: adopting a profile registers it, registering raises the switcher, and the
/// switcher opens profiles, so a missing guard anywhere in that ring is an infinite
/// reload rather than a wrong pixel.
/// </summary>
public class ProfileSwitchingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("absd-profiles").FullName;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not delete is the operating system's
            // problem, not a test failure.
        }
    }

    private string At(string name) => Path.Combine(_root, name);

    private JsonProfileRegistryStore Store(string name = "profiles.json") => new(At(name));

    private ProfileEntry Entry(string file, string org = "acme", string project = "widgets", string label = "") =>
        new(At(file), org, project, label);

    // ------------------------------------------------------------- the file

    [Fact]
    public void AMissingRegistryFileReadsAsAnEmptyRegistryRatherThanAFailure()
    {
        // First run. A failure here would put an error banner on a machine that has
        // simply never opened a profile.
        var read = Store().Read();

        Assert.True(read.IsSuccess);
        Assert.True(read.Value.IsEmpty);
    }

    [Fact]
    public void AProfileSurvivesAWriteAndAReadWithItsBoardAndItsName()
    {
        var store = Store();
        var registry = ProfileRegistry.Empty.Add(Entry("a.json", label: "Platform")).Value;

        Assert.True(store.Write(registry).IsSuccess);
        var read = store.Read();

        Assert.True(read.IsSuccess);
        var only = Assert.Single(read.Value.Profiles);
        Assert.Equal(At("a.json"), only.ConfigPath);
        Assert.Equal("Platform", only.Label);
        Assert.Equal("acme/widgets", only.BoardDisplay);
        Assert.Equal(At("a.json"), read.Value.ActiveConfigPath);
    }

    [Fact]
    public void TheWrittenFileHoldsExactlyTheFieldsItIsAllowedToHold()
    {
        // An allow-list, not a search for suspicious words: "pat" is a substring of
        // "config_path", so a blocklist here passes or fails for the wrong reasons.
        // ProfileEntry has no field for a credential today, and this fails the day
        // one is added rather than the day it reaches someone's disk.
        var store = Store();
        store.Write(ProfileRegistry.Empty.Add(Entry("a.json", label: "Platform")).Value);

        using var document = JsonDocument.Parse(File.ReadAllText(store.RegistryPath));

        Assert.Equal(
            ["active_config_path", "profiles", "version"],
            document.RootElement.EnumerateObject().Select(p => p.Name).Order());

        Assert.Equal(
            ["config_path", "display_name", "org", "project"],
            document.RootElement.GetProperty("profiles")[0].EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public void TheRegistryIsWrittenUnderTheUsersOwnDataRatherThanBesideABacklog()
    {
        // A file that followed the backlog into a git checkout is a file that
        // eventually gets committed, and this one names every board a person has.
        var path = JsonProfileRegistryStore.DefaultPath();

        Assert.Equal("profiles.json", Path.GetFileName(path));
        Assert.Equal("AdoBoardSync", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void AWriteLeavesNoTemporaryFileBehind()
    {
        // Temp-then-rename is what makes a crash mid-write leave the previous
        // registry intact. A leftover temp file per write would be a slow leak in
        // the user's own data directory.
        var store = Store();
        store.Write(ProfileRegistry.Empty.Add(Entry("a.json")).Value);
        store.Write(ProfileRegistry.Empty.Add(Entry("b.json")).Value);

        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void AnUnparseableRegistryIsReportedRatherThanOverwritten()
    {
        // Rewriting it would discard the profiles the file still names. The user
        // can fix or delete it; this app cannot guess what it meant.
        var store = Store();
        File.WriteAllText(store.RegistryPath, "{ not json");

        var read = store.Read();

        Assert.True(read.IsFailure);
        Assert.Equal("profiles.invalid_json", read.Error!.Code);
    }

    [Fact]
    public void AHandEditedFileNamingOneProfileTwiceLoadsItOnce()
    {
        // Read folds every entry back through Add, so a file edited by hand obeys
        // the same one-entry-per-path rule the running app does.
        var store = Store();
        File.WriteAllText(store.RegistryPath, $$"""
            {"version":1,"profiles":[
              {"config_path":{{JsonSerializer.Serialize(At("a.json"))}},"org":"acme","project":"widgets","display_name":"First"},
              {"config_path":{{JsonSerializer.Serialize(At("a.json"))}},"org":"acme","project":"widgets","display_name":"Second"}
            ]}
            """);

        var read = store.Read();

        Assert.True(read.IsSuccess);
        Assert.Equal("Second", Assert.Single(read.Value.Profiles).Label);
    }

    [Fact]
    public void AStoredActiveProfileThatIsNotInTheListIsRepairedRatherThanRefused()
    {
        // A registry that will not load is a switcher that will not open, and the
        // file is not the user's to fix.
        var store = Store();
        File.WriteAllText(store.RegistryPath, $$"""
            {"version":1,
             "active_config_path":{{JsonSerializer.Serialize(At("gone.json"))}},
             "profiles":[{"config_path":{{JsonSerializer.Serialize(At("a.json"))}},"org":"acme","project":"widgets","display_name":""}]}
            """);

        var read = store.Read();

        Assert.True(read.IsSuccess);
        Assert.Equal(At("a.json"), read.Value.ActiveConfigPath);
    }

    [Fact]
    public void AnEntryThatNamesNoBoardFailsTheReadInsteadOfLoadingHalfTheFile()
    {
        var store = Store();
        File.WriteAllText(store.RegistryPath, $$"""
            {"version":1,"profiles":[{"config_path":{{JsonSerializer.Serialize(At("a.json"))}},"org":"","project":"","display_name":""}]}
            """);

        var read = store.Read();

        Assert.True(read.IsFailure);
        Assert.Equal("profiles.invalid_entry", read.Error!.Code);
    }

    // --------------------------------------------------------- the switcher

    /// <summary>A store that answers from memory, and can be told to refuse writes.</summary>
    private sealed class FakeStore : IProfileRegistryStore
    {
        public ProfileRegistry Registry { get; set; } = ProfileRegistry.Empty;

        public Error? ReadError { get; set; }

        public Error? WriteError { get; set; }

        public int Writes { get; private set; }

        public Result<ProfileRegistry> Read() => ReadError is { } error ? error : Registry;

        public Result<bool> Write(ProfileRegistry registry)
        {
            Writes++;
            if (WriteError is { } error)
            {
                return error;
            }

            Registry = registry;
            return true;
        }
    }

    [Fact]
    public async Task LoadingAnEmptyRegistryLeavesTheSwitcherWithNothingToOffer()
    {
        var switcher = new ProfileRegistryViewModel(new FakeStore());

        await switcher.LoadAsync();

        Assert.False(switcher.HasProfiles);
        Assert.False(switcher.HasActiveProfile);
        Assert.False(switcher.HasError);
    }

    [Fact]
    public async Task AnUnreadableRegistryIsReportedWithItsCode()
    {
        var switcher = new ProfileRegistryViewModel(
            new FakeStore { ReadError = Error.SourceFailure("profiles.unreadable", "denied") });

        await switcher.LoadAsync();

        Assert.True(switcher.HasError);
        Assert.Contains("profiles.unreadable", switcher.ErrorText);
    }

    [Fact]
    public async Task AddingAProfilePersistsItAndAnnouncesItAsActive()
    {
        var store = new FakeStore();
        var switcher = new ProfileRegistryViewModel(store);
        var announced = new List<string?>();
        switcher.ActiveProfileChanged += (profile, _) =>
        {
            announced.Add(profile?.ConfigPath);
            return Task.CompletedTask;
        };

        await switcher.AddAsync(Entry("a.json"));

        Assert.Equal(1, store.Writes);
        Assert.Equal([At("a.json")], announced);
        Assert.Equal(At("a.json"), switcher.ActiveProfile?.ConfigPath);
    }

    [Fact]
    public async Task ReAddingTheProfileThatIsAlreadyOpenAnnouncesNothing()
    {
        // Adopt registers on every open, so this fires constantly. An announcement
        // each time would reload the profile that is already on screen — and the
        // reload adopts, which registers, which announces.
        var switcher = new ProfileRegistryViewModel(new FakeStore());
        var announced = 0;
        switcher.ActiveProfileChanged += (_, _) =>
        {
            announced++;
            return Task.CompletedTask;
        };

        await switcher.AddAsync(Entry("a.json"));
        await switcher.AddAsync(Entry("a.json"));
        await switcher.AddAsync(Entry("a.json"));

        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task AddingASecondProfileDoesNotAnnounceASwitch()
    {
        var switcher = new ProfileRegistryViewModel(new FakeStore());
        await switcher.AddAsync(Entry("a.json"));

        var announced = 0;
        switcher.ActiveProfileChanged += (_, _) =>
        {
            announced++;
            return Task.CompletedTask;
        };

        await switcher.AddAsync(Entry("b.json"));

        Assert.Equal(0, announced);
        Assert.Equal(2, switcher.Profiles.Count);
        Assert.Equal(At("a.json"), switcher.ActiveProfile?.ConfigPath);
    }

    [Fact]
    public async Task ChoosingAnotherProfileAnnouncesItExactlyOnce()
    {
        var switcher = new ProfileRegistryViewModel(new FakeStore());
        await switcher.AddAsync(Entry("a.json"));
        await switcher.AddAsync(Entry("b.json"));

        var announced = new List<string?>();
        switcher.ActiveProfileChanged += (profile, _) =>
        {
            announced.Add(profile?.ConfigPath);
            return Task.CompletedTask;
        };

        await switcher.SetActiveAsync(At("b.json"));
        await switcher.SetActiveAsync(At("b.json"));

        Assert.Equal([At("b.json")], announced);
    }

    [Fact]
    public async Task ExactlyOneRowIsMarkedActive()
    {
        var switcher = new ProfileRegistryViewModel(new FakeStore());
        await switcher.AddAsync(Entry("a.json"));
        await switcher.AddAsync(Entry("b.json"));
        await switcher.SetActiveAsync(At("b.json"));

        Assert.Single(switcher.Profiles, row => row.IsActive);
        Assert.Equal(At("b.json"), switcher.Profiles.Single(row => row.IsActive).ConfigPath);
    }

    [Fact]
    public async Task RemovingTheActiveProfileAnnouncesWhicheverIsLeft()
    {
        var switcher = new ProfileRegistryViewModel(new FakeStore());
        await switcher.AddAsync(Entry("a.json"));
        await switcher.AddAsync(Entry("b.json"));

        var announced = new List<string?>();
        switcher.ActiveProfileChanged += (profile, _) =>
        {
            announced.Add(profile?.ConfigPath);
            return Task.CompletedTask;
        };

        await switcher.RemoveAsync(At("a.json"));

        Assert.Equal([At("b.json")], announced);
    }

    [Fact]
    public async Task AProfileWithNoConfigFileOnDiskIsRefusedWithAReasonAndNotWritten()
    {
        var store = new FakeStore();
        var switcher = new ProfileRegistryViewModel(store);

        await switcher.AddAsync(Workspace(configPath: null));

        Assert.True(switcher.HasError);
        Assert.Contains("profile.no_path", switcher.ErrorText);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task AnUnwritableRegistryStillOpensTheProfileAndSaysItWillNotBeRemembered()
    {
        // Losing the registry is an inconvenience; refusing to open the profile
        // because of it would be a much larger one.
        var switcher = new ProfileRegistryViewModel(
            new FakeStore { WriteError = Error.SourceFailure("profiles.unsaved", "read-only volume") });

        await switcher.AddAsync(Entry("a.json"));

        Assert.True(switcher.HasError);
        Assert.Contains("will not remember", switcher.ErrorText);
        Assert.Equal(At("a.json"), switcher.ActiveProfile?.ConfigPath);
    }

    [Fact]
    public async Task TheSwitcherIsMarkedBusyWhileASubscriberIsStillOpeningTheProfile()
    {
        // The combo is disabled off this flag. A second choice made while the first
        // is still loading would race two profiles onto one shell.
        var switcher = new ProfileRegistryViewModel(new FakeStore());
        var busyDuringSwitch = false;
        switcher.ActiveProfileChanged += (_, _) =>
        {
            busyDuringSwitch = switcher.IsSwitching;
            return Task.CompletedTask;
        };

        await switcher.AddAsync(Entry("a.json"));

        Assert.True(busyDuringSwitch);
        Assert.False(switcher.IsSwitching);
    }

    // ------------------------------------------------------------- the shell

    private static BacklogWorkspace Workspace(string? configPath, string extra = "")
    {
        const string markdown = "## Epic 1\n\n### PROJ-101 · A\n";
        var config = BoardConfig.Parse(
            $$"""{"org":"acme","project":"widgets","code_prefix":"PROJ","board_file":"backlog.md"{{extra}}}""",
            Path.GetTempPath()).Value;

        return new BacklogWorkspace(
            configPath, config, "backlog.md", markdown, [], 0,
            FileStamp.For(DateTimeOffset.UnixEpoch, markdown));
    }

    [Fact]
    public void AdoptingAProfileRegistersItSoItReappearsInTheSwitcher()
    {
        var store = new FakeStore();
        var switcher = new ProfileRegistryViewModel(store);
        var shell = Shell.WithSurfaces(new ShellSurfaces(
            new PlanViewModel(), new AuditViewModel(),
            new SprintPlanningViewModel(), new AssigneePlanningViewModel(),
            Profiles: switcher));

        shell.Adopt(Workspace(At("a.json")));

        Assert.Equal(At("a.json"), Assert.Single(store.Registry.Profiles).ConfigPath);
    }

    [Fact]
    public void AdoptingAProfileThatWasNeverSavedRegistersNothing()
    {
        var store = new FakeStore();
        var shell = Shell.WithSurfaces(new ShellSurfaces(
            new PlanViewModel(), new AuditViewModel(),
            new SprintPlanningViewModel(), new AssigneePlanningViewModel(),
            Profiles: new ProfileRegistryViewModel(store)));

        shell.Adopt(Workspace(configPath: null));

        Assert.True(store.Registry.IsEmpty);
        Assert.Equal(0, store.Writes);
    }

    private const string Tables =
        ""","iterations":[{"name":"S1","items":["PROJ-101"]},{"name":"S2","items":[]}]"""
        + ""","assignees":{"ana@example.com":["PROJ-101"]}""";

    [Fact]
    public void AdoptingAProfileFillsTheSprintAndAssigneeTablesFromIt()
    {
        // Both tables were built and tested before anything opened them. Adopt is
        // the one call that puts a profile into them, so this is what makes the two
        // sections show a board rather than an empty form.
        var shell = Shell.WithSurfaces(ShellSurfaces.StandAlone());

        shell.Adopt(Workspace(At("a.json"), Tables));

        Assert.Equal(["S1", "S2"], shell.Sprints.Sprints.Select(s => s.Name));
        Assert.Equal("ana@example.com", Assert.Single(shell.Assignees.Owners).Identity);
        Assert.False(shell.Sprints.IsDirty);
        Assert.False(shell.Assignees.IsDirty);
    }

    [Fact]
    public async Task AFailedOpenEmptiesTheTablesRatherThanLeavingThePreviousProfilesInThem()
    {
        // Clear runs on the paths where the profile is gone for good. A table left
        // standing would offer to save one profile's sprints into whatever opens next.
        var shell = Shell.WithSurfaces(ShellSurfaces.StandAlone());
        shell.Adopt(Workspace(At("a.json"), Tables));

        await shell.LoadAsync(At("does-not-exist.json"));

        Assert.True(shell.HasError);
        Assert.Empty(shell.Sprints.Sprints);
        Assert.Empty(shell.Assignees.Owners);
        Assert.Contains("Open a Board profile first", shell.Sprints.SaveBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningAProfileThatIsAlreadyOnScreenDoesNotReopenIt()
    {
        // The ring is Adopt -> register -> announce -> open -> Adopt. Without the
        // path guard in the shell's handler it never terminates.
        var switcher = new ProfileRegistryViewModel(new FakeStore());
        var shell = Shell.WithSurfaces(new ShellSurfaces(
            new PlanViewModel(), new AuditViewModel(),
            new SprintPlanningViewModel(), new AssigneePlanningViewModel(),
            Profiles: switcher));

        // A path that does not exist: were the shell to try to open it, the load
        // would fail and the failure would be on screen.
        shell.Adopt(Workspace(At("a.json")));
        await Task.Yield();

        Assert.False(shell.HasError);
        Assert.NotNull(shell.Workspace);
    }
}
