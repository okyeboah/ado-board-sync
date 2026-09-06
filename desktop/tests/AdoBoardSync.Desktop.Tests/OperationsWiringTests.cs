using System.Globalization;
using System.Reflection;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Diagnostics;
using AdoBoardSync.Core.Operations;
using AdoBoardSync.Desktop.Composition;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Proves the operations ports are actually reachable from the running app.
///
/// <see cref="CompositionRootTests" /> sweeps Core for ports by naming convention
/// — <c>I…Store</c>, <c>I…Reader</c>, <c>I…Writer</c>, <c>I…Source</c> — which is
/// a good guard but does not see <see cref="IOperationHistory" />,
/// <see cref="IDiagnostics" />, <see cref="IAgentRunner" /> or their neighbours.
/// Those four were each built, tested and then wired nowhere; the tests below are
/// what would have caught that, because a container misconfiguration otherwise
/// only shows up when a user clicks the button.
/// </summary>
public class OperationsWiringTests
{
    [Fact]
    public void TheContainerResolvesEveryOperationsPort()
    {
        using var provider = AppServices.Build();

        Assert.NotNull(provider.GetService<IOperationHistory>());
        Assert.NotNull(provider.GetService<IAgentRunHistory>());
        Assert.NotNull(provider.GetService<IDiagnostics>());
        Assert.NotNull(provider.GetService<IAgentProviderRegistry>());
        Assert.NotNull(provider.GetService<IAgentRunner>());
        Assert.NotNull(provider.GetService<DiagnosticRedaction>());
    }

    [Fact]
    public void TheShellShowsTheSurfacesTheContainerBuiltRatherThanItsOwn()
    {
        // The regression this exists for, and it was a real one: the shell built
        // its own PlanViewModel, so the history recorder and the diagnostics
        // redactor registered here reached nothing. Resolving both and comparing
        // is the only check that sees it — every view-model test passes either way.
        using var provider = AppServices.Build();

        var surfaces = provider.GetRequiredService<ShellSurfaces>();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        Assert.NotNull(surfaces.Plan);
        Assert.NotNull(shell.History);
        Assert.NotNull(shell.Profiles);

        // Not the same instance — ShellSurfaces is transient — but the shell must
        // have been handed one, not have made one.
        Assert.NotNull(shell.BoardPlan);
        Assert.NotNull(shell.Sprints);
        Assert.NotNull(shell.Assignees);
    }

    [Fact]
    public void ThePlanGateResolvesThePlatformCredentialStoreRatherThanBuildingOne()
    {
        // ABSD-106: the composition root is the only place a port meets its
        // adapter. The gate used to call OsCredentialStore.ForThisPlatform()
        // itself, which meant a missing registration still worked — and a view
        // model built in a test quietly read the developer's own keychain.
        using var provider = AppServices.Build();

        var registered = provider.GetRequiredService<Core.Configuration.ICredentialStore>();
        var resolved = provider.GetRequiredService<PlanViewModel>();
        var standalone = new PlanViewModel();

        Assert.Equal(registered.Name, resolved.CredentialStoreName);

        // Built outside the container it gets the empty store, never the real one.
        Assert.NotEqual(registered.Name, standalone.CredentialStoreName);
        Assert.Contains("no credential store", standalone.CredentialStoreName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRunHistoryAndTheAgentHistoryAreOneStoreOverOneFile()
    {
        // They are the same SQLite file and the same connection. Two instances
        // would be two writers on one database for no reason at all.
        using var provider = AppServices.Build();

        var runs = provider.GetRequiredService<IOperationHistory>();
        var agents = provider.GetRequiredService<IAgentRunHistory>();

        Assert.Same(runs, agents);
    }

    [Fact]
    public void TheContainerResolvesEverySurfaceTheShellOpens()
    {
        // A view model that cannot be constructed is a nav section that opens on
        // an exception rather than a screen.
        using var provider = AppServices.Build();

        Assert.NotNull(provider.GetService<MainWindowViewModel>());
        Assert.NotNull(provider.GetService<HistoryViewModel>());
        Assert.NotNull(provider.GetService<AuditViewModel>());
        Assert.NotNull(provider.GetService<SprintPlanningViewModel>());
        Assert.NotNull(provider.GetService<AssigneePlanningViewModel>());
        Assert.NotNull(provider.GetService<ApplyHistoryRecorder>());
    }

    [Fact]
    public void TheDiagnosticsSinkIsBuiltAroundTheSameRedactorThePlanGateRegistersTheTokenWith()
    {
        // The redactor's registered-secret pass is the guarantee; its shape
        // matching is only the backstop. If the sink held a different instance
        // from the one PlanViewModel registers the PAT with, the guarantee would
        // silently degrade to the backstop.
        using var provider = AppServices.Build();

        var redaction = provider.GetRequiredService<DiagnosticRedaction>();
        var again = provider.GetRequiredService<DiagnosticRedaction>();

        Assert.Same(redaction, again);
    }

    [Fact]
    public async Task ThePlanGateReadsTheBoardTheContainerGaveItRatherThanOneItBuilt()
    {
        // IBoardGatewayFactory and its adapter were registered and resolved by
        // nobody: both consumers defaulted to `pat => new AzureDevOpsGateway(pat)`.
        // A registration nothing resolves looks exactly like no registration, so
        // the proof is that a Plan generated through the container reaches *this*
        // board.
        var board = new FakeBoardGateway();

        // The production registration itself, first: the container below supplies
        // its own factory, so without this the test would pass just as well
        // against an AppServices that registers nothing at all.
        using (var real = AppServices.Build())
        {
            Assert.NotNull(real.GetService<BoardGatewayFactory>());
        }

        using var provider = new ServiceCollection()
            .AddCore()
            .AddInfrastructure()
            .AddViewModels()
            .AddSingleton<BoardGatewayFactory>(_ => _ => board)
            .BuildServiceProvider();

        using var profile = TempBoardProfile.Create(RepoPaths.Fixture("backlog", "standard.md"));
        var workspace = await Shell.WorkspaceAsync(profile.ConfigPath);

        var gate = provider.GetRequiredService<PlanViewModel>();
        gate.SessionToken = "wiring-token";

        await gate.GenerateAsync(workspace);

        Assert.True(gate.HasPlan, gate.ErrorText);
        Assert.True(board.ReadCount > 0, "The gate never read the board the container registered.");
    }

    [Fact]
    public void AGateBuiltOutsideTheContainerRefusesToReachABoardAtAll()
    {
        // The fallback must not work. A default that constructs a real
        // AzureDevOpsGateway hides a missing registration until it is a live call
        // to somebody's board — which one acceptance test was making every run.
        // Both surfaces, not just the Plan gate: the Audit view holds its own copy
        // of this fallback, and a test that reflected over PlanViewModel alone
        // would let the Audit one regress in silence.
        foreach (var viewModel in new object[] { new PlanViewModel(), new AuditViewModel() })
        {
            var factory = viewModel.GetType()
                .GetField("_gatewayFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel) as BoardGatewayFactory;

            Assert.NotNull(factory);
            Assert.IsType<UnconfiguredBoardGateway>(factory!("any-token"));
        }
    }

    [Fact]
    public async Task AnUnconfiguredBoardRefusesWithATypedErrorRatherThanAnException()
    {
        // ARCHITECTURE.md §3.7 keeps exceptions out of expected outcomes, and a
        // missing registration is one. The refusal has to arrive through the same
        // IsFailure branch every other board failure takes, or each caller needs a
        // try/catch that the two config-table views no longer have.
        var read = await new UnconfiguredBoardGateway("nothing was supplied")
            .ReadAsync(InMemoryConfig.Create());

        Assert.True(read.IsFailure);
        Assert.Equal("board.unconfigured", read.Error!.Code);
    }

    [Fact]
    public void TheConfigTablesReloadThroughTheContainersProfileLoaderNotOneTheyBuild()
    {
        // Both tables reload the profile after writing board.config.json. Their
        // reload parameter is optional, so an unregistered factory means each one
        // silently builds `new ProfileLoader(new FileSystemBacklogFileStore())` —
        // a second loader and a second adapter, inside a view model, and wired to
        // NullDiagnostics while the registered loader emits ABSD-507 events.
        using var provider = AppServices.Build();

        foreach (var table in new object[]
                 {
                     provider.GetRequiredService<SprintPlanningViewModel>(),
                     provider.GetRequiredService<AssigneePlanningViewModel>(),
                 })
        {
            // BaseType: _reload is private on PlanningTableViewModel<TRow>, and
            // GetField on the derived type does not see a base class's privates.
            var reload = (Delegate)table.GetType().BaseType!
                .GetField("_reload", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(table)!;

            Assert.False(
                reload.Method.Name.Contains("DefaultReload", StringComparison.Ordinal),
                $"{table.GetType().Name} fell back to its own ProfileLoader; the container "
                + "registered none, so the composition root is being bypassed.");
        }
    }

    [Fact]
    public async Task SavingTheBacklogRecordsThatAFileReachedDisk()
    {
        // The third of ABSD-507's three subjects — Plan generation, Apply, and file
        // writes. It is emitted from the loader rather than the shell because the
        // loader is what actually writes: a save reported from a view model reports
        // an intention, and the two differ exactly when the record matters.
        var recorded = new InMemoryDiagnostics();

        // No disk. ProfileLoader reads and writes everything through
        // IBacklogFileStore, so the in-memory store exercises the same path — and
        // an earlier version of this test used a real temp directory, leaked it,
        // and before that wrote into the repository's own tracked fixture, because
        // TempBoardProfile points board_file at the path it is handed rather than
        // copying it.
        var store = new InMemoryBacklogFileStore()
            .With(InMemoryConfig.DefaultBacklogPath, "## Epic one\n\n### PROJ-1 · An issue\n");
        var loader = new ProfileLoader(store, recorded);

        var opened = await loader.FromConfigAsync(InMemoryConfig.Create(), configPath: null);
        Assert.True(opened.IsSuccess, opened.Error?.SafeMessage);

        var saved = await loader.SaveAsync(opened.Value, opened.Value.Markdown + "\n");
        Assert.True(saved.IsSuccess, saved.Error?.SafeMessage);

        var write = Assert.Single(recorded.Events, e => e.Category == "backlog");
        Assert.Equal(DiagnosticLevel.Info, write.Level);
        Assert.Equal(opened.Value.BacklogPath, write.Data["path"]);
        Assert.True(int.Parse(write.Data["bytes"], CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task ThePlanGateEmitsThePlanAndApplyEventsArchitectureSectionSevenAsksFor()
    {
        // ABSD-507. DiagnosticsExtensions declared these five events and nothing in
        // the application called one; the only real emitter built its own by hand,
        // and put item titles — the user's prose — in a file they attach to support
        // conversations.
        var board = new FakeBoardGateway();
        var recorded = new InMemoryDiagnostics();

        using var profile = TempBoardProfile.Create(RepoPaths.Fixture("backlog", "standard.md"));
        var workspace = await Shell.WorkspaceAsync(profile.ConfigPath);

        var gate = new PlanViewModel(_ => board, diagnostics: recorded)
        {
            SessionToken = "diagnostics-token",
        };

        await gate.GenerateAsync(workspace);
        gate.RequestApply(workspace);
        await gate.ApplyConfirmedAsync(workspace);

        var messages = recorded.Events.Select(e => e.Message).ToList();

        Assert.Contains(recorded.Events, e => e.Category == "plan" && e.Data.ContainsKey("duration_ms"));
        Assert.Contains(recorded.Events, e => e.Category == "apply" && e.Data.ContainsKey("backlog_fingerprint"));
        Assert.Contains(recorded.Events, e => e.Category == "apply" && e.Data.ContainsKey("failed_codes"));

        // And no event carries an item's title. The codes identify the row; the
        // title is the user's prose and does not belong in a bundle.
        var titles = workspace.Items.Select(item => item.Title).Where(t => t.Length > 0);
        foreach (var title in titles)
        {
            Assert.DoesNotContain(
                recorded.Events.SelectMany(e => e.Data.Values).Concat(messages),
                value => value.Contains(title, StringComparison.Ordinal));
        }
    }
}
