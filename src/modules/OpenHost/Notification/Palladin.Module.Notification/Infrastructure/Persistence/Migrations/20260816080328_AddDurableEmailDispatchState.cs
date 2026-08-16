using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Notification.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableEmailDispatchState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Instant>(
                name: "SentAt",
                table: "EmailDeliveries",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(Instant),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<Instant>(
                name: "DispatchLeaseExpiresAt",
                table: "EmailDeliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DispatchToken",
                table: "EmailDeliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "Status",
                table: "EmailDeliveries",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<Instant>(
                name: "UpdatedAt",
                table: "EmailDeliveries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: NodaTime.Instant.FromUnixTimeTicks(0L));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DispatchLeaseExpiresAt",
                table: "EmailDeliveries");

            migrationBuilder.DropColumn(
                name: "DispatchToken",
                table: "EmailDeliveries");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "EmailDeliveries");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "EmailDeliveries");

            migrationBuilder.AlterColumn<Instant>(
                name: "SentAt",
                table: "EmailDeliveries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: NodaTime.Instant.FromUnixTimeTicks(0L),
                oldClrType: typeof(Instant),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
