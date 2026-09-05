using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// A board that lives in a list, so the Plan Builder and the Apply Executor can be
/// driven through every branch — the failure ones included — without a token.
///
/// Apply runs its independent writes concurrently, so every mutation records
/// under a lock and id allocation stays unique under racing creates.
/// </summary>
internal sealed class FakeBoardGateway : IBoardGateway
{
    private readonly object _gate = new();
    private int _nextId = 1000;

    public List<BoardWorkItem> Items { get; } = [];

    public List<(string Type, string Title, string Description, int? ParentId)> Created { get; } = [];

    public List<(int Id, IReadOnlyList<BoardFieldChange> Changes)> Updated { get; } = [];

    public List<int> Deleted { get; } = [];

    /// <summary>Iteration nodes ensure-iteration was asked for, in call order.</summary>
    public List<(string Name, string? Start, string? Finish)> Iterations { get; } = [];

    /// <summary>Iteration identifiers added to a team's selected sprints.</summary>
    public List<(string Team, string Identifier)> TeamIterations { get; } = [];

    /// <summary>What <see cref="DefaultTeamAsync" /> answers. Null models a project with no team.</summary>
    public string? DefaultTeam { get; set; } = "Fixture Team";

    /// <summary>Set to make every ensure-iteration fail.</summary>
    public Error? IterationError { get; set; }

    /// <summary>Set to make the next read fail.</summary>
    public Error? ReadError { get; set; }

    /// <summary>Set to make every create fail.</summary>
    public Error? CreateError { get; set; }

    /// <summary>Set to make every update fail.</summary>
    public Error? UpdateError { get; set; }

    /// <summary>Set to make every delete fail.</summary>
    public Error? DeleteError { get; set; }

    /// <summary>Swapped in between the Plan read and the Apply read, to fake a race.</summary>
    public Func<BoardSnapshot, BoardSnapshot>? MutateOnRead { get; set; }

    public int ReadCount { get; private set; }

    public Task<Result<BoardSnapshot>> ReadAsync(
        BoardConfig config, CancellationToken cancellationToken = default)
    {
        List<BoardWorkItem> copy;
        lock (_gate)
        {
            ReadCount++;
            if (ReadError is { } error)
            {
                return Task.FromResult<Result<BoardSnapshot>>(error);
            }

            copy = [.. Items];
        }

        var snapshot = BoardSnapshot.From(copy);
        if (MutateOnRead is { } mutate)
        {
            snapshot = mutate(snapshot);
        }

        return Task.FromResult<Result<BoardSnapshot>>(snapshot);
    }

    public Task<Result<int>> CreateAsync(
        BoardConfig config,
        string workItemType,
        string title,
        string descriptionHtml,
        int? parentId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (CreateError is { } error)
            {
                return Task.FromResult<Result<int>>(error);
            }

            var id = _nextId++;
            Created.Add((workItemType, title, descriptionHtml, parentId));
            Items.Add(new BoardWorkItem
            {
                Id = id,
                Title = title,
                WorkItemType = workItemType,
                Description = descriptionHtml,
                ParentId = parentId,
            });

            return Task.FromResult<Result<int>>(id);
        }
    }

    public Task<Result<bool>> UpdateAsync(
        BoardConfig config,
        int workItemId,
        IReadOnlyList<BoardFieldChange> changes,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (UpdateError is { } error)
            {
                return Task.FromResult<Result<bool>>(error);
            }

            Updated.Add((workItemId, changes));

            // The write is applied, not merely recorded. Create and Delete already
            // mutate Items; an Update that did not left the fake unable to answer
            // "what does the board look like now?", which is the only question a
            // parity comparison against the CLI can ask.
            var index = Items.FindIndex(i => i.Id == workItemId);
            if (index >= 0)
            {
                Items[index] = Apply(Items[index], changes);
            }

            return Task.FromResult<Result<bool>>(true);
        }
    }

    /// <summary>
    /// Writes one patch onto an item, by the Azure DevOps reference names the
    /// gateway sends. A field this fake does not know is ignored rather than
    /// throwing: the real board accepts fields the port has no opinion about, and
    /// a fake that refused them would fail tests the board would pass.
    /// </summary>
    private static BoardWorkItem Apply(BoardWorkItem item, IReadOnlyList<BoardFieldChange> changes)
    {
        foreach (var change in changes)
        {
            item = change.Field switch
            {
                BoardFieldChange.TitleField => item with { Title = change.Value },
                BoardFieldChange.DescriptionField => item with { Description = change.Value },
                BoardFieldChange.StateField => item with { State = change.Value },
                BoardFieldChange.IterationPathField => item with { IterationPath = change.Value },

                // A write sends the identity as a string; the board echoes it back
                // as the unique name, so the other two facets are cleared rather
                // than left describing whoever held the item before.
                BoardFieldChange.AssignedToField => item with
                {
                    AssignedTo = change.Value,
                    AssignedToId = string.Empty,
                    AssignedToDisplayName = string.Empty,
                },
                _ => item,
            };
        }

        return item;
    }

    public Task<Result<bool>> DeleteAsync(
        BoardConfig config,
        int workItemId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (DeleteError is { } error)
            {
                return Task.FromResult<Result<bool>>(error);
            }

            Deleted.Add(workItemId);
            Items.RemoveAll(i => i.Id == workItemId);
            return Task.FromResult<Result<bool>>(true);
        }
    }

    public Task<Result<IterationNode>> EnsureIterationAsync(
        BoardConfig config,
        string name,
        string? start,
        string? finish,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (IterationError is { } error)
            {
                return Task.FromResult<Result<IterationNode>>(error);
            }

            Iterations.Add((name, start, finish));

            // A deterministic identifier so a test can assert which node reached
            // the team call without inventing a GUID of its own.
            return Task.FromResult<Result<IterationNode>>(
                new IterationNode(name, $"id-{name}", "created"));
        }
    }

    public Task<Result<string?>> DefaultTeamAsync(
        BoardConfig config, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<string?>>(DefaultTeam);

    public Task<Result<bool>> AddTeamIterationAsync(
        BoardConfig config,
        string team,
        string identifier,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            TeamIterations.Add((team, identifier));
            return Task.FromResult<Result<bool>>(true);
        }
    }

    /// <summary>
    /// Seeds one item directly onto the board, bypassing Create's recorded path.
    /// Tasks pass their parent id here, as the real batched read carries it.
    /// </summary>
    public int Seed(
        string workItemType,
        string title,
        string description = "",
        int? parentId = null,
        string state = "New",
        string assignedTo = "",
        string iterationPath = "")
    {
        lock (_gate)
        {
            var id = _nextId++;
            Items.Add(new BoardWorkItem
            {
                Id = id,
                Title = title,
                WorkItemType = workItemType,
                Description = description,
                ParentId = parentId,
                State = state,
                AssignedTo = assignedTo,
                IterationPath = iterationPath,
            });
            return id;
        }
    }

    /// <summary>Replaces one seeded item, for a test that needs to move it after the fact.</summary>
    public void Update(int id, Func<BoardWorkItem, BoardWorkItem> change)
    {
        lock (_gate)
        {
            var index = Items.FindIndex(i => i.Id == id);
            if (index >= 0)
            {
                Items[index] = change(Items[index]);
            }
        }
    }
}
