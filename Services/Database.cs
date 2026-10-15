using System.Collections.Concurrent;
using CampusBrowser.Server.Models;
using Microsoft.Data.Sqlite;

namespace CampusBrowser.Server.Services;

/// <summary>
/// SQLite 数据访问：设置、配置版本、设备、登录令牌。
/// 数据库文件放在程序目录下的 data/，Windows/Linux 行为一致，备份即拷贝文件。
/// </summary>
public class Database
{
    /// <summary>令牌有效期内存缓存（v0.8.2）：token -> expires_at(Unix 秒)，免去每请求查表。</summary>
    private readonly ConcurrentDictionary<string, long> _tokenExpiry = new();

    /// <summary>下次允许清理过期令牌的时刻（Unix 秒），把惰性清理从请求路径挪到节流点。</summary>
    private long _nextTokenPurgeUnix;

    private readonly string _connectionString;

    public Database(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "campus.db");
        // Pooling=True：复用底层连接，省掉每次请求 open/close 的系统调用与 SQLite 文件句柄建立；
        // Default Timeout=30：写锁竞争时等待而不是立刻 SQLITE_BUSY。
        _connectionString = $"Data Source={dbPath};Pooling=True;Default Timeout=30";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Interlocked.Increment(ref DbStats.ConnOpens);
        using (var pragma = conn.CreateCommand())
        {
            // busy_timeout 是连接级（内存态）设置，写冲突时等待 5 秒而不是立即报错；
            // journal_mode / synchronous 是数据库头部的持久属性，已在 Init() 里设置一次，
            // 原来每次开连接都跑一遍 PRAGMA journal_mode=WAL 纯属白付开销。
            pragma.CommandText = "PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    /// <summary>写入语句执行：累计写入计数（性能验收用）。</summary>
    private static int Write(SqliteCommand cmd)
    {
        Interlocked.Increment(ref DbStats.Writes);
        return cmd.ExecuteNonQuery();
    }

    public void Init()
    {
        using var conn = Open();
        using (var pragma = conn.CreateCommand())
        {
            // WAL + synchronous=NORMAL：WAL 模式下 NORMAL 只在 checkpoint 时 fsync，
            // 写入吞吐提升约一个数量级；journal_mode 是持久属性，只需设置一次。
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
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
        Write(cmd);
    }

    public bool AdminExists() => GetSetting("admin_user") is not null;

    // ---------- 登录令牌 ----------

    /// <summary>签发令牌，expires_at = now + ttlSeconds（v0.7.1，L3）。</summary>
    public void InsertToken(string token, long ttlSeconds)
    {
        var expiresAt = DateTimeOffset.Now.ToUnixTimeSeconds() + ttlSeconds;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO auth_tokens(token, created_at, expires_at) VALUES($t, $c, $e);
            """;
        cmd.Parameters.AddWithValue("$t", token);
        cmd.Parameters.AddWithValue("$c", Now());
        cmd.Parameters.AddWithValue("$e", expiresAt);
        Write(cmd);
        _tokenExpiry[token] = expiresAt;
    }

    /// <summary>
    /// 校验令牌（v0.8.2 优化）：有效期内只在内存缓存里判定，不查表、不写库。
    ///
    /// 改造前：每个已认证请求 1 次连接 + 2 个写事务（先 DELETE 全表过期令牌，再 UPDATE 续期），
    /// 后台首页 1 秒刷新时等于每秒 2 次 fsync 却没有任何数据变化。
    /// 改造后：首次命中查一次表并缓存；只有续期推进量 ≥ 半个 TTL（默认 3 小时）才写一次库；
    /// 过期令牌清理改为按 10 分钟节流 + 每天随日志清理再兜底跑一次。
    ///
    /// 权衡：进程重启后数据库里的 expires_at 最多比真实续期落后半个 TTL，最坏情况是
    /// 管理员需要重新登录一次；单实例部署（本项目形态）没有多进程一致性问题。
    /// </summary>
    public bool TokenValid(string? token, long ttlSeconds)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        Interlocked.Increment(ref DbStats.TokenLookups);
        PurgeExpiredTokensIfDue(now);

        if (_tokenExpiry.TryGetValue(token, out var expiresAt))
        {
            Interlocked.Increment(ref DbStats.TokenCacheHits);
        }
        else
        {
            expiresAt = ReadTokenExpiry(token);
            if (expiresAt <= 0) return false; // 不存在
        }
        if (expiresAt <= now)
        {
            _tokenExpiry.TryRemove(token, out _);
            return false; // 已过期
        }

        // 滑动续期：只有推进量达到半个 TTL 才落库，避免高频轮询把每次请求变成写事务。
        var renewed = now + ttlSeconds;
        if (renewed - expiresAt >= ttlSeconds / 2)
        {
            if (!RenewToken(token, renewed))
            {
                _tokenExpiry.TryRemove(token, out _);
                return false; // 行已被清理/登出
            }
            expiresAt = renewed;
        }
        _tokenExpiry[token] = expiresAt;
        return true;
    }

    private long ReadTokenExpiry(string token)
    {
        using var conn = Open();
        using var find = conn.CreateCommand();
        find.CommandText = "SELECT expires_at FROM auth_tokens WHERE token=$t;";
        find.Parameters.AddWithValue("$t", token);
        var res = find.ExecuteScalar();
        return res is null || res == DBNull.Value ? 0 : Convert.ToInt64(res);
    }

    private bool RenewToken(string token, long expiresAt)
    {
        using var conn = Open();
        using var renew = conn.CreateCommand();
        renew.CommandText = "UPDATE auth_tokens SET expires_at=$e WHERE token=$t;";
        renew.Parameters.AddWithValue("$e", expiresAt);
        renew.Parameters.AddWithValue("$t", token);
        Interlocked.Increment(ref DbStats.TokenRenewWrites);
        return Write(renew) > 0;
    }

    /// <summary>过期令牌清理节流入口（请求路径调用，最多 10 分钟一次）。</summary>
    public void PurgeExpiredTokensIfDue(long now)
    {
        var next = Interlocked.Read(ref _nextTokenPurgeUnix);
        if (now < next) return;
        if (Interlocked.CompareExchange(ref _nextTokenPurgeUnix, now + 600, next) != next) return;
        PurgeExpiredTokens(now);
    }

    /// <summary>删除已过期令牌并同步清掉内存缓存（由清理节流点与日志清理服务调用）。</summary>
    public int PurgeExpiredTokens(long now)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM auth_tokens WHERE expires_at < $now;";
        cmd.Parameters.AddWithValue("$now", now);
        var deleted = Write(cmd);
        foreach (var kv in _tokenExpiry)
        {
            if (kv.Value <= now) _tokenExpiry.TryRemove(kv.Key, out _);
        }
        return deleted;
    }

    public void DeleteToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        _tokenExpiry.TryRemove(token, out _);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM auth_tokens WHERE token=$t;";
        cmd.Parameters.AddWithValue("$t", token);
        Write(cmd);
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
        Write(cmd);
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

    /// <summary>
    /// 版本列表（v0.8.2 瘦身）：只取前台真正用到的 version / published_at / note。
    /// 改造前 SELECT 里带着 config_json（每版含内置 HTML 正文，单版可达数百 KB），
    /// 一页 50 版 = 把几十 MB 文本读进内存再整包丢掉。
    /// </summary>
    public List<(string Version, string PublishedAt, string? Note)> ListVersionSummaries()
    {
        var result = new List<(string, string, string?)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, published_at, note FROM config_versions ORDER BY id DESC LIMIT 50;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
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
        Write(cmd);
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
        Write(cmd);
    }

    /// <summary>
    /// 合并的「注册 / 心跳」写入（v0.8.2）：一条 UPSERT 同时更新 name / app_version / ip_address / last_seen / config_version。
    /// 改造前 /api/v1/devices/heartbeat 走 UpsertDevice + TouchDeviceSeen = 两次连接、两次事务、两次 fsync。
    /// </summary>
    public void TouchDeviceHeartbeat(string deviceId, string name, string appVersion, string ipAddress, string configVersion)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices(device_id, name, app_version, config_version, ip_address, registered_at, last_seen)
            VALUES($id, $n, $v, $cv, $ip, $now, $now)
            ON CONFLICT(device_id) DO UPDATE SET
                name=COALESCE(NULLIF(excluded.name,''), devices.name),
                app_version=excluded.app_version,
                config_version=CASE WHEN excluded.config_version='' THEN devices.config_version ELSE excluded.config_version END,
                ip_address=CASE WHEN excluded.ip_address='' THEN devices.ip_address ELSE excluded.ip_address END,
                last_seen=excluded.last_seen;
            """;
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.Parameters.AddWithValue("$n", name ?? "");
        cmd.Parameters.AddWithValue("$v", appVersion ?? "");
        cmd.Parameters.AddWithValue("$cv", configVersion ?? "");
        cmd.Parameters.AddWithValue("$ip", ipAddress ?? "");
        cmd.Parameters.AddWithValue("$now", Now());
        Write(cmd);
    }

    /// <summary>只取 last_seen（在线采样每 5 秒一次；不必读 10 列也不必建 DeviceRow）。</summary>
    public List<string> ListDeviceLastSeen()
    {
        var result = new List<string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_seen FROM devices;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        return result;
    }

    public List<DeviceRow> ListDevices() => ListDevices(0, 0);

    /// <summary>
    /// 设备列表（v0.8.2，L2）：支持分页。limit &lt;= 0 表示不分页（内部/导出用途），
    /// 管理后台接口传 limit/offset，避免设备表被伪造注册撑爆后一次回全部行。
    /// </summary>
    public List<DeviceRow> ListDevices(int limit, int offset)
    {
        var result = new List<DeviceRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = limit > 0
            ? """
              SELECT device_id, name, app_version, config_version, ip_address, registered_at, last_seen,
                     last_photo_at, photo_skip_reason, last_wrong_pin_photo_at
              FROM devices ORDER BY last_seen DESC LIMIT $lim OFFSET $off;
              """
            : """
              SELECT device_id, name, app_version, config_version, ip_address, registered_at, last_seen,
                     last_photo_at, photo_skip_reason, last_wrong_pin_photo_at
              FROM devices ORDER BY last_seen DESC;
              """;
        if (limit > 0)
        {
            cmd.Parameters.AddWithValue("$lim", limit);
            cmd.Parameters.AddWithValue("$off", Math.Max(offset, 0));
        }
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
        Write(cmd);
    }

    /// <summary>设备上报跳过拍照的原因（如无前置摄像头），不更新 last_photo_at。</summary>
    public void SetPhotoSkip(string deviceId, string reason)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE devices SET photo_skip_reason=$r WHERE device_id=$id;";
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$id", deviceId);
        Write(cmd);
    }

    /// <summary>输错密码拍照接收成功后记录时间（与启动拍照独立，用于后台告警）。</summary>
    public void SetWrongPinPhotoTaken(string deviceId, string at)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE devices SET last_wrong_pin_photo_at=$at WHERE device_id=$id;";
        cmd.Parameters.AddWithValue("$at", at);
        cmd.Parameters.AddWithValue("$id", deviceId);
        Write(cmd);
    }

    // ---------- 操作日志（v0.8.0） ----------

    /// <summary>写入一条操作日志；失败不抛（日志不能影响主流程）。</summary>
    /// <summary>
    /// 批量写入审计日志（v0.8.2 性能优化）。
    ///
    /// 改造前：AuditLogWriter 每批最多 256 行逐条调用 InsertLog，每行一次连接取用 + 一次
    /// autocommit —— WAL + synchronous=FULL 下每次 autocommit 都是一次 fsync，
    /// 256 行 ≈ 256 次连接 + 最多 256 次 fsync，是整个写路径最贵的一处。
    /// 改造后：一个连接 + 一个显式事务 + 复用一条命令，一次提交。
    /// </summary>
    public void InsertLogs(IReadOnlyList<LogRow> rows)
    {
        if (rows.Count == 0) return;
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            Interlocked.Increment(ref DbStats.Transactions);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO logs(ts, event, ip, actor, device_id, method, path, status, detail, user_agent)
                VALUES($ts, $ev, $ip, $ac, $dev, $m, $p, $st, $dt, $ua);
                """;
            var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
            var pEv = cmd.Parameters.Add("$ev", SqliteType.Text);
            var pIp = cmd.Parameters.Add("$ip", SqliteType.Text);
            var pAc = cmd.Parameters.Add("$ac", SqliteType.Text);
            var pDev = cmd.Parameters.Add("$dev", SqliteType.Text);
            var pM = cmd.Parameters.Add("$m", SqliteType.Text);
            var pP = cmd.Parameters.Add("$p", SqliteType.Text);
            var pSt = cmd.Parameters.Add("$st", SqliteType.Integer);
            var pDt = cmd.Parameters.Add("$dt", SqliteType.Text);
            var pUa = cmd.Parameters.Add("$ua", SqliteType.Text);
            foreach (var row in rows)
            {
                pTs.Value = row.Ts;
                pEv.Value = row.Event;
                pIp.Value = row.Ip ?? "";
                pAc.Value = row.Actor ?? "";
                pDev.Value = row.DeviceId ?? "";
                pM.Value = row.Method ?? "";
                pP.Value = row.Path ?? "";
                pSt.Value = row.Status;
                pDt.Value = row.Detail ?? "";
                pUa.Value = row.UserAgent ?? "";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            Interlocked.Add(ref DbStats.Writes, rows.Count);
            Interlocked.Increment(ref DbStats.LogBatches);
        }
        catch
        {
            // 忽略：日志写入失败不得影响业务响应
        }
    }

    /// <summary>单条写入（内部转批量路径，行为与改造前一致）。</summary>
    public void InsertLog(LogRow row) => InsertLogs(new[] { row });

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
        return Write(cmd);
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

    /// <summary>
    /// HTML 页列表（不含正文，v0.8.2 瘦身）：后台列表只需要标题/大小/时间，
    /// 正文由 GET /api/admin/html-pages/{id} 单页取。改造前列表把每页正文（单页上限 512 KB）全带上，
    /// 10 页即 5 MB 响应。
    /// </summary>
    public List<(long Id, string Slug, string Title, string CreatedAt, string UpdatedAt, long Size)> ListHtmlPageSummaries()
    {
        var result = new List<(long, string, string, string, string, long)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, slug, title, created_at, updated_at, LENGTH(html) FROM html_pages ORDER BY id DESC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? 0 : reader.GetInt64(5)));
        }
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
            return Write(cmd) > 0;
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
        return Write(cmd) > 0;
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
        Write(cmd);
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

    /// <summary>
    /// 压测/验收种子（仅当环境变量 CAMPUS_PERF_SEED=1 时的 diag 接口调用）：
    /// 一次事务灌入 [count] 个采样点，间隔 [intervalSeconds] 秒，从今天 00:00 起算，
    /// 用来复现「日终 17,280 个点」的真实负载——空库压测是量不出读路径代价的。
    /// </summary>
    public int SeedOnlineSamples(int count, int intervalSeconds)
    {
        var dayStart = new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset).ToUnixTimeSeconds();
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Interlocked.Increment(ref DbStats.Transactions);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO online_samples(ts, online, total) VALUES($ts, $o, $t)
            ON CONFLICT(ts) DO UPDATE SET online=excluded.online, total=excluded.total;
            """;
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var pO = cmd.Parameters.Add("$o", SqliteType.Integer);
        var pT = cmd.Parameters.Add("$t", SqliteType.Integer);
        var written = 0;
        for (var i = 0; i < count; i++)
        {
            var ts = dayStart + (long)i * intervalSeconds;
            if (ts > now) break;
            pTs.Value = ts;
            pO.Value = 60 + i % 40;
            pT.Value = 100;
            cmd.ExecuteNonQuery();
            written++;
        }
        tx.Commit();
        Interlocked.Add(ref DbStats.Writes, written);
        return written;
    }

    /// <summary>压测/验收种子：一次事务灌入 [count] 台设备，last_seen 从 [onlineEverySeconds] 秒前开始按秒散布。</summary>
    public int SeedDevices(int count)
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Interlocked.Increment(ref DbStats.Transactions);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO devices(device_id, name, app_version, config_version, ip_address, registered_at, last_seen)
            VALUES($id, $n, $v, '1.0.0', $ip, $now, $seen)
            ON CONFLICT(device_id) DO UPDATE SET last_seen=excluded.last_seen;
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pN = cmd.Parameters.Add("$n", SqliteType.Text);
        var pV = cmd.Parameters.Add("$v", SqliteType.Text);
        var pIp = cmd.Parameters.Add("$ip", SqliteType.Text);
        var pNow = cmd.Parameters.Add("$now", SqliteType.Text);
        var pSeen = cmd.Parameters.Add("$seen", SqliteType.Text);
        var written = 0;
        for (var i = 0; i < count; i++)
        {
            pId.Value = $"perf-dev-{i:D3}";
            pN.Value = $"Perf {i:D3}";
            pV.Value = "0.8.2";
            pIp.Value = $"10.0.0.{i % 250}";
            pNow.Value = Now();
            // 三分之二在线（150 秒内）、三分之一离线（超过 1 小时）
            pSeen.Value = DateTimeOffset.Now.AddSeconds(i % 3 == 0 ? -4000 - i : -i * 3).ToString("yyyy-MM-dd HH:mm:ss zzz");
            cmd.ExecuteNonQuery();
            written++;
        }
        tx.Commit();
        Interlocked.Add(ref DbStats.Writes, written);
        return written;
    }

    /// <summary>删除早于 keepDays 天的采样点。</summary>
    public int PurgeOldOnlineSamples(int keepDays)
    {
        if (keepDays <= 0) return 0;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM online_samples WHERE ts < $before;";
        cmd.Parameters.AddWithValue("$before", DateTimeOffset.Now.AddDays(-keepDays).ToUnixTimeSeconds());
        return Write(cmd);
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
}
