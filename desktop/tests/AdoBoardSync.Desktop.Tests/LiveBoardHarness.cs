using System.Text.Json.Nodes;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Infrastructure;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>Skips unless the live board is configured, and says which switch is missing.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (LiveBoard.ConfigPath is null)
        {
            Skip = $"Set {LiveBoard.ConfigVariable} to a board profile to run the live tests.";
        }
    }

    /// <summary>Set on a test that creates or edits work items.</summary>
    public bool Writes
    {
        get;
        set
        {
            field = value;
            if (value && Skip is null && !LiveBoard.WritesAllowed)
            {
                Skip = $"Set {LiveBoard.WriteVariable}=1 to let the live tests write to that board.";
            }
        }
    }
}

internal static class LiveBoard
{
    internal const string ConfigVariable = "ADO_BOARD_SYNC_LIVE_CONFIG";
    internal const string WriteVariable = "ADO_BOARD_SYNC_LIVE_WRITE";

    internal static string? ConfigPath
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(ConfigVariable);
            return string.IsNullOrWhiteSpace(value) || !File.Exists(value) ? null : value;
        }
    }

    internal static bool WritesAllowed =>
        Environment.GetEnvironmentVariable(WriteVariable) == "1";

    /// <summary>
    /// The configured profile, optionally with one value swapped — so a test can
    /// point at a scratch backlog or a deliberately wrong project without
    /// needing a second profile on disk.
    /// </summary>
    internal static BoardConfig Config(string? project = null, string? boardFile = null)
    {
        var path = ConfigPath ?? throw new InvalidOperationException($"{ConfigVariable} is not set.");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        if (project is not null)
        {
            json["project"] = project;
        }

        if (boardFile is not null)
        {
            json["board_file"] = boardFile;
        }

        // A relative pat_file resolves against the config's own directory, so the
        // rewritten document has to keep that directory as its base.
        var parsed = BoardConfig.Parse(json.ToJsonString(), Path.GetDirectoryName(Path.GetFullPath(path))!);
        Assert.True(parsed.IsSuccess, Explain(parsed.Error));
        return parsed.Value;
    }

    internal static AzureDevOpsGateway Gateway(BoardConfig config)
    {
        var resolver = new PatResolver([
            new EnvironmentPatSource(config.PatEnv),
            new FilePatSource(config.ResolvePath(config.PatFile)),
        ]);

        var token = resolver.Resolve();
        Assert.True(token is not null, $"No token. Checked {resolver.DescribeSources()}.");
        return new AzureDevOpsGateway(token!);
    }

    /// <summary>
    /// Removes the work items a live write test created, so the suite can be
    /// pointed at a shared project without leaving debris behind.
    ///
    /// This is a test-only capability. <see cref="IBoardGateway"/> deliberately has
    /// no delete: the product must not be able to remove a work item, and adding
    /// one here to tidy up would have quietly created that path.
    /// </summary>
    internal static async Task DiscardAsync(BoardConfig config, IEnumerable<int> boardIds)
    {
        var resolver = new PatResolver([
            new EnvironmentPatSource(config.PatEnv),
            new FilePatSource(config.ResolvePath(config.PatFile)),
        ]);

        if (resolver.Resolve() is not { } token)
        {
            return;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{token}")));

        foreach (var id in boardIds.Distinct())
        {
            // Recycle bin, not destroy: recoverable if this ever runs somewhere
            // it should not have.
            using var response = await http.DeleteAsync(
                $"{config.OrgUrl}/wit/workitems/{id}?api-version={config.ApiVersion}");

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"live cleanup: could not delete work item {id} ({(int)response.StatusCode}).");
            }
        }
    }

    internal static async Task<IReadOnlyList<int>> ImportAsync(
        AzureDevOpsGateway gateway, BoardConfig config, ScratchBacklogFile backlog)
    {
        var board = await gateway.ReadAsync(config);
        Assert.True(board.IsSuccess, Explain(board.Error));

        var plan = PlanBuilder.BuildImport(config, backlog.Items, board.Value, backlog.Markdown);
        if (!plan.HasWork)
        {
            return [];
        }

        var report = await ApplyExecutor.ApplyAsync(
            gateway, config, plan,
            PlanBuilder.FingerprintBacklog(backlog.Markdown), board.Value.Fingerprint,
            progress: null);

        Assert.True(report.IsSuccess, Explain(report.Error));
        Assert.True(report.Value.AllSucceeded, Explain(report.Value));

        return [.. report.Value.Outcomes.Select(o => o.BoardId).OfType<int>()];
    }

    /// <summary>
    /// A backlog whose codes are unique to this run, so repeated runs against the
    /// same throwaway project neither collide nor need cleaning up first.
    /// </summary>
    internal static ScratchBacklogFile ScratchBacklog() => ScratchBacklogFile.Create();

    /// <summary>
    /// The codes the CLI's read-only <c>audit</c> reports as description drift.
    /// Invoked as the CLI, not through the parity driver, because this comparison
    /// is about the command a user would actually run against their board.
    /// </summary>
    internal static IReadOnlyList<string> CliAuditDescriptionDrift()
    {
        var info = new System.Diagnostics.ProcessStartInfo(PythonReference.Interpreter)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepoPaths.Root,
        };

        info.Environment["PYTHONPATH"] = Path.Combine(RepoPaths.Root, "src");
        info.ArgumentList.Add("-m");
        info.ArgumentList.Add("ado_board_sync");
        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(ConfigPath!);
        info.ArgumentList.Add("audit");

        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException("Could not start the CLI.");

        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            output.Contains("out of sync", StringComparison.Ordinal) || output.Length > 0,
            $"The CLI audit produced nothing. stderr: {errors}");

        var drift = new System.Text.RegularExpressions.Regex(
            @"^\s*x\s+(\S+)\s+description out of sync\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        return [.. drift.Matches(output).Select(m => m.Groups[1].Value)];
    }

    internal static string Explain(Error? error) =>
        error is null ? "no error" : $"{error.Kind}/{error.Code}: {error.SafeMessage}";

    internal static string Explain(ApplyReport report) =>
        string.Join("; ", report.Outcomes.Where(o => !o.Succeeded).Select(o => $"{o.Row.Label}: {o.Message}"));
}

/// <summary>A scratch backlog on disk, re-parsed whenever it is rewritten.</summary>
internal sealed class ScratchBacklogFile : IDisposable
{
    private readonly string _directory;
    private BoardConfig _config = null!;

    private ScratchBacklogFile(string directory, string path)
    {
        _directory = directory;
        Path = path;
    }

    public string Path { get; }

    public string Markdown { get; private set; } = string.Empty;

    public IReadOnlyList<Core.Backlog.BacklogItem> Items { get; private set; } = [];

    public static ScratchBacklogFile Create()
    {
        var directory = Directory.CreateTempSubdirectory("abs-live-").FullName;
        var path = System.IO.Path.Combine(directory, "backlog.md");
        var file = new ScratchBacklogFile(directory, path);

        // Unique per run: the same throwaway project is reused across runs, and
        // import matches on the code, so reused codes would plan zero work.
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + Random.Shared.Next(100, 999);
        var prefix = LiveBoard.Config().CodePrefix;

        file.Rewrite($"""
            ## Epic {stamp} — Sync smoke test

            *Created by the live connector tests. Safe to delete.*

            ### {prefix}-{stamp}1 · Sync smoke test — first issue

            Description with `inline code` and **bold**, so the converter is
            exercised on the wire and not only in a unit test.

            - First task
            - Second task with `code`

            ### {prefix}-{stamp}2 · Sync smoke test — second issue

            A second issue under the same Epic, so parenting is asserted on more
            than one child.

            - Only task
            """);

        return file;
    }

    public void Rewrite(string markdown)
    {
        File.WriteAllText(Path, markdown);
        Markdown = markdown;

        using var profile = TempBoardProfile.Create(Path, json => json["code_prefix"] = LiveBoard.Config().CodePrefix);
        var parsed = BoardConfig.Load(profile.ConfigPath);
        Assert.True(parsed.IsSuccess, LiveBoard.Explain(parsed.Error));

        _config = parsed.Value;
        Items = Core.Backlog.BacklogParser.Parse(_config, markdown);
        Assert.NotEmpty(Items);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
