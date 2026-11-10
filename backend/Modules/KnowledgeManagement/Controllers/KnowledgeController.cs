using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAloha.Api.Core.Auth;
using SAloha.Api.Core.Data;
using SAloha.Api.Core.Records;
using SAloha.Api.Core.Notifications;

namespace SAloha.Api.Modules.KnowledgeManagement;

[Route("api/knowledge")]
public class KnowledgeController(AppDbContext db) : RecordController<KnowledgeArticle, KnowledgeArticleDto, KnowledgeArticleView>(db)
{
    protected override KnowledgeArticleView View(KnowledgeArticle e) => KnowledgeArticleView.From(e);
    protected override string Code => "KB";
    protected override string LinkType => "article";
    protected override string[] Statuses => KnowledgeArticle.Statuses;
    protected override IReadOnlyDictionary<string, string[]> Transitions => KnowledgeArticle.Transitions;
    // ADR-0014 D2 : un article publié se lit dans toute l'organisation.
    protected override System.Linq.Expressions.Expression<Func<KnowledgeArticle, bool>> SharedWithAll => a => a.Status == "Publié";

    protected override string? Apply(KnowledgeArticle e, KnowledgeArticleDto dto, bool creating)
    {
        var error = Allowed.Check("Type d'article", dto.ArticleType, KnowledgeArticle.Types);
        if (error is not null) return error;
        e.ArticleType = dto.ArticleType!;
        e.Content = dto.Content ?? "";
        e.Keywords = dto.Keywords ?? "";
        e.ReviewDate = Dates.Utc(dto.ReviewDate);
        return null;
    }

    // Publier engage l'organisation ; archiver un article ou le réactiver aussi (KB-18).
    protected override bool RequiresManager(string from, string to) =>
        to is "Publié" or "Archivé" || from == "Archivé";

    protected override string? CheckStatus(KnowledgeArticle e, string status) =>
        status == "Publié" && string.IsNullOrWhiteSpace(e.Content) ? "Un article publié doit avoir un contenu." : null;

    private static readonly string[] ContentFields = ["title", "description", "content", "articleType"];

    /// <summary>KB-15, KB-16 : rédacteurs d'un article — son créateur et quiconque en a modifié le contenu (journal d'audit).</summary>
    private async Task<List<string>> WritersAsync(KnowledgeArticle e)
    {
        var writers = new List<string> { (e.Id == 0 ? Me : e.CreatedBy).ToLowerInvariant() };
        if (e.Id != 0)
            writers.AddRange(await Db.AuditEntries.AsNoTracking()
                .Where(a => a.EntityType == LinkType && a.EntityId == e.Id && a.Action == Core.Audit.AuditEntry.Modification && ContentFields.Contains(a.Field!))
                .Select(a => a.Author.ToLower()).ToListAsync());
        return writers.Where(w => w != "").Distinct().ToList();
    }

    private List<string>? _authors;

    /// <summary>KB-16 : quatre yeux — un gestionnaire ne publie pas un article dont il est le seul rédacteur, sauf Admin.</summary>
    protected override async Task<string?> CheckStatusAsync(KnowledgeArticle e, string status)
    {
        var error = CheckStatus(e, status);
        if (error is not null || status != "Publié" || e.Status == "Publié") return error;
        _authors = await WritersAsync(e);
        if (!User.IsInRole("Admin") && _authors.All(w => w == Me.ToLowerInvariant()))
            return "Quatre yeux : vous êtes le seul rédacteur de cet article ; un autre gestionnaire le publie (ou un Admin).";
        return null;
    }

    private bool _published;

    /// <summary>
    /// KB-15 : la version publiée est conservée (un article naît brouillon : seule une mise à jour
    /// le publie) ; KB-13 : les votes portaient sur la version précédente, ils sont remis à zéro.
    /// </summary>
    protected override async Task BeforeSaveAsync(KnowledgeArticle e, bool creating)
    {
        if (creating) return;
        // Un gestionnaire qui retouche un article publié le republie : nouvelle version.
        var entry = Db.Entry(e);
        if (!_published && e.Status == "Publié" && entry.Property(x => x.Status).OriginalValue == "Publié"
            && (entry.Property(x => x.Title).IsModified || entry.Property(x => x.Description).IsModified
                || entry.Property(x => x.Content).IsModified || entry.Property(x => x.ArticleType).IsModified))
        {
            e.Version++; e.PublishedAt = DateTime.UtcNow; e.PublishedBy = MyDisplay; _published = true;
        }
        if (!_published) return;
        Db.KnowledgeArticleVersions.Add(new KnowledgeArticleVersion
        {
            ArticleId = e.Id, Version = e.Version, Title = e.Title, Description = e.Description, ArticleType = e.ArticleType,
            Content = e.Content, Authors = string.Join(", ", _authors ?? await WritersAsync(e)), PublishedBy = e.PublishedBy ?? MyDisplay,
            PublishedAt = e.PublishedAt ?? DateTime.UtcNow,
        });
        Db.KnowledgeFeedbacks.RemoveRange(await Db.KnowledgeFeedbacks.Where(f => f.ArticleId == e.Id).ToListAsync());
        (e.HelpfulCount, e.NotHelpfulCount) = (0, 0);
    }

    // Un article publié retouché par un non-gestionnaire repasse en brouillon :
    // la nouvelle version attend une nouvelle publication.
    protected override string? StatusAfterEdit(KnowledgeArticle e, KnowledgeArticleDto dto) =>
        e.Status == "Publié" && (dto.Title.Trim() != e.Title || (dto.Description ?? "") != e.Description
                                 || (dto.Content ?? "") != e.Content || dto.ArticleType != e.ArticleType)
            ? "Brouillon" : null;

    /// <summary>
    /// KB-03, KB-16 (décision tacite du 2026-11-09, #215) : la retouche d'un gestionnaire vaut
    /// publication, sauf s'il serait le seul rédacteur de la nouvelle version (hors Admin) :
    /// l'article repasse en brouillon, un autre gestionnaire le republie.
    /// </summary>
    protected override async Task<string?> StatusAfterManagerEditAsync(KnowledgeArticle e, KnowledgeArticleDto dto)
    {
        if (StatusAfterEdit(e, dto) is null || User.IsInRole("Admin")) return null;
        return (await WritersAsync(e)).All(w => w == Me.ToLowerInvariant()) ? "Brouillon" : null;
    }

    // KB-20 : publication → rédacteur (la soumission est notifiée par Submit).
    protected override void OnNotify(KnowledgeArticle e, string? from, Notifier n)
    {
        if (from is not null && e.Status == "Publié") n.ToCreator(e, LinkType, "Article publié");
    }

    protected override void OnStatusChanged(KnowledgeArticle e, string from)
    {
        if (e.Status == "Publié")
        {
            e.PublishedAt = DateTime.UtcNow; e.PublishedBy = MyDisplay;
            e.Version++; _published = true;   // KB-15
            // KB-11 : sans date de revue, l'article se revoit un an après sa publication.
            e.ReviewDate ??= DateTime.UtcNow.Date.AddMonths(12);
        }
        else if (e.Status == "Brouillon") { e.PublishedAt = null; e.PublishedBy = null; }
        // Une décision est prise : la soumission (KB-21) et la demande d'archivage (KB-17) sont soldées.
        if (e.Status != "Brouillon") { e.SubmittedAt = null; e.SubmittedBy = null; }
        if (e.Status != "Publié") { e.ArchiveRequestedAt = null; e.ArchiveRequestedBy = null; e.ArchiveRequestReason = null; }
    }

    /// <summary>Responsable de l'article (en personne ou par un groupe AD) ou gestionnaire.</summary>
    private bool OwnsOrManages(KnowledgeArticle e) => IsManager || User.Designates(e.OwnerType, e.OwnerId);

    /// <summary>KB-21 : un brouillon est déclaré prêt ; il remonte dans « Décisions » des gestionnaires.</summary>
    [HttpPost("{id:int}/submit")]
    public async Task<IActionResult> Submit(int id)
    {
        var e = await Db.KnowledgeArticles.FindAsync(id);
        if (e is null || !await VisibleAsync(e)) return NotFound();
        // Agir sur l'article suppose d'être dans son périmètre (PER-05, PER-06).
        if (!(await ScopeAsync()).Sees(e.PerimeterId)) return Forbid();
        ActOn(e.PerimeterId);
        if (e.Status != "Brouillon") return BadRequest(new { message = "Seul un brouillon se soumet à publication." });
        if (string.IsNullOrWhiteSpace(e.Content)) return BadRequest(new { message = "Rédiger le contenu avant de soumettre l'article." });
        e.SubmittedAt = DateTime.UtcNow; e.SubmittedBy = MyDisplay;
        await Db.SaveChangesAsync();
        // KB-20 : brouillon soumis → gestionnaires.
        Notify.ToManagers(e, LinkType, "Article soumis à publication");
        await Notify.SaveAsync();
        return Ok(View(e));
    }

    /// <summary>KB-17 : le responsable confirme l'article à sa revue ; il reste publié.</summary>
    [HttpPost("{id:int}/review")]
    public async Task<IActionResult> Review(int id, ReviewDto dto)
    {
        var e = await Db.KnowledgeArticles.FindAsync(id);
        if (e is null || !await VisibleAsync(e)) return NotFound();
        // Agir sur l'article suppose d'être dans son périmètre (PER-05, PER-06).
        if (!(await ScopeAsync()).Sees(e.PerimeterId)) return Forbid();
        ActOn(e.PerimeterId);
        if (e.Status != "Publié") return BadRequest(new { message = "Seul un article publié se revoit." });
        if (!OwnsOrManages(e)) return Forbid();
        var next = Dates.Utc(dto.NextReviewDate) ?? DateTime.UtcNow.Date.AddMonths(12);
        if (next <= DateTime.UtcNow) return BadRequest(new { message = "La prochaine revue doit être dans le futur." });
        e.ReviewDate = next;
        e.LastReviewedAt = DateTime.UtcNow; e.LastReviewedBy = MyDisplay;
        e.ArchiveRequestedAt = null; e.ArchiveRequestedBy = null; e.ArchiveRequestReason = null;
        await Db.SaveChangesAsync();
        return Ok(View(e));
    }

    /// <summary>KB-17 : à la revue, le responsable demande l'archivage ; un gestionnaire décide.</summary>
    [HttpPost("{id:int}/archive-request")]
    public async Task<IActionResult> RequestArchive(int id, ArchiveRequestDto dto)
    {
        var e = await Db.KnowledgeArticles.FindAsync(id);
        if (e is null || !await VisibleAsync(e)) return NotFound();
        // Agir sur l'article suppose d'être dans son périmètre (PER-05, PER-06).
        if (!(await ScopeAsync()).Sees(e.PerimeterId)) return Forbid();
        ActOn(e.PerimeterId);
        if (e.Status != "Publié") return BadRequest(new { message = "Seul un article publié se propose à l'archivage." });
        if (!OwnsOrManages(e)) return Forbid();
        if (string.IsNullOrWhiteSpace(dto.Reason)) return BadRequest(new { message = "Motiver la demande d'archivage." });
        e.ArchiveRequestedAt = DateTime.UtcNow; e.ArchiveRequestedBy = MyDisplay;
        e.ArchiveRequestReason = dto.Reason.Trim();
        var error = Lengths.Check(e);
        if (error is not null) return BadRequest(new { message = error });
        await Db.SaveChangesAsync();
        return Ok(View(e));
    }

    /// <summary>KB-15 : versions publiées, de la plus récente à la plus ancienne.</summary>
    [HttpGet("{id:int}/versions")]
    public async Task<IActionResult> Versions(int id)
    {
        if (!await VisibleAsync(id)) return NotFound();
        return Ok((await Db.KnowledgeArticleVersions.AsNoTracking().Where(v => v.ArticleId == id).OrderByDescending(v => v.Version).ToListAsync())
            .Select(KnowledgeArticleVersionView.From));
    }

    /// <summary>
    /// KB-13 : « utile » ou « pas utile » sur un article publié, un vote par lecteur (modifiable).
    /// Trois « pas utile » de plus que d'« utile » signalent l'article à son responsable (notifié une fois).
    /// </summary>
    [HttpPost("{id:int}/feedback")]
    public async Task<IActionResult> Feedback(int id, FeedbackDto dto)
    {
        var e = await Db.KnowledgeArticles.FindAsync(id);
        if (e is null || !await VisibleAsync(e)) return NotFound();
        if (e.Status != "Publié") return BadRequest(new { message = "Seul un article publié reçoit des retours." });
        var me = Me.ToLowerInvariant();
        var wasFlagged = e.FlaggedUnhelpful;
        var vote = await Db.KnowledgeFeedbacks.FirstOrDefaultAsync(f => f.ArticleId == id && f.Username == me);
        if (vote is null) Db.KnowledgeFeedbacks.Add(vote = new KnowledgeFeedback { ArticleId = id, Username = me });
        else if (vote.Helpful) e.HelpfulCount--; else e.NotHelpfulCount--;
        (vote.Helpful, vote.At) = (dto.Helpful, DateTime.UtcNow);
        if (dto.Helpful) e.HelpfulCount++; else e.NotHelpfulCount++;
        await Db.SaveChangesAsync();
        if (e.FlaggedUnhelpful && !wasFlagged)
        {
            Notify.ToOwner(e, LinkType, "Article jugé peu utile par ses lecteurs");
            await Notify.SaveAsync();
        }
        return Ok(new { View = View(e), MyVote = dto.Helpful });
    }

    /// <summary>KB-13 : mon vote sur l'article, ou null.</summary>
    [HttpGet("{id:int}/feedback")]
    public async Task<IActionResult> MyFeedback(int id)
    {
        if (!await VisibleAsync(id)) return NotFound();
        var me = Me.ToLowerInvariant();
        return Ok(new { MyVote = await Db.KnowledgeFeedbacks.AsNoTracking().Where(f => f.ArticleId == id && f.Username == me).Select(f => (bool?)f.Helpful).FirstOrDefaultAsync() });
    }

    /// <summary>KB-14 : incidents et demandes reliés à l'article (résolus ou traités grâce à lui).</summary>
    [HttpGet("{id:int}/usage")]
    public async Task<IActionResult> Usage(int id)
    {
        if (!await VisibleAsync(id)) return NotFound();
        var others = await Db.ItemLinks.AsNoTracking()
            .Where(l => (l.FromType == LinkType && l.FromId == id) || (l.ToType == LinkType && l.ToId == id))
            .Select(l => l.FromType == LinkType && l.FromId == id ? l.ToType : l.FromType).ToListAsync();
        return Ok(new { Incidents = others.Count(t => t == "incident"), Requests = others.Count(t => t == "request") });
    }

    // Le registre n'affiche pas le contenu, potentiellement long : il reste dans la fiche.
    protected override async Task<IEnumerable<object>> ListItems(IQueryable<KnowledgeArticle> q) =>
        await q.Select(a => new
        {
            a.Id, a.Reference, a.Title, a.Status, a.ArticleType, a.Keywords, a.ReviewDate,
            a.OwnerId, a.OwnerDisplayName, a.CreatedAt, a.SubmittedAt, a.ArchiveRequestedAt
        }).ToListAsync();

    // La recherche porte aussi sur les mots-clés et le contenu.
    protected override IQueryable<KnowledgeArticle> Search(IQueryable<KnowledgeArticle> q, string key) =>
        q.Where(a => a.Title.ToLower().Contains(key) || a.Reference.ToLower().Contains(key)
                     || a.Keywords.ToLower().Contains(key) || a.Content.ToLower().Contains(key));

    protected override IQueryable<KnowledgeArticle> Filter(IQueryable<KnowledgeArticle> q)
    {
        var type = Request.Query["type"].ToString();
        return string.IsNullOrEmpty(type) ? q : q.Where(a => a.ArticleType == type);
    }
}
