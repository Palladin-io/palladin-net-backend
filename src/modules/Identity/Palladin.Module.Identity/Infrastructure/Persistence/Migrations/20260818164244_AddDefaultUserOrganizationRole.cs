using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultUserOrganizationRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO "Roles" (
                    "OrganizationId",
                    "Id",
                    "Name",
                    "NormalizedName",
                    "Permissions",
                    "IsSystem",
                    "CreatedAt")
                SELECT
                    organization."Id",
                    md5(organization."Id"::text || ':palladin:system-role:user')::uuid,
                    'User',
                    'USER',
                    12,
                    TRUE,
                    CURRENT_TIMESTAMP
                FROM "Organizations" AS organization;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "Roles" AS role
                USING "Organizations" AS organization
                WHERE role."OrganizationId" = organization."Id"
                  AND role."Id" = md5(organization."Id"::text || ':palladin:system-role:user')::uuid
                  AND role."Name" = 'User'
                  AND role."NormalizedName" = 'USER'
                  AND role."Permissions" = 12
                  AND role."IsSystem" = TRUE;
                """);
        }
    }
}
