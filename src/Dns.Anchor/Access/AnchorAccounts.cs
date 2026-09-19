using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.KGSM.Dns.Anchor.Access;

/// <summary>
/// This anchor's own copy of the cluster's accounts, and the only thing it resolves a person against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authority is read here on every request, never off the session.</b> A session carries the tier it
/// was minted with; the accounts carry the tier the person holds now. Reading the copy is what makes a
/// demotion or a disabled account land on the next request, the same way it does on every other member.
/// </para>
/// <para>
/// <b>The copy is this member's alone.</b> It lives in this anchor's state directory and is filled by what
/// the holder of <c>auth</c> publishes — the snapshot on joining and every change after — so it shares no
/// file with any other member on the machine.
/// </para>
/// <para>
/// <b>Unavailable is a real answer.</b> A store that will not open, or a copy never filled, is reported as
/// "cannot tell", never as "you hold nothing": the second would refuse an admin on the strength of an
/// outage and say they lack access.
/// </para>
/// </remarks>
internal sealed class AnchorAccounts : IReplicatedAccounts
{
    private readonly UserStoreAuthority? _authority;
    private readonly SqliteUserStore? _store;
    private readonly AccountReplica? _replica;
    private volatile bool _replicaSeen;

    public AnchorAccounts(AnchorOptions options, ILogger<AnchorAccounts> logger)
    {
        string path = options.AccountsPath;
        try
        {
            var storeOptions = new UserStoreOptions { Path = path };
            _store = new SqliteUserStore(storeOptions);
            _authority = new UserStoreAuthority(_store);
            _replica = new AccountReplica(_store, new SqliteAccountVersions(storeOptions));
        }
        catch (UserStoreSchemaException e)
        {
            UnavailableReason = e.Message;
            logger.LogError(e, "the account copy at {Path} is a schema this build does not read", path);
        }
        catch (Exception e)
        {
            UnavailableReason = $"The account copy at '{path}' could not be opened.";
            logger.LogError(e, "the account copy at {Path} could not be opened", path);
        }
    }

    /// <inheritdoc />
    public AccountReplica? Replica => _replica;

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    /// <summary>What the accounts say about one identity, or null when they cannot say.</summary>
    /// <returns>
    /// The answer, or null when the copy could not be opened, could not be read, or has not been filled
    /// yet — every case in which nobody can be told apart here.
    /// </returns>
    public async Task<AuthorityAnswer?> ResolveAsync(KgsmIdentity identity, CancellationToken ct)
    {
        if (_authority is null || _store is null)
            return null;

        // An empty copy has not been given the cluster's accounts yet. Everybody would resolve as a
        // stranger, which is a claim about them this member has no basis to make.
        if (!_replicaSeen)
        {
            try
            {
                if ((await _store.ListAsync(ct).ConfigureAwait(false)).Count == 0)
                    return null;
            }
            catch (Exception)
            {
                return null;
            }

            _replicaSeen = true;
        }

        try
        {
            return await _authority.ResolveAsync(identity, ct).ConfigureAwait(false);
        }
        catch (KgsmAuthProviderException)
        {
            return null;
        }
    }
}
