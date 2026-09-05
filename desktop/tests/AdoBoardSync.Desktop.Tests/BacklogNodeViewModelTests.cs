using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Desktop.ViewModels;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Pins the preview to the CLI's own converter: what the pane shows must be the
/// exact string <c>import</c> sends, never a second renderer's version of it.
/// The node is also the editing unit, so these pin that an edited buffer is
/// derived exactly as the same text would be after a save and re-parse.
/// </summary>
public class BacklogNodeViewModelTests
{
    private static BacklogItem Issue(
        string code,
        IReadOnlyList<string>? description = null) =>
        new()
        {
            Level = BacklogLevel.Issue,
            Title = "A title",
            Code = code,
            DescriptionLines = description ?? [],
            Bullets = BacklogParser.BulletsOf(BacklogLevel.Issue, description ?? []),
        };

    [Fact]
    public void HtmlIsExactlyWhatTheSharedConverterProduces()
    {
        string[] lines = ["Some **bold** text", string.Empty, "- a bullet"];

        var node = new BacklogNodeViewModel(Issue("PROJ-1", description: lines));

        Assert.Equal(MarkdownHtml.ToHtml(lines), node.Html);
    }

    [Fact]
    public void TaskTitlesUseThePlainFormTheCliSends()
    {
        var bullet = "Implement the `EventStore` **now**";
        var node = new BacklogNodeViewModel(Issue("PROJ-1", description: ["- " + bullet]));

        var task = Assert.Single(node.Tasks);
        Assert.Equal(MarkdownHtml.Plain(bullet), task.Title);
        Assert.Equal(MarkdownHtml.Inline(bullet), task.Html);
    }

    [Fact]
    public void AuthoredAngleBracketsAreEscapedRatherThanTreatedAsMarkup()
    {
        // The converter escapes authored HTML, so a user typing <b> gets a literal
        // <b> in Azure DevOps — not bold, and not a balance problem either.
        var node = new BacklogNodeViewModel(Issue("PROJ-1", description: ["a <b> literal"]));

        Assert.Contains("&lt;b&gt;", node.Html);
        Assert.Empty(node.Problems);
    }

    [Fact]
    public void WellFormedContentReportsNoProblems()
    {
        var node = new BacklogNodeViewModel(Issue(
            "PROJ-1",
            description: ["Text with **bold** and `code`", "- A task with *italics*"]));

        Assert.Empty(node.Problems);
        Assert.False(node.HasProblems);
        Assert.Equal("Markup is well formed.", node.ProblemSummary);
    }

    [Theory]
    [InlineData("ABSD-101 · Create the thing", "Create the thing")]
    [InlineData("ABSD-101 - Create the thing", "Create the thing")]
    [InlineData("ABSD-101: Create the thing", "Create the thing")]
    [InlineData("ABSD-101 Create the thing", "Create the thing")]
    public void TheRowTitleDropsACodeTheBadgeAlreadyShows(string heading, string expected)
    {
        var node = new BacklogNodeViewModel(new BacklogItem
        {
            Level = BacklogLevel.Issue,
            Title = heading,
            Code = "ABSD-101",
        });

        Assert.Equal(expected, node.DisplayTitle);
        Assert.Equal(heading, node.Title);
    }

    [Fact]
    public void ATitleThatDoesNotRepeatTheCodeIsLeftAlone()
    {
        var node = new BacklogNodeViewModel(Issue("ABSD-101"));

        Assert.Equal("A title", node.DisplayTitle);
    }

    [Fact]
    public void AnEpicTitleIsNeverTrimmed()
    {
        var epic = new BacklogNodeViewModel(new BacklogItem
        {
            Level = BacklogLevel.Epic,
            Title = "Epic ABSD-100: Product foundation",
        });

        Assert.Equal("Epic ABSD-100: Product foundation", epic.DisplayTitle);
    }

    [Fact]
    public void AnEpicIsBadgedAsAnEpicAndCountsItsIssues()
    {
        var epic = new BacklogNodeViewModel(new BacklogItem
        {
            Level = BacklogLevel.Epic,
            Title = "Epic 1 — Foundations",
        });
        epic.Children.Add(new BacklogNodeViewModel(Issue("PROJ-1")));

        Assert.True(epic.IsEpic);
        Assert.Equal("EPIC", epic.Badge);
        Assert.Equal("1 issue", epic.Detail);
    }

    [Fact]
    public void AnIssueIsBadgedWithItsCodeAndCountsItsTasks()
    {
        var node = new BacklogNodeViewModel(Issue("PROJ-101", description: ["- one", "- two"]));

        Assert.False(node.IsEpic);
        Assert.Equal("PROJ-101", node.Badge);
        Assert.Equal("2 tasks", node.Detail);
    }

    // ------------------------------------------------------------- editing

    [Fact]
    public void EditingTheSourceRecomputesEveryDerivedView()
    {
        var node = new BacklogNodeViewModel(Issue("PROJ-101", description: ["old text"]));

        node.SetEditedSource("new **bold** text\n- a typed task");

        Assert.True(node.IsDirty);
        Assert.Equal("new **bold** text\n- a typed task", node.Source);
        Assert.Equal(MarkdownHtml.ToHtml(["new **bold** text", "- a typed task"]), node.Html);
        Assert.Single(node.Tasks);
        Assert.Equal("1 task", node.Detail);
    }

    [Fact]
    public void ABufferEqualToTheFileIsNotDirty()
    {
        var node = new BacklogNodeViewModel(Issue("PROJ-101", description: ["same text"]));

        Assert.False(node.SetEditedSource("same text"));
        Assert.False(node.IsDirty);
        Assert.Equal(node.OriginalSource, node.Source);
    }

    [Fact]
    public void DiscardEditsRestoresTheParsedTextExactly()
    {
        var node = new BacklogNodeViewModel(Issue("PROJ-101", description: ["original", "- a task"]));
        node.SetEditedSource("scrap this");

        node.DiscardEdits();

        Assert.False(node.IsDirty);
        Assert.Equal("original\n- a task", node.Source);
        Assert.Equal(MarkdownHtml.ToHtml(["original", "- a task"]), node.Html);
        Assert.Single(node.Tasks);
    }

    [Fact]
    public void AnEpicBufferIsNeverMinedForTasks()
    {
        // The parser gives Epics no bullets even when their prose has dash lines,
        // and the live buffer must derive the same way.
        var epic = new BacklogNodeViewModel(new BacklogItem
        {
            Level = BacklogLevel.Epic,
            Title = "Epic 1 — Foundations",
            DescriptionLines = ["- not a task, epics have none"],
        });
        epic.Children.Add(new BacklogNodeViewModel(Issue("PROJ-1")));

        Assert.Empty(epic.Tasks);
        Assert.Equal("1 issue", epic.Detail);
    }
}
