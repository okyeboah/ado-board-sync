using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Diagnostics;
using AdoBoardSync.Core.Operations;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.Infrastructure;
using AdoBoardSync.Infrastructure.Agents;
using AdoBoardSync.Infrastructure.Configuration;
using AdoBoardSync.Infrastructure.Diagnostics;
using AdoBoardSync.Infrastructure.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace AdoBoardSync.Desktop.Composition;

/// <summary>
/// The single composition root (ABSD-106). Every port declared in Core is bound to
/// its adapter here and nowhere else, so a view or a view model never constructs
/// one — which is what makes the seams real rather than decorative.
///
/// The two methods are split along the dependency direction they represent:
/// <see cref="AddCore" /> registers what needs no platform, and
/// <see cref="AddInfrastructure" /> the adapters that touch the filesystem and the
/// network. They both live in this project because it is the only one that
/// references both, and because Core must keep its zero-PackageReference csproj.
/// </summary>
public static class AppServices
{
    /// <summary>Everything Core offers that has no platform dependency.</summary>
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton<ProfileLoader>();

        // The snapshot/run/verdict cycle around one agent edit (ABSD-704). It holds
        // no state between runs, so a singleton is only a saving; the invariant it
        // protects is in the sequence, not in the instance.
        services.AddSingleton<AgentEditSession>();
        return services;
    }

    /// <summary>The adapters: filesystem, Azure DevOps.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IBacklogFileStore, FileSystemBacklogFileStore>();

        // Whichever store this platform actually has, resolved once (ABSD-103).
        // A machine with none gets UnavailableCredentialStore, so the port is
        // always bound and PatResolver simply omits it from the source list.
        services.AddSingleton(_ => OsCredentialStore.ForThisPlatform());

        // A factory rather than the gateway itself: the connector is built around a
        // resolved PAT, and the PAT is resolved per action and never cached.
        services.AddSingleton<IBoardGatewayFactory, AzureDevOpsGatewayFactory>();

        // One SQLite file, registered under both ports it implements (ABSD-501,
        // ABSD-706). Registered as the concrete type first and forwarded, so both
        // resolutions share one instance and one connection — two instances would
        // mean two writers on one file for no reason.
        services.AddSingleton<SqliteOperationHistory>();
        services.AddSingleton<IOperationHistory>(s => s.GetRequiredService<SqliteOperationHistory>());
        services.AddSingleton<IAgentRunHistory>(s => s.GetRequiredService<SqliteOperationHistory>());

        // Diagnostics are on by default (ABSD-507): a failure the user reports is
        // only reconstructible if the log was already being written when it
        // happened. The sink never throws, so an unwritable log directory costs
        // nothing but the log.
        services.AddSingleton<DiagnosticRedaction>();
        services.AddSingleton<IDiagnostics>(s => new JsonLinesDiagnosticsSink(
            DiagnosticsPaths.DefaultDirectory, s.GetRequiredService<DiagnosticRedaction>()));

        services.AddSingleton<IAgentProviderRegistry, AgentProviderRegistry>();
        services.AddSingleton<IAgentRunner, AgentRunner>();

        // Bytes rather than text, and therefore its own adapter (ABSD-704):
        // rejecting an agent's edit has to put the file back exactly as it was, and
        // a decode/encode round trip through IBacklogFileStore would write what this
        // app believes the file said.
        services.AddSingleton<IAgentEditFileStore, FileSystemAgentEditFileStore>();

        // One registry file for the whole app (ABSD-502). A singleton because two
        // instances would each hold their own idea of which profile is active and
        // race each other writing it back.
        services.AddSingleton<IProfileRegistryStore, JsonProfileRegistryStore>();

        return services;
    }

    /// <summary>The view models the shell resolves.</summary>
    public static IServiceCollection AddViewModels(this IServiceCollection services)
    {
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<AuditViewModel>();
        services.AddTransient<SprintPlanningViewModel>();
        services.AddTransient<AssigneePlanningViewModel>();
        services.AddTransient<ApplyHistoryRecorder>();
        services.AddTransient<AgentAuthoringViewModel>();

        // The switcher is a singleton where the surfaces it feeds are transient: it
        // holds which profile is open, and a second copy of that answer is a second
        // active profile (ABSD-502).
        services.AddSingleton<ProfileRegistryViewModel>();
        return services;
    }

    /// <summary>The provider the application host builds once at startup.</summary>
    public static ServiceProvider Build() => new ServiceCollection()
        .AddCore()
        .AddInfrastructure()
        .AddViewModels()
        .BuildServiceProvider();
}
