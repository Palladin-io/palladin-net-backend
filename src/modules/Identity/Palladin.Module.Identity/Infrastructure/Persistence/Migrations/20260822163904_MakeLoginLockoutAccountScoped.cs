using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palladin.Module.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeLoginLockoutAccountScoped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "SourceIpAddresses",
                table: "LoginLockouts",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            // The previous model kept one transient counter per (email, IP) pair. Collapse those
            // rows before introducing the account-wide unique index. Active locks are preserved,
            // while in-flight pre-deployment counters restart because the old one-minute windows
            // cannot be reconstructed faithfully as the new account-wide five-minute window.
            migrationBuilder.Sql(
                """
                WITH account_state AS (
                    SELECT
                        "Email",
                        (array_agg("Id" ORDER BY "LockedUntil" DESC NULLS LAST, "UpdatedAt" DESC, "Id"))[1] AS survivor_id,
                        MAX("LockedUntil") AS locked_until,
                        MIN("CreatedAt") AS created_at,
                        MAX("UpdatedAt") AS updated_at,
                        MAX("Version") AS version
                    FROM "LoginLockouts"
                    GROUP BY "Email"
                ),
                updated AS (
                    UPDATE "LoginLockouts" AS lockout
                    SET
                        "FailedCount" = 0,
                        "SourceIpAddresses" = ARRAY[]::text[],
                        "LockedUntil" = account_state.locked_until,
                        "WindowStartedAt" = account_state.updated_at,
                        "CreatedAt" = account_state.created_at,
                        "UpdatedAt" = account_state.updated_at,
                        "Version" = account_state.version
                    FROM account_state
                    WHERE lockout."Id" = account_state.survivor_id
                    RETURNING lockout."Id"
                )
                DELETE FROM "LoginLockouts" AS lockout
                USING account_state
                WHERE lockout."Email" = account_state."Email"
                  AND lockout."Id" <> account_state.survivor_id
                  AND EXISTS (
                      SELECT 1
                      FROM updated
                      WHERE updated."Id" = account_state.survivor_id);
                """);

            migrationBuilder.DropIndex(
                name: "IX_LoginLockouts_Email_IpAddress",
                table: "LoginLockouts");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "LoginLockouts");

            migrationBuilder.CreateIndex(
                name: "IX_LoginLockouts_Email",
                table: "LoginLockouts",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LoginLockouts_Email",
                table: "LoginLockouts");

            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                table: "LoginLockouts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "migration");

            migrationBuilder.Sql(
                """
                UPDATE "LoginLockouts"
                SET "IpAddress" = COALESCE("SourceIpAddresses"[1], 'migration');
                """);

            migrationBuilder.DropColumn(
                name: "SourceIpAddresses",
                table: "LoginLockouts");

            migrationBuilder.CreateIndex(
                name: "IX_LoginLockouts_Email_IpAddress",
                table: "LoginLockouts",
                columns: new[] { "Email", "IpAddress" },
                unique: true);
        }
    }
}
