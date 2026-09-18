using TheKrystalShip.KGSM.Dns.Anchor.Providers;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

/// <summary>
/// A zone held in memory, behaving the way the real one does where it matters: a name a record already
/// holds cannot be created a second time.
/// </summary>
/// <remarks>
/// That refusal is the lock two claims for one name race against, so a fake that allowed duplicates
/// would pass tests the live provider fails.
/// </remarks>
internal sealed class FakeProvider : IDnsProvider
{
    private readonly List<ProviderRecord> _records = [];
    private int _next;

    public bool Configured => true;

    /// <summary>Every call the reconciler made that changed the zone, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Put a record in the zone that this cluster did not make.</summary>
    public void Seed(string name, string type, string content, string comment = "")
    {
        _records.Add(new ProviderRecord(
            Id: "seed-" + (++_next), Name: name, Type: type, Content: content,
            Ttl: 60, Proxied: false, Comment: comment));
    }

    public ProviderRecord? Find(string name) =>
        _records.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    public Task<IReadOnlyList<ProviderRecord>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProviderRecord>>([.. _records]);

    public Task<ProviderRecord> CreateAsync(DesiredRecord record, CancellationToken ct = default)
    {
        if (Find(record.Name) is not null)
            throw new DnsNameTakenException(record.Name, "a record with that host already exists");

        var created = new ProviderRecord(
            Id: "rec-" + (++_next), Name: record.Name, Type: record.Type, Content: record.Content,
            Ttl: record.Ttl, Proxied: false, Comment: record.Comment);

        _records.Add(created);
        Calls.Add("create " + record.Name);
        return Task.FromResult(created);
    }

    public Task<ProviderRecord> UpdateAsync(
        string id, DesiredRecord record, CancellationToken ct = default)
    {
        int index = _records.FindIndex(r => r.Id == id);
        if (index < 0)
            throw new DnsProviderException("no such record");

        var updated = new ProviderRecord(
            Id: id, Name: record.Name, Type: record.Type, Content: record.Content,
            Ttl: record.Ttl, Proxied: false, Comment: record.Comment);

        _records[index] = updated;
        Calls.Add("update " + record.Name);
        return Task.FromResult(updated);
    }

    public Task<IReadOnlyList<string>> NameServersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(["ns1.example.net", "ns2.example.net"]);

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ProviderRecord? record = _records.FirstOrDefault(r => r.Id == id);
        if (record is null)
            return Task.CompletedTask;

        _records.Remove(record);
        Calls.Add("delete " + record.Name);
        return Task.CompletedTask;
    }
}
