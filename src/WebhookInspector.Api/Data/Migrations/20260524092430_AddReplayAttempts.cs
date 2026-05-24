using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebhookInspector.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReplayAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReplayAttempt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CapturedRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TargetUrl = table.Column<string>(type: "text", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    ResponseHeaders = table.Column<string>(type: "jsonb", nullable: false),
                    ResponseBody = table.Column<byte[]>(type: "bytea", nullable: false),
                    ResponseTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayAttempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReplayAttempt_CapturedRequest_CapturedRequestId",
                        column: x => x.CapturedRequestId,
                        principalTable: "CapturedRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReplayAttempt_CapturedRequestId_StartedAt_Id",
                table: "ReplayAttempt",
                columns: new[] { "CapturedRequestId", "StartedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReplayAttempt");
        }
    }
}
