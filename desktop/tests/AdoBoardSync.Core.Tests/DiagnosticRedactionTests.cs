using System.Collections.ObjectModel;
using AdoBoardSync.Core.Diagnostics;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// The ABSD-507 rule that has to hold before diagnostics may be written anywhere:
/// a credential does not reach a sink. These are the tests that decide whether the
/// rest of the feature is safe to enable, so they assert on absence — the secret
/// appears nowhere in the rendered event — rather than on the redactor returning
/// the string someone expected.
/// </summary>
public class DiagnosticRedactionTests
{
    // Deliberately shapeless: no pattern in the redactor matches it, so every
    // assertion below is about registration doing the work rather than a lucky
    // shape match covering for it.
    private const string Secret = "Swordfish-Passphrase-9f3a";

    // 52 characters of lowercase base32 — the classic Azure DevOps PAT shape.
    private const string PatShaped = "abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrst";

    // The width the redactor guarantees: no run this long or longer survives.
    private const int GuaranteedRunLength = 8;

    [Fact]
    public void ARegisteredSecretInsideALongerMessageIsRemovedEntirely()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register(Secret);

        var redacted = redaction.Apply(Event(
            message: $"POST /_apis/wit/workitems failed while authenticating with {Secret} against dev.azure.com."));

        AssertNoTraceOfTheSecret(redacted);
        Assert.Contains("[redacted]", redacted.Message, StringComparison.Ordinal);
        Assert.Contains("POST /_apis/wit/workitems failed", redacted.Message, StringComparison.Ordinal);
        Assert.Contains("against dev.azure.com.", redacted.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegisteredSecretInsideADataValueIsRemovedEntirely()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register(Secret);

        var redacted = redaction.Apply(Event(
            message: "Apply failed.",
            data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Not a key the name rules would catch: the value has to be found
                // by its content, which is the only defence when a secret lands
                // somewhere nobody labelled as holding one.
                ["request_url"] = $"https://dev.azure.com/org/_apis?auth={Secret}&api-version=7.0",
            }));

        AssertNoTraceOfTheSecret(redacted);
        Assert.Contains("api-version=7.0", redacted.Data["request_url"], StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedPrefixOfARegisteredSecretDoesNotSurvive()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register(Secret);

        // The "safe" logging that is not: a caller trims the token itself, believing
        // a prefix is harmless. It identifies which credential leaked and shortens
        // any search for the rest, so the redactor has to treat it as the whole one.
        var redacted = redaction.Apply(Event(
            message: $"Token {Secret[..12]}... was rejected.",
            data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["prefix"] = Secret[..GuaranteedRunLength],
            }));

        AssertNoTraceOfTheSecret(redacted);
    }

    [Fact]
    public void ATruncatedTailOrSliceOfARegisteredSecretDoesNotSurviveEither()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register(Secret);

        var redacted = redaction.Apply(Event(
            message: $"...{Secret[^12..]} and {Secret[6..18]} appeared in a retry log.",
            data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tail"] = Secret[^GuaranteedRunLength..],
            }));

        AssertNoTraceOfTheSecret(redacted);
    }

    [Fact]
    public void ARedactedValueIsReplacedWholeRatherThanShortened()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register(Secret);

        var redacted = redaction.Redact($"before {Secret} after");

        // Not "before Swordf… after": the placeholder stands in for all of it, and
        // the text either side is untouched so the line is still readable.
        Assert.Equal("before [redacted] after", redacted);
    }

    [Fact]
    public void TheSameSecretIsRemovedFromEveryPlaceItAppears()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register(Secret);

        var redacted = redaction.Apply(Event(
            message: $"{Secret} then {Secret} then {Secret[..10]}",
            data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["first"] = Secret,
                ["second"] = $"prefix-{Secret}-suffix",
            }));

        AssertNoTraceOfTheSecret(redacted);
        Assert.Equal("[redacted]", redacted.Data["first"]);
        Assert.Equal("prefix-[redacted]-suffix", redacted.Data["second"]);
    }

    [Fact]
    public void OneRegisteredSecretContainedInAnotherLeavesNoTail()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register("inner-secret-value");
        redaction.Register("outer-inner-secret-value-outer");

        var redacted = redaction.Redact("carrying outer-inner-secret-value-outer home");

        Assert.Equal("carrying [redacted] home", redacted);
    }

    [Fact]
    public void AValueUnderACredentialKeyIsDroppedEvenThoughItWasNeverRegistered()
    {
        var redaction = new DiagnosticRedaction();

        var redacted = redaction.Apply(Event(
            message: "Resolved a credential.",
            data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pat"] = "short",
                ["pat_env"] = "also-short",
                ["accessToken"] = "value",
                ["Authorization"] = "value",
                ["api_key"] = "value",
                ["userPassword"] = "value",
            }));

        foreach (var entry in redacted.Data)
        {
            Assert.Equal("[redacted]", entry.Value);
        }
    }

    [Fact]
    public void APathKeyIsNotMistakenForAPatKey()
    {
        // "pat" is a substring of "path" and of "patch". Matching it loosely would
        // empty the one field that makes a file-write event traceable, so the key
        // rules match it as a word and this test is what holds that in place.
        Assert.False(DiagnosticRedaction.IsSecretKey("path"));
        Assert.False(DiagnosticRedaction.IsSecretKey("backlog_path"));
        Assert.False(DiagnosticRedaction.IsSecretKey("patchOperations"));
        Assert.False(DiagnosticRedaction.IsSecretKey("author"));

        Assert.True(DiagnosticRedaction.IsSecretKey("pat"));
        Assert.True(DiagnosticRedaction.IsSecretKey("PAT"));
        Assert.True(DiagnosticRedaction.IsSecretKey("patFile"));
        Assert.True(DiagnosticRedaction.IsSecretKey("pat_env"));
    }

    [Fact]
    public void AFileWriteEventKeepsThePathItRecorded()
    {
        var redaction = new DiagnosticRedaction();

        var redacted = redaction.Apply(Event(
            message: "Wrote 4096 bytes to /Users/someone/board/backlog.md.",
            data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = "/Users/someone/board/backlog.md",
                ["bytes"] = "4096",
            }));

        Assert.Equal("/Users/someone/board/backlog.md", redacted.Data["path"]);
        Assert.Equal("4096", redacted.Data["bytes"]);
    }

    [Fact]
    public void APatShapedValueIsRemovedWithNothingRegisteredAtAll()
    {
        var redaction = new DiagnosticRedaction();

        var redacted = redaction.Redact($"authenticating with {PatShaped} now");

        Assert.Equal("authenticating with [redacted] now", redacted);
    }

    [Fact]
    public void ABacklogFingerprintSurvivesTheShapeBackstop()
    {
        var redaction = new DiagnosticRedaction();

        // A stale-plan report is useless without both fingerprints, and they are
        // long opaque strings too. Lowercase hex is what separates them from a PAT.
        const string Fingerprint = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

        Assert.Equal(Fingerprint, redaction.Redact(Fingerprint));
    }

    [Fact]
    public void AnAuthorizationHeaderLosesItsValueAndKeepsItsScheme()
    {
        var redaction = new DiagnosticRedaction();

        var redacted = redaction.Redact("sent Authorization: Basic b3JnOnBhdC12YWx1ZS1oZXJl to the connector");

        Assert.Contains("Basic [redacted]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("b3JnOnBhdC12YWx1ZS1oZXJl", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void APatPastedIntoACloneUrlIsRemoved()
    {
        var redaction = new DiagnosticRedaction();

        var redacted = redaction.Redact($"remote https://someone:{PatShaped}@dev.azure.com/org/project/_git/repo");

        Assert.DoesNotContain(PatShaped, redacted, StringComparison.Ordinal);
        Assert.Contains("dev.azure.com/org/project/_git/repo", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCategoryAndCodeSurviveSoASupportConversationKeepsItsVocabulary()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register(Secret);

        var redacted = redaction.Apply(Event(
            message: $"The PAT {Secret} was rejected.",
            code: "board.unauthorized"));

        Assert.Equal("board.unauthorized", redacted.Code);
        Assert.Equal("apply", redacted.Category);
        Assert.Equal(DiagnosticLevel.Error, redacted.Level);
    }

    [Fact]
    public void AnEventWithNothingToRemoveComesBackAsTheSameInstance()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register(Secret);

        var original = Event(
            message: "Generated an Import plan of 12 rows.",
            data: new Dictionary<string, string>(StringComparer.Ordinal) { ["rows"] = "12" });

        Assert.Same(original, redaction.Apply(original));
    }

    [Fact]
    public void AValueTooShortToRegisterIsIgnoredRatherThanEmptyingEveryMessage()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register("a");
        redaction.Register("of");
        redaction.Register("   ");
        redaction.Register(null);

        Assert.Equal(0, redaction.RegisteredCount);
        Assert.Equal(
            "Generated a plan of 12 rows.",
            redaction.Redact("Generated a plan of 12 rows."));
    }

    [Fact]
    public void RegisteringTheSameSecretTwiceRegistersItOnce()
    {
        var redaction = new DiagnosticRedaction();
        redaction.Register(Secret);
        redaction.Register(Secret);

        Assert.Equal(1, redaction.RegisteredCount);
    }

    [Fact]
    public async Task ASecretRegisteredWhileEventsAreBeingWrittenIsStillCaught()
    {
        // An Apply run resolves its PAT on one thread while its worker tasks are
        // already writing events on others; a redactor that tore under that would
        // leak exactly when the load is highest.
        var redaction = new DiagnosticRedaction();
        var registering = Task.Run(
            () => Parallel.For(0, 50, index => redaction.Register($"registered-secret-value-{index:D4}")));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 500),
            (_, _) =>
            {
                redaction.Redact($"carrying {Secret} along");
                return ValueTask.CompletedTask;
            });

        await registering;

        redaction.Register(Secret);
        Assert.DoesNotContain(Secret, redaction.Redact($"carrying {Secret} along"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The assertion the ticket turns on: no run of the secret long enough to
    /// identify it survives anywhere in the event — not in the message, not in a
    /// key, not in a value. Written as a sweep over every window rather than a
    /// single <c>DoesNotContain</c> so that a redactor which shortened the secret
    /// instead of replacing it fails here rather than passing.
    /// </summary>
    private static void AssertNoTraceOfTheSecret(DiagnosticEvent redacted)
    {
        var rendered = string.Join(
            " ",
            redacted.Message,
            redacted.Category,
            redacted.Code ?? string.Empty,
            string.Join(" ", redacted.Data.Select(entry => $"{entry.Key}={entry.Value}")));

        for (var start = 0; start + GuaranteedRunLength <= Secret.Length; start++)
        {
            var window = Secret.Substring(start, GuaranteedRunLength);
            Assert.DoesNotContain(window, rendered, StringComparison.Ordinal);
        }
    }

    private static DiagnosticEvent Event(
        string message,
        IReadOnlyDictionary<string, string>? data = null,
        string? code = null) => new()
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Level = DiagnosticLevel.Error,
            Category = "apply",
            Code = code,
            Message = message,
            Data = data is null
                ? ReadOnlyDictionary<string, string>.Empty
                : new ReadOnlyDictionary<string, string>(data.ToDictionary(StringComparer.Ordinal)),
        };
}
