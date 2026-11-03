using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SAloha.Api.Tests;

/// <summary>Incidents, sortie du MVP : rôle du responsable d'un incident majeur et sortie de « Nouveau » (INC-13), SLA en risque ou dépassé (INC-24).</summary>
[Collection("api")]
public class IncidentExitMvpTests(ApiFixture api)
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode} : {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static int Id(JsonElement e) => e.GetProperty("id").GetInt32();

    private static object Major(string owner, int? perimeterId = null) => new
    {
        title = "INC-13", impact = "Élevé", urgency = "Élevée", isMajor = true, perimeterId,
        ownerType = "User", ownerId = owner, ownerDisplayName = owner
    };

    [Fact]
    public async Task INC_13_le_responsable_d_un_incident_majeur_est_gestionnaire_du_perimetre()
    {
        var m = api.Client("Manager", "inc13.manager");
        // Un User de l'annuaire, un compte inconnu : refusés.
        var refused = await m.PostAsJsonAsync("/api/incidents", Major("simple.user"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("gestionnaire du périmètre", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await m.PostAsJsonAsync("/api/incidents", Major("inconnu.x"))).StatusCode);
        // Un Manager AD (sans affectation : rôle AD sur « Organisation »), un Admin : acceptés.
        var inc = await Json(await m.PostAsJsonAsync("/api/incidents", Major("chef.inc13")));
        await Json(await m.PostAsJsonAsync("/api/incidents", Major("admin.inc13")));
        // Annuaire injoignable : la désignation passe, pour ne pas bloquer une crise.
        await Json(await m.PostAsJsonAsync("/api/incidents", Major("annuaire.hs")));

        // Un incident qui devient majeur : son responsable est vérifié aussi.
        var normal = await Json(await m.PostAsJsonAsync("/api/incidents", new
        {
            title = "INC-13 normal", impact = "Faible", urgency = "Faible", ownerType = "User", ownerId = "simple.user", ownerDisplayName = "Simple"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, (await m.PutAsJsonAsync($"/api/incidents/{Id(normal)}", Major("simple.user"))).StatusCode);

        // Rôle par périmètre (PER-03) : Manager par son groupe sur l'Usine, User ailleurs.
        var admin = api.Client();
        var usine = Id(await Json(await admin.PostAsJsonAsync("/api/admin/perimeters", new { name = "Usine INC-13" })));
        await Json(await admin.PostAsJsonAsync($"/api/admin/perimeters/{usine}/assignments",
            new { principalType = "Group", principalId = "grp-chefs-inc13", principalDisplayName = "Chefs", role = "Manager" }));
        await Json(await admin.PostAsJsonAsync("/api/incidents", Major("tech.inc13@grp-chefs-inc13", usine)));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/incidents", Major("tech.inc13@grp-chefs-inc13"))).StatusCode);

        // Une modification qui garde le même responsable ne revérifie pas.
        await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", new
        {
            title = "INC-13 renommé", impact = "Élevé", urgency = "Élevée", isMajor = true, ownerType = "User", ownerId = "chef.inc13", ownerDisplayName = "Chef"
        }));
    }

    [Fact]
    public async Task INC_13_un_majeur_sans_responsable_ne_sort_pas_de_Nouveau()
    {
        // Décision du 2026-10-26 (#200) : un majeur se déclare sans responsable, mais ne quitte
        // « Nouveau » qu'avec un gestionnaire pour responsable ; la console le signale.
        var m = api.Client("Manager", "inc13c.manager");
        object Inc(string? status, string? owner) => new
        {
            title = "INC-13 sans responsable", impact = "Élevé", urgency = "Élevée", isMajor = true, status,
            ownerType = "User", ownerId = owner, ownerDisplayName = owner,
            resolution = status == "Résolu" ? "Rétabli" : null, resolutionCode = status == "Résolu" ? "Correctif appliqué" : null
        };
        var inc = await Json(await m.PostAsJsonAsync("/api/incidents", Inc(null, null)));
        var url = $"/api/incidents/{Id(inc)}";
        string? Status(JsonElement console) => console.GetProperty("management").GetProperty("majorIncidents").EnumerateArray()
            .Single(i => Id(i) == Id(inc)).GetProperty("status").GetString();
        Assert.Equal("Responsable à désigner", Status(await Json(await m.GetAsync("/api/console"))));

        // Relié à un problème (INC-15), il ne se résout pas pour autant sans responsable.
        var p = await Json(await m.PostAsJsonAsync("/api/problems", new
        {
            title = "Cause INC-13", description = "d", impact = "Moyen", urgency = "Moyenne", category = "", affectedService = "", source = "Proactif"
        }));
        await Json(await m.PostAsJsonAsync("/api/links", new { fromType = "incident", fromId = Id(inc), toReference = p.GetProperty("reference").GetString() }));
        foreach (var target in new[] { "Résolu", "En cours" })
        {
            var refused = await m.PutAsJsonAsync(url, Inc(target, null));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Incident majeur : désigner un gestionnaire", await refused.Content.ReadAsStringAsync());
        }

        // La résolution d'un parent ne l'en sort pas non plus : il reste Nouveau, listé avec sa raison (INC-16).
        var parent = await Json(await m.PostAsJsonAsync("/api/incidents", new
        {
            title = "INC-13 parent", impact = "Faible", urgency = "Faible", ownerType = "User", ownerId = "inc13c.tech", ownerDisplayName = "Tech"
        }));
        await Json(await m.PutAsJsonAsync($"{url}/parent", new { parentReference = parent.GetProperty("reference").GetString() }));
        var resolved = await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(parent)}", new
        {
            title = "INC-13 parent", impact = "Faible", urgency = "Faible", status = "Résolu", ownerType = "User", ownerId = "inc13c.tech",
            ownerDisplayName = "Tech", resolution = "Rétabli", resolutionCode = "Correctif appliqué"
        }));
        var left = Assert.Single(resolved.GetProperty("unresolvedChildren").EnumerateArray());
        Assert.Equal(inc.GetProperty("reference").GetString(), left.GetProperty("reference").GetString());
        Assert.Contains("responsable", left.GetProperty("reason").GetString());
        Assert.Equal("Nouveau", (await Json(await m.GetAsync(url))).GetProperty("status").GetString());

        // Un gestionnaire désigné : la prise en charge passe, la console n'alerte plus.
        await Json(await m.PutAsJsonAsync(url, Inc("En cours", "chef.inc13c")));
        Assert.Equal("En cours", Status(await Json(await m.GetAsync("/api/console"))));
    }

    [Fact]
    public async Task INC_24_etat_SLA_sur_la_fiche_le_registre_et_la_console()
    {
        var m = api.Client("Manager", "inc24.manager");
        object Inc(string title) => new
        {
            title, impact = "Faible", urgency = "Faible", ownerType = "User", ownerId = "inc24.tech", ownerDisplayName = "Tech"
        };
        var late = await Json(await m.PostAsJsonAsync("/api/incidents", Inc("INC-24 dépassé")));
        var risk = await Json(await m.PostAsJsonAsync("/api/incidents", Inc("INC-24 en risque")));
        var none = await Json(await m.PostAsJsonAsync("/api/incidents", Inc("INC-24 sans SLA")));
        await using (var db = api.Database())
        {
            var now = DateTime.UtcNow;
            var l = await db.Incidents.FirstAsync(x => x.Id == Id(late));
            l.CreatedAt = now.AddHours(-10); l.ResolutionDueAt = now.AddHours(-1);
            var r = await db.Incidents.FirstAsync(x => x.Id == Id(risk));
            r.CreatedAt = now.AddHours(-9); r.ResolutionDueAt = now.AddHours(1);
            await db.SaveChangesAsync();
        }

        Assert.Equal("SLA dépassé", (await Json(await m.GetAsync($"/api/incidents/{Id(late)}"))).GetProperty("slaState").GetString());
        var list = (await Json(await m.GetAsync("/api/incidents?q=INC-24"))).EnumerateArray().ToList();
        Assert.Equal("SLA en risque", list.Single(i => Id(i) == Id(risk)).GetProperty("slaState").GetString());
        Assert.Equal("Sans SLA", list.Single(i => Id(i) == Id(none)).GetProperty("slaState").GetString());

        // Console : Urgent du responsable, et des gestionnaires.
        var mine = (await Json(await api.Client("User", "inc24.tech").GetAsync("/api/console"))).GetProperty("mySlaIncidents").EnumerateArray().ToList();
        Assert.Equal([Id(late), Id(risk)], mine.Where(i => Id(i) == Id(late) || Id(i) == Id(risk)).Select(Id).ToList());   // échéance la plus proche d'abord
        var managed = (await Json(await m.GetAsync("/api/console"))).GetProperty("management").GetProperty("slaIncidents").EnumerateArray().ToList();
        Assert.Contains(managed, i => Id(i) == Id(late) && i.GetProperty("status").GetString() == "SLA dépassé");
        Assert.DoesNotContain(managed, i => Id(i) == Id(none));
    }
}
