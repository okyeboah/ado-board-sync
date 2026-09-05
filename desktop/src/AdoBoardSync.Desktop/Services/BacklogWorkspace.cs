using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;

namespace AdoBoardSync.Desktop.Services;

/// <summary>
///     One open Board profile: the validated config, the backlog Markdown it points
///     at, the parsed items, and the offline markup audit over them.
///     <see cref="MarkupProblemCount" /> is what Apply's markup gate reads: malformed
///     description markup blocks Apply until it is resolved (PRD-AC-03). It is part
///     of the workspace rather than recomputed at the gate so there is one audit per
///     open profile and every surface — tree badges, header chip, problems card,
///     Apply — answers from the same number.
///     <see cref="ConfigPath" /> is null for a profile described in onboarding and not
///     saved. That is a supported state: the config is complete, it just has no file.
///     This record holds no file API of its own (ABSD-107). Reading, saving and
///     exporting belong to <see cref="ProfileLoader" />, which owns the
///     <see cref="IBacklogFileStore" /> seam — so a view-model test can drive the
///     whole load path with no disk.
/// </summary>
public sealed record BacklogWorkspace(
    string? ConfigPath,
    BoardConfig Config,
    string BacklogPath,
    string Markdown,
    IReadOnlyList<BacklogItem> Items,
    int MarkupProblemCount,
    FileStamp Stamp)
{
    public string ProfileName => $"{Config.Org}/{Config.Project}";

    public string OriginDisplay => ConfigPath ?? $"{ProfileName} (unsaved profile)";

    /// <summary>
    ///     Identifies this profile in the operation history and the profile registry.
    ///     Derived from the organisation and project rather than the config path, so a
    ///     profile described in onboarding and the same profile saved to disk later
    ///     are one profile, not two (ABSD-502).
    ///     The formula is not repeated here. It lived in two places until ABSD-502,
    ///     and two spellings of a history key is a timeline that reads empty for a
    ///     profile that has been running all day.
    /// </summary>
    public string ProfileKey => AdoBoardSync.Infrastructure.Operations.ProfileKey.For(Config);
}
