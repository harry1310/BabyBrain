using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabyBrain.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReportedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReportedAt",
                table: "EventOccurrences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventOccurrences_ReportedAt",
                table: "EventOccurrences",
                column: "ReportedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventOccurrences_ReportedAt",
                table: "EventOccurrences");

            migrationBuilder.DropColumn(
                name: "ReportedAt",
                table: "EventOccurrences");
        }
    }
}
