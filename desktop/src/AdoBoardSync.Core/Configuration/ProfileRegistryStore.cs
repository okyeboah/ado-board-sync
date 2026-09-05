using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Configuration;

/// <summary>
/// Where the known profiles are kept between runs (ABSD-502).
///
/// A port rather than a static writer because the registry lives outside the
/// repository, under the user's own profile directory, and a test that exercised
/// switching would otherwise have to write to the machine's real one.
///
/// Read and write are synchronous: the document is a short list of paths, and an
/// asynchronous seam here would buy nothing but a state machine per switch.
/// </summary>
public interface IProfileRegistryStore
{
    /// <summary>
    /// The persisted registry, or <see cref="ProfileRegistry.Empty"/> on a machine
    /// that has never saved one. A missing file is not a failure — it is a first run.
    /// </summary>
    Result<ProfileRegistry> Read();

    Result<bool> Write(ProfileRegistry registry);
}
