using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameSkuToProductCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "sku",
                schema: "orders",
                table: "orders",
                newName: "product_code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "product_code",
                schema: "orders",
                table: "orders",
                newName: "sku");
        }
    }
}
