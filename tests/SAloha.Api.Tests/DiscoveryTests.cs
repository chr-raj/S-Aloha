using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAloha.Api.Modules.ServiceConfigurationManagement;
using SAloha.Api.Modules.ServiceConfigurationManagement.Discovery;

namespace SAloha.Api.Tests;

/// <summary>Découverte et sas de staging : CFG-40 à CFG-46 (collecteurs factices, FakeCollector).</summary>
[Collection("api")]
public class DiscoveryTests(ApiFixture api)
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode} : {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    private static int Id(JsonElement e) => e.GetProperty("id").GetInt32();
    private static string Str(JsonElement e, string p) => e.GetProperty(p).GetString()!;

    /// <summary>Périmètre propre au test (les actifs du sas sont propres à un périmètre) et son gestionnaire.</summary>
    private async Task<(int Perimeter, HttpClient Admin, HttpClient Manager)> Perimeter(string tag)
    {
        var admin = api.Client("Admin", $"admin.{tag}");
        var p = Id(await Json(await admin.PostAsJsonAsync("/api/admin/perimeters", new { name = $"Site {tag}" })));
        await Json(await admin.PostAsJsonAsync($"/api/admin/perimeters/{p}/assignments",
            new { principalType = "User", principalId = $"chef.{tag}", principalDisplayName = "Chef", role = "Manager" }));
        return (p, admin, api.Client("User", $"chef.{tag}"));
    }

    private static object Server(string title, int perimeter, string? serial = null, string? ip = null) => new
    {
        title, ciType = "Serveur", environment = "Production", status = "En service", perimeterId = perimeter,
        ownerType = "User", ownerId = "owner.cfg", ownerDisplayName = "Owner", serialNumber = serial, ipAddress = ip
    };

    private static async Task<JsonElement> RunAndWait(HttpClient client, int jobId)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/discovery/jobs/{jobId}/run", null)).StatusCode);
        for (var i = 0; i < 100; i++)
        {
            var job = await Json(await client.GetAsync($"/api/discovery/jobs/{jobId}"));
            if (Str(job, "lastStatus") is DiscoveryJob.Succeeded or DiscoveryJob.Failed) return job;
            await Task.Delay(100);
        }
        throw new TimeoutException("Le scan ne s'est pas terminé.");
    }

    private static async Task<List<JsonElement>> Staged(HttpClient client, int perimeter) =>
        (await Json(await client.GetAsync($"/api/discovery/staging?perimeter={perimeter}"))).EnumerateArray().ToList();

    [Fact]
    public async Task CFG_45_le_secret_est_chiffre_au_repos_jamais_renvoye_et_dechiffre_pour_le_scan()
    {
        var (p, admin, manager) = await Perimeter("cfg45");
        var created = await Json(await admin.PostAsJsonAsync("/api/discovery/credentials",
            new { name = "Communauté du site", credentialType = "SnmpV2c", secret = "communaute-cfg45", perimeterId = p }));
        Assert.True(created.GetProperty("hasSecret").GetBoolean());

        var list = await admin.GetStringAsync("/api/discovery/credentials");
        Assert.DoesNotContain("communaute-cfg45", list);
        await using (var db = api.Database())
        {
            var stored = await db.DiscoveryCredentials.SingleAsync(c => c.Id == Id(created));
            Assert.StartsWith("v1:", stored.EncryptedSecret);
            Assert.DoesNotContain("communaute-cfg45", stored.EncryptedSecret);
        }
        // Le gestionnaire lit la liste (pour choisir l'identifiant d'une tâche), seul l'Admin l'écrit.
        Assert.DoesNotContain("communaute-cfg45", await manager.GetStringAsync("/api/discovery/credentials"));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/discovery/credentials",
            new { name = "Pirate", credentialType = "SnmpV2c", secret = "x", perimeterId = p })).StatusCode);

        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs", new
        {
            name = "SNMP du site", scanType = "Snmp", targetRange = "10.45.0.0/24", credentialId = Id(created), perimeterId = p
        }));
        Assert.Equal(DiscoveryJob.Succeeded, Str(await RunAndWait(manager, Id(job)), "lastStatus"));
        Assert.Equal("communaute-cfg45", FakeCollector.SecretsReceived["10.45.0.0/24"]);

        // Rotation : le nouveau secret remplace l'ancien ; un identifiant utilisé ne se supprime pas (409).
        await Json(await admin.PutAsJsonAsync($"/api/discovery/credentials/{Id(created)}",
            new { name = "Communauté du site", credentialType = "SnmpV2c", secret = "nouvelle-cfg45" }));
        await RunAndWait(manager, Id(job));
        Assert.Equal("nouvelle-cfg45", FakeCollector.SecretsReceived["10.45.0.0/24"]);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/discovery/credentials/{Id(created)}")).StatusCode);
    }

    [Fact]
    public async Task CFG_46_une_tache_valide_sa_plage_ses_ports_son_identifiant_et_ne_se_relance_pas_en_cours()
    {
        var (p, _, manager) = await Perimeter("cfg46");
        async Task<HttpStatusCode> Create(object dto) => (await manager.PostAsJsonAsync("/api/discovery/jobs", dto)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "Trop large", scanType = "IpSweep", targetRange = "10.0.0.0/16", perimeterId = p }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "Port", scanType = "IpSweep", targetRange = "10.0.0.0/24", portList = "22,99999", perimeterId = p }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "SNMP", scanType = "Snmp", targetRange = "10.0.0.0/24", perimeterId = p }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "Fenêtre", scanType = "IpSweep", targetRange = "10.0.0.1", windowStart = "25:00", windowEnd = "06:00", perimeterId = p }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "SSH", scanType = "Ssh", targetRange = "10.0.0.1", perimeterId = p }));

        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Balayage", scanType = "IpSweep", targetRange = "10.46.0.0/24", perimeterId = p }));
        Assert.Equal(DiscoveryJob.NeverRun, Str(job, "lastStatus"));
        Assert.Equal("20:00", Str(job, "windowStart"));
        await using (var db = api.Database())
        {
            var j = await db.DiscoveryJobs.SingleAsync(x => x.Id == Id(job));
            j.LastStatus = DiscoveryJob.Running;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync($"/api/discovery/jobs/{Id(job)}/run", null)).StatusCode);
        // Un simple utilisateur ne voit pas les tâches ; seul l'Admin supprime.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client("User", "lambda.cfg46").GetAsync("/api/discovery/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync($"/api/discovery/jobs/{Id(job)}")).StatusCode);
    }

    [Fact]
    public async Task CFG_40_CFG_41_CFG_42_entree_dans_le_sas_rapprochement_et_deduplication()
    {
        var (p, admin, manager) = await Perimeter("cfg41");
        var bySerial = await Json(await admin.PostAsJsonAsync("/api/configuration-items", Server("srv-serie-41", p, serial: "SN-41-A")));
        var byName = await Json(await admin.PostAsJsonAsync("/api/configuration-items", Server("srv-nom-41", p)));
        await Json(await admin.PostAsJsonAsync("/api/configuration-items", Server("srv-ip-41", p, ip: "10.41.0.30")));
        await Json(await admin.PostAsJsonAsync("/api/configuration-items", Server("srv-autre-41", p, serial: "SN-41-D")));
        // Une application homonyme n'est jamais rapprochée d'un hôte.
        await Json(await admin.PostAsJsonAsync("/api/configuration-items", new
        {
            title = "inconnu-41", ciType = "Application", environment = "Production", status = "Planifié", perimeterId = p
        }));

        FakeCollector.Observations["10.41.0.0/24"] =
        [
            new("IpSweep", IpAddress: "10.41.0.10", SerialNumber: "sn-41-a", Os: "Linux / Unix", Type: "Serveur"),
            new("IpSweep", IpAddress: "10.41.0.20", Hostname: "SRV-NOM-41.corp.local", MacAddress: "00-1a-2b-3c-4d-41", Type: "Serveur"),
            new("IpSweep", IpAddress: "10.41.0.30", Type: "Serveur"),
            new("IpSweep", IpAddress: "10.41.0.40", Hostname: "srv-autre-41", SerialNumber: "SN-41-X", Type: "Serveur"),
            new("IpSweep", IpAddress: "10.41.0.50", Hostname: "inconnu-41.corp.local", Type: "Poste de travail", OpenPorts: "445"),
            new("IpSweep", IpAddress: "10.41.0.60", Hostname: "pc-annuaire-41.corp.local", Type: "Poste de travail"),
        ];
        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Balayage 41", scanType = "IpSweep", targetRange = "10.41.0.0/24", perimeterId = p }));
        var done = await RunAndWait(manager, Id(job));
        Assert.Equal(6, done.GetProperty("lastFoundCount").GetInt32());

        var assets = await Staged(manager, p);
        JsonElement At(string ip) => assets.Single(a => Str(a, "ipAddress") == ip);
        // Numéro de série (100) et nom d'hôte (80) : rapprochés seuls, le CI reçoit l'identification technique.
        Assert.Equal((DiscoveredAsset.Reconciled, 100), (Str(At("10.41.0.10"), "status"), At("10.41.0.10").GetProperty("confidenceScore").GetInt32()));
        Assert.Equal((DiscoveredAsset.Reconciled, 80), (Str(At("10.41.0.20"), "status"), At("10.41.0.20").GetProperty("confidenceScore").GetInt32()));
        var updated = await Json(await admin.GetAsync($"/api/configuration-items/{Id(byName)}"));
        Assert.Equal(("10.41.0.20", "00:1A:2B:3C:4D:41"), (Str(updated, "ipAddress"), Str(updated, "macAddress")));
        Assert.Equal("Linux / Unix", Str(await Json(await admin.GetAsync($"/api/configuration-items/{Id(bySerial)}")), "operatingSystem"));
        // Adresse IP seule (50) ou numéro de série contradictoire : en conflit, rien n'est modifié.
        Assert.Equal((DiscoveredAsset.InConflict, "IpAddress"), (Str(At("10.41.0.30"), "status"), Str(At("10.41.0.30"), "matchRule")));
        Assert.Equal(DiscoveredAsset.InConflict, Str(At("10.41.0.40"), "status"));
        Assert.Contains("SN-41-D", Str(At("10.41.0.40"), "conflictReason"));
        // Aucun rapprochement : à créer, et inconnu tant qu'il n'est pas dans l'annuaire (CFG-42).
        var unknown = At("10.41.0.50");
        Assert.Equal((DiscoveredAsset.ToReconcile, "CreateNew"), (Str(unknown, "status"), Str(unknown, "suggestedAction")));
        Assert.True(unknown.GetProperty("unknown").GetBoolean());

        // Synchronisation de l'annuaire : l'ordinateur rejoint l'actif déjà vu (même nom), sans doublon.
        FakeCollector.Observations["DC=cfg41"] = [new("ActiveDirectory", Hostname: "pc-annuaire-41.corp.local", Os: "Windows 11 Pro", Type: "Poste de travail", InDirectory: true)];
        var ad = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Annuaire 41", scanType = "ActiveDirectory", targetRange = "DC=cfg41", perimeterId = p }));
        await RunAndWait(manager, Id(ad));
        await RunAndWait(manager, Id(job));
        assets = await Staged(manager, p);
        Assert.Equal(6, assets.Count);
        var known = At("10.41.0.60");
        Assert.False(known.GetProperty("unknown").GetBoolean());
        Assert.Equal(("Windows 11 Pro", "IpSweep,ActiveDirectory"), (Str(known, "detectedOs"), Str(known, "sources")));
        Assert.True(At("10.41.0.50").GetProperty("unknown").GetBoolean());

        // Un gestionnaire d'un autre périmètre ne voit pas ces actifs.
        var (_, _, other) = await Perimeter("cfg41-autre");
        Assert.Empty(await Staged(other, p));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/discovery/staging/{Id(unknown)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client("User", "lambda.cfg41").GetAsync("/api/discovery/staging")).StatusCode);
    }

    [Fact]
    public async Task CFG_40_reconcilier_cree_un_CI_aux_regles_du_registre_ou_met_a_jour_le_CI_choisi()
    {
        var (p, admin, manager) = await Perimeter("cfg40");
        var target = await Json(await admin.PostAsJsonAsync("/api/configuration-items", Server("srv-cible-40", p, ip: "10.40.0.9")));
        FakeCollector.Observations["10.40.0.0/24"] =
        [
            new("IpSweep", IpAddress: "10.40.0.5", Hostname: "pc-nouveau-40", Type: "Poste de travail"),
            new("IpSweep", IpAddress: "10.40.0.9", Os: "Linux / Unix", Type: "Serveur"),
            new("IpSweep", IpAddress: "10.40.0.7", Hostname: "imprimante-40", Type: "Autre"),
        ];
        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Balayage 40", scanType = "IpSweep", targetRange = "10.40.0.0/24", perimeterId = p }));
        await RunAndWait(manager, Id(job));
        var assets = await Staged(manager, p);
        int At(string ip) => Id(assets.Single(a => Str(a, "ipAddress") == ip));

        // La fiche propose un CI pré-rempli.
        var sheet = await Json(await manager.GetAsync($"/api/discovery/staging/{At("10.40.0.5")}"));
        Assert.Equal(("pc-nouveau-40", "Poste de travail"), (Str(sheet.GetProperty("proposal"), "title"), Str(sheet.GetProperty("proposal"), "ciType")));

        object Ci(string status, string? owner = null, string? serial = null) => new
        {
            action = "create", ci = new
            {
                title = "pc-nouveau-40", ciType = "Poste de travail", environment = "Production", status, location = "Siège, 2e étage",
                ownerType = owner is null ? null : "User", ownerId = owner, ownerDisplayName = owner, vendor = "Dell", serialNumber = serial,
                ipAddress = "10.40.0.5"
            }
        };
        var reconcile = $"/api/discovery/staging/{At("10.40.0.5")}/reconcile";
        // CFG-11 puis CFG-44 : mêmes contrôles que le registre.
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync(reconcile, Ci("En service"))).StatusCode);
        var noSerial = await manager.PostAsJsonAsync(reconcile, Ci("En service", owner: "tiana.cfg40"));
        Assert.Equal(HttpStatusCode.BadRequest, noSerial.StatusCode);
        Assert.Contains("CFG-44", await noSerial.Content.ReadAsStringAsync());
        var created = await Json(await manager.PostAsJsonAsync(reconcile, Ci("En service", owner: "tiana.cfg40", serial: "SN-40-NEW")));
        var ci = created.GetProperty("configurationItem");
        Assert.StartsWith("CI-", Str(ci, "reference"));
        var fresh = await Json(await admin.GetAsync($"/api/configuration-items/{Id(ci)}"));
        Assert.Equal((p, "10.40.0.5", "SN-40-NEW"), (fresh.GetProperty("perimeterId").GetInt32(), Str(fresh, "ipAddress"), Str(fresh, "serialNumber")));
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync(reconcile, Ci("Planifié"))).StatusCode);   // déjà rapproché

        // Rapprochement manuel d'un conflit vers le CI choisi.
        Assert.Equal(DiscoveredAsset.InConflict, Str(assets.Single(a => Str(a, "ipAddress") == "10.40.0.9"), "status"));
        await Json(await manager.PostAsJsonAsync($"/api/discovery/staging/{At("10.40.0.9")}/reconcile", new { action = "update", targetCiId = Id(target) }));
        Assert.Equal("Linux / Unix", Str(await Json(await admin.GetAsync($"/api/configuration-items/{Id(target)}")), "operatingSystem"));

        // Ignorer, puis rétablir.
        var printer = At("10.40.0.7");
        Assert.Equal(DiscoveredAsset.Ignored, Str(await Json(await manager.PostAsync($"/api/discovery/staging/{printer}/ignore", null)), "status"));
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync($"/api/discovery/staging/{printer}/ignore", null)).StatusCode);
        Assert.Equal(DiscoveredAsset.ToReconcile, Str(await Json(await manager.PostAsync($"/api/discovery/staging/{printer}/restore", null)), "status"));

        var summary = await Json(await manager.GetAsync("/api/discovery/staging/summary"));
        Assert.Equal((1, 2), (summary.GetProperty("toReconcile").GetInt32(), summary.GetProperty("reconciled").GetInt32()));
        // Console des gestionnaires (CFG-40).
        var console = await Json(await manager.GetAsync($"/api/console?perimeter={p}"));
        Assert.Equal(1, console.GetProperty("management").GetProperty("staging").GetProperty("toReconcile").GetInt32());
    }

    [Fact]
    public async Task CFG_46_une_tache_en_echec_note_son_motif_sans_le_secret()
    {
        var (p, _, manager) = await Perimeter("cfg46b");
        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Balayage", scanType = "IpSweep", targetRange = "10.47.0.0/24", perimeterId = p }));
        await using (var db = api.Database())
        {
            // Type introuvable parmi les collecteurs : l'exécution échoue proprement.
            var j = await db.DiscoveryJobs.SingleAsync(x => x.Id == Id(job));
            j.ScanType = "Inexistant";
            await db.SaveChangesAsync();
        }
        var done = await RunAndWait(manager, Id(job));
        Assert.Equal(DiscoveryJob.Failed, Str(done, "lastStatus"));
        Assert.Contains("Inexistant", Str(done, "lastError"));
    }

    [Fact]
    public async Task CFG_49_CFG_50_constructeur_par_prefixe_MAC_et_bannieres_au_sas()
    {
        var (p, admin, manager) = await Perimeter("cfg50");
        const string oui = "Registry,Assignment,Organization Name,Organization Address\n"
            + "MA-L,0C5050,\"Fabricant Cinquante, Inc.\",\"1 rue X, Paris FR\"\n"
            + "MA-M,0C5051A,Registre moyen,adresse\n"
            + "MA-L,ZZZZZZ,Mal formé,adresse\n";
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/discovery/mac-vendors/import", new { csv = oui })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/discovery/mac-vendors/import", new { csv = "a,b\n1,2\n" })).StatusCode);
        var report = await Json(await admin.PostAsJsonAsync("/api/discovery/mac-vendors/import", new { csv = oui }));
        Assert.Equal((1, 2), (report.GetProperty("imported").GetInt32(), report.GetProperty("skipped").GetInt32()));
        var status = await Json(await manager.GetAsync("/api/discovery/mac-vendors"));
        Assert.Equal(1, status.GetProperty("count").GetInt32());

        FakeCollector.Observations["10.50.0.0/24"] =
        [
            new("IpSweep", IpAddress: "10.50.0.10", MacAddress: "0c:50:50:00:00:10", Type: "Serveur", OpenPorts: "22",
                Os: "Linux Ubuntu", Banners: "22 SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5"),
            new("IpSweep", IpAddress: "10.50.0.20", MacAddress: "0e:50:50:00:00:20", Type: "Poste de travail"),
        ];
        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Balayage 50", scanType = "IpSweep", targetRange = "10.50.0.0/24", perimeterId = p }));
        await RunAndWait(manager, Id(job));
        var assets = await Staged(manager, p);
        var known = assets.Single(a => Str(a, "ipAddress") == "10.50.0.10");
        Assert.Equal("Fabricant Cinquante", Str(known, "vendor"));
        var detail = (await Json(await manager.GetAsync($"/api/discovery/staging/{Id(known)}"))).GetProperty("asset");
        Assert.Equal("22 SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5", Str(detail, "banners"));
        // Adresse administrée localement (aléatoire) : pas de constructeur.
        Assert.Equal(JsonValueKind.Null, assets.Single(a => Str(a, "ipAddress") == "10.50.0.20").GetProperty("vendor").ValueKind);
    }

    [Fact]
    public async Task CFG_51_identifiant_SNMP_v3_au_coffre_et_interfaces_au_sas()
    {
        var (p, admin, manager) = await Perimeter("cfg51");
        object V3(string? user = "saloha", string? secret = "phrase-auth-51", string? priv = "phrase-priv-51", string auth = "SHA256", string privProtocol = "AES") =>
            new { name = "Utilisateur v3", credentialType = "SnmpV3", username = user, secret, privacySecret = priv, authProtocol = auth, privProtocol, perimeterId = p };
        foreach (var refused in new[] { V3(user: null), V3(secret: "court"), V3(priv: null), V3(auth: "MD5"), V3(privProtocol: "DES") })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/discovery/credentials", refused)).StatusCode);
        var cred = await Json(await admin.PostAsJsonAsync("/api/discovery/credentials", V3()));
        Assert.Equal(("SHA256", "AES"), (Str(cred, "authProtocol"), Str(cred, "privProtocol")));
        Assert.DoesNotContain("phrase", cred.GetRawText());
        // Rotation de la seule phrase d'authentification : celle du chiffrement est conservée.
        await Json(await admin.PutAsJsonAsync($"/api/discovery/credentials/{Id(cred)}", V3(secret: "nouvelle-auth-51", priv: null)));

        FakeCollector.Observations["10.51.0.0/24"] =
        [
            new("Snmp", IpAddress: "10.51.0.2", Hostname: "sw-acces-51", Type: "Réseau",
                Interfaces: "Gi0/1 · ethernet · active · 1000 Mb/s · 00:1A:2B:3C:51:01\nGi0/2 · ethernet · inactive"),
        ];
        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "SNMP v3 51", scanType = "Snmp", targetRange = "10.51.0.0/24", credentialId = Id(cred), perimeterId = p }));
        await RunAndWait(manager, Id(job));
        var secret = SnmpV3Secret.Parse(FakeCollector.SecretsReceived["10.51.0.0/24"]!);
        Assert.Equal(new SnmpV3Secret("nouvelle-auth-51", "phrase-priv-51"), secret);
        var asset = Assert.Single(await Staged(manager, p));
        var detail = (await Json(await manager.GetAsync($"/api/discovery/staging/{Id(asset)}"))).GetProperty("asset");
        Assert.Contains("Gi0/2 · ethernet · inactive", Str(detail, "interfaces"));
    }

    [Fact]
    public async Task CFG_52_compte_SSH_au_coffre_et_logiciels_au_sas()
    {
        var (p, admin, manager) = await Perimeter("cfg52");
        foreach (var refused in new object[]
                 {
                     new { name = "SSH", credentialType = "Ssh", secret = "x", perimeterId = p },                              // sans compte
                     new { name = "SSH", credentialType = "Ssh", username = "inventaire", perimeterId = p },                  // sans secret
                     new { name = "SSH", credentialType = "Ssh", username = "inventaire", privateKey = "pas une clé", perimeterId = p },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/discovery/credentials", refused)).StatusCode);
        var cred = await Json(await admin.PostAsJsonAsync("/api/discovery/credentials",
            new { name = "Compte d'inventaire", credentialType = "Ssh", username = "inventaire", secret = "mot-de-passe-52", perimeterId = p }));
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "SSH sans compte", scanType = "Ssh", targetRange = "10.52.0.0/24", perimeterId = p })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "SSH deux ports", scanType = "Ssh", targetRange = "10.52.0.0/24", portList = "22,2222", credentialId = Id(cred), perimeterId = p })).StatusCode);
        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Inventaire SSH 52", scanType = "Ssh", targetRange = "10.52.0.0/24", credentialId = Id(cred), perimeterId = p }));

        Observation Seen(params SoftwareItem[] software) => new("Ssh", IpAddress: "10.52.0.5", Hostname: "srv-52", Os: "Ubuntu 22.04.4 LTS",
            Type: "Serveur", Software: software, Services: "nginx\nssh", ListeningPorts: "22,80");
        FakeCollector.Observations["10.52.0.0/24"] = [Seen(new SoftwareItem("nginx", "1.18.0"), new SoftwareItem("openssh-server", "8.9p1"))];
        await RunAndWait(manager, Id(job));
        Assert.Equal(new SshSecret("mot-de-passe-52", null, null), SshSecret.Parse(FakeCollector.SecretsReceived["10.52.0.0/24"]!));
        var asset = Assert.Single(await Staged(manager, p));
        // Un second relevé remplace la liste des logiciels.
        FakeCollector.Observations["10.52.0.0/24"] = [Seen(new SoftwareItem("nginx", "1.24.0"))];
        await RunAndWait(manager, Id(job));
        var detail = await Json(await manager.GetAsync($"/api/discovery/staging/{Id(asset)}"));
        var software = Assert.Single(detail.GetProperty("software").EnumerateArray());
        Assert.Equal(("nginx", "1.24.0"), (Str(software, "name"), Str(software, "version")));
        Assert.Equal(("nginx\nssh", "22,80"), (Str(detail.GetProperty("asset"), "services"), Str(detail.GetProperty("asset"), "listeningPorts")));
    }

    [Fact]
    public async Task CFG_55_relation_Heberge_proposee_acceptee_ou_ecartee_par_le_gestionnaire()
    {
        var (p, admin, manager) = await Perimeter("cfg55");
        var hostCi = await Json(await admin.PostAsJsonAsync("/api/configuration-items", Server("pve55", p, serial: "SN-55-H")));
        var vmCi = await Json(await admin.PostAsJsonAsync("/api/configuration-items", Server("vm-55", p, serial: "SN-55-V")));
        var cred = await Json(await admin.PostAsJsonAsync("/api/discovery/credentials",
            new { name = "Jeton", credentialType = "ProxmoxToken", username = "inventaire@pve!saloha", secret = "s", perimeterId = p }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/discovery/credentials",
            new { name = "Jeton", credentialType = "ProxmoxToken", username = "inventaire", secret = "s", perimeterId = p })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Proxmox", scanType = "Proxmox", targetRange = "10.55.0.0/24", credentialId = Id(cred), perimeterId = p })).StatusCode);
        const string target = "https://pve55.test:8006";
        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Proxmox 55", scanType = "Proxmox", targetRange = target, credentialId = Id(cred), perimeterId = p }));
        FakeCollector.Observations[target] =
        [
            new("Proxmox", IpAddress: "10.55.0.1", Hostname: "pve55", SerialNumber: "SN-55-H", Os: "Proxmox VE 8.2.4", Type: "Serveur"),
            new("Proxmox", Hostname: "vm-55", SerialNumber: "SN-55-V", Type: "Serveur", HostedBy: "pve55", HostedReason: "Proxmox VE : VM 101 sur le nœud pve55"),
            new("Proxmox", Hostname: "vm-55b", MacAddress: "BC:24:11:55:00:02", Type: "Serveur", HostedBy: "pve55"),
        ];
        await RunAndWait(manager, Id(job));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client("User", "agent.cfg55").GetAsync("/api/discovery/relations")).StatusCode);
        var proposals = (await Json(await manager.GetAsync("/api/discovery/relations?status=Proposée"))).EnumerateArray()
            .Where(r => r.GetProperty("perimeterId").GetInt32() == p).ToList();
        Assert.Equal(2, proposals.Count);
        JsonElement For(string name) => proposals.Single(r => Str(r.GetProperty("target"), "hostname") == name);
        Assert.Equal(("Héberge", "pve55", "Proxmox VE : VM 101 sur le nœud pve55"),
            (Str(For("vm-55"), "type"), Str(For("vm-55").GetProperty("source"), "hostname"), Str(For("vm-55"), "reason")));

        // Rien n'entre dans la CMDB sans décision ; une VM non rapprochée ne peut pas être reliée.
        await using (var db = api.Database())
            Assert.False(await db.CiRelationships.AnyAsync(r => r.SourceId == Id(hostCi)));
        Assert.True(For("vm-55").GetProperty("ready").GetBoolean());
        Assert.False(For("vm-55b").GetProperty("ready").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync($"/api/discovery/relations/{Id(For("vm-55b"))}/accept", null)).StatusCode);
        var accepted = await Json(await manager.PostAsync($"/api/discovery/relations/{Id(For("vm-55"))}/accept", null));
        Assert.Equal(RelationProposal.Accepted, Str(accepted, "status"));
        await using (var db = api.Database())
            Assert.True(await db.CiRelationships.AnyAsync(r => r.SourceId == Id(hostCi) && r.TargetId == Id(vmCi) && r.Type == "Héberge"));
        await Json(await manager.PostAsync($"/api/discovery/relations/{Id(For("vm-55b"))}/dismiss", null));

        // Un nouveau scan ne repropose ni la relation acceptée, ni l'écartée.
        await RunAndWait(manager, Id(job));
        var after = (await Json(await manager.GetAsync("/api/discovery/relations"))).EnumerateArray().Where(r => r.GetProperty("perimeterId").GetInt32() == p).ToList();
        Assert.Equal(new[] { RelationProposal.Accepted, RelationProposal.Dismissed }.Order(), after.Select(r => Str(r, "status")).Order());
    }

    [Fact]
    public async Task CFG_56_flux_reseau_proposes_en_Se_connecte_a()
    {
        var (p, admin, manager) = await Perimeter("cfg56");
        var cred = await Json(await admin.PostAsJsonAsync("/api/discovery/credentials",
            new { name = "SSH", credentialType = "Ssh", username = "inventaire", secret = "mot-de-passe-56", perimeterId = p }));
        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Inventaire 56", scanType = "Ssh", targetRange = "10.56.0.0/24", credentialId = Id(cred), perimeterId = p }));
        FakeCollector.Observations["10.56.0.0/24"] =
        [
            new("Ssh", IpAddress: "10.56.0.10", Hostname: "app-56", Type: "Serveur", ListeningPorts: "22,443",
                Flows: [new Flow(true, "10.56.0.20", 5432), new Flow(true, "10.56.0.20", 6379), new Flow(false, "10.56.0.30", 443), new Flow(true, "192.0.2.1", 443)]),
            new("Ssh", IpAddress: "10.56.0.20", Hostname: "db-56", Type: "Serveur"),
            new("Ssh", IpAddress: "10.56.0.30", Hostname: "proxy-56", Type: "Serveur"),
        ];
        await RunAndWait(manager, Id(job));
        var proposals = (await Json(await manager.GetAsync("/api/discovery/relations?status=Proposée"))).EnumerateArray()
            .Where(r => r.GetProperty("perimeterId").GetInt32() == p).ToList();
        string Pair(JsonElement r) => $"{Str(r.GetProperty("source"), "hostname")} → {Str(r.GetProperty("target"), "hostname")}";
        // Sortant vers la base (deux ports, une proposition) ; entrant depuis le proxy ; l'adresse inconnue du sas est ignorée.
        Assert.Equal(new[] { "app-56 → db-56", "proxy-56 → app-56" }, proposals.Select(Pair).Order());
        Assert.All(proposals, r => Assert.Equal("Se connecte à", Str(r, "type")));
        var db = proposals.Single(r => Pair(r) == "app-56 → db-56");
        Assert.Contains("port 5432", Str(db, "reason"));
        Assert.Contains("port 6379", Str(db, "reason"));
    }
    [Fact]
    public async Task PER_07_une_relation_proposee_ne_decrit_ni_ne_relie_un_CI_deplace_vers_un_autre_perimetre()
    {
        var (a, admin, manager) = await Perimeter("per07r");
        var b = Id(await Json(await admin.PostAsJsonAsync("/api/admin/perimeters", new { name = "Site per07r-b" })));
        var hostCi = await Json(await admin.PostAsJsonAsync("/api/configuration-items", Server("pve-per07r", a, serial: "SN-PER07R-H")));
        var vmCi = await Json(await admin.PostAsJsonAsync("/api/configuration-items", Server("vm-per07r", a, serial: "SN-PER07R-V")));
        var cred = await Json(await admin.PostAsJsonAsync("/api/discovery/credentials",
            new { name = "Jeton", credentialType = "ProxmoxToken", username = "inventaire@pve!saloha", secret = "s", perimeterId = a }));
        const string target = "https://pve-per07r.test:8006";
        var job = await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Proxmox per07r", scanType = "Proxmox", targetRange = target, credentialId = Id(cred), perimeterId = a }));
        FakeCollector.Observations[target] =
        [
            new("Proxmox", IpAddress: "10.88.0.1", Hostname: "pve-per07r", SerialNumber: "SN-PER07R-H", Type: "Serveur"),
            new("Proxmox", Hostname: "vm-per07r", SerialNumber: "SN-PER07R-V", Type: "Serveur", HostedBy: "pve-per07r"),
        ];
        await RunAndWait(manager, Id(job));
        async Task<JsonElement> Proposal() => (await Json(await manager.GetAsync("/api/discovery/relations?status=Proposée"))).EnumerateArray()
            .Single(r => r.GetProperty("perimeterId").GetInt32() == a);
        Assert.True((await Proposal()).GetProperty("ready").GetBoolean());

        // La VM passe au périmètre B, que le gestionnaire de A ne voit pas.
        await Json(await admin.PutAsJsonAsync($"/api/configuration-items/{Id(vmCi)}", Server("vm-per07r", b, serial: "SN-PER07R-V")));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/configuration-items/{Id(vmCi)}")).StatusCode);
        var proposal = await Proposal();
        Assert.Equal(JsonValueKind.Null, proposal.GetProperty("target").GetProperty("ci").ValueKind);
        Assert.Equal(Id(hostCi), Id(proposal.GetProperty("source").GetProperty("ci")));
        Assert.False(proposal.GetProperty("ready").GetBoolean());
        var refused = await manager.PostAsync($"/api/discovery/relations/{Id(proposal)}/accept", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.DoesNotContain("vm-per07r", await refused.Content.ReadAsStringAsync());
        await using (var db = api.Database())
            Assert.False(await db.CiRelationships.AnyAsync(r => r.SourceId == Id(hostCi) && r.TargetId == Id(vmCi)));
    }

    [Fact]
    public async Task PER_07_un_actif_rapproche_ne_suit_pas_son_CI_deplace_vers_un_autre_perimetre()
    {
        var (a, admin, manager) = await Perimeter("per07d");
        var b = Id(await Json(await admin.PostAsJsonAsync("/api/admin/perimeters", new { name = "Site per07d-b" })));
        var ci = await Json(await admin.PostAsJsonAsync("/api/configuration-items", Server("srv-per07d", a, "SN-PER07D", "10.87.0.9")));
        FakeCollector.Observations["10.87.0.0/24"] = [new("IpSweep", IpAddress: "10.87.0.9", SerialNumber: "SN-PER07D", Type: "Serveur")];
        var job = Id(await Json(await manager.PostAsJsonAsync("/api/discovery/jobs",
            new { name = "Balayage per07d", scanType = "IpSweep", targetRange = "10.87.0.0/24", perimeterId = a })));
        await RunAndWait(manager, job);
        var asset = (await Staged(manager, a)).Single();
        Assert.Equal(DiscoveredAsset.Reconciled, Str(asset, "status"));

        // Le CI passe au périmètre B, que le gestionnaire de A ne voit pas.
        await Json(await admin.PutAsJsonAsync($"/api/configuration-items/{Id(ci)}", Server("srv-per07d", b, "SN-PER07D", "10.87.0.9")));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/configuration-items/{Id(ci)}")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Staged(manager, a)).Single().GetProperty("matchedCi").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Json(await manager.GetAsync($"/api/discovery/staging/{Id(asset)}"))).GetProperty("targetCi").ValueKind);

        // Une nouvelle découverte de A ne réécrit pas le CI de B.
        FakeCollector.Observations["10.87.0.0/24"] = [new("IpSweep", IpAddress: "10.87.0.77", SerialNumber: "SN-PER07D", Os: "OS-ECRIT-DEPUIS-A", Type: "Serveur")];
        await RunAndWait(manager, job);
        var moved = await Json(await admin.GetAsync($"/api/configuration-items/{Id(ci)}"));
        Assert.Equal((b, "10.87.0.9"), (moved.GetProperty("perimeterId").GetInt32(), Str(moved, "ipAddress")));
        Assert.NotEqual(DiscoveredAsset.Reconciled, Str((await Staged(manager, a)).Single(), "status"));
    }
}
