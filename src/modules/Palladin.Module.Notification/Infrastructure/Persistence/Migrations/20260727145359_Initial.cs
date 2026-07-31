using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Notification.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailDeliveries",
                columns: table => new
                {
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailDeliveries", x => x.IdempotencyKey);
                });

            migrationBuilder.CreateTable(
                name: "InboxItems",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    TitleKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    ScopeType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ScopeItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Collapsible = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    RequiredPermission = table.Column<int>(type: "integer", nullable: true),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxItems", x => new { x.OrganizationId, x.UserId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    InboxEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SignalREnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PushEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => new { x.OrganizationId, x.UserId, x.Type });
                });

            migrationBuilder.CreateTable(
                name: "PushTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Scopes",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scopes", x => new { x.OrganizationId, x.UserId, x.Type, x.ItemId });
                });

            migrationBuilder.CreateTable(
                name: "SuppressedEmails",
                columns: table => new
                {
                    Address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuppressedEmails", x => x.Address);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_OrganizationId_SubjectId",
                table: "InboxItems",
                columns: new[] { "OrganizationId", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_OrganizationId_Type_SubjectId",
                table: "InboxItems",
                columns: new[] { "OrganizationId", "Type", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_OrganizationId_UserId_OccurredAt_Id",
                table: "InboxItems",
                columns: new[] { "OrganizationId", "UserId", "OccurredAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_OrganizationId_UserId_Permission",
                table: "InboxItems",
                columns: new[] { "OrganizationId", "UserId" },
                filter: "\"RequiredPermission\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_OrganizationId_UserId_ScopeType_ScopeItemId",
                table: "InboxItems",
                columns: new[] { "OrganizationId", "UserId", "ScopeType", "ScopeItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_OrganizationId_UserId_Type_SubjectId",
                table: "InboxItems",
                columns: new[] { "OrganizationId", "UserId", "Type", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_OrganizationId_UserId_Unread",
                table: "InboxItems",
                columns: new[] { "OrganizationId", "UserId" },
                filter: "\"ReadAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PushTokens_OrganizationId",
                table: "PushTokens",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_PushTokens_Token",
                table: "PushTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Scopes_OrganizationId_Type_ItemId",
                table: "Scopes",
                columns: new[] { "OrganizationId", "Type", "ItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailDeliveries");

            migrationBuilder.DropTable(
                name: "InboxItems");

            migrationBuilder.DropTable(
                name: "NotificationPreferences");

            migrationBuilder.DropTable(
                name: "PushTokens");

            migrationBuilder.DropTable(
                name: "Scopes");

            migrationBuilder.DropTable(
                name: "SuppressedEmails");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
