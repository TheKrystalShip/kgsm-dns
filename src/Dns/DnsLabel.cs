using System.Text;

namespace TheKrystalShip.KGSM.Dns;

/// <summary>
/// One level of a name — the part a cluster chooses, such as <c>jessie</c> in
/// <c>jessie.nodes.example.com</c> or <c>minecraft</c> in <c>minecraft.play.example.com</c>.
/// </summary>
/// <remarks>
/// A suggestion arrives from something that was never shaped for DNS: a blueprint id, a member id, a
/// name somebody typed. So a suggestion is reduced rather than rejected, and what comes out is
/// either a valid label or nothing at all — an empty answer says "this cannot be a name", which the
/// caller reports, instead of a label that the provider will refuse later for reasons the caller
/// cannot see.
/// </remarks>
public static class DnsLabel
{
    /// <summary>The longest a single label may be, from the DNS specification.</summary>
    public const int MaxLength = 63;

    /// <summary>
    /// Reduce a suggestion to a label: lowercase, ASCII letters, digits and hyphens, with runs of
    /// anything else collapsed to a single hyphen and hyphens trimmed from both ends.
    /// </summary>
    /// <returns>The label, or an empty string when nothing usable survives.</returns>
    public static string Sanitize(string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion))
            return "";

        var builder = new StringBuilder(suggestion.Length);
        bool pendingHyphen = false;

        foreach (char c in suggestion)
        {
            char lower = char.ToLowerInvariant(c);
            bool usable = (lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9');

            if (usable)
            {
                // A hyphen is only written once something follows it, so a run of separators costs one
                // hyphen and a trailing run costs none.
                if (pendingHyphen && builder.Length > 0)
                    builder.Append('-');

                pendingHyphen = false;
                builder.Append(lower);

                if (builder.Length == MaxLength)
                    break;
            }
            else
            {
                pendingHyphen = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>Whether a string is already a label this cluster would publish.</summary>
    public static bool IsValid(string? label) =>
        !string.IsNullOrEmpty(label)
        && label.Length <= MaxLength
        && label[0] != '-'
        && label[^1] != '-'
        && label.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');

    /// <summary>
    /// The first label in the series <c>base</c>, <c>base-2</c>, <c>base-3</c>… that
    /// <paramref name="taken"/> does not already contain.
    /// </summary>
    /// <remarks>
    /// Numbered rather than randomized so the answer is reproducible, and so a name freed by an
    /// uninstall is the one the next claim for that game receives — a player's address survives a
    /// reinstall when nothing else has taken it meanwhile.
    /// <para>
    /// The suffix is trimmed out of the base first when it would push the label past
    /// <see cref="MaxLength"/>, because a truncated label that collides with another truncation would
    /// otherwise loop without ever finding a free one.
    /// </para>
    /// </remarks>
    public static string FirstFree(string label, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        if (!IsValid(label))
            return "";

        if (!taken(label))
            return label;

        for (int n = 2; n < 1000; n++)
        {
            string suffix = "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string stem = label.Length + suffix.Length <= MaxLength
                ? label
                : label[..(MaxLength - suffix.Length)].TrimEnd('-');

            string candidate = stem + suffix;
            if (IsValid(candidate) && !taken(candidate))
                return candidate;
        }

        // Every label in the series is held. Nothing is published rather than something arbitrary:
        // a thousand servers of one game on one cluster is a state to report, not to work around.
        return "";
    }
}
