using CampusBrowser.Server.Models;
using Microsoft.Data.Sqlite;

namespace CampusBrowser.Server.Services;

/// <summary>
/// SQLite 数据访问：设置、配置版本、设备、登录令牌。
/// 数据库文件放在程序目录下的 data/，Windows/Linux 行为一致，备份即拷贝文件。
/// </summary>
public class Database
{
    private readonly string _connectionString;

    public Database(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "campus.db");
        _connectionString = $"Data Source={dbPath}";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    public void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS settings(
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS config_versions(
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                version       TEXT NOT NULL UNIQUE,
                config_json   TEXT NOT NULL,
                signature     TEXT NOT NULL,
                published_at  TEXT NOT NULL,
                is_current    INTEGER NOT NULL DEFAULT 0,
                note          TEXT
            );
            CREATE TABLE IF NOT EXISTS devices(
                device_id      TEXT PRIMARY KEY,
                name           TEXT,
                app_version    TEXT,
                config_version TEXT,
                ip_address     TEXT,
                registered_at  TEXT NOT NULL,
                last_seen      TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS auth_tokens(
                token      TEXT PRIMARY KEY,
                created_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // 旧库迁移：devices 表补 ip_address 列（CREATE TABLE IF NOT EXISTS 不会改已有表）
        EnsureColumn(conn, "devices", "ip_address", "TEXT");
    }

    /// <summary>列不存在时 ALTER TABLE 补列，保护已部署的旧数据库。</summary>
    private static void EnsureColumn(SqliteConnection conn, string table, string column, string type)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == column) return; // 已存在
        }
        reader.Close();
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();
    }

    // ---------- 设置 ----------

    public string? GetSetting(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value) VALUES($k, $v)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public bool AdminExists() => GetSetting("admin_user") is not null;

    // ---------- 登录令牌 ----------

    public void InsertToken(string token)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO auth_tokens(token, created_at) VALUES($t, $c);";
        cmd.Parameters.AddWithValue("$t", token);
        cmd.Parameters.AddWithValue("$c", Now());
        cmd.ExecuteNonQuery();
    }

    public bool TokenValid(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM auth_tokens WHERE token=$t;";
        cmd.Parameters.AddWithValue("$t", token);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public void DeleteToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM auth_tokens WHERE token=$t;";
        cmd.Parameters.AddWithValue("$t", token);
        cmd.ExecuteNonQuery();
    }

    // ---------- 配置版本 ----------

    public long InsertVersion(string version, string configJson, string signature, string? note)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO config_versions(version, config_json, signature, published_at, is_current, note)
            VALUES($v, $j, $s, $t, 0, $n);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$j", configJson);
        cmd.Parameters.AddWithValue("$s", signature);
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.Parameters.AddWithValue("$n", note ?? "");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void SetCurrent(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE config_versions SET is_current = CASE WHEN id=$id THEN 1 ELSE 0 END;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public VersionRow? GetCurrentVersion()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, config_json, signature, published_at, note FROM config_versions WHERE is_current=1 LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadVersion(reader) : null;
    }

    public VersionRow? GetVersion(string version)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, config_json, signature, published_at, note FROM config_versions WHERE version=$v;";
        cmd.Parameters.AddWithValue("$v", version);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadVersion(reader) : null;
    }

    public List<VersionRow> ListVersions()
    {
        var result = new List<VersionRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, config_json, signature, published_at, note FROM config_versions ORDER BY id DESC LIMIT 50;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadVersion(reader));
        return result;
    }

    private static VersionRow ReadVersion(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4));

    // ---------- 设备 ----------

    public void UpsertDevice(string deviceId, string name, string appVersion, string ipAddress)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices(device_id, name, app_version, config_version, ip_address, registered_at, last_seen)
            VALUES($id, $n, $v, '', $ip, $now, $now)
            ON CONFLICT(device_id) DO UPDATE SET
                name=COALESCE(NULLIF(excluded.name,''), devices.name),
                app_version=excluded.app_version,
                ip_address=CASE WHEN excluded.ip_address='' THEN devices.ip_address ELSE excluded.ip_address END;
            """;
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.Parameters.AddWithValue("$n", name ?? "");
        cmd.Parameters.AddWithValue("$v", appVersion ?? "");
        cmd.Parameters.AddWithValue("$ip", ipAddress ?? "");
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.ExecuteNonQuery();
    }

    public void TouchDeviceSeen(string deviceId, string configVersion)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE devices SET last_seen=$now,
                config_version=CASE WHEN $cv='' THEN config_version ELSE $cv END
            WHERE device_id=$id;
            """;
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$cv", configVersion ?? "");
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery();
    }

    public List<DeviceRow> ListDevices()
    {
        var result = new List<DeviceRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, name, app_version, config_version, ip_address, registered_at, last_seen
            FROM devices ORDER BY last_seen DESC;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DeviceRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }
        return result;
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
}
