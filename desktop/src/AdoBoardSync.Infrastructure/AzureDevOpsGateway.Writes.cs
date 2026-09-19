using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure;

/// <summary>
/// The write half of the connector: work-item create (with the never-retried
/// hierarchy-reverse parent link), update, delete, and the iteration/team calls the
/// sprints command needs. Transport, retry and error mapping live in the main file.
/// </summary>
public sealed partial class AzureDevOpsGateway
{
    public async Task<Result<int>> CreateAsync(
        BoardConfig config,
        string workItemType,
        string title,
        string descriptionHtml,
        int? parentId,
        CancellationToken cancellationToken = default)
    {
        var operations = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = title },
        };

        if (!string.IsNullOrEmpty(descriptionHtml))
        {
            operations.Add(new { op = "add", path = "/fields/System.Description", value = descriptionHtml });
        }

        if (parentId is { } parent)
        {
            operations.Add(new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "System.LinkTypes.Hierarchy-Reverse",
                    url = $"{config.OrgUrl}/wit/workItems/{parent}",
                },
            });
        }

        var url =
            $"{config.BaseUrl}/wit/workitems/{Uri.EscapeDataString("$" + workItemType)}" +
            $"?api-version={config.ApiVersion}";

        // Never retriable: a create that succeeded before the connection dropped
        // would duplicate the work item on a second attempt.
        var response = await SendAsync(
            config, HttpMethod.Post, url, JsonSerializer.Serialize(operations),
            JsonPatchContentType, retriable: false, cancellationToken);

        if (response.IsFailure)
        {
            return response.Error!;
        }

        var parsed = ParseJson(response.Value, "board.create");
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        using var document = parsed.Value;
        return document.RootElement.TryGetProperty("id", out var id)
            ? id.GetInt32()
            : Error.SourceFailure("board.create", $"Created {workItemType} but the response carried no id.");
    }

    public async Task<Result<bool>> UpdateAsync(
        BoardConfig config,
        int workItemId,
        IReadOnlyList<BoardFieldChange> changes,
        CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
        {
            return true;
        }

        var operations = changes
            .Select(c => new { op = "add", path = $"/fields/{c.Field}", value = c.Value })
            .ToArray();

        var url = $"{config.OrgUrl}/wit/workitems/{workItemId}?api-version={config.ApiVersion}";

        // Fixed-value field writes reach the same end state applied once or twice,
        // so a transport retry is safe. This gateway sends no array-append patch.
        var response = await SendAsync(
            config, HttpMethod.Patch, url, JsonSerializer.Serialize(operations),
            JsonPatchContentType, retriable: true, cancellationToken);

        return response.IsFailure ? response.Error! : true;
    }

    public async Task<Result<bool>> DeleteAsync(
        BoardConfig config,
        int workItemId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{config.OrgUrl}/wit/workitems/{workItemId}?api-version={config.ApiVersion}";

        // Retriable like a read: the repeat either deletes again (the first try
        // never landed) or 404s (it did) — nothing duplicates and nothing corrupts.
        var response = await SendAsync(
            config, HttpMethod.Delete, url, null, null, retriable: true, cancellationToken);

        return response.IsFailure ? response.Error! : true;
    }

    public async Task<Result<IterationNode>> EnsureIterationAsync(
        BoardConfig config,
        string name,
        string? start,
        string? finish,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = $"{config.BaseUrl}/wit/classificationnodes/iterations";
        var encoded = Uri.EscapeDataString(name);

        // Asked for first, exactly as the CLI does: an existing node is reported,
        // never recreated, which is what makes running sprints twice harmless.
        var existing = await SendAsync(
            config, HttpMethod.Get, $"{baseUrl}/{encoded}?api-version={config.ApiVersion}",
            null, null, retriable: true, cancellationToken);

        if (existing.IsSuccess)
        {
            return new IterationNode(name, ReadIdentifier(existing.Value), "exists");
        }

        var body = JsonSerializer.Serialize(IterationBody(name, start, finish));

        var created = await SendAsync(
            config, HttpMethod.Post, $"{baseUrl}?api-version={config.ApiVersion}",
            body, "application/json", retriable: false, cancellationToken);

        if (created.IsSuccess)
        {
            return new IterationNode(name, ReadIdentifier(created.Value), "created");
        }

        // A 409 means the node appeared between the read and the create. The CLI
        // patches it so the configured dates are authoritative either way; the
        // patch is a fixed-value write on a named node, so it may be retried.
        if (created.Error!.Kind != ErrorKind.Conflict)
        {
            return created.Error!;
        }

        var patched = await SendAsync(
            config, HttpMethod.Patch, $"{baseUrl}/{encoded}?api-version={config.ApiVersion}",
            body, "application/json", retriable: true, cancellationToken);

        return patched.IsFailure
            ? patched.Error!
            : new IterationNode(name, ReadIdentifier(patched.Value), "exists; dates synced");
    }

    public async Task<Result<string?>> DefaultTeamAsync(
        BoardConfig config, CancellationToken cancellationToken = default)
    {
        var url =
            $"{config.OrgUrl}/projects/{Uri.EscapeDataString(config.Project)}/teams" +
            $"?api-version={config.ApiVersion}";

        var response = await SendAsync(
            config, HttpMethod.Get, url, null, null, retriable: true, cancellationToken);
        if (response.IsFailure)
        {
            return response.Error!;
        }

        var parsed = ParseJson(response.Value, "board.teams");
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        using var document = parsed.Value;
        if (!document.RootElement.TryGetProperty("value", out var teams) ||
            teams.ValueKind != JsonValueKind.Array)
        {
            return (string?)null;
        }

        // The "<Project> Team" default first, then whatever came back first — the
        // CLI's own guess, so both surfaces pick the same team on the same board.
        var wanted = $"{config.Project} team";
        string? first = null;
        foreach (var team in teams.EnumerateArray())
        {
            if (!team.TryGetProperty("name", out var nameElement) ||
                nameElement.GetString() is not { } teamName)
            {
                continue;
            }

            if (string.Equals(teamName, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return teamName;
            }

            first ??= teamName;
        }

        return first;
    }

    public async Task<Result<bool>> AddTeamIterationAsync(
        BoardConfig config,
        string team,
        string identifier,
        CancellationToken cancellationToken = default)
    {
        // Team- and project-scoped, not org-scoped: teamsettings/iterations 404s
        // without both segments in the route.
        var url =
            $"https://dev.azure.com/{Uri.EscapeDataString(config.Org)}/" +
            $"{Uri.EscapeDataString(config.Project)}/{Uri.EscapeDataString(team)}" +
            $"/_apis/work/teamsettings/iterations?api-version={config.ApiVersion}";

        var body = JsonSerializer.Serialize(new { id = identifier });

        // Idempotent by Azure DevOps' own contract — see the 400 below — so a
        // repeat after a dropped connection is harmless.
        var response = await SendAsync(
            config, HttpMethod.Post, url, body, "application/json", retriable: true, cancellationToken);

        if (response.IsSuccess)
        {
            return true;
        }

        // 400 is what Azure DevOps answers when the iteration is already one of the
        // team's selected sprints. The state the caller asked for is the state that
        // holds, so this is success, not a failure to report.
        return response.Error!.Code == "board.request_failed" &&
               response.Error.SafeMessage.Contains("returned 400", StringComparison.Ordinal)
            ? true
            : response.Error!;
    }

    private static object IterationBody(string name, string? start, string? finish)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(start))
        {
            attributes["startDate"] = $"{start}T00:00:00Z";
        }

        if (!string.IsNullOrWhiteSpace(finish))
        {
            attributes["finishDate"] = $"{finish}T00:00:00Z";
        }

        return attributes.Count == 0
            ? new { name }
            : new { name, attributes };
    }

    private static string? ReadIdentifier(string payload)
    {
        var parsed = ParseJson(payload, "board.iteration");
        if (parsed.IsFailure)
        {
            return null;
        }

        using var document = parsed.Value;
        return document.RootElement.TryGetProperty("identifier", out var identifier) &&
               identifier.ValueKind == JsonValueKind.String
            ? identifier.GetString()
            : null;
    }

}
