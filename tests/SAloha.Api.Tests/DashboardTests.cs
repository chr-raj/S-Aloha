using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SAloha.Api.Tests;

/// <summary>Tableau de bord du reporting (REP-xx) et indicateurs d'incident qui l'alimentent (INC-21, INC-23, INC-27).</summary>
[Collection("api")]
public class DashboardTests(ApiFixture api)
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode} : {body}");
        return JsonDocument.Parse(body).RootElement;
    }
    private static int Id(JsonElement e) => e.GetProperty("id").GetInt32();

    // Service propre au test : la base est partagée, le filtre de service isole les comptes.
    private readonly string _svc = $"Paie {Guid.NewGuid():N}"[..14];

    private object Incident(string title, string? status = null, bool major = false, DateTime? occurredAt = null) => new
    {
        title, description = "d", status, impact = "Élevé", urgency = "Moyenne", category = "Réseau",
        affectedService = _svc, isMajor = major, occurredAt,
        resolution = status is "Résolu" ? "Redémarrage" : null, resolutionCode = status is "Résolu" ? "Correctif appliqué" : null,
        ownerType = "User", ownerId = "hery.rakoto", ownerDisplayName = "Hery Rakoto"
    };

    private async Task ServiceWithSla(HttpClient m)
    {
        var svc = await Json(await m.PostAsJsonAsync("/api/services", new
        {
            title = _svc, criticality = "Élevée", status = "En service",
            ownerType = "User", ownerId = "hery.rakoto", ownerDisplayName = "Hery Rakoto"
        }));
        object Sla(string? status) => new
        {
            title = "SLA paie", serviceId = Id(svc), customer = "RH", status, validFrom = DateTime.UtcNow.Date.AddDays(-1),
            resolutionHoursP1 = 4, resolutionHoursP2 = 8, resolutionHoursP3 = 24, resolutionHoursP4 = 72
        };
        var sla = await Json(await m.PostAsJsonAsync("/api/agreements", Sla(null)));
        await Json(await m.PutAsJsonAsync($"/api/agreements/{Id(sla)}", Sla("En vigueur")));
    }

    [Fact]
    public async Task INC_21_echeance_de_resolution_tiree_du_SLA_du_service()
    {
        var m = api.Client("Manager");
        await ServiceWithSla(m);
        var inc = await Json(await m.PostAsJsonAsync("/api/incidents", Incident("Paie lente")));
        var hours = new Dictionary<string, int> { ["P1"] = 4, ["P2"] = 8, ["P3"] = 24, ["P4"] = 72 }[inc.GetProperty("priority").GetString()!];
        var due = inc.GetProperty("resolutionDueAt").GetDateTime() - inc.GetProperty("createdAt").GetDateTime();
        Assert.Equal(hours, due.TotalHours, 3);

        // Sans SLA pour le service : pas d'échéance.
        var other = await Json(await m.PostAsJsonAsync("/api/incidents", new
        {
            title = "Hors SLA", impact = "Faible", urgency = "Faible", affectedService = "Service sans SLA", isMajor = false
        }));
        Assert.Equal(JsonValueKind.Null, other.GetProperty("resolutionDueAt").ValueKind);
    }

    [Fact]
    public async Task INC_27_debut_constate_jamais_posterieur_a_la_declaration()
    {
        var res = await api.Client("User").PostAsJsonAsync("/api/incidents", Incident("Futur", occurredAt: DateTime.UtcNow.AddHours(2)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("début constaté", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task REP_10_a_15_onglet_incidents_MTTD_FCR_backlog_et_suivi()
    {
        var m = api.Client("Manager");
        await ServiceWithSla(m);
        // Résolu d'emblée : premier contact (INC-23), détecté 2 h après le début (INC-27).
        var occurred = DateTime.UtcNow.AddHours(-2);
        var first = await Json(await m.PostAsJsonAsync("/api/incidents", Incident("Premier contact", occurredAt: occurred)));
        var fcr = await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(first)}", Incident("Premier contact", "Résolu", occurredAt: occurred)));
        Assert.True(fcr.GetProperty("firstContactResolution").GetBoolean());
        // Pris en charge puis résolu : pas au premier contact.
        var second = await Json(await m.PostAsJsonAsync("/api/incidents", Incident("Escaladé")));
        await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(second)}", Incident("Escaladé", "En cours")));
        var resolved = await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(second)}", Incident("Escaladé", "Résolu")));
        Assert.False(resolved.GetProperty("firstContactResolution").GetBoolean());
        // Majeur ouvert : à suivre.
        await Json(await m.PostAsJsonAsync("/api/incidents", Incident("Paie arrêtée", major: true)));

        var tab = await Json(await api.Client("User").GetAsync($"/api/reports/dashboard/incidents?service={Uri.EscapeDataString(_svc)}"));
        var k = tab.GetProperty("kpis");
        Assert.Equal(2, k.GetProperty("resolved").GetInt32());
        Assert.Equal(50, k.GetProperty("fcr").GetDouble());
        Assert.Equal(2, k.GetProperty("mttdHours").GetDouble(), 1);
        Assert.Equal(1, k.GetProperty("open").GetInt32());
        Assert.Equal(1, tab.GetProperty("backlog")[0].GetProperty("count").GetInt32());
        var burnUp = tab.GetProperty("burnUp").EnumerateArray().Last();
        Assert.Equal(3, burnUp.GetProperty("created").GetInt32());
        Assert.Equal(2, burnUp.GetProperty("resolved").GetInt32());
        var watch = Assert.Single(tab.GetProperty("watch").EnumerateArray());
        Assert.True(watch.GetProperty("isMajor").GetBoolean());

        // Synthèse : le majeur ouvert pèse sur le score et alerte l'onglet Incidents.
        var overview = await Json(await m.GetAsync($"/api/reports/dashboard/overview?service={Uri.EscapeDataString(_svc)}"));
        Assert.Equal(1, overview.GetProperty("kpis").GetProperty("majorIncidentsOpen").GetInt32());
        Assert.Equal(100, overview.GetProperty("kpis").GetProperty("slaRate").GetDouble());
        // Les projets, sans service, comptent quel que soit le filtre : on vérifie la pénalité et la somme.
        var health = overview.GetProperty("health");
        var factors = health.GetProperty("factors").EnumerateArray().ToList();
        Assert.Equal(25, factors.Single(f => f.GetProperty("label").GetString() == "Incidents majeurs ouverts").GetProperty("penalty").GetInt32());
        Assert.Equal(100 - factors.Sum(f => f.GetProperty("penalty").GetInt32()), health.GetProperty("score").GetInt32());
        Assert.Equal("bad", overview.GetProperty("badges").GetProperty("incidents").GetProperty("tone").GetString());
        Assert.Equal(30, overview.GetProperty("volume").GetArrayLength());
        var critical = overview.GetProperty("priorityMatrix")[0];
        Assert.Equal("Critique", critical.GetProperty("level").GetString());
        Assert.Equal(1, critical.GetProperty("incidents").GetInt32());
    }

    [Fact]
    public async Task REP_20_a_25_onglet_problemes_entonnoir()
    {
        var m = api.Client("Manager");
        await Json(await m.PostAsJsonAsync("/api/problems", new
        {
            title = "Lenteurs récurrentes", description = "d", impact = "Moyen", urgency = "Moyenne", category = "Réseau", affectedService = _svc
        }));
        var tab = await Json(await m.GetAsync($"/api/reports/dashboard/problems?service={Uri.EscapeDataString(_svc)}"));
        Assert.Equal(1, tab.GetProperty("kpis").GetProperty("open").GetInt32());
        var funnel = tab.GetProperty("funnel");
        Assert.Equal("Identifié", funnel[0].GetProperty("stage").GetString());
        Assert.Equal(1, funnel[0].GetProperty("count").GetInt32());
        Assert.Equal(_svc, tab.GetProperty("topServices")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task REP_32_tolerances_des_projets_serialisees()
    {
        var m = api.Client("Manager");
        // Projet en retard au-delà de sa tolérance de délai.
        object Dto(string? status) => new
        {
            title = $"Retard {_svc}", description = "", status, method = "Scrum", justification = "ROI",
            ownerType = "User", ownerId = "hery.rakoto", ownerDisplayName = "Hery Rakoto",
            startDate = DateTime.UtcNow.Date.AddDays(-60), targetEndDate = DateTime.UtcNow.Date.AddDays(-10), toleranceDays = 2
        };
        var p = await Json(await m.PostAsJsonAsync("/api/projects", Dto(null)));
        await Json(await m.PutAsJsonAsync($"/api/projects/{Id(p)}", Dto("En cours")));

        var tab = await Json(await m.GetAsync("/api/reports/dashboard/changes-projects"));
        var row = tab.GetProperty("projects").EnumerateArray().Single(x => Id(x) == Id(p));
        Assert.Equal("Hors tolérance", row.GetProperty("tolerance").GetProperty("time").GetString());
        Assert.Equal("Sans objet", row.GetProperty("tolerance").GetProperty("cost").GetString());
    }

    [Fact]
    public async Task REP_60_a_63_onglet_demandes_delai_respect_approbation_satisfaction()
    {
        var m = api.Client("Manager", "chef.rep60");
        var svc = await Json(await m.PostAsJsonAsync("/api/services", new
        {
            title = _svc, criticality = "Moyenne", status = "En service",
            ownerType = "User", ownerId = "hery.rakoto", ownerDisplayName = "Hery Rakoto"
        }));
        var item = await Json(await m.PostAsJsonAsync("/api/request-catalog", new
        {
            name = $"Badge {_svc}", description = "d", serviceId = Id(svc), requiresApproval = true, targetHours = 1000, fulfillmentGroup = "GRP-ACCUEIL"
        }));
        var requester = api.Client("User", "demandeur.rep60");
        object R(string? status, string? reason = null, DateTime? due = null) => new { title = "Badge", requestedItem = "", catalogItemId = Id(item), status, rejectionReason = reason, dueDate = due,
            ownerType = "Group", ownerId = "GRP-ACCUEIL", ownerDisplayName = "GRP-ACCUEIL" };   // REQ-13 : responsable renvoyé avec la fiche
        async Task<int> Create() => Id(await Json(await requester.PostAsJsonAsync("/api/requests", R(null))));
        // Une mise à jour renvoie l'échéance calculée à l'approbation (REQ-22).
        async Task Move(HttpClient c, int id, string status)
        {
            var due = (await Json(await c.GetAsync($"/api/requests/{id}"))).GetProperty("dueDate");
            await Json(await c.PutAsJsonAsync($"/api/requests/{id}", R(status, due: due.ValueKind == JsonValueKind.Null ? null : due.GetDateTime())));
        }

        var done = await Create();
        foreach (var st in new[] { "Approuvée", "En cours", "Satisfaite" }) await Move(m, done, st);
        await Move(requester, done, "Close");
        await Json(await requester.PostAsJsonAsync($"/api/requests/{done}/satisfaction", new { score = 4 }));
        var rejected = await Create();
        await Json(await m.PutAsJsonAsync($"/api/requests/{rejected}", R("Rejetée", "Hors politique")));
        var late = await Create();
        await Move(m, late, "Approuvée");
        await using (var db = api.Database())
        {
            var e = await db.ServiceRequests.SingleAsync(x => x.Id == late);
            e.DueDate = DateTime.UtcNow.AddDays(-3);
            await db.SaveChangesAsync();
        }

        var tab = await Json(await m.GetAsync($"/api/reports/dashboard/requests?service={Uri.EscapeDataString(_svc)}"));
        var k = tab.GetProperty("kpis");
        Assert.Equal(1, k.GetProperty("fulfilled").GetInt32());
        Assert.Equal(100, k.GetProperty("onTime").GetDouble());
        Assert.Equal(66.7, k.GetProperty("approvalRate").GetDouble());
        Assert.Equal(4, k.GetProperty("satisfaction").GetDouble());
        Assert.Equal((1, 1), (k.GetProperty("open").GetInt32(), k.GetProperty("overdue").GetInt32()));
        var row = Assert.Single(tab.GetProperty("byItem").EnumerateArray());
        Assert.Equal((3, 66.7), (row.GetProperty("created").GetInt32(), row.GetProperty("approvalRate").GetDouble()));
        Assert.Equal(1, tab.GetProperty("scores")[3].GetProperty("count").GetInt32());
        Assert.Equal(late, Id(Assert.Single(tab.GetProperty("watch").EnumerateArray())));
        var overview = await Json(await m.GetAsync($"/api/reports/dashboard/overview?service={Uri.EscapeDataString(_svc)}"));
        Assert.Equal(1, overview.GetProperty("badges").GetProperty("requests").GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("changes-projects", "kpis")]
    [InlineData("compliance", "knowledge")]
    [InlineData("requests", "scores")]
    public async Task Onglets_accessibles_a_tout_utilisateur(string tab, string key)
    {
        var body = await Json(await api.Client("User").GetAsync($"/api/reports/dashboard/{tab}?from=2026-01-01&to=2026-12-31"));
        Assert.True(body.TryGetProperty(key, out _));
    }
}
