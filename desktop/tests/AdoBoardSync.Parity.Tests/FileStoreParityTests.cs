using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Parity.Tests;

/// <summary>
/// A backlog saved by a Windows editor reaches the desktop app as UTF-8 bytes with a
/// byte-order mark and CRLF line endings, and the app reads those bytes through the
/// file store instead of through the CLI. Parsing a hand-built string proves nothing
/// about that path: both the mark and the line endings exist only on disk, and only
/// survive as far as the decoder carries them. These scenarios therefore write a real
/// file, decode it the way the store does, and require the CLI's parser.py to agree
/// item for item on the same file.
/// </summary>
public sealed class FileStoreParityTests
{
    private const string ByteOrderMark = "\uFEFF";

    /// <summary>
    /// Mirrors the decoding rule in
    /// <c>desktop/src/AdoBoardSync.Infrastructure/FileSystemBacklogFileStore.cs</c>.
    /// This project references Core and TestKit only, so the real adapter cannot be
    /// called from here; naming the rule in one place at least makes a divergence
    /// visible as a failure here rather than nowhere at all.
    /// </summary>
    private static readonly UTF8Encoding StoreCodec = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Modelled on <c>fixtures/backlog/standard.md</c> so the comparison runs over a
    /// realistic document — two Epics, an Issue with nested bullets, a table, and a
    /// stop heading — rather than over the single heading a mark could plausibly
    /// break. The em dash and middle dot also put multi-byte sequences behind it.
    /// </summary>
    private static readonly string[] FixtureLines =
    [
        "# Project backlog",
        "",
        "Prose before the first Epic heading is ignored by the parser.",
        "",
        "## Epic 1 — Platform Foundations",
        "*Context: shared libraries every other layer depends on.*",
        "",
        "### PROJ-101 · Build the core event store",
        "*Reference: ADR-002*",
        "- Implement the append-only `EventStore`",
        "- Add optimistic-concurrency checks",
        "  * note: covered by the snapshot policy below",
        "",
        "### PROJ-102 · Wire up local orchestration",
        "",
        "Some description with **bold** text and a table:",
        "",
        "| Field | Meaning |",
        "| --- | --- |",
        "| `id` | the identity |",
        "",
        "- A task after a table",
        "",
        "## Epic 2 — Delivery",
        "",
        "### PROJ-201 · Ship the first slice",
        "- Only one task here",
        "",
        "### Not an issue heading without a code",
        "",
        "## Appendix — Deferred items",
        "### PROJ-999 · Never parsed, after the stop heading",
        "- never a task"
    ];

    [Fact]
    public void ACrlfBacklogSavedWithAByteOrderMarkParsesTheSameWayAsThePythonImplementation()
    {
        var backlog = WriteCrlfFixture(withByteOrderMark: true);
        try
        {
            using var profile = TempBoardProfile.Create(backlog);
            using var reference = PythonReference.WithConfig("parse", profile.ConfigPath);

            var config = BoardConfig.Load(profile.ConfigPath);
            Assert.True(config.IsSuccess, config.Error?.SafeMessage);

            var actual = BacklogParser.Parse(config.Value, ReadTheWayTheFileStoreDoes(backlog));

            // Guards the comparison against passing vacuously: had the mark broken the
            // first Epic heading, both implementations would agree on an empty list.
            Assert.NotEmpty(actual);

            Assert.Equal(
                Canonical(reference.RootElement.GetProperty("items")),
                Canonical(actual));
        }
        finally
        {
            File.Delete(backlog);
        }
    }

    [Fact]
    public void TheSameCrlfBacklogWithoutAByteOrderMarkParsesTheSameWayAsThePythonImplementation()
    {
        var backlog = WriteCrlfFixture(withByteOrderMark: false);
        try
        {
            using var profile = TempBoardProfile.Create(backlog);
            using var reference = PythonReference.WithConfig("parse", profile.ConfigPath);

            var config = BoardConfig.Load(profile.ConfigPath);
            Assert.True(config.IsSuccess, config.Error?.SafeMessage);

            var actual = BacklogParser.Parse(config.Value, ReadTheWayTheFileStoreDoes(backlog));

            Assert.NotEmpty(actual);

            Assert.Equal(
                Canonical(reference.RootElement.GetProperty("items")),
                Canonical(actual));
        }
        finally
        {
            File.Delete(backlog);
        }
    }

    [Fact]
    public void TheByteOrderMarkReachesTheParserAndStillChangesNothingAboutTheParsedItems()
    {
        var marked = WriteCrlfFixture(withByteOrderMark: true);
        var plain = WriteCrlfFixture(withByteOrderMark: false);
        try
        {
            using var profile = TempBoardProfile.Create(marked);
            var config = BoardConfig.Load(profile.ConfigPath);
            Assert.True(config.IsSuccess, config.Error?.SafeMessage);

            var markedText = ReadTheWayTheFileStoreDoes(marked);
            var plainText = ReadTheWayTheFileStoreDoes(plain);

            // The store decodes rather than sniffs, so the mark survives into the string
            // the parser sees — as Python's "utf-8" codec also leaves it in place, and is
            // why the two scenarios above are genuinely different inputs.
            Assert.StartsWith(ByteOrderMark, markedText, StringComparison.Ordinal);
            Assert.DoesNotContain(ByteOrderMark, plainText, StringComparison.Ordinal);

            Assert.Equal(
                Canonical(BacklogParser.Parse(config.Value, markedText)),
                Canonical(BacklogParser.Parse(config.Value, plainText)));
        }
        finally
        {
            File.Delete(marked);
            File.Delete(plain);
        }
    }

    private static string ReadTheWayTheFileStoreDoes(string path) =>
        StoreCodec.GetString(File.ReadAllBytes(path));

    private static string WriteCrlfFixture(bool withByteOrderMark)
    {
        var path = Path.Combine(Path.GetTempPath(), $"abs-filestore-parity-{Guid.NewGuid():N}.md");
        var text = (withByteOrderMark ? ByteOrderMark : string.Empty)
            + string.Join("\r\n", FixtureLines)
            + "\r\n";

        // The codec never emits an identifier of its own, so the leading U+FEFF above is
        // the only thing that can put mark bytes at the head of the file.
        File.WriteAllBytes(path, StoreCodec.GetBytes(text));
        return path;
    }

    /// <summary>
    /// Repeats the projection <c>BacklogParserParityTests</c> uses, because that copy is
    /// private and no shared one exists yet; lifting either into the TestKit would let
    /// both files compare through the same code.
    /// </summary>
    private static string Canonical(JsonElement pythonItems)
    {
        var array = new JsonArray();
        foreach (var item in pythonItems.EnumerateArray())
        {
            var level = item.GetProperty("level").GetString()!;
            array.Add(Node(
                level,
                item.GetProperty("title").GetString()!,
                level == "issue" ? item.GetProperty("code").GetString() : null,
                [.. item.GetProperty("desc_lines").EnumerateArray().Select(e => e.GetString()!)],
                level == "issue"
                    ? [.. item.GetProperty("bullets").EnumerateArray().Select(e => e.GetString()!)]
                    : null));
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Canonical(IReadOnlyList<BacklogItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var isIssue = item.Level == BacklogLevel.Issue;
            array.Add(Node(
                isIssue ? "issue" : "epic",
                item.Title,
                isIssue ? item.Code : null,
                item.DescriptionLines,
                isIssue ? item.Bullets : null));
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Node(
        string level,
        string title,
        string? code,
        IReadOnlyList<string> descriptionLines,
        IReadOnlyList<string>? bullets)
    {
        var node = new JsonObject
        {
            ["level"] = level,
            ["title"] = title,
            ["desc_lines"] = new JsonArray([.. descriptionLines.Select(l => JsonValue.Create(l))])
        };

        if (code is not null)
        {
            node["code"] = code;
        }

        if (bullets is not null)
        {
            node["bullets"] = new JsonArray([.. bullets.Select(b => JsonValue.Create(b))]);
        }

        return node;
    }
}
