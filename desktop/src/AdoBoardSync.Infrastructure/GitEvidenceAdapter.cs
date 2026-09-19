using System.Diagnostics;
using System.Text.RegularExpressions;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure;

/// <summary>
///     Probes local git repositories for the commit evidence <c>BuildAdvance</c>
///     plans from — the Infrastructure half of the CLI's <c>gitstate</c>. Every call
///     goes through <see cref="GitRunner" />, so tests supply what git would have said
///     instead of spawning it, exactly like the credential store's runner seam.
/// </summary>
public sealed class GitEvidenceAdapter : IGitEvidenceSource
{
    private readonly GitRunner _run;

    public GitEvidenceAdapter() : this(RunGit)
    {
    }

    internal GitEvidenceAdapter(GitRunner run)
    {
        _run = run;
    }

    public Task<Result<GitProbeReport>> ProbeAsync(
        IReadOnlyList<string> repos,
        string baseRef,
        bool fetch,
        string codePattern,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Probe(repos, baseRef, fetch, codePattern), cancellationToken);
    }

    private Result<GitProbeReport> Probe(
        IReadOnlyList<string> repos,
        string baseRef,
        bool fetch,
        string codePattern)
    {
        var regex = new Regex(codePattern);
        var evidence = new List<GitBranchEvidence>();
        var skipped = new List<string>();

        foreach (var repo in repos)
        {
            if (_run(repo, ["rev-parse", "--git-dir"]).Exit != 0)
            {
                skipped.Add($"Skipped {repo}: not a git repository.");
                continue;
            }

            if (fetch && _run(repo, ["fetch", "--prune", "--quiet"]).Exit != 0)
            {
                skipped.Add($"Skipped {repo}: fetch failed (refs would be stale).");
                continue;
            }

            var listing = _run(repo, ["branch", "-a", "--format=%(refname:short)"]).Output;
            foreach (var branch in listing.Split('\n',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var match = regex.Match(branch);
                if (!match.Success) continue;

                var counted = _run(repo, ["rev-list", "--count", $"{baseRef}..{branch}"]);
                if (counted.Exit != 0 || !int.TryParse(counted.Output.Trim(), out var ahead)) continue;

                if (ahead > 0) evidence.Add(new GitBranchEvidence(repo, branch, match.Groups[1].Value, ahead));
            }
        }

        return new GitProbeReport(evidence, skipped);
    }

    private static (int Exit, string Output) RunGit(string repo, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repo);
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start);
        if (process is null) return (-1, string.Empty);

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    /// <summary>Runs one git invocation in <paramref name="repo" />; exit code and stdout come back.</summary>
    internal delegate (int Exit, string Output) GitRunner(string repo, IReadOnlyList<string> args);
}