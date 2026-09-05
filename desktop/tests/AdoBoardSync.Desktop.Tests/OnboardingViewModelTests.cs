using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The two routes in, and the scaffold that makes a first profile work end to
/// end: a brand-new organisation should reach an open, editable, parseable
/// backlog without hand-writing a Markdown file first.
/// </summary>
public class OnboardingViewModelTests
{
    private static OnboardingViewModel Form(string backlogPath, string prefix = "ABX")
    {
        var form = Shell.Onboarding();
        form.Organisation = "org";
        form.Project = "project";
        form.CodePrefix = prefix;
        form.BacklogPath = backlogPath;
        return form;
    }

    [Fact]
    public async Task ABacklogFileThatDoesNotExistIsScaffoldedIntoAWorkingBacklog()
    {
        var directory = Directory.CreateTempSubdirectory("abs-onboard-scaffold-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "docs", "backlog.md");
            var form = Form(backlog);

            var created = await form.CreateProfileAsync();

            Assert.True(created.IsSuccess, created.Error?.SafeMessage);
            Assert.True(File.Exists(backlog));

            // The starter is a real backlog: parsed by the same engine, with the
            // form's own prefix, ready for the editor and for a Plan.
            var items = BacklogParser.Parse(created.Value.Config, File.ReadAllText(backlog));
            Assert.Equal(BacklogLevel.Epic, items[0].Level);
            Assert.Contains(items, i => i.Code == "ABX-101");
            Assert.Equal(0, BacklogMarkupAudit.Total(items));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnExistingBacklogIsNeverOverwrittenByTheScaffold()
    {
        var directory = Directory.CreateTempSubdirectory("abs-onboard-keep-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "backlog.md");
            const string authored = "## Epic 1 — Mine\n### ABX-201 · Already here\n";
            File.WriteAllText(backlog, authored);

            var created = await Form(backlog).CreateProfileAsync();

            Assert.True(created.IsSuccess, created.Error?.SafeMessage);
            Assert.Equal(authored, File.ReadAllText(backlog));
            Assert.Contains(created.Value.Items, i => i.Code == "ABX-201");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClearingTheScaffoldOptionLeavesAMissingFileAnError()
    {
        var directory = Directory.CreateTempSubdirectory("abs-onboard-missing-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "never-written.md");
            var form = Form(backlog);
            form.ScaffoldStarterBacklog = false;

            var created = await form.CreateProfileAsync();

            Assert.True(created.IsFailure);
            Assert.Contains("profile.backlog_missing", form.ErrorText);
            Assert.False(File.Exists(backlog));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheScaffoldOptionAppearsOnlyWhenTheFileIsMissing()
    {
        var directory = Directory.CreateTempSubdirectory("abs-onboard-appear-").FullName;
        try
        {
            var form = Form(Path.Combine(directory, "backlog.md"));
            Assert.True(form.CanScaffold);

            File.WriteAllText(form.BacklogPath, "## Epic 1 — One\n");
            Assert.False(form.CanScaffold);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheStarterContentParsesWithAnyPrefix()
    {
        var json = """{"org": "o", "project": "p", "code_prefix": "zz"}""";
        var config = BoardConfig.Parse(json, Path.GetTempPath());
        Assert.True(config.IsSuccess, config.Error?.SafeMessage);

        var items = BacklogParser.Parse(config.Value, StarterBacklog.Content("zz"));

        Assert.Equal(2, items.Count);
        Assert.Equal(BacklogLevel.Epic, items[0].Level);
        Assert.Equal("ZZ-101", items[1].Code);
        Assert.Equal(["A first task", "A second task"], items[1].Bullets);
    }
}
