using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IssueTracker.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueLogicalClockVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Issues",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "Issues");
        }
    }
}
