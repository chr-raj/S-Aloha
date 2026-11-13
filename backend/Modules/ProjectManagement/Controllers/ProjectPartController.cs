using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAloha.Api.Core.Data;
using SAloha.Api.Core.Perimeters;
using SAloha.Api.Core.Records;

namespace SAloha.Api.Modules.ProjectManagement;

/// <summary>
/// CRUD d'un élément de projet, sous /api/projects/{projectId}/… : portée au
/// projet, composant activé (PRJ-03, 400 sinon), droits (PRJ-04), projet
/// clôturé ou abandonné figé (PRJ-07).
/// </summary>
[ApiController]
[Authorize(Policy = "User")]
public abstract class ProjectPartController<T, TDto, TView>(AppDbContext db) : ControllerBase where T : ProjectPart, new()
{
    /// <summary>A2 (ADR-0016) : la réponse de l'API pour ce composant — jamais l'entité EF.</summary>
    protected abstract TView View(T e);

    protected AppDbContext Db => db;
    protected string Me => User.Identity?.Name ?? "";
    protected string MyDisplay => User.FindFirst("displayName")?.Value ?? Me;

    /// <summary>Composants dont l'un doit être activé pour accéder à cet élément.</summary>
    protected abstract string[] Components { get; }
    /// <summary>Structure du projet (phases, sprints, jalons) : chef de projet ou gestionnaire.</summary>
    protected virtual bool LeadOnly => false;
    protected abstract string? Apply(T e, TDto dto, Project project, bool creating);
    protected virtual IQueryable<T> Order(IQueryable<T> q) => q.OrderBy(x => x.Id);
    /// <summary>Contrôles nécessitant la base (un seul sprint actif…).</summary>
    protected virtual Task<string?> ValidateAsync(T e, T? before) => Task.FromResult<string?>(null);
    protected virtual Task OnSavingAsync(T e, T? before) => Task.CompletedTask;

    private PerimeterScope? _scope;
    /// <summary>Portée de l'utilisateur (PER-03), chargée une fois par requête.</summary>
    protected async Task<PerimeterScope> ScopeAsync() =>
        _scope ??= await HttpContext.RequestServices.GetRequiredService<PerimeterAccess>().GetAsync();
    /// <summary>Chef de projet ou gestionnaire du périmètre du projet ; la portée est chargée par <see cref="ProjectAsync"/>.</summary>
    protected bool IsLead(Project p) => ProjectRights.IsLead(p, User, _scope!);

    /// <summary>Le projet, s'il est visible de l'utilisateur (PER-05) ; null sinon.</summary>
    protected async Task<Project?> ProjectAsync(int projectId)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        return p is not null && (await ScopeAsync()).Sees(p.PerimeterId) ? p : null;
    }

    /// <summary>
    /// Garde commune de toute route du registre : projet visible, composant activé (PRJ-03) et,
    /// en écriture, projet non figé (PRJ-07) puis droit du chef de projet. Une route d'action
    /// propre à un registre (validation de fin d'étape…) passe par elle aussi.
    /// </summary>
    protected async Task<(Project? Project, IActionResult? Error)> Load(int projectId, bool writing)
    {
        var p = await ProjectAsync(projectId);
        if (p is null) return (null, NotFound());
        if (!Components.Any(p.Has))
            return (null, BadRequest(new { message = $"Composant non activé pour ce projet ({p.Method})." }));
        if (writing && Project.Done.Contains(p.Status))
            return (null, BadRequest(new { message = $"Projet {p.Status.ToLower()} : il ne se modifie plus." }));
        if (writing && LeadOnly && !IsLead(p)) return (null, Forbid());
        return (p, null);
    }

    [HttpGet]
    public async Task<IActionResult> List(int projectId)
    {
        var (p, error) = await Load(projectId, false);
        if (error is not null) return error;
        return Ok((await Order(db.Set<T>().AsNoTracking().Where(x => x.ProjectId == p!.Id)).ToListAsync()).Select(View));
    }

    [HttpPost]
    public async Task<IActionResult> Create(int projectId, TDto dto)
    {
        var (p, error) = await Load(projectId, true);
        if (error is not null) return error;
        var e = new T { ProjectId = p!.Id, CreatedBy = Me };
        var message = Apply(e, dto, p, true) ?? Lengths.Check(e) ?? await ValidateAsync(e, null);
        if (message is not null) return BadRequest(new { message });
        await OnSavingAsync(e, null);
        db.Set<T>().Add(e);
        await db.SaveChangesAsync();
        return Ok(View(e));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int projectId, int id, TDto dto)
    {
        var (p, error) = await Load(projectId, true);
        if (error is not null) return error;
        var e = await db.Set<T>().FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId);
        if (e is null) return NotFound();
        var before = (T)db.Entry(e).OriginalValues.ToObject();
        var message = Apply(e, dto, p!, false) ?? Lengths.Check(e) ?? await ValidateAsync(e, before);
        if (message is not null) return BadRequest(new { message });
        await OnSavingAsync(e, before);
        await db.SaveChangesAsync();
        return Ok(View(e));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int projectId, int id)
    {
        var (p, error) = await Load(projectId, true);
        if (error is not null) return error;
        // Supprimer reste un geste de chef de projet, même pour un élément ouvert à tous.
        if (!IsLead(p!)) return Forbid();
        var e = await db.Set<T>().FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId);
        if (e is null) return NotFound();
        db.Set<T>().Remove(e);
        await db.SaveChangesAsync();
        return NoContent();
    }

    protected static string? Required(string? value, string label) =>
        string.IsNullOrWhiteSpace(value) ? $"{label} : champ obligatoire." : null;
}
