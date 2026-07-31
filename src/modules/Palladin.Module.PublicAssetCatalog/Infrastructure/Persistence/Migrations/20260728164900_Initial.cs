using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentRevision = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UploadSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploaderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceSubject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpectedDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpectedMediaType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpectedByteLength = table.Column<long>(type: "bigint", nullable: false),
                    StagingKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicAssetAlias",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicAssetAlias", x => new { x.AssetId, x.Kind, x.Value });
                    table.ForeignKey(
                        name: "FK_PublicAssetAlias_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicAssetRevision",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MediaType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PublishedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicAssetRevision", x => new { x.AssetId, x.Revision });
                    table.ForeignKey(
                        name: "FK_PublicAssetRevision_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_OrganizationId_Type_OwnerId",
                table: "Assets",
                columns: new[] { "OrganizationId", "Type", "OwnerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Type_Status_Name",
                table: "Assets",
                columns: new[] { "Type", "Status", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicAssetAlias_Kind_Value",
                table: "PublicAssetAlias",
                columns: new[] { "Kind", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicAssetRevision_Digest",
                table: "PublicAssetRevision",
                column: "Digest");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_UploaderId_ExpiresAt",
                table: "UploadSessions",
                columns: new[] { "UploaderId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicAssetAlias");

            migrationBuilder.DropTable(
                name: "PublicAssetRevision");

            migrationBuilder.DropTable(
                name: "UploadSessions");

            migrationBuilder.DropTable(
                name: "Assets");
        }
    }
}
