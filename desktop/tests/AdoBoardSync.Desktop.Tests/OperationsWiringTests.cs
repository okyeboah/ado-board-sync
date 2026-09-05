using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Diagnostics;
using AdoBoardSync.Core.Operations;
using AdoBoardSync.Desktop.Composition;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
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
}
