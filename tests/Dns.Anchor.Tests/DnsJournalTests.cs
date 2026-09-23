using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Dns.Anchor.Status;
using TheKrystalShip.KGSM.Dns.Journal;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

/// <summary>
/// What this anchor puts on a line, beyond the payload.
/// </summary>
/// <remarks>
/// A reader renders an event it has never heard of from three envelope fields — the weight, the outcome
/// and the sentence — so a line missing any of them draws grey and reads as its own raw name. Nothing
/// downstream can detect that: an absent field is quiet, not malformed. These assert the producer's own
/// half of the bargain, which is the only place it can be asserted.
/// </remarks>
public sealed class DnsJournalTests
{
    /// <summary>A line as this writer handed it over, with everything the envelope carries.</summary>
    private sealed record Line(
        string Type, JsonElement Data, string? Actor, string? Origin,
        EventSeverity? Severity, EventOutcome? Outcome, string? Summary);

    /// <summary>Keeps every line instead of writing one, so a test reads what the anchor said.</summary>
    private sealed class CapturingWriter : IEventJournalWriter
    {
        public string Producer => DnsJournal.ProducerId;

        public List<Line> Lines { get; } = [];

        public ValueTask<bool> AppendAsync(
            EventName eventType, JsonElement data, string? actor = null, string? origin = null,
            EventSeverity? severity = null, EventOutcome? outcome = null, string? summary = null,
            CancellationToken token = default)
        {
            Lines.Add(new Line(eventType.Value, data.Clone(), actor, origin, severity, outcome, summary));
            return ValueTask.FromResult(true);
        }
    }

    private static (DnsJournal Journal, CapturingWriter Writer) Recorder()
    {
        var writer = new CapturingWriter();
        return (new DnsJournal(writer, NullLogger<DnsJournal>.Instance), writer);
    }

    private static ZoneChange Change(
        ZoneChangeKind kind, NameKind nameKind = NameKind.Game, string? from = null, string? reason = null) =>
        new(kind, "terraria.play.example.com", nameKind, "hotrod", "terraria", "CNAME",
            "walter.nodes.example.com", from, reason);

    /// <summary>
    /// Every line this anchor writes carries all three. Written as one sweep over every method rather
    /// than a case each, because the property is about the producer and not about any one event: an
    /// event added later that forgets them fails here the day it is written.
    /// </summary>
    [Fact]
    public async Task EveryLine_CarriesWeightOutcomeAndASentence()
    {
        (DnsJournal journal, CapturingWriter writer) = Recorder();
        var ct = CancellationToken.None;

        await journal.PassAsync(new RecordReconciler.Pass(
        [
            Change(ZoneChangeKind.Published),
            Change(ZoneChangeKind.Moved, from: "jessie.nodes.example.com"),
            Change(ZoneChangeKind.Recovered),
            Change(ZoneChangeKind.Withdrawn),
            Change(ZoneChangeKind.Contested, reason: "held by a record without this cluster's mark"),
        ], []), ct);

        await journal.ZoneAsync(reachable: false, "the provider returned 503", ct);
        await journal.ZoneAsync(reachable: true, null, ct);
        await journal.IssuedAsync("walter.nodes.example.com", "hotrod", DateTimeOffset.UtcNow.AddDays(90), ct);
        await journal.FailedAsync("walter.nodes.example.com", "hotrod", "rate limited", ct);
        await journal.RefusedAsync("walter.nodes.example.com", "jessie", "jessie does not hold that name", ct);
        await journal.ZoneCheckRequestedAsync("local:claude", ct);
        await journal.RenewRequestedAsync("walter.nodes.example.com", "hotrod", "local:claude", ct);
        await journal.AliasAddedAsync("ark.play.example.com", "terraria.play.example.com", "local:claude", ct);
        await journal.AliasRemovedAsync("ark.play.example.com", "terraria.play.example.com", "local:claude", ct);

        Assert.NotEmpty(writer.Lines);
        foreach (Line line in writer.Lines)
        {
            Assert.NotNull(line.Severity);
            Assert.NotNull(line.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(line.Summary), $"{line.Type} carries no summary");
            // A sentence, not the name restated: a reader that falls back to the raw name gets that
            // for free, so a summary equal to it says nothing the row did not already carry.
            Assert.NotEqual(line.Type, line.Summary);
        }
    }

    /// <summary>
    /// Every name this anchor writes is well-formed: lowercase, dotted, at least two segments. The
    /// shape is what the panel's icon trie walks and what its category filter groups on, so a name
    /// that is not a name renders under nothing.
    /// </summary>
    [Fact]
    public void EveryDeclaredName_IsWellFormed()
    {
        string[] names =
        [
            DnsEvents.NamePublished, DnsEvents.NameMoved, DnsEvents.NameWithdrawn,
            DnsEvents.NameContested, DnsEvents.NameRecovered,
            DnsEvents.CertificateIssued, DnsEvents.CertificateFailed, DnsEvents.CertificateRefused,
            DnsEvents.CertificateRenewRequested,
            DnsEvents.ZoneUnreachable, DnsEvents.ZoneRestored, DnsEvents.ZoneCheckRequested,
            DnsEvents.AliasAdded, DnsEvents.AliasRemoved,
        ];

        foreach (string name in names)
        {
            Assert.Equal(name.ToLowerInvariant(), name);
            Assert.True(name.Split('.').Length >= 2, $"{name} has no namespace");
            Assert.StartsWith("dns.", name, StringComparison.Ordinal);
            // EventName refuses anything the envelope cannot carry, so parsing is the real check.
            Assert.Equal(name, EventName.Parse(name).Value);
        }
    }

    /// <summary>
    /// The weight each kind of change carries. Publishing and correcting leave a name reachable; a name
    /// leaving the zone or held away from it stops resolving for everyone who has it, and those are what
    /// somebody reading the feed is looking for.
    /// </summary>
    [Theory]
    [InlineData(ZoneChangeKind.Published, EventSeverity.Info, EventOutcome.Success)]
    [InlineData(ZoneChangeKind.Moved, EventSeverity.Info, EventOutcome.Success)]
    [InlineData(ZoneChangeKind.Recovered, EventSeverity.Info, EventOutcome.Success)]
    [InlineData(ZoneChangeKind.Withdrawn, EventSeverity.Warn, EventOutcome.Neutral)]
    [InlineData(ZoneChangeKind.Contested, EventSeverity.Warn, EventOutcome.Failure)]
    public async Task AChange_CarriesTheWeightItsEffectDeserves(
        ZoneChangeKind kind, EventSeverity severity, EventOutcome outcome)
    {
        (DnsJournal journal, CapturingWriter writer) = Recorder();

        await journal.PassAsync(
            new RecordReconciler.Pass([Change(kind, from: "jessie.nodes.example.com")], []),
            CancellationToken.None);

        Line line = Assert.Single(writer.Lines);
        Assert.Equal(severity, line.Severity);
        Assert.Equal(outcome, line.Outcome);
    }

    /// <summary>
    /// A name freezes into the sentence as it was spelled then, beside what it points at — so a later
    /// move leaves the earlier line still saying where the name went at the time.
    /// </summary>
    [Fact]
    public async Task AMove_NamesBothEnds()
    {
        (DnsJournal journal, CapturingWriter writer) = Recorder();

        await journal.PassAsync(
            new RecordReconciler.Pass(
                [Change(ZoneChangeKind.Moved, NameKind.Capability, from: "jessie.nodes.example.com")], []),
            CancellationToken.None);

        Line line = Assert.Single(writer.Lines);
        Assert.Contains("jessie.nodes.example.com", line.Summary!, StringComparison.Ordinal);
        Assert.Contains("walter.nodes.example.com", line.Summary!, StringComparison.Ordinal);
        Assert.Contains("capability", line.Summary!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A node's own name is stored under the word "member" and read by people as a node. The wire
    /// spelling and the prose are separate on purpose, and the mark's spelling is the one that must not
    /// move.
    /// </summary>
    [Fact]
    public async Task ANodesOwnName_ReadsAsANodeAndIsMarkedAsAMember()
    {
        (DnsJournal journal, CapturingWriter writer) = Recorder();

        await journal.PassAsync(
            new RecordReconciler.Pass([Change(ZoneChangeKind.Published, NameKind.Member)], []),
            CancellationToken.None);

        Line line = Assert.Single(writer.Lines);
        Assert.Contains("node name", line.Summary!, StringComparison.Ordinal);
        Assert.Equal("member", line.Data.GetProperty("Kind").GetString());
    }

    /// <summary>
    /// A refusal names the member that asked, because nothing a person did led here and naming this
    /// anchor would hide which member wanted a name it does not hold.
    /// </summary>
    [Fact]
    public async Task ARefusal_IsAttributedToTheMemberThatAsked()
    {
        (DnsJournal journal, CapturingWriter writer) = Recorder();

        await journal.RefusedAsync(
            "walter.nodes.example.com", "jessie", "jessie does not hold that name", CancellationToken.None);

        Line line = Assert.Single(writer.Lines);
        Assert.Equal("member:jessie", line.Actor);
        Assert.Contains("jessie does not hold that name", line.Summary!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The zone not answering is the whole capability down — no name can be written and no certificate
    /// ordered behind it — and it is the one thing this anchor rates danger.
    /// </summary>
    [Fact]
    public async Task AnUnreachableZone_IsTheOneDangerAndNamesWhatTheProviderSaid()
    {
        (DnsJournal journal, CapturingWriter writer) = Recorder();

        await journal.ZoneAsync(reachable: false, "the provider returned 503", CancellationToken.None);

        Line line = Assert.Single(writer.Lines);
        Assert.Equal(EventSeverity.Danger, line.Severity);
        Assert.Equal(EventOutcome.Failure, line.Outcome);
        Assert.Contains("the provider returned 503", line.Summary!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An order that ended without a certificate is a condition worth watching rather than something
    /// that has broken: the member asks again, and the name keeps the certificate it already holds.
    /// </summary>
    [Fact]
    public async Task AFailedOrder_IsWarnAndNamesWhy()
    {
        (DnsJournal journal, CapturingWriter writer) = Recorder();

        await journal.FailedAsync(
            "walter.nodes.example.com", "hotrod", "rate limited", CancellationToken.None);

        Line line = Assert.Single(writer.Lines);
        Assert.Equal(EventSeverity.Warn, line.Severity);
        Assert.Equal(EventOutcome.Failure, line.Outcome);
        Assert.Contains("rate limited", line.Summary!, StringComparison.Ordinal);
    }

    /// <summary>An admin's action carries that admin and the surface it came through.</summary>
    [Fact]
    public async Task AnAdminAction_CarriesWhoAndThroughWhat()
    {
        (DnsJournal journal, CapturingWriter writer) = Recorder();

        await journal.ZoneCheckRequestedAsync("local:claude", CancellationToken.None);

        Line line = Assert.Single(writer.Lines);
        Assert.Equal("local:claude", line.Actor);
        Assert.Equal(DnsJournal.OriginUi, line.Origin);
        Assert.Equal(EventSeverity.Info, line.Severity);
    }
}
