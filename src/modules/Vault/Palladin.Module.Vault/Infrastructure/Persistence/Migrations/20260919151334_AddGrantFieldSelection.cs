using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGrantFieldSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FieldSelectionMode",
                table: "GrantEntryScopes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SelectedFieldIds",
                table: "GrantEntryScopes",
                type: "character varying(32768)",
                maxLength: 32768,
                nullable: false,
                defaultValue: "");
            migrationBuilder.Sql("UPDATE \"GrantEntryScopes\" SET \"SelectedFieldIds\" = \"FieldIds\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FieldSelectionMode",
                table: "GrantEntryScopes");

            migrationBuilder.DropColumn(
                name: "SelectedFieldIds",
                table: "GrantEntryScopes");
        }
    }
}
