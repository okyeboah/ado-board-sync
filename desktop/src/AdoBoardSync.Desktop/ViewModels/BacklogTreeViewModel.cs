using System.Collections.ObjectModel;
using System.ComponentModel;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
///     The backlog rail and the editor's model: the Epic→Issue tree, the counts the
///     header chips show, and the dirty-tracking that feeds the unsaved-edits gate.
///
///     Extracted from the shell because it is a self-contained mechanism — rebuild
///     from a parse, keep the selection by identity, collect dirty buffers for the
///     splice — that four surfaces read and only the editor writes. The shell keeps
///     the orchestration; this owns the tree.
/// </summary>
public sealed partial class BacklogTreeViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBacklog))]
    private int _epicCount;

    [ObservableProperty]
    private int _issueCount;

    [ObservableProperty]
    private int _taskCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    [NotifyPropertyChangedFor(nameof(MarkupSummary))]
    private int _problemCount;

    /// <summary>
    ///     True while any editor buffer differs from the file. While it is set,
    ///     the Plan gate refuses to run: a Plan is computed from the file, and
    ///     the file is the source of truth.
    /// </summary>
    [ObservableProperty]
    private bool _hasUnsavedEdits;

    /// <summary>
    ///     Two-way: the tree view writes the selection the user clicked; the shell
    ///     reads it to scope the agent and to preserve it across a rebuild.
    /// </summary>
    [ObservableProperty]
    private BacklogNodeViewModel? _selectedNode;

    public ObservableCollection<BacklogNodeViewModel> Nodes { get; } = [];

    public bool HasBacklog => EpicCount > 0;

    public bool HasProblems => ProblemCount > 0;

    public string MarkupSummary => ProblemCount switch
    {
        0 => "✓ Markup clean",
        1 => "! 1 markup problem",
        var n => $"! {n} markup problems",
    };

    /// <summary>
    ///     The counts the header chips show, read after a rebuild rather than bound:
    ///     the shell's status line composes them with facts the tree does not hold.
    /// </summary>
    public (int Epics, int Issues, int Tasks) Counts => (EpicCount, IssueCount, TaskCount);

    /// <summary>What a re-parse cannot change: the level, the issue code, the heading text.</summary>
    public sealed record NodeIdentity(bool IsEpic, string? Code, string Title)
    {
        public static NodeIdentity? Of(BacklogNodeViewModel? node) =>
            node is null ? null : new(node.IsEpic, node.Item.Code, node.Item.Title);
    }

    /// <summary>The selection as an identity, to hand back to <see cref="Rebuild" />.</summary>
    public NodeIdentity? SelectionIdentity => NodeIdentity.Of(SelectedNode);

    /// <summary>
    ///     Rebuilds the tree from a parsed workspace. BacklogParser returns a flat
    ///     list in document order: an Epic owns every Issue that follows it until the
    ///     next Epic. An Issue written above the first Epic is dropped upstream,
    ///     exactly as the CLI drops it.
    /// </summary>
    public void Rebuild(BacklogWorkspace workspace, NodeIdentity? preferredSelection = null)
    {
        Nodes.Clear();

        BacklogNodeViewModel? epic = null;
        foreach (var item in workspace.Items)
        {
            var node = new BacklogNodeViewModel(item);
            if (item.Level == BacklogLevel.Epic)
            {
                epic = node;
                Nodes.Add(node);
            }
            else
            {
                epic?.Children.Add(node);
            }
        }

        HookDirtyTracking(Nodes);

        SelectedNode = FindNode(Nodes, preferredSelection)
            ?? Nodes.FirstOrDefault()?.Children.FirstOrDefault()
            ?? Nodes.FirstOrDefault();

        EpicCount = workspace.Items.Count(i => i.Level == BacklogLevel.Epic);
        IssueCount = workspace.Items.Count(i => i.Level == BacklogLevel.Issue);
        TaskCount = workspace.Items.Sum(i => i.Bullets.Count);
        ProblemCount = CountProblems(Nodes);
        HasUnsavedEdits = false;
    }

    /// <summary>The dirty buffers ready for last-to-first splicing by the shell's save.</summary>
    public List<(BacklogItem Item, string Text)> CollectEdits()
    {
        var edits = new List<(BacklogItem, string)>();
        Collect(Nodes);
        return edits.OrderByDescending(edit => edit.Item1.DescriptionStart).ToList();

        void Collect(IEnumerable<BacklogNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirty)
                {
                    edits.Add((node.Item, node.Source));
                }

                Collect(node.Children);
            }
        }
    }

    public void Clear()
    {
        Nodes.Clear();
        SelectedNode = null;
        EpicCount = 0;
        IssueCount = 0;
        TaskCount = 0;
        ProblemCount = 0;
        HasUnsavedEdits = false;
    }

    /// <summary>Dirty nodes announce themselves so the header chip and the Plan gate stay current.</summary>
    private void HookDirtyTracking(IEnumerable<BacklogNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.PropertyChanged += OnNodeChanged;
            HookDirtyTracking(node.Children);
        }
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BacklogNodeViewModel.IsDirty))
        {
            HasUnsavedEdits = AnyDirty(Nodes);
        }
        else if (e.PropertyName == nameof(BacklogNodeViewModel.Problems))
        {
            // The header chip answers from the same live audit the tree badges do:
            // what the user is looking at is what check-html would see. While the
            // buffer is dirty, Apply is refused anyway; on save the workspace
            // re-audits the file and the two agree again.
            ProblemCount = CountProblems(Nodes);
        }
    }

    private static bool AnyDirty(IEnumerable<BacklogNodeViewModel> nodes) =>
        nodes.Any(node => node.IsDirty || AnyDirty(node.Children));

    private static int CountProblems(IEnumerable<BacklogNodeViewModel> nodes) =>
        nodes.Sum(n => n.Problems.Count + CountProblems(n.Children));

    /// <summary>Finds a node again after a rebuild, by the identity that survives a re-parse.</summary>
    private static BacklogNodeViewModel? FindNode(
        IEnumerable<BacklogNodeViewModel> nodes, NodeIdentity? wanted)
    {
        if (wanted is not { } target)
        {
            return null;
        }

        foreach (var node in nodes)
        {
            if (NodeIdentity.Of(node) == target)
            {
                return node;
            }

            var found = FindNode(node.Children, wanted);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
