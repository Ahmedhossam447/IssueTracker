using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IssueTracker.Infrastructure.Migrations
{
    /// <summary>
    /// DDIA Ch. 4: Inverted Index (PostgreSQL GIN) for Full-Text Search on Issues(Title, Description).
    /// Title is weighted 'A' (highest relevance) and Description is weighted 'B'.
    /// </summary>
    public partial class AddIssueFullTextSearchGinIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""Issues""
                ADD COLUMN IF NOT EXISTS ""SearchVector"" tsvector
                GENERATED ALWAYS AS (
                    setweight(to_tsvector('english', coalesce(""Title"", '')), 'A') ||
                    setweight(to_tsvector('english', coalesce(""Description"", '')), 'B')
                ) STORED;

                CREATE INDEX IF NOT EXISTS ""IX_Issues_SearchVector_GIN""
                ON ""Issues"" USING GIN (""SearchVector"");
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_Issues_SearchVector_GIN"";
                ALTER TABLE ""Issues"" DROP COLUMN IF EXISTS ""SearchVector"";
            ");
        }
    }
}
