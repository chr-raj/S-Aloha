using Microsoft.EntityFrameworkCore;
using SAloha.Api.Core.Data;

namespace SAloha.Api.Modules.ServiceConfigurationManagement.Discovery;

/// <summary>
/// Sas de staging (CFG-40) et rapprochement (CFG-41) : fusionne les observations des
/// collecteurs en actifs découverts, les compare aux CI d'équipement du même périmètre,
/// rapproche seul un actif sûr (score ≥ 80) et laisse les autres à la décision du
/// gestionnaire. Rien n'entre dans la CMDB sans passer par ici.
/// </summary>
public class MatchingEngine(AppDbContext db, IConfiguration config)
{
    public const string System = "système";
    public const int AutoThreshold = 80;

    public record Match(ConfigurationItem? Ci, int Score, string Rule, string Action, string? Conflict);
    public record IngestResult(int Seen, int Added, int AutoReconciled, int Conflicts);

    private bool AutoReconcile => config.GetValue("Discovery:AutoReconcile", true);

    /// <summary>Meilleur rapprochement : numéro de série (100), MAC (90), nom d'hôte (80), IP seule (50).</summary>
    public async Task<Match> FindAsync(DiscoveredAsset a)
    {
        // Seuls les CI d'équipement : un hôte « erp » ne se rapproche pas de l'application « ERP ».
        var types = ConfigurationItem.HardwareTypes;
        var candidates = db.ConfigurationItems.AsNoTracking()
            .Where(c => c.Status != "Retiré" && c.PerimeterId == a.PerimeterId && types.Contains(c.CiType));
        ConfigurationItem? ci = null;
        var (score, rule) = (0, "None");
        if (a.SerialNumber is { } serial)
        {
            var key = serial.ToLower();
            ci = await candidates.FirstOrDefaultAsync(c => c.SerialNumber != null && c.SerialNumber.ToLower() == key);
            (score, rule) = (100, "SerialNumber");
        }
        if (ci is null && a.MacAddress is { } mac)
        {
            ci = await candidates.FirstOrDefaultAsync(c => c.MacAddress == mac);
            (score, rule) = (90, "MacAddress");
        }
        if (ci is null && a.Hostname is { } host)
        {
            var full = host.ToLower();
            var shortName = full.Split('.')[0];
            ci = await candidates.FirstOrDefaultAsync(c => c.Title.ToLower() == full || c.Title.ToLower() == shortName);
            (score, rule) = (80, "Hostname");
        }
        if (ci is null && a.IpAddress is { } ip)
        {
            ci = await candidates.FirstOrDefaultAsync(c => c.IpAddress == ip);
            if (ci is not null)
                return new Match(ci, 50, "IpAddress", "Conflict",
                    $"Rapproché de {ci.Reference} par la seule adresse IP : vérifier qu'il s'agit du même équipement (l'adresse a pu être réattribuée).");
        }
        if (ci is null) return new Match(null, 0, "None", "CreateNew", null);
        // Critères contradictoires : même nom ou même MAC, mais un autre numéro de série.
        if (rule != "SerialNumber" && a.SerialNumber is { } s && ci.SerialNumber is { } other
            && !string.Equals(s, other, StringComparison.OrdinalIgnoreCase))
            return new Match(ci, score, rule, "Conflict",
                $"Rapproché de {ci.Reference} ({Label(rule)}), mais numéro de série différent : {s} découvert, {other} en CMDB.");
        return new Match(ci, score, rule, "UpdateExisting", null);
    }

    private static string Label(string rule) => rule switch
    {
        "SerialNumber" => "numéro de série", "MacAddress" => "adresse MAC", "Hostname" => "nom d'hôte", _ => "adresse IP"
    };

    /// <summary>
    /// Entrée des observations d'une tâche dans le sas : un équipement déjà présent (même série,
    /// même MAC, même nom, ou même IP sans indice contraire) est mis à jour, pas dupliqué.
    /// </summary>
    public async Task<IngestResult> IngestAsync(DiscoveryJob job, IEnumerable<Observation> observations, CancellationToken ct = default)
    {
        var staged = await db.Set<DiscoveredAsset>().Where(x => x.PerimeterId == job.PerimeterId).ToListAsync(ct);
        int seen = 0, added = 0, auto = 0, conflicts = 0;
        var touched = new HashSet<DiscoveredAsset>();
        var hosted = new List<(DiscoveredAsset Asset, string Host, string? Reason)>();
        var flows = new List<(DiscoveredAsset Asset, IReadOnlyList<Flow> Flows, string Source)>();
        foreach (var raw in observations)
        {
            var o = raw with
            {
                IpAddress = CiRules.NormalizeIp(raw.IpAddress), MacAddress = CiRules.NormalizeMac(raw.MacAddress),
                Hostname = string.IsNullOrWhiteSpace(raw.Hostname) ? null : raw.Hostname.Trim().TrimEnd('.').ToLowerInvariant(),
                SerialNumber = string.IsNullOrWhiteSpace(raw.SerialNumber) ? null : raw.SerialNumber.Trim()
            };
            if (o.IpAddress is null && o.Hostname is null && o.MacAddress is null && o.SerialNumber is null) continue;
            seen++;
            var asset = FindStaged(staged, o);
            if (asset is null)
            {
                asset = new DiscoveredAsset { PerimeterId = job.PerimeterId, FirstSeenAt = DateTime.UtcNow };
                db.Set<DiscoveredAsset>().Add(asset);
                staged.Add(asset);
                added++;
            }
            Merge(asset, o, job.Id);
            touched.Add(asset);
            if (o.Software is not null) await ReplaceSoftwareAsync(asset, o.Software, ct);
            if (o.HostedBy is { } host) hosted.Add((asset, host.Trim().ToLowerInvariant(), o.HostedReason));
            if (o.Flows is { Count: > 0 } f) flows.Add((asset, f, o.Source));
        }
        await VendorsFromMacAsync(touched, ct);

        foreach (var asset in touched)
        {
            ct.ThrowIfCancellationRequested();
            if (asset.Status == DiscoveredAsset.Ignored) continue;
            // Déjà rapproché : la découverte rafraîchit l'identification technique de son CI, tant
            // qu'il reste au périmètre de l'actif (PER-06) ; déplacé ailleurs (PER-07), l'actif
            // repasse par FindAsync, limité à son périmètre.
            if (asset.Status == DiscoveredAsset.Reconciled && asset.MatchedCiId is { } ciId
                && await db.ConfigurationItems.FirstOrDefaultAsync(
                    c => c.Id == ciId && c.Status != "Retiré" && c.PerimeterId == asset.PerimeterId, ct) is { } current)
            {
                if (await ApplyAsync(current, asset) is { } reason) { MarkConflict(asset, reason); conflicts++; }
                continue;
            }
            var match = await FindAsync(asset);
            asset.ConfidenceScore = match.Score;
            asset.MatchRule = match.Rule;
            asset.SuggestedAction = match.Action;
            asset.MatchedCiId = match.Ci?.Id;
            asset.ConflictReason = match.Conflict;
            if (match.Action == "Conflict") { asset.Status = DiscoveredAsset.InConflict; conflicts++; continue; }
            asset.Status = DiscoveredAsset.ToReconcile;
            if (match.Action != "UpdateExisting" || match.Score < AutoThreshold || !AutoReconcile) continue;
            var target = await db.ConfigurationItems.FirstAsync(c => c.Id == match.Ci!.Id, ct);
            if (await ApplyAsync(target, asset) is { } conflict) { MarkConflict(asset, conflict); conflicts++; continue; }
            MarkReconciled(asset, target, System);
            auto++;
        }
        await db.SaveChangesAsync(ct);
        if (hosted.Count > 0) await ProposeHostingAsync(job, touched, hosted, ct);
        if (flows.Count > 0) await ProposeFlowsAsync(job, flows, ct);
        return new IngestResult(seen, added, auto, conflicts);
    }

    /// <summary>
    /// CFG-55 : « hôte Héberge machine » proposé pour chaque machine dont l'hôte a été vu par la
    /// même tâche (nom d'hôte ou adresse IP). Une proposition existante est seulement revue ;
    /// écartée, elle n'est pas reproposée.
    /// </summary>
    private async Task ProposeHostingAsync(DiscoveryJob job, HashSet<DiscoveredAsset> touched,
        List<(DiscoveredAsset Asset, string Host, string? Reason)> hosted, CancellationToken ct)
    {
        DiscoveredAsset? HostOf(string host) => touched.FirstOrDefault(a => a.Hostname == host || a.IpAddress == host
            || a.Hostname is not null && a.Hostname.Split('.')[0] == host.Split('.')[0] && !char.IsDigit(host[0]));
        var ids = hosted.Select(h => h.Asset.Id).ToList();
        var existing = await db.RelationProposals.Where(r => r.Type == "Héberge" && ids.Contains(r.TargetAssetId)).ToListAsync(ct);
        foreach (var (machine, host, reason) in hosted)
        {
            if (HostOf(host) is not { } server || server == machine) continue;
            var current = existing.FirstOrDefault(r => r.SourceAssetId == server.Id && r.TargetAssetId == machine.Id);
            if (current is not null) { current.LastSeenAt = DateTime.UtcNow; continue; }
            var proposal = new RelationProposal
            {
                PerimeterId = job.PerimeterId, SourceAssetId = server.Id, TargetAssetId = machine.Id, Type = "Héberge",
                Reason = Truncated(reason ?? $"{host} héberge {machine.Hostname ?? machine.IpAddress}", 300)
            };
            db.RelationProposals.Add(proposal);
            existing.Add(proposal);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// CFG-56 : « client Se connecte à serveur » pour chaque connexion établie vers (ou depuis)
    /// une adresse IP d'un autre actif du sas du même périmètre. Le motif réunit les ports vus.
    /// </summary>
    private async Task ProposeFlowsAsync(DiscoveryJob job, List<(DiscoveredAsset Asset, IReadOnlyList<Flow> Flows, string Source)> seen,
        CancellationToken ct)
    {
        var peers = seen.SelectMany(s => s.Flows.Select(f => f.PeerIp)).Distinct().ToList();
        var byIp = (await db.Set<DiscoveredAsset>().Where(a => a.PerimeterId == job.PerimeterId && a.IpAddress != null && peers.Contains(a.IpAddress))
            .ToListAsync(ct)).GroupBy(a => a.IpAddress!).ToDictionary(g => g.Key, g => g.First());
        var ids = seen.Select(s => s.Asset.Id).ToList();
        var existing = await db.RelationProposals
            .Where(r => r.Type == "Se connecte à" && (ids.Contains(r.SourceAssetId) || ids.Contains(r.TargetAssetId))).ToListAsync(ct);
        foreach (var (asset, list, source) in seen)
            foreach (var flow in list)
            {
                if (!byIp.TryGetValue(flow.PeerIp, out var peer) || peer.Id == asset.Id) continue;
                var (from, to) = flow.Outbound ? (asset, peer) : (peer, asset);
                var port = $"port {flow.Port}";
                var current = existing.FirstOrDefault(r => r.SourceAssetId == from.Id && r.TargetAssetId == to.Id);
                if (current is not null)
                {
                    current.LastSeenAt = DateTime.UtcNow;
                    if (!current.Reason.Contains(port) && current.Reason.Length + port.Length + 2 <= 300) current.Reason += ", " + port;
                    continue;
                }
                var proposal = new RelationProposal
                {
                    PerimeterId = job.PerimeterId, SourceAssetId = from.Id, TargetAssetId = to.Id, Type = "Se connecte à",
                    Reason = Truncated($"{(source == "Ssh" ? "SSH" : source)} : connexion établie de {from.Hostname ?? from.IpAddress} vers {to.Hostname ?? to.IpAddress}, {port}", 300)
                };
                db.RelationProposals.Add(proposal);
                existing.Add(proposal);
            }
        await db.SaveChangesAsync(ct);
    }

    private static string Truncated(string v, int max) => v.Length <= max ? v : v[..max];

    /// <summary>CFG-52 : le dernier relevé des logiciels remplace le précédent.</summary>
    private async Task ReplaceSoftwareAsync(DiscoveredAsset asset, IReadOnlyList<SoftwareItem> software, CancellationToken ct)
    {
        if (asset.Id != 0) await db.Entry(asset).Collection(a => a.Software).LoadAsync(ct);
        asset.Software.Clear();
        asset.Software.AddRange(software.Select(s => new DiscoveredSoftware { Name = s.Name, Version = s.Version }));
    }

    /// <summary>
    /// CFG-50 : sans constructeur connu par une source plus précise (SNMP), celui du préfixe
    /// MAC dans la base OUI importée ; rien si la base est vide ou l'adresse aléatoire.
    /// </summary>
    private async Task VendorsFromMacAsync(IEnumerable<DiscoveredAsset> assets, CancellationToken ct)
    {
        var waiting = assets.Where(a => a.Vendor is null).Select(a => (Asset: a, Prefix: MacVendors.PrefixOf(a.MacAddress)))
            .Where(x => x.Prefix is not null).ToList();
        if (waiting.Count == 0) return;
        var prefixes = waiting.Select(x => x.Prefix!).Distinct().ToList();
        var names = await db.MacVendors.AsNoTracking().Where(v => prefixes.Contains(v.Prefix)).ToDictionaryAsync(v => v.Prefix, v => v.Organization, ct);
        foreach (var (asset, prefix) in waiting)
            if (names.TryGetValue(prefix!, out var name)) asset.Vendor = name;
    }

    private static DiscoveredAsset? FindStaged(List<DiscoveredAsset> staged, Observation o)
    {
        string? Short(string? h) => h?.Split('.')[0];
        return (o.SerialNumber is { } s ? staged.FirstOrDefault(x => string.Equals(x.SerialNumber, s, StringComparison.OrdinalIgnoreCase)) : null)
               ?? (o.MacAddress is { } m ? staged.FirstOrDefault(x => x.MacAddress == m) : null)
               ?? (o.Hostname is { } h ? staged.FirstOrDefault(x => x.Hostname == h || (x.Hostname is not null && Short(x.Hostname) == Short(h)
                   && (!x.Hostname.Contains('.') || !h.Contains('.')))) : null)
               ?? (o.IpAddress is { } ip ? staged.FirstOrDefault(x => x.IpAddress == ip
                   && (x.Hostname is null || o.Hostname is null || x.Hostname == o.Hostname)
                   && (x.MacAddress is null || o.MacAddress is null || x.MacAddress == o.MacAddress)
                   && (x.SerialNumber is null || o.SerialNumber is null)) : null);
    }

    /// <summary>Fusion d'une observation : la plus précise l'emporte (annuaire et SNMP avant le balayage pour le système).</summary>
    private static void Merge(DiscoveredAsset a, Observation o, int jobId)
    {
        a.IpAddress = o.IpAddress ?? a.IpAddress;
        a.MacAddress = o.MacAddress ?? a.MacAddress;
        // Un nom complet (FQDN) remplace un nom court, pas l'inverse.
        if (o.Hostname is not null && (a.Hostname is null || o.Hostname.Contains('.') || !a.Hostname.Contains('.'))) a.Hostname = o.Hostname;
        a.SerialNumber ??= o.SerialNumber;
        // Le balayage (bannières) ne remplace pas un constructeur déjà connu par SNMP ou l'OUI.
        if (o.Vendor is not null && (a.Vendor is null || o.Source != "IpSweep")) a.Vendor = o.Vendor;
        a.Model = o.Model ?? a.Model;
        if (o.Os is not null && (a.DetectedOs is null || o.Source != "IpSweep")) a.DetectedOs = o.Os;
        if (o.Type != "Autre" && (a.DetectedType == "Autre" || o.Source != "IpSweep")) a.DetectedType = o.Type;
        a.Location = o.Location ?? a.Location;
        a.OpenPorts = o.OpenPorts ?? a.OpenPorts;
        a.Banners = o.Banners ?? a.Banners;
        a.Interfaces = o.Interfaces ?? a.Interfaces;
        a.Services = o.Services ?? a.Services;
        a.ListeningPorts = o.ListeningPorts ?? a.ListeningPorts;
        a.InDirectory |= o.InDirectory;
        a.RawData = o.Raw ?? a.RawData;
        var sources = a.Sources.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!sources.Contains(o.Source)) sources.Add(o.Source);
        a.Sources = string.Join(",", sources);
        a.JobId = jobId;
        a.LastSeenAt = DateTime.UtcNow;
        Truncate(a);
    }

    /// <summary>Les collecteurs lisent des valeurs arbitraires : bornées aux tailles des colonnes.</summary>
    private static void Truncate(DiscoveredAsset a)
    {
        static string? Cut(string? v, int max) => v is null || v.Length <= max ? v : v[..max];
        a.Hostname = Cut(a.Hostname, 200); a.SerialNumber = Cut(a.SerialNumber, 100);
        a.Vendor = Cut(a.Vendor, 100); a.Model = Cut(a.Model, 100); a.DetectedOs = Cut(a.DetectedOs, 150);
        a.Location = Cut(a.Location, 150); a.OpenPorts = Cut(a.OpenPorts, 250); a.Banners = Cut(a.Banners, 500); a.Interfaces = Cut(a.Interfaces, 8000); a.Services = Cut(a.Services, 8000); a.ListeningPorts = Cut(a.ListeningPorts, 250);
    }

    /// <summary>
    /// Reporte l'identification technique d'un actif sur son CI : IP, MAC et système à jour ;
    /// constructeur, modèle et numéro de série seulement s'ils manquent (une saisie n'est pas
    /// écrasée). Un numéro de série déjà porté par un autre CI est un conflit (CFG-44) : rien
    /// n'est modifié et le motif est renvoyé.
    /// </summary>
    public async Task<string?> ApplyAsync(ConfigurationItem ci, DiscoveredAsset a)
    {
        if (ci.SerialNumber is null && a.SerialNumber is { } serial && ConfigurationItem.HardwareTypes.Contains(ci.CiType))
        {
            var key = serial.ToLower();
            var holder = await db.ConfigurationItems.AsNoTracking()
                .Where(c => c.Id != ci.Id && c.Status != "Retiré" && c.SerialNumber != null && c.SerialNumber.ToLower() == key)
                .Select(c => c.Reference).FirstOrDefaultAsync();
            if (holder is not null) return $"Le numéro de série {serial} est déjà porté par {holder}.";
            ci.SerialNumber = serial;
        }
        if (ConfigurationItem.HardwareTypes.Contains(ci.CiType))
        {
            ci.Vendor ??= a.Vendor;
            ci.Model ??= a.Model;
        }
        var changed = (a.IpAddress is not null && ci.IpAddress != a.IpAddress) || (a.MacAddress is not null && ci.MacAddress != a.MacAddress)
                      || (a.DetectedOs is not null && ci.OperatingSystem != a.DetectedOs);
        ci.IpAddress = a.IpAddress ?? ci.IpAddress;
        ci.MacAddress = a.MacAddress ?? ci.MacAddress;
        ci.OperatingSystem = a.DetectedOs ?? ci.OperatingSystem;
        if (changed || db.Entry(ci).State == EntityState.Modified) ci.UpdatedAt = DateTime.UtcNow;
        return null;
    }

    public static void MarkReconciled(DiscoveredAsset a, ConfigurationItem ci, string by)
    {
        a.Status = DiscoveredAsset.Reconciled;
        a.MatchedCiId = ci.Id;
        a.ConflictReason = null;
        a.ReconciledAt = DateTime.UtcNow;
        a.ReconciledBy = by;
    }

    private static void MarkConflict(DiscoveredAsset a, string reason)
    {
        a.Status = DiscoveredAsset.InConflict;
        a.SuggestedAction = "Conflict";
        a.ConflictReason = reason.Length > 300 ? reason[..300] : reason;
    }
}
