using System.Reflection;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Composition;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.Infrastructure;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The composition root and the dependency direction it exists to protect
/// (ABSD-106).
///
/// The port sweep below is built by reflecting over Core rather than by listing
/// the registrations, because a test written the other way round can only ever
/// confirm what is already registered: add a port to Core, bind it nowhere, and
/// an enumeration of the container still passes while the first caller that needs
/// it goes back to constructing its own adapter. Discovery has to come from the
/// side that declares the ports.
///
/// The direction tests read the compiled assembly references instead of trusting
/// CONVENTIONS rule 3's prose. A layering rule that lives only in a document is
/// enforced by whoever happens to review the pull request.
/// </summary>
public class CompositionRootTests
{
    private static readonly Assembly CoreAssembly = typeof(Error).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(FileSystemBacklogFileStore).Assembly;
    private static readonly Assembly DesktopAssembly = typeof(AppServices).Assembly;

    /// <summary>The naming convention Core uses for a port: <c>I…Store</c>, <c>I…Reader</c>, and so on.</summary>
    private static readonly string[] PortSuffixes = ["Store", "Reader", "Writer", "Source"];

    /// <summary>
    /// The ports the container deliberately does not bind, and why each one is
    /// here. Every entry costs the sweep a port, so each needs a reason that would
    /// survive being read aloud — and each is checked back against Core on every
    /// run, so a misspelling fails the sweep instead of quietly widening it, and a
    /// port that later gains a binding is reported by
    /// <see cref="AnExcludedPortThatGainsAnAdapterMustLeaveTheExclusionSet" />.
    /// </summary>
    private static readonly string[] PortsTheContainerDoesNotBind =
    [
        // A strategy chosen per profile, not a service. PatResolver composes an
        // ordered list of these — an environment variable name, a token file path,
        // a token typed this session — so every implementation takes a constructor
        // argument the container cannot supply and no single binding is correct.
        "IPatSource",
    ];

    [Fact]
    public void EveryPortCoreDeclaresIsBoundInTheCompositionRootRatherThanBuiltByItsCaller()
    {
        var ports = CorePorts();

        // Both guards keep the sweep honest rather than merely green: an empty
        // discovery would mean the filter, not the container, is what passes.
        Assert.NotEmpty(ports);

        var strangers = PortsTheContainerDoesNotBind
            .Where(name => !ports.Any(port => string.Equals(port.Name, name, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            strangers.Count == 0,
            $"The exclusion set names ports Core does not declare: {string.Join(", ", strangers)}. "
            + "A name that matches nothing excludes nothing and hides the fact that it excludes nothing.");

        var required = ports
            .Where(port => !PortsTheContainerDoesNotBind.Contains(port.Name, StringComparer.Ordinal))
            .ToList();

        Assert.NotEmpty(required);

        using var provider = AppServices.Build();

        var unbound = required
            .Where(port => provider.GetService(port) is null)
            .Select(port => port.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unbound.Count == 0,
            $"Core declares these ports and AppServices.Build() binds no adapter to them: "
            + $"{string.Join(", ", unbound)}. Whoever needs one next has to construct an adapter "
            + "itself, which is the seam this composition root exists to close.");
    }

    [Fact]
    public void AnExcludedPortThatGainsAnAdapterMustLeaveTheExclusionSet()
    {
        using var provider = AppServices.Build();

        var bound = CorePorts()
            .Where(port => PortsTheContainerDoesNotBind.Contains(port.Name, StringComparer.Ordinal))
            .Where(port => provider.GetService(port) is not null)
            .Select(port => port.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            bound.Count == 0,
            $"These ports are bound but still excluded from the sweep: {string.Join(", ", bound)}. "
            + "Remove them from PortsTheContainerDoesNotBind — an exclusion that outlives its reason "
            + "is how a sweep stops sweeping without anyone noticing.");
    }

    [Fact]
    public void TheProviderResolvesTheProfileLoaderAndTheShellViewModel()
    {
        using var provider = AppServices.Build();

        Assert.IsType<ProfileLoader>(provider.GetService(typeof(ProfileLoader)));
        Assert.IsType<MainWindowViewModel>(provider.GetService(typeof(MainWindowViewModel)));
    }

    [Fact]
    public void TheBacklogFileStorePortIsBoundToTheFilesystemAdapter()
    {
        using var provider = AppServices.Build();

        Assert.IsType<FileSystemBacklogFileStore>(provider.GetService(typeof(IBacklogFileStore)));
    }

    [Fact]
    public void CoreDependsOnNeitherInfrastructureNorTheDesktopHost()
    {
        var referenced = ReferencedNames(CoreAssembly);

        Assert.DoesNotContain(NameOf(InfrastructureAssembly), referenced);
        Assert.DoesNotContain(NameOf(DesktopAssembly), referenced);
    }

    [Fact]
    public void InfrastructureDependsOnCoreAndNotOnTheDesktopHost()
    {
        var referenced = ReferencedNames(InfrastructureAssembly);

        Assert.Contains(NameOf(CoreAssembly), referenced);
        Assert.DoesNotContain(NameOf(DesktopAssembly), referenced);
    }

    [Fact]
    public void CoreCarriesNoStorageHttpOrUserInterfaceDependency()
    {
        string[] forbidden = ["Avalonia", "System.Net.Http", "Microsoft.Data.Sqlite", "System.Windows"];

        var carried = ReferencedNames(CoreAssembly)
            .Where(name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(
            carried.Count == 0,
            $"Core references {string.Join(", ", carried)}. Storage, HTTP and the UI belong behind a "
            + "port in Infrastructure or Desktop; a reference here is the moment that stops being true.");
    }

    [Fact]
    public void TheCoreProjectDeclaresNoPackageReference()
    {
        var csproj = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "desktop", "src", "AdoBoardSync.Core", "AdoBoardSync.Core.csproj"));

        // Read as text rather than parsed: the claim is about the file a reviewer
        // opens, and central package management means a version-less
        // PackageReference here would restore cleanly and look deliberate.
        Assert.DoesNotContain("PackageReference", csproj, StringComparison.Ordinal);
    }

    private static IReadOnlyList<Type> CorePorts() =>
        [.. CoreAssembly.GetExportedTypes()
            .Where(type => type.IsInterface
                && type.Name.StartsWith('I')
                && PortSuffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal)))
            .OrderBy(type => type.Name, StringComparer.Ordinal)];

    private static IReadOnlyList<string> ReferencedNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)];

    private static string NameOf(Assembly assembly) => assembly.GetName().Name ?? assembly.ToString();
}
