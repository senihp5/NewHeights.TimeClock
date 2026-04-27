using NewHeights.TimeClock.Data.Entities;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Backing store for /help. Reads articles from TC_HelpArticles. Admins
/// can update Title / Summary / BodyHtml inline through the page; the
/// service stamps ModifiedBy + ModifiedDate so the seed migration's MERGE
/// won't clobber edits on re-run.
/// </summary>
public interface IHelpArticleService
{
    /// <summary>All active articles, grouped by section, ordered by
    /// (SectionOrder, ArticleOrder). Caller filters by user policy.</summary>
    Task<IReadOnlyList<TcHelpArticle>> GetActiveAsync(CancellationToken ct = default);

    /// <summary>Update an existing article's authoring fields. ModifiedBy
    /// + ModifiedDate are stamped automatically.</summary>
    Task UpdateArticleAsync(int helpArticleId, string title, string? summary, string bodyHtml, string editorEmail, CancellationToken ct = default);
}
