using AdoBoardSync.Core.Configuration;

namespace AdoBoardSync.Infrastructure.Operations;

/// <summary>
/// How a Board profile is named in the operation history and, later, in the
/// multi-profile registry (ABSD-502). Stated once here so the two cannot drift:
/// a registry that derived the key differently would file a profile's runs under
/// a name the timeline never queries, and the history would read empty for a
/// profile that had run all day.
///
/// Derived from the organisation and project rather than the config path, so a
/// profile described in onboarding and the same profile saved to disk later are
/// one profile rather than two.
/// </summary>
public static class ProfileKey
{
    public static string For(BoardConfig config) => For(config.Org, config.Project);

    // Lower-cased because Azure DevOps treats org and project names
    // case-insensitively: "Contoso/Board" and "contoso/board" are one board, and
    // filing them apart would split one profile's history down the middle.
    public static string For(string org, string project) => $"{org}/{project}".ToLowerInvariant();
}
