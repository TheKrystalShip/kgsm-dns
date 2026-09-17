using System.Security.Cryptography;

using TheKrystalShip.KGSM.Dns;

namespace TheKrystalShip.KGSM.Dns.Anchor.Names;

/// <summary>
/// The names members are drawn from, and the ones an operator has pinned.
/// </summary>
/// <remarks>
/// <para>
/// A member is given a name rather than told one, so nothing on a machine has to be configured with
/// what that machine is called. The list is the cluster's, held here with the credential, and a member
/// never learns the list — only the one name it was given.
/// </para>
/// <para>
/// A pin is how a member keeps a name it already answers to. It is stated as
/// <c>member=label</c>, matched on the member id, and it wins over the draw — so a machine people
/// already reach at a name goes on being reached there when it joins.
/// </para>
/// </remarks>
public sealed class MemberNamePool(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> pins)
{
    /// <summary>The labels in the list, already reduced and de-duplicated.</summary>
    public IReadOnlyList<string> Names { get; } =
        [.. names.Select(DnsLabel.Sanitize).Where(DnsLabel.IsValid).Distinct(StringComparer.Ordinal)];

    /// <summary>The pinned labels, by member id.</summary>
    public IReadOnlyDictionary<string, string> Pins { get; } = pins;

    /// <summary>
    /// A label for one member: its pin when it has one, otherwise one drawn at random from what is free.
    /// </summary>
    /// <param name="member">The member id being named.</param>
    /// <param name="taken">Whether a label is already held by some other row.</param>
    /// <returns>The label, or an empty string when nothing is free.</returns>
    /// <remarks>
    /// Drawn at random rather than in order so two members joining a fresh cluster do not both receive
    /// the first entry and then race; and a pinned label is returned even when it is held, because the
    /// caller refusing a pin somebody else took is a clearer failure than silently drawing around it.
    /// </remarks>
    public string Draw(string member, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        if (Pins.TryGetValue(member, out string? pinned))
        {
            string label = DnsLabel.Sanitize(pinned);
            if (DnsLabel.IsValid(label))
                return label;
        }

        List<string> free = [.. Names.Where(n => !taken(n))];
        if (free.Count == 0)
        {
            // The list is exhausted. A member id reduced to a label is not drawn as a fallback: the
            // list is what keeps a member's public name from spelling out what the machine is, and
            // quietly falling back to the id would undo that at the worst moment.
            return "";
        }

        return free[RandomNumberGenerator.GetInt32(free.Count)];
    }
}
