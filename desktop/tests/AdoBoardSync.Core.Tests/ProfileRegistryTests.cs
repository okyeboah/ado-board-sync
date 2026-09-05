using AdoBoardSync.Core.Configuration;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// The multi-profile registry (ABSD-502) — the value type, with no store behind it.
///
/// The invariants worth pinning are the ones a person notices when they break:
/// one entry per config file however the path was typed, an active profile that is
/// actually in the list, and a registry that never holds a credential. That last
/// one is structural rather than asserted: <see cref="ProfileEntry" /> has nowhere
/// to put a token, which is why this file is in Core.Tests and needs no fixture.
/// </summary>
public class ProfileRegistryTests
{
    private static string Path(string name) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);

    private static ProfileEntry Entry(string name, string org = "acme", string project = "widgets") =>
        new(Path(name), org, project, string.Empty);

    [Fact]
    public void AnEmptyRegistryHasNoActiveProfile()
    {
        Assert.True(ProfileRegistry.Empty.IsEmpty);
        Assert.Null(ProfileRegistry.Empty.Active);
        Assert.Null(ProfileRegistry.Empty.ActiveConfigPath);
    }

    [Fact]
    public void TheFirstProfileAddedBecomesTheActiveOne()
    {
        // Otherwise the first thing a new user does — open a profile — leaves the
        // switcher pointing at nothing.
        var added = ProfileRegistry.Empty.Add(Entry("a.json"));

        Assert.True(added.IsSuccess);
        Assert.Equal(Path("a.json"), added.Value.Active?.ConfigPath);
    }

    [Fact]
    public void AddingASecondProfileDoesNotStealTheActiveOne()
    {
        // Registering happens as a side effect of opening, and also of merely
        // having opened before. A registration that moved the active profile would
        // switch the board out from under whoever is looking at it.
        var registry = ProfileRegistry.Empty.Add(Entry("a.json")).Value.Add(Entry("b.json")).Value;

        Assert.Equal(2, registry.Profiles.Count);
        Assert.Equal(Path("a.json"), registry.Active?.ConfigPath);
    }

    [Fact]
    public void ReAddingTheSamePathUpdatesTheEntryRatherThanDuplicatingIt()
    {
        var registry = ProfileRegistry.Empty
            .Add(new ProfileEntry(Path("a.json"), "acme", "widgets", "Old name")).Value
            .Add(new ProfileEntry(Path("a.json"), "acme", "gadgets", "New name")).Value;

        var only = Assert.Single(registry.Profiles);
        Assert.Equal("New name", only.Label);
        Assert.Equal("acme/gadgets", only.BoardDisplay);
    }

    [Fact]
    public void APathIsNormalisedBeforeItIsCompared()
    {
        // The same file reached two ways is one profile. A registry that disagreed
        // would show two entries that open the same board and keep two histories.
        var direct = Path("a.json");
        var roundabout = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sub", "..", "a.json");

        var registry = ProfileRegistry.Empty
            .Add(new ProfileEntry(direct, "acme", "widgets", string.Empty)).Value
            .Add(new ProfileEntry(roundabout, "acme", "widgets", string.Empty)).Value;

        Assert.Single(registry.Profiles);
    }

    [Fact]
    public void AProfileWithNoConfigFileIsRefused()
    {
        // A profile described in onboarding but never saved has nothing to reopen.
        // Registering it would put an entry in the switcher that cannot be chosen.
        var added = ProfileRegistry.Empty.Add(new ProfileEntry(string.Empty, "acme", "widgets", string.Empty));

        Assert.True(added.IsFailure);
        Assert.Equal("profile.no_path", added.Error!.Code);
    }

    [Theory]
    [InlineData("", "widgets")]
    [InlineData("acme", "")]
    [InlineData("   ", "widgets")]
    public void AnEntryThatDoesNotNameABoardIsRefused(string org, string project)
    {
        // The org and project are what scope the operation history and every board
        // read. An entry missing either would scope them to nothing.
        var added = ProfileRegistry.Empty.Add(new ProfileEntry(Path("a.json"), org, project, string.Empty));

        Assert.True(added.IsFailure);
        Assert.Equal("profile.no_board", added.Error!.Code);
    }

    [Fact]
    public void AnEntryWithNoDisplayNameIsLabelledByItsBoard()
    {
        var registry = ProfileRegistry.Empty.Add(Entry("a.json")).Value;

        Assert.Equal("acme/widgets", registry.Profiles[0].Label);
    }

    [Fact]
    public void RemovingTheActiveProfileMovesActiveToTheOneThatIsLeft()
    {
        // Not to null: there is still a profile to show, and leaving the switcher
        // empty while the list is not is the state a user cannot get out of.
        var registry = ProfileRegistry.Empty.Add(Entry("a.json")).Value.Add(Entry("b.json")).Value;

        var removed = registry.Remove(Path("a.json"));

        Assert.True(removed.IsSuccess);
        Assert.Equal(Path("b.json"), removed.Value.Active?.ConfigPath);
    }

    [Fact]
    public void RemovingTheLastProfileLeavesNoActiveOne()
    {
        var registry = ProfileRegistry.Empty.Add(Entry("a.json")).Value;

        var removed = registry.Remove(Path("a.json")).Value;

        Assert.True(removed.IsEmpty);
        Assert.Null(removed.Active);
    }

    [Fact]
    public void RemovingAProfileThatIsNotActiveLeavesTheActiveOneAlone()
    {
        var registry = ProfileRegistry.Empty.Add(Entry("a.json")).Value.Add(Entry("b.json")).Value;

        var removed = registry.Remove(Path("b.json")).Value;

        Assert.Equal(Path("a.json"), removed.Active?.ConfigPath);
    }

    [Fact]
    public void RemovingAProfileThatWasNeverRegisteredChangesNothing()
    {
        var registry = ProfileRegistry.Empty.Add(Entry("a.json")).Value;

        var removed = registry.Remove(Path("never.json"));

        Assert.True(removed.IsSuccess);
        Assert.Single(removed.Value.Profiles);
    }

    [Fact]
    public void ActivatingAnUnregisteredProfileIsRefusedRatherThanAdded()
    {
        // SetActive is the switcher's operation, and the switcher can only offer
        // what is registered. Silently adding here would let a stale path — one
        // read from a hand-edited file — put a board in the list that nobody chose.
        var registry = ProfileRegistry.Empty.Add(Entry("a.json")).Value;

        var activated = registry.SetActive(Path("b.json"));

        Assert.True(activated.IsFailure);
        Assert.Equal("profile.unknown", activated.Error!.Code);
    }

    [Fact]
    public void ActivatingAProfileFindsItHoweverThePathWasTyped()
    {
        var registry = ProfileRegistry.Empty.Add(Entry("a.json")).Value.Add(Entry("b.json")).Value;
        var roundabout = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sub", "..", "b.json");

        var activated = registry.SetActive(roundabout);

        Assert.True(activated.IsSuccess);
        Assert.Equal(Path("b.json"), activated.Value.Active?.ConfigPath);
    }

    [Fact]
    public void EveryOperationLeavesTheOriginalRegistryUntouched()
    {
        // The registry is a value, and the view model holds the one it last wrote.
        // An operation that mutated in place would leave a failed write showing a
        // list the file does not contain.
        var registry = ProfileRegistry.Empty.Add(Entry("a.json")).Value;

        registry.Add(Entry("b.json"));
        registry.Remove(Path("a.json"));
        registry.SetActive(Path("a.json"));

        Assert.Single(registry.Profiles);
        Assert.Equal(Path("a.json"), registry.Active?.ConfigPath);
    }

    [Fact]
    public void FindingANullOrBlankPathAnswersNothingRatherThanThrowing()
    {
        var registry = ProfileRegistry.Empty.Add(Entry("a.json")).Value;

        Assert.Null(registry.Find(null));
        Assert.Null(registry.Find(string.Empty));
        Assert.Null(registry.Find("   "));
    }
}
