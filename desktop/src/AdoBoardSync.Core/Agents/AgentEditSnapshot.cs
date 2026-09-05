using System.Text;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Agents;

/// <summary>
/// The backlog file exactly as it was before an agent ran (ABSD-704).
///
/// It holds bytes, not text, because rejecting an edit has to put the file back
/// byte for byte. Text would round-trip through a decoder and an encoder, and a
/// reject would then write what this app <em>believes</em> the file said rather
/// than what it said — a difference a user only ever discovers after they have
/// already thrown the agent's version away.
///
/// The array is copied in and copied out, so the snapshot cannot be edited from
/// underneath the reject that depends on it.
/// </summary>
public sealed class AgentEditSnapshot
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly byte[] _bytes;

    public AgentEditSnapshot(string path, byte[] bytes)
    {
        Path = path;
        _bytes = [.. bytes];
    }

    public string Path { get; }

    public int ByteCount => _bytes.Length;

    /// <summary>A copy, for handing to the write that restores the file.</summary>
    public byte[] ToArray() => [.. _bytes];

    public bool Matches(byte[] other) => _bytes.AsSpan().SequenceEqual(other);

    public Result<string> DecodeText() => Decode(_bytes, Path);

    /// <summary>
    /// Decodes as strictly as <c>FileSystemBacklogFileStore</c> does. An agent that
    /// writes something that is not UTF-8 has produced a backlog this app cannot
    /// re-open, and saying so here is better than showing a diff full of
    /// replacement characters that the reviewer would accept.
    /// </summary>
    public static Result<string> Decode(byte[] bytes, string path)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Error.Validation(
                "agent.edit.undecodable",
                $"{path} is no longer valid UTF-8 text. The edit was not shown, and the file was put back as it was.");
        }
    }
}

/// <summary>
/// Byte-level access to the one file an agent may change (ABSD-704).
///
/// Separate from <see cref="Backlog.IBacklogFileStore" /> rather than an extra
/// member on it: that port speaks in decoded text on purpose — the editor, the
/// parser and the stamp all want text — and this one exists precisely because a
/// reject must not go through a decoder.
/// </summary>
public interface IAgentEditFileStore
{
    Result<byte[]> ReadBytes(string path);

    Result<bool> WriteBytes(string path, byte[] bytes);
}
