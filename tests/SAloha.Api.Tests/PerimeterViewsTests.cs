using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SAloha.Api.Tests;

/// <summary>Étape 6 du lot 1, vues transverses par périmètre : liens (PER-08), console, reporting, recherche (PER-09).</summary>
[Collection("api")]
public class PerimeterViewsTests(ApiFixture api)
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode} : {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static int Id(JsonElement e) => e.GetProperty("id").GetInt32();
    private static string Ref(JsonElement e) => e.GetProperty("reference").GetString()!;

    /// <summary>Siège et Usine ; Tiana User du Siège ; Lova Manager du Siège et User de l'Usine.</summary>
    private async Task<(int Siege, int Usine, HttpClient Tiana, HttpClient Lova)> Setup(string tag)
    {
        var admin = api.Client();
        var siege = Id(await Json(await admin.PostAsJsonAsync("/api/admin/perimeters", new { name = $"Siège {tag}" })));
        var usine = Id(await Json(await admin.PostAsJsonAsync("/api/admin/perimeters", new { name = $"Usine {tag}" })));
        async Task Assign(int p, string id, string role) =>
            await Json(await admin.PostAsJsonAsync($"/api/admin/perimeters/{p}/assignments",
                new { principalType = "User", principalId = id, principalDisplayName = id, role }));
        await Assign(siege, $"tiana.{tag}", "User");
        await Assign(siege, $"lova.{tag}", "Manager");
        await Assign(usine, $"lova.{tag}", "User");
        return (siege, usine, api.Client("User", $"tiana.{tag}"), api.Client("User", $"lova.{tag}"));
    }

    private static object Incident(string title, int perimeter, bool major = false, string? owner = null, string? status = null) => new
    {
        title, description = "Panne", impact = "Moyen", urgency = "Moyenne", perimeterId = perimeter, isMajor = major, status,
        resolution = status is null ? null : "Remplacement de la carte", resolutionCode = status is null ? null : "Correctif appliqué",
        ownerType = owner is null ? null : "User", ownerId = owner, ownerDisplayName = owner
    };

    [Fact]
    public async Task PER_08_un_lien_ne_donne_pas_acces_a_l_autre_perimetre()
    {
        var (siege, usine, tiana, _) = await Setup("per08");
        var admin = api.Client();
        var mine = await Json(await tiana.PostAsJsonAsync("/api/incidents", Incident("Incident Siège", siege)));
        var other = await Json(await admin.PostAsJsonAsync("/api/incidents", Incident("Incident Usine", usine)));
        await Json(await admin.PostAsJsonAsync("/api/links", new { fromType = "incident", fromId = Id(mine), toReference = Ref(other) }));

        // Le lien se voit, pas l'enregistrement de l'Usine.
        var links = await Json(await tiana.GetAsync($"/api/links?type=incident&id={Id(mine)}"));
        var hidden = links.EnumerateArray().Single();
        Assert.True(hidden.GetProperty("hidden").GetBoolean());
        Assert.Equal(0, Id(hidden));
        Assert.Equal("", Ref(hidden));
        // Tiana ne le retire pas, ne lit pas les liens de l'Usine, ne relie pas vers l'Usine.
        Assert.Equal(HttpStatusCode.NotFound, (await tiana.DeleteAsync($"/api/links/{hidden.GetProperty("linkId").GetInt32()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tiana.GetAsync($"/api/links?type=incident&id={Id(other)}")).StatusCode);
        var second = await Json(await tiana.PostAsJsonAsync("/api/incidents", Incident("Autre incident Siège", siege)));
        Assert.Equal(HttpStatusCode.BadRequest, (await tiana.PostAsJsonAsync("/api/links",
            new { fromType = "incident", fromId = Id(second), toReference = Ref(other) })).StatusCode);
        // Entre deux enregistrements visibles, le lien reste permis.
        await Json(await tiana.PostAsJsonAsync("/api/links", new { fromType = "incident", fromId = Id(second), toReference = Ref(mine) }));
    }

    [Fact]
    public async Task PER_09_console_limitee_aux_perimetres_pilotage_aux_perimetres_geres()
    {
        var (siege, usine, tiana, lova) = await Setup("per09c");
        var admin = api.Client();
        var majorSiege = await Json(await admin.PostAsJsonAsync("/api/incidents", Incident("Majeur Siège", siege, major: true)));
        var majorUsine = await Json(await admin.PostAsJsonAsync("/api/incidents", Incident("Majeur Usine", usine, major: true)));
        // Confié à Tiana, mais dans un périmètre qui n'est pas le sien.
        var outside = await Json(await admin.PostAsJsonAsync("/api/incidents", Incident("Hors périmètre", usine, owner: "tiana.per09c")));

        var c = await Json(await tiana.GetAsync("/api/console"));
        Assert.DoesNotContain(c.GetProperty("myAssignments").EnumerateArray(), x => Ref(x) == Ref(outside));
        Assert.Equal(JsonValueKind.Null, c.GetProperty("management").ValueKind);   // User partout : pas de pilotage

        // Lova est Manager du Siège (pilotage) et User de l'Usine (pas de pilotage).
        var l = await Json(await lova.GetAsync("/api/console"));
        var majors = l.GetProperty("management").GetProperty("majorIncidents").EnumerateArray().Select(Ref).ToList();
        Assert.Contains(Ref(majorSiege), majors);
        Assert.DoesNotContain(Ref(majorUsine), majors);

        // L'Admin voit tout, et filtre par périmètre.
        var all = (await Json(await admin.GetAsync("/api/console"))).GetProperty("management").GetProperty("majorIncidents").EnumerateArray().Select(Ref).ToList();
        Assert.Contains(Ref(majorUsine), all);
        var onlySiege = (await Json(await admin.GetAsync($"/api/console?perimeter={siege}"))).GetProperty("management")
            .GetProperty("majorIncidents").EnumerateArray().Select(Ref).ToList();
        Assert.Contains(Ref(majorSiege), onlySiege);
        Assert.DoesNotContain(Ref(majorUsine), onlySiege);
    }

    [Fact]
    public async Task PER_09_reporting_limite_aux_perimetres()
    {
        var (siege, usine, tiana, _) = await Setup("per09r");
        var admin = api.Client();
        await Json(await admin.PostAsJsonAsync("/api/incidents", Incident("Reporting Siège", siege, major: true)));
        await Json(await admin.PostAsJsonAsync("/api/incidents", Incident("Reporting Usine", usine, major: true)));

        int Majors(JsonElement overview) => overview.GetProperty("kpis").GetProperty("majorIncidentsOpen").GetInt32();
        Assert.Equal(1, Majors(await Json(await tiana.GetAsync("/api/reports/dashboard/overview"))));
        Assert.Equal(1, Majors(await Json(await admin.GetAsync($"/api/reports/dashboard/overview?perimeter={usine}"))));
        // Un périmètre qui n'est pas le sien ne donne rien.
        Assert.Equal(0, Majors(await Json(await tiana.GetAsync($"/api/reports/dashboard/overview?perimeter={usine}"))));
        var summary = await Json(await tiana.GetAsync("/api/reports/summary"));
        Assert.Equal(1, summary.GetProperty("majorIncidentsOpen").GetInt32());
    }

    [Fact]
    public async Task PER_09_recherche_limitee_aux_perimetres()
    {
        var (_, usine, tiana, lova) = await Setup("per09s");
        var admin = api.Client();
        var inc = await Json(await admin.PostAsJsonAsync("/api/incidents", Incident("Rotative zx81 bloquée", usine)));
        await Json(await admin.PutAsJsonAsync($"/api/incidents/{Id(inc)}", Incident("Rotative zx81 bloquée", usine, status: "Résolu")));

        async Task<bool> Finds(HttpClient c) => (await Json(await c.GetAsync("/api/search?q=rotative%20zx81")))
            .EnumerateArray().Any(h => h.GetProperty("reference").GetString() == Ref(inc));
        // L'indexation est asynchrone : on attend que l'Admin le trouve.
        for (var i = 0; i < 50 && !await Finds(admin); i++) await Task.Delay(200);
        Assert.True(await Finds(admin));
        Assert.True(await Finds(lova));     // User de l'Usine
        Assert.False(await Finds(tiana));   // Siège seulement
        Assert.Equal(HttpStatusCode.NotFound, (await tiana.GetAsync($"/api/search/similar?type=incident&id={Id(inc)}")).StatusCode);
    }
    [Fact]
    public async Task RAG_13_le_filtre_des_perimetres_precede_le_classement()
    {
        var (siege, usine, _, _) = await Setup("rag13");
        var admin = api.Client();
        await Json(await admin.PostAsJsonAsync($"/api/admin/perimeters/{usine}/assignments",
            new { principalType = "User", principalId = "fara.rag13", principalDisplayName = "Fara", role = "User" }));
        var fara = api.Client("User", "fara.rag13");   // Usine seulement
        async Task<JsonElement> Resolved(string title, int p)
        {
            var e = await Json(await admin.PostAsJsonAsync("/api/incidents", Incident(title, p)));
            return await Json(await admin.PutAsJsonAsync($"/api/incidents/{Id(e)}", Incident(title, p, status: "Résolu")));
        }
        // Le Siège occupe la tête du classement ; l'incident de l'Usine vient après.
        var siegeHits = new List<JsonElement>();
        for (var i = 0; i < 4; i++) siegeHits.Add(await Resolved($"Rotative kq77 kq77 kq77 bloquée {i}", siege));
        var usineHit = await Resolved("Rotative kq77", usine);

        async Task<List<string>> Refs(HttpClient c, int limit) => (await Json(await c.GetAsync($"/api/search?q=kq77&limit={limit}")))
            .EnumerateArray().Select(h => h.GetProperty("reference").GetString()!).ToList();
        // L'indexation est asynchrone, et une reconstruction de l'index demandée par un autre test
        // peut le vider puis le remplir pendant celui-ci : chaque lecture attend son résultat.
        async Task<List<string>> Until(HttpClient c, int limit, Func<List<string>, bool> ok)
        {
            var refs = await Refs(c, limit);
            for (var i = 0; i < 50 && !ok(refs); i++) { await Task.Delay(200); refs = await Refs(c, limit); }
            return refs;
        }
        Assert.Equal(5, (await Until(admin, 50, r => r.Count == 5)).Count);
        Assert.DoesNotContain(Ref(usineHit), await Until(admin, 3, r => r.Count == 3));   // témoin : hors des trois premiers

        // Un seul résultat demandé : celui de l'Usine, plus loin que trois fois la limite.
        Assert.Equal([Ref(usineHit)], await Until(fara, 1, r => r.Count == 1));

        // PER-07 : un enregistrement déplacé se trouve depuis le nouveau périmètre dès le déplacement.
        var moved = siegeHits[0];
        await Json(await admin.PutAsJsonAsync($"/api/incidents/{Id(moved)}", Incident(moved.GetProperty("title").GetString()!, usine, status: "Résolu")));
        var after = await Until(fara, 10, r => r.Count == 2);
        Assert.Contains(Ref(moved), after);
        Assert.Equal(2, after.Count);
    }
}
