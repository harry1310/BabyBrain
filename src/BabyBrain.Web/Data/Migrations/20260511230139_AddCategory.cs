using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabyBrain.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default existing rows to "community" — the predominant kind so far.
            // Then promote the Islington findyour rows to "library" since that
            // scraper now stamps everything it emits with Categories.Library.
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "EventOccurrences",
                type: "TEXT",
                nullable: false,
                defaultValue: "community");

            migrationBuilder.Sql(
                "UPDATE \"EventOccurrences\" SET \"Category\" = 'library' WHERE \"Source\" = 'islington_findyour';");

            migrationBuilder.CreateIndex(
                name: "IX_EventOccurrences_Category",
                table: "EventOccurrences",
                column: "Category");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventOccurrences_Category",
                table: "EventOccurrences");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "EventOccurrences");
        }
    }
}
