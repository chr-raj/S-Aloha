using Novell.Directory.Ldap;
using SAloha.Api.Core.Directory;

namespace SAloha.Api.Core.Auth;

public class LdapService(IConfiguration config, ILogger<LdapService> logger) : IUserDirectory, IAuthenticator, IDirectoryRoster
{
    private string Host => config["Ldap:Host"]!;
    private int Port => int.Parse(config["Ldap:Port"] ?? "389");
    private bool UseSsl => bool.Parse(config["Ldap:UseSsl"] ?? "false");
    private string BaseDn => config["Ldap:BaseDn"]!;
    /// <summary>R7 : délai maximal de connexion et de chaque opération (5 s par défaut).</summary>
    private int TimeoutMs => (int.TryParse(config["Ldap:TimeoutSeconds"], out var s) && s > 0 ? s : 5) * 1000;

    // Novell.Directory.Ldap 4 : API asynchrone (connexion, liaison, recherche).
    // R6 : serveur absent ou injoignable, ou compte de service refusé → DirectoryUnavailableException ;
    // seule la liaison d'un utilisateur avec un mauvais mot de passe reste une LdapException.
    private async Task<LdapConnection> ConnectAsync(string dn, string password, bool service = false)
    {
        var conn = new LdapConnection { SecureSocketLayer = UseSsl, ConnectionTimeout = TimeoutMs };
        // LDAPS avec l'autorité de l'annuaire (S2) ; sans réglage, validation par défaut du système.
        try { LdapTls.Apply(conn, config); }
        catch (InvalidOperationException ex)
        {
            conn.Dispose();
            logger.LogError("{Message}", ex.Message);
            throw new DirectoryUnavailableException(ex.Message, ex);
        }
        var constraints = conn.Constraints;
        constraints.TimeLimit = TimeoutMs;   // liaison et recherches bornées (R7)
        conn.Constraints = constraints;
        try
        {
            await conn.ConnectAsync(Host, Port);
        }
        catch (Exception ex)
        {
            conn.Dispose();
            throw new DirectoryUnavailableException($"Annuaire injoignable ({Host}:{Port}).", ex);
        }
        try
        {
            await conn.BindAsync(dn, password);
            return conn;
        }
        catch (LdapException ex) when (service || Unavailable(ex))
        {
            conn.Dispose();
            throw new DirectoryUnavailableException(service ? "Compte de service de l'annuaire refusé." : "Annuaire injoignable.", ex);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Codes LDAP d'un serveur injoignable ou indisponible (et non d'un refus).</summary>
    private static bool Unavailable(LdapException ex) => ex.ResultCode is LdapException.ServerDown or LdapException.ConnectError
        or LdapException.Unavailable or LdapException.Busy or LdapException.LdapTimeout;

    private Task<LdapConnection> ServiceConnectAsync() =>
        ConnectAsync(config["Ldap:BindUser"]!, config["Ldap:BindPassword"]!, service: true);

    /// <summary>ADM-20 : l'annuaire répond et accepte le compte de service ; lève <see cref="DirectoryUnavailableException"/> sinon.</summary>
    public async Task ProbeAsync()
    {
        using var _ = await ServiceConnectAsync();
    }

    /// <summary>
    /// Entrées d'une recherche en sous-arbre, <paramref name="max"/> au plus. Les références
    /// qu'un AD renvoie depuis la racine du domaine (Configuration, DomainDnsZones,
    /// ForestDnsZones) sont sautées : la recherche paginée de Novell les lève en exception et
    /// les suivre exigerait de résoudre le nom du domaine. La limite de taille du serveur
    /// (MaxPageSize, 1 000 par défaut sur un AD) arrête la lecture sans perdre les entrées lues.
    /// </summary>
    public static async Task<List<LdapEntry>> SearchEntriesAsync(LdapConnection conn, string baseDn, string filter, string[] attributes,
        int max, CancellationToken ct = default)
    {
        var constraints = new LdapSearchConstraints(conn.SearchConstraints) { MaxResults = max, ReferralFollowing = false };
        var search = await conn.SearchAsync(baseDn, LdapConnection.ScopeSub, filter, attributes, false, constraints, ct);
        var entries = new List<LdapEntry>();
        try
        {
            while (entries.Count < max && await search.HasMoreAsync(ct))
            {
                try { entries.Add(await search.NextAsync(ct)); }
                catch (LdapReferralException) { /* référence vers une autre partition */ }
            }
        }
        catch (LdapException ex) when (ex.ResultCode == LdapException.SizeLimitExceeded) { /* limite du serveur : entrées lues gardées */ }
        return entries;
    }

    /// <summary>Première entrée d'une recherche ; les référents d'un AD (Configuration, DomainDnsZones…) ne sont pas des comptes.</summary>
    private static async Task<LdapEntry?> FirstEntryAsync(ILdapSearchResults search)
    {
        while (await search.HasMoreAsync())
        {
            try { return await search.NextAsync(); }
            catch (LdapReferralException) { }
        }
        return null;
    }

    /// <summary>
    /// Authentifie l'utilisateur et retourne (sAMAccountName de l'annuaire,
    /// displayName, rôle applicatif) ou null. L'identifiant renvoyé est celui
    /// de l'AD, pas la saisie : l'AD ignore la casse, l'application compare
    /// des chaînes (M2, revue du 2026-10-06). Les groupes AD directs de
    /// l'utilisateur (CN) sont renvoyés pour le jeton (SOC-10).
    /// </summary>
    public async Task<(string Username, string DisplayName, string Role, IReadOnlyCollection<string> Groups)?> AuthenticateAsync(string username, string password)
    {
        // O2 : durée et résultat de la connexion (sans identifiant ni mot de passe dans la trace).
        using var activity = Core.Observability.Telemetry.Source.StartActivity("Annuaire : connexion");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = "injoignable";
        try
        {
            var result = await AuthenticateCoreAsync(username, password);
            outcome = result is null ? "refus" : "ok";
            return result;
        }
        finally
        {
            activity?.SetTag("saloha.result", outcome);
            Core.Observability.Telemetry.DirectoryLogin.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("result", outcome));
        }
    }

    private async Task<(string Username, string DisplayName, string Role, IReadOnlyCollection<string> Groups)?> AuthenticateCoreAsync(string username, string password)
    {
        try
        {
            using var svc = await ServiceConnectAsync();
            var filter = string.Format(config["Ldap:UserFilter"] ?? "(sAMAccountName={0})", EscapeFilter(username));
            var search = await svc.SearchAsync(BaseDn, LdapConnection.ScopeSub, filter,
                new[] { "distinguishedName", "sAMAccountName", "displayName", "memberOf" }, false);
            var entry = await FirstEntryAsync(search);
            if (entry is null) return null;

            // vérifie le mot de passe par un bind avec le DN de l'utilisateur
            using (var userConn = await ConnectAsync(entry.Dn, password)) { }

            var display = GetAttr(entry, "displayName") ?? username;
            var groups = GetAttrValues(entry, "memberOf")
                .Select(dn => dn.Split(',')[0].Replace("CN=", "", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var account = GetAttr(entry, "sAMAccountName") ?? username;
            var role = ResolveRole(groups);
            return role is null ? null : (account, display, role, groups);
        }
        catch (LdapException ex) when (Unavailable(ex))
        {
            throw new DirectoryUnavailableException("Annuaire injoignable.", ex);
        }
        catch (LdapException ex)
        {
            // Saisie non authentifiée : sans retrait des sauts de ligne, un
            // identifiant forgé injecterait de fausses lignes dans le journal.
            var logged = username.Replace("\r", "").Replace("\n", "");
            logger.LogWarning(ex, "Échec d'authentification LDAP pour {User}", logged);
            return null;
        }
    }

    /// <summary>INC-13 : rôle et groupes d'un compte, lus par le compte de service (sans mot de passe de l'utilisateur).</summary>
    public async Task<DirectoryAccount?> FindAsync(string username)
    {
        using var svc = await ServiceConnectAsync();
        var filter = string.Format(config["Ldap:UserFilter"] ?? "(sAMAccountName={0})", EscapeFilter(username));
        var search = await svc.SearchAsync(BaseDn, LdapConnection.ScopeSub, filter, new[] { "memberOf" }, false);
        // Un AD renvoie des référents (DomainDnsZones…) : ils sont ignorés. Sans cela, un compte
        // inexistant levait une exception, prise pour un annuaire injoignable.
        var entry = await FirstEntryAsync(search);
        if (entry is null) return null;
        var groups = GetAttrValues(entry, "memberOf")
            .Select(dn => dn.Split(',')[0].Replace("CN=", "", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var role = ResolveRole(groups);
        return role is null ? null : new DirectoryAccount(role, groups);
    }

    private string? ResolveRole(HashSet<string> groups)
    {
        var map = config.GetSection("Ldap:Groups").GetChildren(); // Admin, Manager, User -> nom de groupe AD
        foreach (var role in new[] { "Admin", "Manager", "User" })
        {
            var g = config[$"Ldap:Groups:{role}"];
            if (g != null && groups.Contains(g)) return role;
        }
        return null; // utilisateur hors des groupes autorisés
    }

    /// <summary>
    /// ADM-10 : membres directs des groupes de rôle (`Ldap:Groups`), par recherche paginée sur
    /// `memberOf` ; un compte membre de plusieurs groupes n'apparaît qu'une fois, à son rôle le
    /// plus élevé.
    /// </summary>
    public async Task<List<RosterEntry>> RoleMembersAsync()
    {
        try
        {
            using var svc = await ServiceConnectAsync();
            var members = new Dictionary<string, (string Display, HashSet<string> Groups)>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in new[] { "Admin", "Manager", "User" })
            {
                var name = config[$"Ldap:Groups:{role}"];
                if (string.IsNullOrWhiteSpace(name)) continue;
                var groups = await SearchEntriesAsync(svc, BaseDn, $"(&(objectClass=group)(cn={EscapeFilter(name)}))", ["distinguishedName"], 1);
                if (groups.FirstOrDefault() is not { } group) continue;
                var users = await SearchEntriesAsync(svc, BaseDn, $"(&(objectClass=user)(memberOf={EscapeFilter(group.Dn)}))",
                    ["sAMAccountName", "displayName", "memberOf"], 1000);
                foreach (var u in users)
                {
                    var account = GetAttr(u, "sAMAccountName");
                    if (account is null || members.ContainsKey(account)) continue;
                    members[account] = (GetAttr(u, "displayName") ?? account, GetAttrValues(u, "memberOf")
                        .Select(dn => dn.Split(',')[0].Replace("CN=", "", StringComparison.OrdinalIgnoreCase))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase));
                }
            }
            var roleGroups = new[] { "Admin", "Manager", "User" }.Select(r => config[$"Ldap:Groups:{r}"]).OfType<string>().ToList();
            return members.Select(m => new RosterEntry(m.Key, m.Value.Display, ResolveRole(m.Value.Groups) ?? "User",
                    roleGroups.Where(g => m.Value.Groups.Contains(g)).ToList(), m.Value.Groups.Order().ToList()))
                .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (LdapException ex) when (Unavailable(ex))
        {
            throw new DirectoryUnavailableException("Annuaire injoignable.", ex);
        }
    }

    /// <summary>Recherche d'utilisateurs et de groupes pour l'affectation RACI.</summary>
    public async Task<List<DirectoryEntry>> SearchAsync(string query, int max = 15)
    {
        var results = new List<DirectoryEntry>();
        using var svc = await ServiceConnectAsync();
        var q = EscapeFilter(query);
        var filter = $"(|(&(objectClass=user)(|(sAMAccountName=*{q}*)(displayName=*{q}*)))(&(objectClass=group)(cn=*{q}*)))";
        var search = await svc.SearchAsync(BaseDn, LdapConnection.ScopeSub, filter,
            new[] { "sAMAccountName", "displayName", "cn", "objectClass" }, false);
        while (results.Count < max && await search.HasMoreAsync())
        {
            try
            {
                var e = await search.NextAsync();
                var classes = GetAttrValues(e, "objectClass");
                bool isGroup = classes.Contains("group");
                var id = GetAttr(e, isGroup ? "cn" : "sAMAccountName") ?? "";
                var name = GetAttr(e, "displayName") ?? GetAttr(e, "cn") ?? id;
                if (id != "") results.Add(new DirectoryEntry(id, name, isGroup ? "Group" : "User"));
            }
            catch (LdapReferralException) { /* ignorer les référents */ }
        }
        return results;
    }

    private static string? GetAttr(LdapEntry e, string attr)
    {
        try { return e.GetOrDefault(attr, null!)?.StringValue; }
        catch { return null; }
    }

    private static IEnumerable<string> GetAttrValues(LdapEntry e, string attr)
    {
        try { return e.GetOrDefault(attr, null!)?.StringValueArray ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string EscapeFilter(string v) => v
        .Replace(@"\", @"\5c").Replace("*", @"\2a")
        .Replace("(", @"\28").Replace(")", @"\29").Replace("\0", @"\00");
}
