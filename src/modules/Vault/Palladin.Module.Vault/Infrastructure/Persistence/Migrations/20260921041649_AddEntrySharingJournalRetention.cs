using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntrySharingJournalRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_EntryShareActivities_PublishedAt_ShareId_Sequence",
                table: "EntryShareActivities",
                columns: new[] { "PublishedAt", "ShareId", "Sequence" },
                filter: "\"PublishedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EntryShareActivities_PublishedAt_ShareId_Sequence",
                table: "EntryShareActivities");
        }
    }
}
