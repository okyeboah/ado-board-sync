using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// The first-run choice: open a profile you already have, or describe one here.
/// Both produce the same schema-validated <see cref="Core.Configuration.BoardConfig"/>;
/// saving the second to disk is optional.
/// </summary>
public sealed partial class OnboardingViewModel(IBacklogFileStore store, ProfileLoader loader)
    : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private string _organisation = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private string _project = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private string _codePrefix = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyPropertyChangedFor(nameof(BacklogDisplay))]
    [NotifyPropertyChangedFor(nameof(CanScaffold))]
    private string _backlogPath = string.Empty;

    [ObservableProperty]
    private string _team = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    /// <summary>
    ///     Why opening an existing board.config.json failed, shown on this screen
    ///     beside the route that produced it — the first-run screen stays up, so the
    ///     message is where the file was chosen, not on a blank error page.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportError))]
    private string? _importErrorText;

    /// <summary>Also write the profile out, so the CLI can share it.</summary>
    [ObservableProperty]
    private bool _alsoSaveToDisk;

    [ObservableProperty]
    private string _savePath = string.Empty;

    /// <summary>
    ///     The chosen backlog file does not exist yet. When set, opening the profile
    ///     writes a starter backlog there first; when cleared, a missing file is the
    ///     error it would be in the CLI.
    /// </summary>
    [ObservableProperty]
    private bool _scaffoldStarterBacklog = true;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasImportError => !string.IsNullOrEmpty(ImportErrorText);

    public bool CanScaffold =>
        !string.IsNullOrWhiteSpace(BacklogPath) && !store.Exists(BacklogPath);

    public bool CanCreate =>
        !string.IsNullOrWhiteSpace(Organisation) &&
        !string.IsNullOrWhiteSpace(Project) &&
        !string.IsNullOrWhiteSpace(CodePrefix) &&
        !string.IsNullOrWhiteSpace(BacklogPath);

    public string BacklogDisplay =>
        string.IsNullOrWhiteSpace(BacklogPath) ? "No backlog file chosen" : BacklogPath;

    /// <summary>Suggests a save path beside the backlog, where the CLI looks by default.</summary>
    public void SuggestSavePath()
    {
        if (!string.IsNullOrWhiteSpace(SavePath) || string.IsNullOrWhiteSpace(BacklogPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(BacklogPath));
        if (!string.IsNullOrEmpty(directory))
        {
            SavePath = Path.Combine(directory, "board.config.json");
        }
    }

    /// <summary>Validates the form and opens the profile it describes.</summary>
    public async Task<Result<BacklogWorkspace>> CreateProfileAsync(
        CancellationToken cancellationToken = default)
    {
        var draft = new BoardProfileDraft
        {
            Organisation = Organisation,
            Project = Project,
            CodePrefix = CodePrefix,
            BacklogPath = BacklogPath,
            Team = Team,
        };

        string? savedTo = null;
        if (AlsoSaveToDisk)
        {
            if (string.IsNullOrWhiteSpace(SavePath))
            {
                ErrorText = "Choose where to save the profile, or clear the save option.";
                return Error.Validation("profile.save_path_required", ErrorText);
            }

            var saved = draft.SaveTo(store, SavePath);
            if (saved.IsFailure)
            {
                ErrorText = $"{saved.Error!.SafeMessage} ({saved.Error.Code})";
                return saved.Error;
            }

            savedTo = saved.Value;
        }

        var config = draft.Build(store);
        if (config.IsFailure)
        {
            // A missing backlog file is a decision here, not a dead end: ticked,
            // the scaffold writes a working starter and the build is retried;
            // unticked, it stays the same error the CLI gives.
            if (config.Error!.Code != "profile.backlog_missing" || !ScaffoldStarterBacklog)
            {
                ErrorText = $"{config.Error.SafeMessage} ({config.Error.Code})";
                return config.Error;
            }

            // The config upper-cases the prefix (see BoardProfileDraft.ToJson),
            // and the heading regex matches it case-sensitively — the starter's
            // headings must use exactly what the config will hold.
            var written = StarterBacklog.Write(
                store, BacklogPath.Trim(), CodePrefix.Trim().ToUpperInvariant());
            if (written.IsFailure)
            {
                ErrorText = $"{written.Error!.SafeMessage} ({written.Error.Code})";
                return written.Error;
            }

            config = draft.Build(store);
            if (config.IsFailure)
            {
                ErrorText = $"{config.Error!.SafeMessage} ({config.Error.Code})";
                return config.Error;
            }
        }

        var workspace = await loader.FromConfigAsync(config.Value, savedTo, cancellationToken)
            .ConfigureAwait(false);
        if (workspace.IsFailure)
        {
            ErrorText = $"{workspace.Error!.SafeMessage} ({workspace.Error.Code})";
            return workspace.Error;
        }

        ErrorText = null;
        return workspace.Value;
    }
}
