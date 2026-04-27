/*
===============================================================================
Migration 054 — Create TC_HelpArticles
Date: 2026-04-27
Purpose:
  Backing store for the in-portal help system. One row per article.
  Articles are grouped into sections (Getting Started, My Time, Supervisor,
  HR, Reception, Admin), gated by an existing AuthorizeView policy name
  (RequireAnyStaff / RequireHourly / RequireSupervisor / RequireHR /
  RequireReception / RequireAdmin), and rendered on /help by HelpArticleService.

Schema:
  HelpArticleId  INT IDENTITY PK
  Slug           NVARCHAR(100)  unique URL anchor — used for /help#slug deep links
  Title          NVARCHAR(200)
  SectionKey     NVARCHAR(50)   stable enum-like key (getting-started, my-time, ...)
  SectionTitle   NVARCHAR(100)  display label for the section header
  SectionOrder   INT            ordering of sections on the page (1=first)
  ArticleOrder   INT            ordering within a section (1=first)
  PolicyName     NVARCHAR(50)   AuthorizeView policy name; cascades naturally
  Summary        NVARCHAR(500)  optional one-liner shown under the title
  BodyHtml       NVARCHAR(MAX)  pre-sanitized HTML; admin authors via inline edit
  IsActive       BIT
  CreatedDate    DATETIME
  ModifiedDate   DATETIME
  ModifiedBy     NVARCHAR(100)  who last edited (admin email)

Idempotent — drops/recreates only if the table doesn't exist.
===============================================================================
*/
SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'Migration 054: Create TC_HelpArticles';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_HelpArticles')
BEGIN
    CREATE TABLE TC_HelpArticles (
        HelpArticleId  INT IDENTITY(1,1) NOT NULL,
        Slug           NVARCHAR(100) NOT NULL,
        Title          NVARCHAR(200) NOT NULL,
        SectionKey     NVARCHAR(50)  NOT NULL,
        SectionTitle   NVARCHAR(100) NOT NULL,
        SectionOrder   INT NOT NULL,
        ArticleOrder   INT NOT NULL,
        PolicyName     NVARCHAR(50)  NOT NULL,
        Summary        NVARCHAR(500) NULL,
        BodyHtml       NVARCHAR(MAX) NOT NULL,
        IsActive       BIT NOT NULL CONSTRAINT DF_HelpArticles_IsActive DEFAULT (1),
        CreatedDate    DATETIME NOT NULL CONSTRAINT DF_HelpArticles_CreatedDate DEFAULT (GETDATE()),
        ModifiedDate   DATETIME NOT NULL CONSTRAINT DF_HelpArticles_ModifiedDate DEFAULT (GETDATE()),
        ModifiedBy     NVARCHAR(100) NULL,
        CONSTRAINT PK_HelpArticles PRIMARY KEY CLUSTERED (HelpArticleId),
        CONSTRAINT UQ_HelpArticles_Slug UNIQUE (Slug)
    );
    PRINT '    Created table: TC_HelpArticles';
END
ELSE PRINT '    Skipped: TC_HelpArticles already exists';
GO

-- Index used by the page query: WHERE IsActive = 1 ORDER BY SectionOrder, ArticleOrder.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_HelpArticles_Section'
      AND object_id = OBJECT_ID('TC_HelpArticles')
)
BEGIN
    CREATE INDEX IX_HelpArticles_Section
        ON TC_HelpArticles(SectionOrder, ArticleOrder)
        INCLUDE (Slug, Title, SectionKey, SectionTitle, PolicyName, Summary, IsActive)
        WHERE IsActive = 1;
    PRINT '    Added filtered index: IX_HelpArticles_Section';
END
ELSE PRINT '    Skipped: IX_HelpArticles_Section already exists';
GO

PRINT 'Migration 054 complete.';
GO
