using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure;

/// <summary>
///     The Azure DevOps REST adapter — the only type in the solution that knows the
///     API exists. Ported from the CLI's <c>client.py</c>, including its retry
///     contract: GET and the WIQL POST retry on transport failures and 429/502/503/504;
///     everything else retries only on 429, which is rejected before any side effect.
///     A create is never retried, since the first attempt may already have succeeded.
///     The PAT lives only in the Authorization header — never logged, never written
///     to disk, never in an error message.
/// </summary>
public sealed partial class AzureDevOpsGateway : IBoardGateway, IDisposable
{
    private const string JsonPatchContentType = "application/json-patch+json";

    private static readonly string[] ItemFields =
    [
        "System.Id",
        "System.Title",
        "System.Description",
        "System.WorkItemType",
        // Parent comes back on the same batched get, so no command ever needs a
        // per-item relations expand to ask "which Epic owns this Task" — the
        // per-item round trip that made the CLI's early hierarchy walks slow.
        "System.Parent",
        // The same argument for the three fields the lifecycle commands plan
        // against: close-children needs every descendant's state, assign needs to
        // know who already owns an item, and sprints needs each item's current
        // iteration. Fetched per item at plan time they would be hundreds of round
        // trips; here they cost nothing beyond a longer field list.
        "System.State",
        "System.AssignedTo",
        "System.IterationPath"
    ];

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public AzureDevOpsGateway(string personalAccessToken, HttpMessageHandler? handler = null)
    {
        // Azure DevOps answers a bad or expired PAT with a redirect to its sign-in
        // page. Following it turns an authentication failure into 200 and a page of
        // HTML, so the status that carries the diagnosis never reaches Describe.
        // The CLI's http.client does not redirect either, so this keeps parity.
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        _ownsClient = true;

        // Azure DevOps takes the PAT as the password of an empty-username Basic pair.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{personalAccessToken}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<Result<BoardSnapshot>> ReadAsync(
        BoardConfig config, CancellationToken cancellationToken = default)
    {
        // One WIQL query and one chain of 200-id batch gets cover every level the
        // plans need — Epics, Issues and their Tasks together. Reading Epics and
        // Issues in one pass and Tasks in another would double the round trips
        // every command pays before it can plan anything.
        var epicType = WiqlLiteral(config.Types["epic"]);
        var storyType = WiqlLiteral(config.Types["story"]);
        var taskType = WiqlLiteral(config.Types["task"]);
        var project = WiqlLiteral(config.Project);

        var where =
            $"[System.TeamProject]={project} AND [System.WorkItemType] IN ({epicType},{storyType},{taskType})";

        var ids = await WiqlAsync(config, where, cancellationToken);
        if (ids.IsFailure) return ids.Error!;

        var items = new List<BoardWorkItem>();

        // Batched at 200, the documented maximum for the batch endpoint.
        foreach (var chunk in ids.Value.Chunk(200))
        {
            var url =
                $"{config.OrgUrl}/wit/workitems?ids={string.Join(',', chunk)}" +
                $"&fields={string.Join(',', ItemFields)}&api-version={config.ApiVersion}";

            var response = await SendAsync(
                config, HttpMethod.Get, url, null, null, true, cancellationToken);
            if (response.IsFailure) return response.Error!;

            var parsed = ParseJson(response.Value, "board.batch_get");
            if (parsed.IsFailure) return parsed.Error!;

            using var document = parsed.Value;
            if (!document.RootElement.TryGetProperty("value", out var value))
                return Error.SourceFailure("board.batch_get", "Batch get returned no \"value\" array.");

            foreach (var element in value.EnumerateArray())
            {
                if (!element.TryGetProperty("id", out var idElement) ||
                    !element.TryGetProperty("fields", out var fields))
                    continue;

                items.Add(new BoardWorkItem
                {
                    Id = idElement.GetInt32(),
                    Title = ReadString(fields, "System.Title"),
                    WorkItemType = ReadString(fields, "System.WorkItemType"),
                    Description = ReadString(fields, "System.Description"),
                    ParentId = ReadNullableInt(fields, "System.Parent"),
                    State = ReadString(fields, "System.State"),
                    AssignedTo = ReadIdentityFacet(fields, "System.AssignedTo", "uniqueName", "id"),
                    AssignedToId = ReadIdentityFacet(fields, "System.AssignedTo", "id"),
                    AssignedToDisplayName = ReadIdentityFacet(fields, "System.AssignedTo", "displayName"),
                    IterationPath = ReadString(fields, "System.IterationPath")
                });
            }
        }

        return BoardSnapshot.From(items);
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    private async Task<Result<IReadOnlyList<int>>> WiqlAsync(
        BoardConfig config, string where, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            query = $"SELECT [System.Id] FROM WorkItems WHERE {where}"
        });

        var url = $"{config.BaseUrl}/wit/wiql?api-version={config.ApiVersion}";

        // A WIQL POST reads; it is idempotent despite the verb.
        var response = await SendAsync(
            config, HttpMethod.Post, url, body, "application/json", true, cancellationToken);
        if (response.IsFailure) return response.Error!;

        var parsed = ParseJson(response.Value, "board.wiql");
        if (parsed.IsFailure) return parsed.Error!;

        using var document = parsed.Value;
        if (!document.RootElement.TryGetProperty("workItems", out var workItems))
            return Error.SourceFailure("board.wiql", "WIQL returned no \"workItems\" array.");

        var ids = new List<int>();
        foreach (var element in workItems.EnumerateArray())
            if (element.TryGetProperty("id", out var id))
                ids.Add(id.GetInt32());

        return ids;
    }

    private async Task<Result<string>> SendAsync(
        BoardConfig config,
        HttpMethod method,
        string url,
        string? body,
        string? contentType,
        bool retriable,
        CancellationToken cancellationToken)
    {
        var attempts = 1 + Math.Max(0, config.MaxRetries);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue(contentType ?? "application/json") { CharSet = "utf-8" };
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.Timeout));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException ||
                                       (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Retry only what is safe to repeat.
                if (retriable && attempt < attempts - 1)
                {
                    await Task.Delay(Backoff(config, attempt), cancellationToken);
                    continue;
                }

                return Error.SourceFailure("board.unreachable", $"Request to Azure DevOps failed: {ex.Message}");
            }

            using (response)
            {
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    // A success status carrying HTML is still a sign-in page, which
                    // would otherwise surface as a JSON parse exception thrown clean
                    // through the Result contract.
                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (mediaType is not null &&
                        !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
                        return Error.Authorization(
                            "board.unauthorized",
                            "Azure DevOps returned a sign-in page instead of data. Check that the personal " +
                            "access token is current and has Work Items (Read & write) scope.");

                    return string.IsNullOrWhiteSpace(payload) ? "{}" : payload;
                }

                var status = (int)response.StatusCode;

                // 429 is rejected before any side effect, so it retries for every
                // method. 5xx is only safe for idempotent calls.
                var canRetry = status == 429 || (retriable && status is 502 or 503 or 504);
                if (canRetry && attempt < attempts - 1)
                {
                    await Task.Delay(RetryDelay(config, response, attempt), cancellationToken);
                    continue;
                }

                return Describe(response.StatusCode, status, payload);
            }
        }

        return Error.SourceFailure("board.retries_exhausted", "Azure DevOps did not return a usable response.");
    }

    private static Error Describe(HttpStatusCode code, int status, string payload)
    {
        // The payload can echo request content; truncate rather than surface it
        // whole in a UI banner.
        var detail = payload.Length > 300 ? payload[..300] + "…" : payload;

        return code switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation or
                HttpStatusCode.Found or HttpStatusCode.Redirect
                or HttpStatusCode.MovedPermanently => Error.Authorization(
                    "board.unauthorized",
                    "Azure DevOps rejected the personal access token. Check that it is current and has Work Items (Read & write) scope."),
            HttpStatusCode.Forbidden => Error.Authorization(
                "board.forbidden",
                "The personal access token is valid but lacks permission for this project."),
            HttpStatusCode.NotFound => Error.NotFound(
                "board.not_found",
                "Azure DevOps returned 404. Check the organisation and project names in the profile."),
            // Distinguished because ensure-iteration acts on it: a 409 means the
            // node appeared between the read and the create, which is recoverable
            // by patching, not a failure to report.
            HttpStatusCode.Conflict => Error.Conflict(
                "board.conflict",
                $"Azure DevOps returned 409: {detail}"),
            HttpStatusCode.TooManyRequests => Error.RateLimited(
                "board.rate_limited",
                "Azure DevOps is rate-limiting this account. Try again shortly."),
            _ => Error.SourceFailure("board.request_failed", $"Azure DevOps returned {status}: {detail}")
        };
    }

    private static TimeSpan Backoff(BoardConfig config, int attempt)
    {
        return TimeSpan.FromSeconds(config.Backoff * Math.Pow(2, attempt));
    }

    private static TimeSpan RetryDelay(BoardConfig config, HttpResponseMessage response, int attempt)
    {
        // Honour Retry-After when the service supplies one.
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta;

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }

        return Backoff(config, attempt);
    }

    private static Result<JsonDocument> ParseJson(string payload, string code)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            return Error.SourceFailure(code, $"Azure DevOps returned a body that is not JSON: {ex.Message}");
        }
    }

    private static string ReadString(JsonElement fields, string name)
    {
        return fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    ///     One facet of an identity field. Azure DevOps returns
    ///     <c>System.AssignedTo</c> as an object on a read and takes a string on a
    ///     write; the CLI's <c>_assignee_value</c> writes <c>uniqueName</c> (falling
    ///     back to <c>id</c>), while its <c>_assignee_matches</c> compares against
    ///     <c>uniqueName</c>, <c>id</c> and <c>displayName</c> alike. All three are
    ///     therefore read, and the caller says which one it wants — collapsing them
    ///     here is what would make <c>assign</c> plan a write the CLI would not.
    ///     The first key that holds a non-empty string wins, so passing several
    ///     expresses a fallback order.
    /// </summary>
    private static string ReadIdentityFacet(JsonElement fields, string name, params string[] keys)
    {
        if (!fields.TryGetProperty(name, out var value)) return string.Empty;

        // A board that answers with a bare string has no facets to choose between.
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;

        if (value.ValueKind != JsonValueKind.Object) return string.Empty;

        foreach (var key in keys)
            if (value.TryGetProperty(key, out var candidate) &&
                candidate.ValueKind == JsonValueKind.String &&
                candidate.GetString() is { Length: > 0 } text)
                return text;

        return string.Empty;
    }

    private static int? ReadNullableInt(JsonElement fields, string name)
    {
        return fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
    }

    /// <summary>Quotes a value for WIQL, doubling any embedded single quote.</summary>
    private static string WiqlLiteral(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}