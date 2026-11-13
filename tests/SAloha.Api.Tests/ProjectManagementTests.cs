using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SAloha.Api.Tests;

/// <summary>Gestion de projet — règles PRJ-xx de docs/reference/processus/gestion-de-projet.md.</summary>
[Collection("api")]
public class ProjectManagementTests(ApiFixture api)
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode} : {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    private static object Project(string title, string method, string[]? components = null, string? status = null,
        string? justification = null, DateTime? end = null, decimal? budget = null, decimal? cost = null,
        int? tolDays = null, decimal? tolCost = null, string? ownerId = null, DateTime? start = null, string? abandonReason = null) => new
    {
        title, description = "", status, ownerType = ownerId is null ? null : "User", ownerId, ownerDisplayName = ownerId,
        method, components, startDate = start, targetEndDate = end, budget, actualCost = cost,
        justification, expectedBenefits = (string?)null, expectedCosts = (string?)null, majorRisks = (string?)null,
        toleranceDays = tolDays, toleranceCostPercent = tolCost, toleranceScope = (string?)null, toleranceQuality = (string?)null,
        abandonReason
    };

    private static string[] Components(JsonElement p) => p.GetProperty("components").GetString()!.Split(',');

    [Fact]
    public async Task Le_createur_devient_chef_de_projet_et_seuls_lui_et_les_gestionnaires_modifient()
    {
        var tiana = api.Client("User", "tiana.ravelo");
        var p = await Json(await tiana.PostAsJsonAsync("/api/projects", Project("PRJ-01 Portail RH", "Scrum")));
        Assert.StartsWith("PRJ-", p.GetProperty("reference").GetString());
        Assert.Equal("tiana.ravelo", p.GetProperty("ownerId").GetString());   // PRJ-01
        Assert.Equal("Cadrage", p.GetProperty("status").GetString());
        var id = p.GetProperty("id").GetInt32();

        // PRJ-04
        var other = api.Client("User", "rado.rakoto");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"/api/projects/{id}", Project("Détourné", "Scrum"))).StatusCode);
        await Json(await tiana.PutAsJsonAsync($"/api/projects/{id}", Project("PRJ-01 Portail RH v2", "Scrum", ownerId: "tiana.ravelo")));
        await Json(await api.Client("Manager", "lova.rabe").PutAsJsonAsync($"/api/projects/{id}", Project("PRJ-01 Portail RH v3", "Scrum", ownerId: "tiana.ravelo")));
    }

    [Fact]
    public async Task La_methode_fixe_les_composants_et_les_phases_proposees()
    {
        var m = api.Client("Manager", "lova.rabe");
        // PRJ-03
        var scrum = await Json(await m.PostAsJsonAsync("/api/projects", Project("Scrum", "Scrum")));
        Assert.Equal(["backlog", "sprints", "board", "burndown"], Components(scrum));
        var kanban = await Json(await m.PostAsJsonAsync("/api/projects", Project("Kanban", "Kanban", ["phases"])));
        Assert.Equal(["backlog", "board"], Components(kanban));          // le choix n'est retenu qu'en hybride
        var hybrid = await Json(await m.PostAsJsonAsync("/api/projects", Project("Hybride", "Hybride", ["phases", "sprints", "risks"])));
        Assert.Equal(["sprints", "phases", "risks"], Components(hybrid));
        var bad = await m.PostAsJsonAsync("/api/projects", Project("X", "Hybride", ["gantt"]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // PRJ-10 / PRJ-20 : phases du V et étapes PRINCE2 proposées.
        var v = await Json(await m.PostAsJsonAsync("/api/projects", Project("Cycle en V", "Cycle en V")));
        var phases = await Json(await m.GetAsync($"/api/projects/{v.GetProperty("id").GetInt32()}/phases"));
        Assert.Equal(8, phases.GetArrayLength());
        Assert.Equal("Spécifications", phases[0].GetProperty("name").GetString());
        var p2 = await Json(await m.PostAsJsonAsync("/api/projects", Project("PRINCE2", "PRINCE2", justification: "ROI")));
        var stages = await Json(await m.GetAsync($"/api/projects/{p2.GetProperty("id").GetInt32()}/phases"));
        Assert.All(stages.EnumerateArray(), s => Assert.Equal("Étape", s.GetProperty("kind").GetString()));
        Assert.Equal(0, (await Json(await m.GetAsync($"/api/projects/{scrum.GetProperty("id").GetInt32()}/workitems"))).GetArrayLength());

        // Composant non activé → 400 (pas de sprint dans un projet en V).
        var sprintInV = await m.PostAsJsonAsync($"/api/projects/{v.GetProperty("id").GetInt32()}/sprints", new { name = "S1" });
        Assert.Equal(HttpStatusCode.BadRequest, sprintInV.StatusCode);
    }

    [Fact]
    public async Task Sprints_backlog_et_burndown()
    {
        var lead = api.Client("Manager", "lova.rabe");
        var dev = api.Client("User", "tiana.ravelo");
        var id = (await Json(await lead.PostAsJsonAsync("/api/projects", Project("Agile", "Scrum")))).GetProperty("id").GetInt32();
        var today = DateTime.UtcNow.Date;
        object Sprint(string name, string status) => new { name, goal = "MVP", startDate = today.AddDays(-2), endDate = today.AddDays(7), status };

        // PRJ-04 : les sprints sont la structure du projet.
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.PostAsJsonAsync($"/api/projects/{id}/sprints", Sprint("S0", "Planifié"))).StatusCode);
        var s1 = (await Json(await lead.PostAsJsonAsync($"/api/projects/{id}/sprints", Sprint("S1", "Actif")))).GetProperty("id").GetInt32();
        var twoActive = await lead.PostAsJsonAsync($"/api/projects/{id}/sprints", Sprint("S2", "Actif"));
        Assert.Contains("déjà actif", await twoActive.Content.ReadAsStringAsync());

        // PRJ-14 : tout rôle crée et déplace les éléments de travail.
        object Item(string title, int points, string status) => new
        {
            type = "User story", title, description = "", acceptanceCriteria = "", points, rank = (int?)null, sprintId = s1, status,
            assigneeId = (string?)null, assigneeDisplayName = (string?)null
        };
        var a = await Json(await dev.PostAsJsonAsync($"/api/projects/{id}/workitems", Item("A", 5, "À faire")));
        var b = await Json(await dev.PostAsJsonAsync($"/api/projects/{id}/workitems", Item("B", 3, "À faire")));
        Assert.True(b.GetProperty("rank").GetInt32() > a.GetProperty("rank").GetInt32());   // fin de backlog
        var done = await Json(await dev.PutAsJsonAsync($"/api/projects/{id}/workitems/{a.GetProperty("id").GetInt32()}", Item("A", 5, "Terminé")));
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("completedAt").ValueKind);

        // PRJ-13 : burndown — 8 points, 5 terminés aujourd'hui.
        var burn = await Json(await dev.GetAsync($"/api/projects/{id}/sprints/{s1}/burndown"));
        Assert.Equal(8, burn.GetProperty("total").GetInt32());
        var days = burn.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(10, days.Count);
        Assert.Equal(8, days[0].GetProperty("remaining").GetInt32());
        Assert.Equal(3, days[2].GetProperty("remaining").GetInt32());
        Assert.Equal(JsonValueKind.Null, days[9].GetProperty("remaining").ValueKind);   // jour à venir
        Assert.Equal(0, days[9].GetProperty("ideal").GetDouble());

        // PRJ-12 : à la clôture, l'inachevé retourne au backlog ; un sprint clos ne se rouvre pas.
        await Json(await lead.PutAsJsonAsync($"/api/projects/{id}/sprints/{s1}", Sprint("S1", "Clos")));
        var items = await Json(await dev.GetAsync($"/api/projects/{id}/workitems"));
        var itemB = items.EnumerateArray().Single(x => x.GetProperty("title").GetString() == "B");
        Assert.Equal(JsonValueKind.Null, itemB.GetProperty("sprintId").ValueKind);
        var reopen = await lead.PutAsJsonAsync($"/api/projects/{id}/sprints/{s1}", Sprint("S1", "Actif"));
        Assert.Contains("ne se rouvre pas", await reopen.Content.ReadAsStringAsync());

        // Supprimer reste réservé au chef de projet.
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.DeleteAsync($"/api/projects/{id}/workitems/{b.GetProperty("id").GetInt32()}")).StatusCode);
    }

    [Fact]
    public async Task Prince2_business_case_etapes_et_registres()
    {
        var lead = api.Client("User", "hasina.lead");
        var dto = Project("Migration ERP", "PRINCE2");
        var p = await Json(await lead.PostAsJsonAsync("/api/projects", dto));
        var id = p.GetProperty("id").GetInt32();

        // PRJ-08 : le chef de projet propose, un gestionnaire valide (403 pour le chef de projet).
        var start = DateTime.UtcNow.Date; var end = start.AddMonths(6);
        var bySelf = await lead.PutAsJsonAsync($"/api/projects/{id}", Project("Migration ERP", "PRINCE2", status: "En cours",
            justification: "ROI", ownerId: "hasina.lead", start: start, end: end));
        Assert.Equal(HttpStatusCode.Forbidden, bySelf.StatusCode);
        var manager = api.Client("Manager", "lova.rabe");
        // Conditions de la validation : calendrier, puis (PRJ-06) business case justifié.
        var noDates = await manager.PutAsJsonAsync($"/api/projects/{id}", Project("Migration ERP", "PRINCE2", status: "En cours", justification: "ROI", ownerId: "hasina.lead"));
        Assert.Contains("dates", await noDates.Content.ReadAsStringAsync());
        var noCase = await manager.PutAsJsonAsync($"/api/projects/{id}", Project("Migration ERP", "PRINCE2", status: "En cours", ownerId: "hasina.lead", start: start, end: end));
        Assert.Contains("business case", await noCase.Content.ReadAsStringAsync());
        var validated = await Json(await manager.PutAsJsonAsync($"/api/projects/{id}",
            Project("Migration ERP", "PRINCE2", status: "En cours", justification: "Fin de support de l'ERP actuel", ownerId: "hasina.lead", start: start, end: end)));
        Assert.Equal("lova.rabe", validated.GetProperty("validatedBy").GetString());
        // Le chef de projet peut ensuite suspendre puis reprendre sans nouvelle validation.
        object Running(string s) => Project("Migration ERP", "PRINCE2", status: s, justification: "Fin de support de l'ERP actuel", ownerId: "hasina.lead", start: start, end: end);
        await Json(await lead.PutAsJsonAsync($"/api/projects/{id}", Running("Suspendu")));
        await Json(await lead.PutAsJsonAsync($"/api/projects/{id}", Running("En cours")));

        // PRJ-21 : une étape ne se termine que par sa validation, qui ouvre la suivante.
        var stages = (await Json(await lead.GetAsync($"/api/projects/{id}/phases"))).EnumerateArray().ToList();
        var first = stages[0].GetProperty("id").GetInt32();
        var direct = await lead.PutAsJsonAsync($"/api/projects/{id}/phases/{first}", new { kind = "Étape", name = "Démarrage", order = 1, status = "Terminée" });
        Assert.Equal(HttpStatusCode.BadRequest, direct.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client("User", "tiana.ravelo").PostAsync($"/api/projects/{id}/phases/{first}/approve", null)).StatusCode);
        // Une étape à la fois : pas de validation avant celle des étapes précédentes.
        var early = await lead.PostAsync($"/api/projects/{id}/phases/{stages[1].GetProperty("id").GetInt32()}/approve", null);
        Assert.Contains("Démarrage", await early.Content.ReadAsStringAsync());
        var approved = await Json(await lead.PostAsync($"/api/projects/{id}/phases/{first}/approve", null));
        Assert.Equal("Terminée", approved.GetProperty("status").GetString());
        var next = (await Json(await lead.GetAsync($"/api/projects/{id}/phases")))[1];
        Assert.Equal("En cours", next.GetProperty("status").GetString());

        // PRJ-15 : registres ouverts à tous ; un incident projet se clôt avec une résolution.
        var team = api.Client("User", "tiana.ravelo");
        var risk = await Json(await team.PostAsJsonAsync($"/api/projects/{id}/risks",
            new { title = "Retard du fournisseur", description = "", probability = 4, impact = 5, response = "Réduire", ownerDisplayName = "Hasina", status = "Ouvert" }));
        Assert.Equal(20, risk.GetProperty("score").GetInt32());
        var issue = await Json(await team.PostAsJsonAsync($"/api/projects/{id}/issues",
            new { type = "Demande de changement", title = "Ajouter le module paie", description = "", severity = "Élevée", status = "Ouvert", resolution = (string?)null }));
        var close = await team.PutAsJsonAsync($"/api/projects/{id}/issues/{issue.GetProperty("id").GetInt32()}",
            new { type = "Demande de changement", title = "Ajouter le module paie", description = "", severity = "Élevée", status = "Clos", resolution = (string?)null });
        Assert.Contains("résolution", await close.Content.ReadAsStringAsync());

        var summary = await Json(await team.GetAsync($"/api/projects/{id}/summary"));
        Assert.Equal(1, summary.GetProperty("risks").GetProperty("critical").GetInt32());
        Assert.Equal(1, summary.GetProperty("openIssues").GetInt32());
    }

    [Fact]
    public async Task Tolerances_livrables_et_projet_clos_fige()
    {
        var m = api.Client("Manager", "lova.rabe");
        var today = DateTime.UtcNow.Date;
        // Fin cible dépassée de 5 jours, tolérance de 10 : à surveiller ; coût 115 pour 100 et 10 % : hors tolérance.
        var p = await Json(await m.PostAsJsonAsync("/api/projects",
            Project("Refonte réseau", "Cycle en V", end: today.AddDays(-5), budget: 100, cost: 115, tolDays: 10, tolCost: 10)));
        var id = p.GetProperty("id").GetInt32();
        var tol = (await Json(await m.GetAsync($"/api/projects/{id}/summary"))).GetProperty("tolerances");
        Assert.Equal("À surveiller", tol.GetProperty("time").GetString());
        Assert.Equal("Hors tolérance", tol.GetProperty("cost").GetString());

        // PRJ-11 : seul le chef de projet ou un gestionnaire valide un livrable.
        var team = api.Client("User", "tiana.ravelo");
        var d = await Json(await team.PostAsJsonAsync($"/api/projects/{id}/deliverables", new { name = "Dossier d'architecture", description = "", phaseId = (int?)null, dueDate = (DateTime?)null, status = "Livré" }));
        var did = d.GetProperty("id").GetInt32();
        var byTeam = await team.PutAsJsonAsync($"/api/projects/{id}/deliverables/{did}", new { name = "Dossier d'architecture", description = "", phaseId = (int?)null, dueDate = (DateTime?)null, status = "Validé" });
        Assert.Equal(HttpStatusCode.BadRequest, byTeam.StatusCode);
        var validated = await Json(await m.PutAsJsonAsync($"/api/projects/{id}/deliverables/{did}", new { name = "Dossier d'architecture", description = "", phaseId = (int?)null, dueDate = (DateTime?)null, status = "Validé" }));
        Assert.Equal("Lova Rabe".Length > 0, validated.GetProperty("validatedBy").GetString()!.Length > 0);

        // PRJ-05/07 : clôturé, le projet est définitif et ses éléments figés.
        object Status(string s) => Project("Refonte réseau", "Cycle en V", status: s, start: today.AddMonths(-3), end: today.AddDays(-5), budget: 100, cost: 115, tolDays: 10, tolCost: 10, ownerId: "lova.rabe");
        await Json(await m.PutAsJsonAsync($"/api/projects/{id}", Status("En cours")));
        await Json(await m.PutAsJsonAsync($"/api/projects/{id}", Status("Clôturé")));
        Assert.Equal(HttpStatusCode.BadRequest, (await m.PutAsJsonAsync($"/api/projects/{id}", Status("En cours"))).StatusCode);
        var frozen = await m.PostAsJsonAsync($"/api/projects/{id}/deliverables", new { name = "Tardif", description = "", phaseId = (int?)null, dueDate = (DateTime?)null, status = "À produire" });
        Assert.Contains("ne se modifie plus", await frozen.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Clôturé")]
    [InlineData("Abandonné")]
    public async Task PRJ_07_la_fin_d_etape_ne_se_valide_plus_sur_un_projet_fige(string final)
    {
        var lead = api.Client("User", "hasina.fige");
        var manager = api.Client("Manager", "lova.rabe");
        var start = DateTime.UtcNow.Date;
        object Dto(string? status) => Project($"Projet figé ({final})", "PRINCE2", status: status, justification: "Fin de support",
            ownerId: "hasina.fige", start: start, end: start.AddMonths(6), abandonReason: status == "Abandonné" ? "Besoin disparu" : null);
        var id = (await Json(await lead.PostAsJsonAsync("/api/projects", Dto(null)))).GetProperty("id").GetInt32();
        await Json(await manager.PutAsJsonAsync($"/api/projects/{id}", Dto("En cours")));
        async Task<List<(int Id, string Status, bool Approved)>> Stages() =>
            (await Json(await lead.GetAsync($"/api/projects/{id}/phases"))).EnumerateArray()
                .Select(s => (s.GetProperty("id").GetInt32(), s.GetProperty("status").GetString()!, s.GetProperty("approvedAt").ValueKind != JsonValueKind.Null)).ToList();
        var before = await Stages();
        Assert.True(before.Count >= 2);
        await Json(await lead.PutAsJsonAsync($"/api/projects/{id}", Dto(final)));

        // Le chef de projet comme un gestionnaire : 400, et les étapes ne bougent pas.
        foreach (var client in new[] { lead, manager })
        {
            var refused = await client.PostAsync($"/api/projects/{id}/phases/{before[0].Id}/approve", null);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("ne se modifie plus", await refused.Content.ReadAsStringAsync());
        }
        Assert.Equal(before, await Stages());
    }

    [Fact]
    public async Task PRJ_03_la_fin_d_etape_ne_se_valide_pas_sans_le_composant()
    {
        // La route d'action répond comme les autres écritures du registre quand le projet n'a ni phases ni étapes.
        var m = api.Client("Manager", "lova.rabe");
        var kanban = (await Json(await m.PostAsJsonAsync("/api/projects", Project("Sans étapes", "Kanban")))).GetProperty("id").GetInt32();
        var refused = await m.PostAsync($"/api/projects/{kanban}/phases/1/approve", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Composant non activé", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Un_abandon_se_motive()
    {
        var m = api.Client("Manager", "lova.rabe");
        var id = (await Json(await m.PostAsJsonAsync("/api/projects", Project("Projet abandonné", "Kanban")))).GetProperty("id").GetInt32();
        var noReason = await m.PutAsJsonAsync($"/api/projects/{id}", Project("Projet abandonné", "Kanban", status: "Abandonné", ownerId: "lova.rabe"));
        Assert.Contains("motif", await noReason.Content.ReadAsStringAsync());
        var abandoned = await Json(await m.PutAsJsonAsync($"/api/projects/{id}",
            Project("Projet abandonné", "Kanban", status: "Abandonné", ownerId: "lova.rabe", abandonReason: "Besoin couvert par l'éditeur")));
        Assert.Equal("Abandonné", abandoned.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, abandoned.GetProperty("closedAt").ValueKind);
    }
}
