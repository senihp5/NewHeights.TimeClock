namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Migration 054 (2026-04-27). One row per in-portal help article.
///
/// Articles render on /help, grouped by section, role-filtered via
/// <see cref="PolicyName"/> matching an existing AuthorizeView policy
/// (RequireAnyStaff / RequireHourly / RequireSupervisor / RequireHR /
/// RequireReception / RequireAdmin). Admins bypass the filter via the
/// "Show all roles" toggle on the page.
///
/// BodyHtml is rendered via MarkupString — it is treated as trusted
/// administrator-authored content. The inline edit UI is admin-gated.
/// </summary>
public class TcHelpArticle
{
    public int HelpArticleId { get; set; }

    /// <summary>URL-safe identifier used for deep-link anchors (e.g. /help#sign-in).
    /// Lowercase, hyphenated, unique.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Stable enum-like key used by code (e.g. "getting-started",
    /// "supervisor"). Section grouping/ordering happens by SectionOrder.</summary>
    public string SectionKey { get; set; } = string.Empty;

    /// <summary>Display label for the section header, e.g. "Getting Started".</summary>
    public string SectionTitle { get; set; } = string.Empty;

    /// <summary>1-based ordering of sections on the page.</summary>
    public int SectionOrder { get; set; }

    /// <summary>1-based ordering of articles within a section.</summary>
    public int ArticleOrder { get; set; }

    /// <summary>AuthorizeView policy name. Visible to anyone the policy
    /// authorizes. Admin always sees all rows when "Show all" toggle is on.</summary>
    public string PolicyName { get; set; } = string.Empty;

    /// <summary>One-line summary shown under the title in the accordion.</summary>
    public string? Summary { get; set; }

    /// <summary>HTML body rendered as MarkupString. Admin-authored only.</summary>
    public string BodyHtml { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.Now;

    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    /// <summary>Email of the admin who last edited (NULL for system-seeded
    /// rows; the seed migration's MERGE preserves edits by skipping rows
    /// where ModifiedBy IS NOT NULL).</summary>
    public string? ModifiedBy { get; set; }
}
