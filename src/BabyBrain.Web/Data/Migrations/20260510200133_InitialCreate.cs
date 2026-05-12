using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabyBrain.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventOccurrences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalKey = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Date = table.Column<string>(type: "TEXT", nullable: false),
                    StartTime = table.Column<string>(type: "TEXT", nullable: false),
                    EndTime = table.Column<string>(type: "TEXT", nullable: true),
                    SessionName = table.Column<string>(type: "TEXT", nullable: false),
                    SessionNotes = table.Column<string>(type: "TEXT", nullable: true),
                    VenueName = table.Column<string>(type: "TEXT", nullable: false),
                    VenueAddress = table.Column<string>(type: "TEXT", nullable: true),
                    Postcode = table.Column<string>(type: "TEXT", nullable: true),
                    MinAgeMonths = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxAgeMonths = table.Column<int>(type: "INTEGER", nullable: true),
                    TermTimeOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventOccurrences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventOccurrences_Date_StartTime",
                table: "EventOccurrences",
                columns: new[] { "Date", "StartTime" });

            migrationBuilder.CreateIndex(
                name: "IX_EventOccurrences_ExternalKey",
                table: "EventOccurrences",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventOccurrences_Source",
                table: "EventOccurrences",
                column: "Source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventOccurrences");
        }
    }
}
