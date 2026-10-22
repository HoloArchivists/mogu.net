using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Mogu.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MainCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MainCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RangeBans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Cidr = table.Column<string>(type: "TEXT", maxLength: 18, nullable: false),
                    MaskedAddress = table.Column<long>(type: "INTEGER", nullable: false),
                    Mask = table.Column<long>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TemporarySince = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RangeBans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Trackers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Uri = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false, collation: "NOCASE"),
                    IsWebSeed = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trackers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackerWhitelistEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InfoHash = table.Column<byte[]>(type: "BLOB", maxLength: 20, nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackerWhitelistEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DiscordId = table.Column<long>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, collation: "NOCASE"),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastLoginIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    RegistrationIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    HideComments = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiTokenHash = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubCategories",
                columns: table => new
                {
                    MainCategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubCategories", x => new { x.MainCategoryId, x.Id });
                    table.ForeignKey(
                        name: "FK_SubCategories_MainCategories_MainCategoryId",
                        column: x => x.MainCategoryId,
                        principalTable: "MainCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdminLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AdminId = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminLog_Users_AdminId",
                        column: x => x.AdminId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Bans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AdminId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bans_Users_AdminId",
                        column: x => x.AdminId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Bans_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Torrents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InfoHash = table.Column<byte[]>(type: "BLOB", maxLength: 20, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false, collation: "NOCASE"),
                    TorrentName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Information = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Encoding = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsAnonymous = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsTrusted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRemake = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsComplete = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBanned = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCommentLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasTorrent = table.Column<bool>(type: "INTEGER", nullable: false),
                    UploaderId = table.Column<int>(type: "INTEGER", nullable: true),
                    UploaderIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    MainCategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubCategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    CommentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SeedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LeechCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StatsUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Torrents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Torrents_MainCategories_MainCategoryId",
                        column: x => x.MainCategoryId,
                        principalTable: "MainCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Torrents_SubCategories_MainCategoryId_SubCategoryId",
                        columns: x => new { x.MainCategoryId, x.SubCategoryId },
                        principalTable: "SubCategories",
                        principalColumns: new[] { "MainCategoryId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Torrents_Users_UploaderId",
                        column: x => x.UploaderId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TorrentId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Comments_Torrents_TorrentId",
                        column: x => x.TorrentId,
                        principalTable: "Torrents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Comments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TorrentId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reports_Torrents_TorrentId",
                        column: x => x.TorrentId,
                        principalTable: "Torrents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Reports_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TorrentFileLists",
                columns: table => new
                {
                    TorrentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentFileLists", x => x.TorrentId);
                    table.ForeignKey(
                        name: "FK_TorrentFileLists_Torrents_TorrentId",
                        column: x => x.TorrentId,
                        principalTable: "Torrents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TorrentTrackers",
                columns: table => new
                {
                    TorrentId = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentTrackers", x => new { x.TorrentId, x.TrackerId });
                    table.ForeignKey(
                        name: "FK_TorrentTrackers_Torrents_TorrentId",
                        column: x => x.TorrentId,
                        principalTable: "Torrents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TorrentTrackers_Trackers_TrackerId",
                        column: x => x.TrackerId,
                        principalTable: "Trackers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "MainCategories",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { 1, "Unarchived" },
                    { 2, "Paid" }
                });

            migrationBuilder.InsertData(
                table: "SubCategories",
                columns: new[] { "Id", "MainCategoryId", "Name" },
                values: new object[,]
                {
                    { 1, 1, "Karaoke" },
                    { 2, 1, "Copyright" },
                    { 3, 1, "Yab" },
                    { 1, 2, "Membership" },
                    { 2, 2, "Voice pack" },
                    { 3, 2, "Concert" },
                    { 4, 2, "Songs" },
                    { 5, 2, "Others" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminLog_AdminId",
                table: "AdminLog",
                column: "AdminId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminLog_CreatedAt",
                table: "AdminLog",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Bans_AdminId",
                table: "Bans",
                column: "AdminId");

            migrationBuilder.CreateIndex(
                name: "IX_Bans_UserId",
                table: "Bans",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Bans_UserIp",
                table: "Bans",
                column: "UserIp",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Comments_TorrentId",
                table: "Comments",
                column: "TorrentId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_UserId_CreatedAt",
                table: "Comments",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RangeBans_Mask",
                table: "RangeBans",
                column: "Mask");

            migrationBuilder.CreateIndex(
                name: "IX_RangeBans_MaskedAddress",
                table: "RangeBans",
                column: "MaskedAddress");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Status",
                table: "Reports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_TorrentId",
                table: "Reports",
                column: "TorrentId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_UserId",
                table: "Reports",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Torrents_CommentCount",
                table: "Torrents",
                column: "CommentCount");

            migrationBuilder.CreateIndex(
                name: "IX_Torrents_DisplayName",
                table: "Torrents",
                column: "DisplayName");

            migrationBuilder.CreateIndex(
                name: "IX_Torrents_DownloadCount",
                table: "Torrents",
                column: "DownloadCount");

            migrationBuilder.CreateIndex(
                name: "IX_Torrents_FileSize",
                table: "Torrents",
                column: "FileSize");

            migrationBuilder.CreateIndex(
                name: "IX_Torrents_InfoHash",
                table: "Torrents",
                column: "InfoHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Torrents_LeechCount",
                table: "Torrents",
                column: "LeechCount");

            migrationBuilder.CreateIndex(
                name: "IX_Torrents_MainCategoryId_SubCategoryId",
                table: "Torrents",
                columns: new[] { "MainCategoryId", "SubCategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Torrents_SeedCount",
                table: "Torrents",
                column: "SeedCount");

            migrationBuilder.CreateIndex(
                name: "IX_Torrents_UploaderId_IsDeleted",
                table: "Torrents",
                columns: new[] { "UploaderId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_TorrentTrackers_TrackerId",
                table: "TorrentTrackers",
                column: "TrackerId");

            migrationBuilder.CreateIndex(
                name: "IX_Trackers_Uri",
                table: "Trackers",
                column: "Uri",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ApiTokenHash",
                table: "Users",
                column: "ApiTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_DiscordId",
                table: "Users",
                column: "DiscordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminLog");

            migrationBuilder.DropTable(
                name: "Bans");

            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "RangeBans");

            migrationBuilder.DropTable(
                name: "Reports");

            migrationBuilder.DropTable(
                name: "TorrentFileLists");

            migrationBuilder.DropTable(
                name: "TorrentTrackers");

            migrationBuilder.DropTable(
                name: "TrackerWhitelistEvents");

            migrationBuilder.DropTable(
                name: "Torrents");

            migrationBuilder.DropTable(
                name: "Trackers");

            migrationBuilder.DropTable(
                name: "SubCategories");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "MainCategories");
        }
    }
}
