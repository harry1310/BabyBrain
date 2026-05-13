using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabyBrain.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Cost",
                table: "EventOccurrences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFree",
                table: "EventOccurrences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cost",
                table: "EventOccurrences");

            migrationBuilder.DropColumn(
                name: "IsFree",
                table: "EventOccurrences");
        }
    }
}
