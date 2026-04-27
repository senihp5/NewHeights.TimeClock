using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;

namespace NewHeights.TimeClock.Web.Services;

/// <inheritdoc cref="IHelpArticleService"/>
public class HelpArticleService : IHelpArticleService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly ILogger<HelpArticleService> _logger;

    public HelpArticleService(IDbContextFactory<TimeClockDbContext> dbFactory, ILogger<HelpArticleService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TcHelpArticle>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
        return await ctx.TcHelpArticles
            .AsNoTracking()
            .Where(a => a.IsActive)
            .OrderBy(a => a.SectionOrder)
            .ThenBy(a => a.ArticleOrder)
            .ToListAsync(ct);
    }

    public async Task UpdateArticleAsync(int helpArticleId, string title, string? summary, string bodyHtml, string editorEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title required.", nameof(title));
        if (string.IsNullOrWhiteSpace(bodyHtml))
            throw new ArgumentException("Body required.", nameof(bodyHtml));

        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
        var article = await ctx.TcHelpArticles.FirstOrDefaultAsync(a => a.HelpArticleId == helpArticleId, ct);
        if (article == null)
            throw new InvalidOperationException($"Help article {helpArticleId} not found.");

        article.Title = title.Trim();
        article.Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        article.BodyHtml = bodyHtml;
        article.ModifiedBy = editorEmail;
        article.ModifiedDate = DateTime.Now;

        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "HelpArticle {Id} ({Slug}) edited by {Email}",
            article.HelpArticleId, article.Slug, editorEmail);
    }
}
