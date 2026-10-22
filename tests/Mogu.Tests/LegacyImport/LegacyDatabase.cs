using Microsoft.Data.Sqlite;

namespace Mogu.Tests.LegacyImport;

/// <summary>Builds a small synthetic legacy mogu database with the production DDL (nyaa_* site only).</summary>
internal sealed class LegacyDatabase : IDisposable
{
    public const string Schema = """
        CREATE TABLE trackers (
        	id INTEGER NOT NULL,
        	uri VARCHAR(255) COLLATE "NOCASE" NOT NULL,
        	is_webseed BOOLEAN NOT NULL,
        	disabled BOOLEAN NOT NULL,
        	PRIMARY KEY (id),
        	UNIQUE (uri),
        	CHECK (is_webseed IN (0, 1)),
        	CHECK (disabled IN (0, 1))
        );
        CREATE TABLE users (
        	id INTEGER NOT NULL,
        	username VARCHAR(32) COLLATE "NOCASE" NOT NULL,
        	email VARCHAR(255) COLLATE "NOCASE",
        	password_hash BLOB NOT NULL,
        	status INTEGER NOT NULL,
        	level INTEGER NOT NULL,
        	created_time DATETIME,
        	last_login_date DATETIME,
        	last_login_ip BLOB,
        	registration_ip BLOB,
        	PRIMARY KEY (id),
        	UNIQUE (username),
        	UNIQUE (email)
        );
        CREATE TABLE rangebans (
        	id INTEGER NOT NULL,
        	cidr_string VARCHAR(18) NOT NULL,
        	masked_cidr BIGINT NOT NULL,
        	mask BIGINT NOT NULL,
        	enabled BOOLEAN NOT NULL,
        	"temp" DATETIME,
        	PRIMARY KEY (id),
        	CHECK (enabled IN (0, 1))
        );
        CREATE TABLE nyaa_main_categories (
        	id INTEGER NOT NULL,
        	name VARCHAR(64) NOT NULL,
        	PRIMARY KEY (id)
        );
        CREATE TABLE nyaa_trackerapi (
        	id INTEGER NOT NULL,
        	info_hash BLOB NOT NULL,
        	method VARCHAR(255) NOT NULL,
        	PRIMARY KEY (id)
        );
        CREATE TABLE user_preferences (
        	user_id INTEGER NOT NULL,
        	hide_comments BOOLEAN NOT NULL,
        	PRIMARY KEY (user_id),
        	FOREIGN KEY(user_id) REFERENCES users (id) ON DELETE CASCADE,
        	CHECK (hide_comments IN (0, 1))
        );
        CREATE TABLE bans (
        	id INTEGER NOT NULL,
        	created_time DATETIME,
        	admin_id INTEGER NOT NULL,
        	user_id INTEGER,
        	user_ip BLOB,
        	reason VARCHAR(2048) NOT NULL,
        	PRIMARY KEY (id),
        	FOREIGN KEY(admin_id) REFERENCES users (id),
        	FOREIGN KEY(user_id) REFERENCES users (id)
        );
        CREATE TABLE nyaa_sub_categories (
        	id INTEGER NOT NULL,
        	name VARCHAR(64) NOT NULL,
        	main_category_id INTEGER NOT NULL,
        	PRIMARY KEY (id, main_category_id),
        	FOREIGN KEY(main_category_id) REFERENCES nyaa_main_categories (id)
        );
        CREATE TABLE nyaa_adminlog (
        	id INTEGER NOT NULL,
        	created_time DATETIME,
        	log VARCHAR(1024) NOT NULL,
        	admin_id INTEGER NOT NULL,
        	PRIMARY KEY (id),
        	FOREIGN KEY(admin_id) REFERENCES users (id)
        );
        CREATE TABLE nyaa_torrents (
        	id INTEGER NOT NULL,
        	info_hash BLOB NOT NULL,
        	display_name VARCHAR(255) COLLATE "NOCASE" NOT NULL,
        	torrent_name VARCHAR(255) NOT NULL,
        	information VARCHAR(255) NOT NULL,
        	description VARCHAR NOT NULL,
        	filesize BIGINT NOT NULL,
        	encoding VARCHAR(32) NOT NULL,
        	flags INTEGER NOT NULL,
        	uploader_ip BLOB,
        	has_torrent BOOLEAN NOT NULL,
        	comment_count INTEGER NOT NULL,
        	created_time DATETIME NOT NULL,
        	updated_time DATETIME NOT NULL,
        	sub_category_id INTEGER NOT NULL,
        	uploader_id INTEGER,
        	main_category_id INTEGER NOT NULL,
        	redirect INTEGER,
        	PRIMARY KEY (id),
        	FOREIGN KEY(main_category_id, sub_category_id) REFERENCES nyaa_sub_categories (main_category_id, id),
        	CHECK (has_torrent IN (0, 1)),
        	FOREIGN KEY(uploader_id) REFERENCES users (id),
        	FOREIGN KEY(main_category_id) REFERENCES nyaa_main_categories (id),
        	FOREIGN KEY(redirect) REFERENCES nyaa_torrents (id)
        );
        CREATE TABLE nyaa_torrents_filelist (
        	filelist_blob BLOB,
        	torrent_id INTEGER NOT NULL,
        	PRIMARY KEY (torrent_id),
        	FOREIGN KEY(torrent_id) REFERENCES nyaa_torrents (id) ON DELETE CASCADE
        );
        CREATE TABLE nyaa_statistics (
        	seed_count INTEGER NOT NULL,
        	leech_count INTEGER NOT NULL,
        	download_count INTEGER NOT NULL,
        	last_updated DATETIME,
        	torrent_id INTEGER NOT NULL,
        	PRIMARY KEY (torrent_id),
        	FOREIGN KEY(torrent_id) REFERENCES nyaa_torrents (id) ON DELETE CASCADE
        );
        CREATE TABLE nyaa_torrent_trackers (
        	"order" INTEGER NOT NULL,
        	torrent_id INTEGER NOT NULL,
        	tracker_id INTEGER NOT NULL,
        	PRIMARY KEY (torrent_id, tracker_id),
        	FOREIGN KEY(torrent_id) REFERENCES nyaa_torrents (id) ON DELETE CASCADE,
        	FOREIGN KEY(tracker_id) REFERENCES trackers (id) ON DELETE CASCADE
        );
        CREATE TABLE nyaa_comments (
        	id INTEGER NOT NULL,
        	created_time DATETIME,
        	edited_time DATETIME,
        	text VARCHAR NOT NULL,
        	torrent_id INTEGER NOT NULL,
        	user_id INTEGER,
        	PRIMARY KEY (id),
        	FOREIGN KEY(torrent_id) REFERENCES nyaa_torrents (id) ON DELETE CASCADE,
        	FOREIGN KEY(user_id) REFERENCES users (id) ON DELETE CASCADE
        );
        CREATE TABLE nyaa_reports (
        	id INTEGER NOT NULL,
        	created_time DATETIME,
        	reason VARCHAR(255) NOT NULL,
        	status INTEGER NOT NULL,
        	torrent_id INTEGER NOT NULL,
        	user_id INTEGER,
        	PRIMARY KEY (id),
        	FOREIGN KEY(torrent_id) REFERENCES nyaa_torrents (id) ON DELETE CASCADE,
        	FOREIGN KEY(user_id) REFERENCES users (id)
        );
        CREATE TABLE sukebei_main_categories (
        	id INTEGER NOT NULL,
        	name VARCHAR(64) NOT NULL,
        	PRIMARY KEY (id)
        );
        CREATE TABLE trusted_applications (
        	id INTEGER NOT NULL,
        	submitter_id INTEGER NOT NULL,
        	created_time DATETIME,
        	closed_time DATETIME,
        	why_want VARCHAR(4000) NOT NULL,
        	why_give VARCHAR(4000) NOT NULL,
        	status INTEGER NOT NULL,
        	PRIMARY KEY (id),
        	FOREIGN KEY(submitter_id) REFERENCES users (id)
        );
        CREATE TABLE alembic_version (
        	version_num VARCHAR(32) NOT NULL,
        	CONSTRAINT alembic_version_pkc PRIMARY KEY (version_num)
        );
        INSERT INTO alembic_version VALUES ('5cbcee17bece');
        INSERT INTO nyaa_main_categories VALUES (1, 'Unarchived'), (2, 'Paid');
        INSERT INTO nyaa_sub_categories (main_category_id, id, name) VALUES
            (1, 1, 'Karaoke'), (1, 2, 'Copyright'), (1, 3, 'Yab'),
            (2, 1, 'Membership'), (2, 2, 'Voice pack'), (2, 3, 'Concert'), (2, 4, 'Songs'), (2, 5, 'Others');
        INSERT INTO sukebei_main_categories VALUES (1, 'Art'), (2, 'Real Life');
        """;

    public LegacyDatabase(string? schemaOverride = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "mogu-legacy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        SourcePath = Path.Combine(Root, "legacy.db");
        Execute(schemaOverride ?? Schema);
    }

    public string Root { get; }

    public string SourcePath { get; }

    public string TargetPath => Path.Combine(Root, "mogu.db");

    public string Sub(string name) => Path.Combine(Root, name);

    public void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SourcePath, Pooling = false, ForeignKeys = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
