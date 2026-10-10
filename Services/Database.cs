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
            CREATE TABLE IF NOT EXISTS logs(
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                ts         TEXT NOT NULL,
                event      TEXT NOT NULL,
                ip         TEXT,
                actor      TEXT,
                device_id  TEXT,
                method     TEXT,
                path       TEXT,
                status     INTEGER,
                detail     TEXT,
                user_agent TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_logs_ts    ON logs(ts);
            CREATE INDEX IF NOT EXISTS idx_logs_event ON logs(event);
            CREATE INDEX IF NOT EXISTS idx_logs_ip    ON logs(ip);
            CREATE TABLE IF NOT EXISTS html_pages(
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                slug       TEXT NOT NULL UNIQUE,
                title      TEXT NOT NULL DEFAULT '',
                html       TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS online_samples(
                ts     INTEGER PRIMARY KEY,
                online INTEGER NOT NULL,
                total  INTEGER NOT NULL
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

    /// <summary>设备总数（首页概览指标）。</summary>
    public long CountDevices()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM devices;";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
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

    // ---------- 操作日志（v0.8.0） ----------

    /// <summary>写入一条操作日志；失败不抛（日志不能影响主流程）。</summary>
    public void InsertLog(LogRow row)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO logs(ts, event, ip, actor, device_id, method, path, status, detail, user_agent)
                VALUES($ts, $ev, $ip, $ac, $dev, $m, $p, $st, $dt, $ua);
                """;
            cmd.Parameters.AddWithValue("$ts", row.Ts);
            cmd.Parameters.AddWithValue("$ev", row.Event);
            cmd.Parameters.AddWithValue("$ip", row.Ip ?? "");
            cmd.Parameters.AddWithValue("$ac", row.Actor ?? "");
            cmd.Parameters.AddWithValue("$dev", row.DeviceId ?? "");
            cmd.Parameters.AddWithValue("$m", row.Method ?? "");
            cmd.Parameters.AddWithValue("$p", row.Path ?? "");
            cmd.Parameters.AddWithValue("$st", row.Status);
            cmd.Parameters.AddWithValue("$dt", row.Detail ?? "");
            cmd.Parameters.AddWithValue("$ua", row.UserAgent ?? "");
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 忽略：日志写入失败不得影响业务响应
        }
    }

    /// <summary>
    /// 查询操作日志（倒序，分页）。
    /// from/to 为 UTC 时刻的 Unix 秒（按 ts 文本时间过滤，含边界）。
    /// </summary>
    public List<LogRow> QueryLogs(DateTimeOffset? from, DateTimeOffset? to, string? evt, string? keyword, int limit, int offset)
    {
        var result = new List<LogRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (from is not null)
        {
            where.Add("ts >= $from");
            cmd.Parameters.AddWithValue("$from", from.Value.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        }
        if (to is not null)
        {
            where.Add("ts <= $to");
            cmd.Parameters.AddWithValue("$to", to.Value.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        }
        if (!string.IsNullOrWhiteSpace(evt) && evt != "all")
        {
            where.Add("event = $ev");
            cmd.Parameters.AddWithValue("$ev", evt);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Add("(ip LIKE $kw OR actor LIKE $kw OR device_id LIKE $kw OR detail LIKE $kw OR path LIKE $kw)");
            cmd.Parameters.AddWithValue("$kw", "%" + keyword.Trim() + "%");
        }
        var sql = "SELECT id, ts, event, ip, actor, device_id, method, path, status, detail, user_agent FROM logs";
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        sql += " ORDER BY id DESC LIMIT $lim OFFSET $off;";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$lim", Math.Clamp(limit, 1, 2000));
        cmd.Parameters.AddWithValue("$off", Math.Max(offset, 0));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadLog(reader));
        return result;
    }

    /// <summary>符合条件的日志总条数（后台分页用）。</summary>
    public long CountLogs(DateTimeOffset? from, DateTimeOffset? to, string? evt, string? keyword)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (from is not null)
        {
            where.Add("ts >= $from");
            cmd.Parameters.AddWithValue("$from", from.Value.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        }
        if (to is not null)
        {
            where.Add("ts <= $to");
            cmd.Parameters.AddWithValue("$to", to.Value.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        }
        if (!string.IsNullOrWhiteSpace(evt) && evt != "all")
        {
            where.Add("event = $ev");
            cmd.Parameters.AddWithValue("$ev", evt);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Add("(ip LIKE $kw OR actor LIKE $kw OR device_id LIKE $kw OR detail LIKE $kw OR path LIKE $kw)");
            cmd.Parameters.AddWithValue("$kw", "%" + keyword.Trim() + "%");
        }
        var sql = "SELECT COUNT(*) FROM logs";
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>导出用：按条件取全部匹配日志（上限 5 万条防内存爆）。</summary>
    public List<LogRow> QueryLogsForExport(DateTimeOffset? from, DateTimeOffset? to, string? evt, string? keyword) =>
        QueryLogs(from, to, evt, keyword, 50000, 0);

    /// <summary>删除早于 keepDays 天的日志，返回删除条数。调用方按天惰性触发。</summary>
    public int PurgeOldLogs(int keepDays)
    {
        if (keepDays <= 0) return 0;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM logs WHERE ts < $before;";
        cmd.Parameters.AddWithValue("$before", DateTimeOffset.Now.AddDays(-keepDays).ToString("yyyy-MM-dd HH:mm:ss zzz"));
        return cmd.ExecuteNonQuery();
    }

    private static LogRow ReadLog(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? "" : r.GetString(3),
        r.IsDBNull(4) ? "" : r.GetString(4),
        r.IsDBNull(5) ? "" : r.GetString(5),
        r.IsDBNull(6) ? "" : r.GetString(6),
        r.IsDBNull(7) ? "" : r.GetString(7),
        r.IsDBNull(8) ? 0 : r.GetInt32(8),
        r.IsDBNull(9) ? "" : r.GetString(9),
        r.IsDBNull(10) ? "" : r.GetString(10));

    // ---------- HTML 页（v0.8.0） ----------

    public List<HtmlPageRow> ListHtmlPages()
    {
        var result = new List<HtmlPageRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, slug, title, html, created_at, updated_at FROM html_pages ORDER BY id DESC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadHtmlPage(reader));
        return result;
    }

    public HtmlPageRow? GetHtmlPage(string slug)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, slug, title, html, created_at, updated_at FROM html_pages WHERE slug=$s;";
        cmd.Parameters.AddWithValue("$s", slug);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadHtmlPage(reader) : null;
    }

    public HtmlPageRow? GetHtmlPageById(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, slug, title, html, created_at, updated_at FROM html_pages WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadHtmlPage(reader) : null;
    }

    /// <summary>保存 HTML 页（id=0 为新建，slug 冲突返回 false）。</summary>
    public bool SaveHtmlPage(long id, string slug, string title, string html)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (id > 0)
        {
            cmd.CommandText = """
                UPDATE html_pages SET slug=$s, title=$t, html=$h, updated_at=$u WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO html_pages(slug, title, html, created_at, updated_at) VALUES($s, $t, $h, $u, $u);
                """;
        }
        cmd.Parameters.AddWithValue("$s", slug);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$h", html);
        cmd.Parameters.AddWithValue("$u", Now());
        try
        {
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (SqliteException)
        {
            return false; // slug 唯一约束冲突
        }
    }

    public bool DeleteHtmlPage(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM html_pages WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static HtmlPageRow ReadHtmlPage(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5));

    // ---------- 在线采样（v0.8.0，首页全天曲线） ----------

    /// <summary>写入一个采样点；同一秒重复采样用 REPLACE 覆盖。</summary>
    public void InsertOnlineSample(long ts, int online, int total)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO online_samples(ts, online, total) VALUES($ts, $o, $t)
            ON CONFLICT(ts) DO UPDATE SET online=excluded.online, total=excluded.total;
            """;
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$o", online);
        cmd.Parameters.AddWithValue("$t", total);
        cmd.ExecuteNonQuery();
    }

    /// <summary>取 [fromTs, 现在] 的采样点（升序）。</summary>
    public List<(long Ts, int Online, int Total)> ListOnlineSamples(long fromTs)
    {
        var result = new List<(long, int, int)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ts, online, total FROM online_samples WHERE ts >= $from ORDER BY ts ASC;";
        cmd.Parameters.AddWithValue("$from", fromTs);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2)));
        return result;
    }

    /// <summary>删除早于 keepDays 天的采样点。</summary>
    public int PurgeOldOnlineSamples(int keepDays)
    {
        if (keepDays <= 0) return 0;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM online_samples WHERE ts < $before;";
        cmd.Parameters.AddWithValue("$before", DateTimeOffset.Now.AddDays(-keepDays).ToUnixTimeSeconds());
        return cmd.ExecuteNonQuery();
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
}
