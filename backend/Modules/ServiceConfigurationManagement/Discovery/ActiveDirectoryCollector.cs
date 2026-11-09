using Novell.Directory.Ldap;

namespace SAloha.Api.Modules.ServiceConfigurationManagement.Discovery;

/// <summary>
/// Comptes d'ordinateur actifs de l'annuaire (LDAP / LDAPS) : nom DNS, système et version.
/// Recherche paginée (un AD limite une page à 1 000 résultats). Liaison avec le compte de
/// domaine choisi dans le coffre, sinon avec le compte de service de l'API ; base de la
/// recherche : la cible de la tâche, sinon <c>Ldap:BaseDn</c>.
/// </summary>
public class ActiveDirectoryCollector(IConfiguration config) : IDiscoveryCollector
{
    public string ScanType => "ActiveDirectory";
    // Comptes d'ordinateur non désactivés (bit 2 de userAccountControl).
    private const string Filter = "(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";
    private static readonly string[] Attributes = ["name", "dNSHostName", "operatingSystem", "operatingSystemVersion", "distinguishedName"];

    public async Task<List<Observation>> CollectAsync(CollectContext context, CancellationToken ct)
    {
        var timeoutMs = (int.TryParse(config["Ldap:TimeoutSeconds"], out var s) && s > 0 ? s : 5) * 1000;
        using var conn = new LdapConnection { SecureSocketLayer = bool.Parse(config["Ldap:UseSsl"] ?? "false"), ConnectionTimeout = timeoutMs };
        Core.Auth.LdapTls.Apply(conn, config);   // LDAPS : autorité de l'annuaire (S2)
        var constraints = conn.Constraints;
        constraints.TimeLimit = timeoutMs * 6;
        conn.Constraints = constraints;
        await conn.ConnectAsync(config["Ldap:Host"]!, int.Parse(config["Ldap:Port"] ?? "389"));
        var (dn, password) = context.Credential is { } c
            ? (BindName(c), context.Secret ?? "")
            : (config["Ldap:BindUser"]!, config["Ldap:BindPassword"]!);
        await conn.BindAsync(dn, password);

        var baseDn = string.IsNullOrWhiteSpace(context.Job.TargetRange) ? config["Ldap:BaseDn"]! : context.Job.TargetRange.Trim();
        // Références d'un AD (Configuration, zones DNS) sautées : voir LdapService.SearchEntriesAsync.
        var entries = await Core.Auth.LdapService.SearchEntriesAsync(conn, baseDn, Filter, Attributes, 1000, ct);
        return entries.Select(e =>
        {
            var os = Attr(e, "operatingSystem");
            var version = Attr(e, "operatingSystemVersion");
            var server = os?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true;
            return new Observation("ActiveDirectory",
                Hostname: (Attr(e, "dNSHostName") ?? Attr(e, "name"))?.ToLowerInvariant(),
                Os: os is null ? null : version is null ? os : $"{os} ({version})",
                Type: server ? "Serveur" : "Poste de travail", InDirectory: true, Raw: e.Dn);
        }).Where(o => o.Hostname is not null).ToList();
    }

    /// <summary>Nom de liaison d'un compte de domaine : UPN ou DOMAINE\compte tels quels, sinon préfixé du domaine.</summary>
    private static string BindName(DiscoveryCredential c)
    {
        var user = c.Username ?? "";
        return user.Contains('@') || user.Contains('\\') || string.IsNullOrWhiteSpace(c.Domain) ? user : $"{c.Domain}\\{user}";
    }

    private static string? Attr(LdapEntry e, string name)
    {
        try { return e.GetOrDefault(name, null!)?.StringValue is { Length: > 0 } v ? v : null; }
        catch (KeyNotFoundException) { return null; }
    }
}
