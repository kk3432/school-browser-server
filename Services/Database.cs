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
                created_at TEXT NOT NULL,
                expires_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // 旧库迁移：devices 表补列（CREATE TABLE IF NOT EXISTS 不会改已有表）
        EnsureColumn(conn, "devices", "ip_address", "TEXT");
        EnsureColumn(conn, "devices", "last_photo_at", "TEXT");
        EnsureColumn(conn, "devices", "photo_skip_reason", "TEXT");
        EnsureColumn(conn, "devices", "last_wrong_pin_photo_at", "TEXT");
        // v0.7.1：令牌过期时间（Unix 秒）。旧库补列后，存量令牌 expires_at 给 0，
        // 由 InsertToken 之后的逻辑统一处理：迁移时把旧令牌设为“立即过期”，强制重新登录一次。
        EnsureColumn(conn, "auth_tokens", "expires_at", "INTEGER");
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

    /// <summary>签发令牌，expires_at = now + ttlSeconds（v0.7.1，L3）。</summary>
    public void InsertToken(string token, long ttlSeconds)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO auth_tokens(token, created_at, expires_at) VALUES($t, $c, $e);
            """;
        cmd.Parameters.AddWithValue("$t", token);
        cmd.Parameters.AddWithValue("$c", Now());
        cmd.Parameters.AddWithValue("$e", DateTimeOffset.Now.ToUnixTimeSeconds() + ttlSeconds);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 校验令牌：存在且未过期才有效；有效时把 expires_at 滑动续到 now+ttlSeconds（活跃会话不被踢）。
    /// 过期令牌惰性删除。ttlSeconds 与签发时一致。
    /// </summary>
    public bool TokenValid(string? token, long ttlSeconds)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        using var conn = Open();

        // 先惰性清理所有过期令牌，控制表体积
        using (var purge = conn.CreateCommand())
        {
            purge.CommandText = "DELETE FROM auth_tokens WHERE expires_at < $now;";
            purge.Parameters.AddWithValue("$now", now);
            purge.ExecuteNonQuery();
        }

        long expiresAt;
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT expires_at FROM auth_tokens WHERE token=$t;";
            find.Parameters.AddWithValue("$t", token);
            var res = find.ExecuteScalar();
            if (res is null || res == DBNull.Value) return false;
            expiresAt = Convert.ToInt64(res);
        }
        if (expiresAt < now) return false; // 已过期（正常已被上面清理，双保险）

        // 滑动续期
        using var renew = conn.CreateCommand();
        renew.CommandText = "UPDATE auth_tokens SET expires_at=$e WHERE token=$t;";
        renew.Parameters.AddWithValue("$e", now + ttlSeconds);
        renew.Parameters.AddWithValue("$t", token);
        renew.ExecuteNonQuery();
        return true;
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
            SELECT device_id, name, app_version, config_version, ip_address, registered_at, last_seen,
                   last_photo_at, photo_skip_reason, last_wrong_pin_photo_at
            FROM devices ORDER BY last_seen DESC;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadDevice(reader));
        return result;
    }

    public DeviceRow? GetDevice(string deviceId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, name, app_version, config_version, ip_address, registered_at, last_seen,
                   last_photo_at, photo_skip_reason, last_wrong_pin_photo_at
            FROM devices WHERE device_id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", deviceId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDevice(reader) : null;
    }

    private static DeviceRow ReadDevice(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? "" : reader.GetString(1),
        reader.IsDBNull(2) ? "" : reader.GetString(2),
        reader.IsDBNull(3) ? "" : reader.GetString(3),
        reader.IsDBNull(4) ? "" : reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9));

    /// <summary>照片接收成功后记录时间并清除跳过原因（限频与后台展示依据）。</summary>
    public void SetPhotoTaken(string deviceId, string at)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE devices SET last_photo_at=$at, photo_skip_reason=NULL WHERE device_id=$id;";
        cmd.Parameters.AddWithValue("$at", at);
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>设备上报跳过拍照的原因（如无前置摄像头），不更新 last_photo_at。</summary>
    public void SetPhotoSkip(string deviceId, string reason)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE devices SET photo_skip_reason=$r WHERE device_id=$id;";
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>输错密码拍照接收成功后记录时间（与启动拍照独立，用于后台告警）。</summary>
    public void SetWrongPinPhotoTaken(string deviceId, string at)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE devices SET last_wrong_pin_photo_at=$at WHERE device_id=$id;";
        cmd.Parameters.AddWithValue("$at", at);
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery();
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
}
