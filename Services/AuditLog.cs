using System.Collections.Concurrent;
using System.Threading.Channels;
using CampusBrowser.Server.Models;

namespace CampusBrowser.Server.Services;

/// <summary>
/// 操作日志（v0.8.0）：登录 IP/时间、管理操作、设备上报。
///
/// 设计要点：
/// - 写入走内存 Channel 队列 + 后台单消费者落库，请求线程只做入队，1 秒轮询/批量心跳下也不阻塞响应；
/// - 队列有界（默认 10000），满了丢弃最旧记录并计数，绝不反压业务；
/// - 心跳等高频事件按「事件+对象+IP」节流合并（默认 60 秒一条，detail 里累计次数），避免日志表被写爆；
/// - 只记录元数据：**绝不记录密码、PIN 哈希、令牌**。
/// </summary>
public class AuditLog
{
    private const int MaxQueue = 10000;
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(60);

    private readonly Database _db;
    private readonly Channel<LogRow> _channel;
    private readonly ILogger<AuditLog> _logger;

    /// <summary>节流状态：key -> (窗口起点 Unix 秒, 窗口内累计条数)。</summary>
    private readonly ConcurrentDictionary<string, (long Since, int Count)> _throttle = new();

    public AuditLog(Database db, ILogger<AuditLog> logger)
    {
        _db = db;
        _logger = logger;
        _channel = Channel.CreateBounded<LogRow>(new BoundedChannelOptions(MaxQueue)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>当前队列长度（监控/自检用）。</summary>
    public int Pending => _channel.Reader.Count;

    /// <summary>
    /// 事件名常量（后台筛选项与展示名共用一套值）。
    /// </summary>
    public static class Events
    {
        public const string SetupInit = "setup_init";
        public const string LoginOk = "login_ok";
        public const string LoginFail = "login_fail";
        public const string LoginBlocked = "login_blocked";
        public const string Logout = "logout";
        public const string ConfigPublish = "config_publish";
        public const string ConfigRollback = "config_rollback";
        public const string SecurityUpdate = "security_update";
        public const string HtmlSave = "html_save";
        public const string HtmlDelete = "html_delete";
        public const string ViewPhoto = "view_photo";
        public const string ExportCsv = "export_csv";
        public const string PinVerify = "pin_verify";
        public const string DeviceRegister = "device_register";
        public const string DeviceHeartbeat = "device_heartbeat";
    }

    /// <summary>取真实客户端 IP：连接地址优先（X-Forwarded-For 仅作兜底，与登录限频分区逻辑一致）。</summary>
    public static string IpOf(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString()
        ?? ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
        ?? "unknown";

    /// <summary>取操作人：管理接口记用户名，设备接口记 device_id。</summary>
    public string ActorOf(Database db) => db.GetSetting("admin_user") ?? "";

    /// <summary>
    /// 记录一条日志（非阻塞，仅入队）。
    /// [throttleKey] 非空时启用节流合并：同一 key 在 60 秒窗口内只落一条，[detail] 内追加次数。
    /// </summary>
    public void Log(HttpContext? ctx, string evt, string? detail = null, int? status = null,
        string? actor = null, string? deviceId = null, string? throttleKey = null, string? method = null,
        string? path = null)
    {
        var now = DateTimeOffset.Now;
        if (throttleKey is not null)
        {
            var nowSec = now.ToUnixTimeSeconds();
            var key = evt + "|" + throttleKey;
            var state = _throttle.AddOrUpdate(key,
                _ => (nowSec, 1),
                (_, old) => nowSec - old.Since < (long)ThrottleWindow.TotalSeconds
                    ? (old.Since, old.Count + 1)
                    : (nowSec, 1));
            if (state.Count > 1)
                return; // 窗口内重复事件：合并进首条，不再落库
            detail = (detail ?? "") + "（同类事件 60 秒内合并计数）";
        }

        var row = new LogRow(
            Id: 0,
            Ts: now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            Event: evt,
            Ip: ctx is null ? "" : IpOf(ctx),
            Actor: actor ?? "",
            DeviceId: deviceId ?? "",
            Method: method ?? ctx?.Request.Method ?? "",
            Path: path ?? ctx?.Request.Path.Value ?? "",
            Status: status ?? ctx?.Response.StatusCode ?? 0,
            Detail: detail ?? "",
            UserAgent: ctx is null ? "" : Trim(ctx.Request.Headers.UserAgent.ToString(), 300));

        if (!_channel.Writer.TryWrite(row))
            _logger.LogWarning("操作日志队列已满，本条日志被丢弃：{Event}", evt);
    }

    /// <summary>供后台消费者阻塞等待一条（服务停止返回 null）。</summary>
    public async Task<LogRow?> WaitOneAsync(CancellationToken ct)
    {
        while (await _channel.Reader.WaitToReadAsync(ct))
        {
            if (_channel.Reader.TryRead(out var row)) return row;
        }
        return null;
    }

    /// <summary>非阻塞取一条（消费者批量清空积压用）。</summary>
    public bool TryRead(out LogRow? row) => _channel.Reader.TryRead(out row);

    /// <summary>
    /// 批量补写（消费者刷盘时调用）。
    /// v0.8.2：交给 Database.InsertLogs 走「一个连接 + 一个显式事务」，
    /// 改造前这里是 foreach 逐条 InsertLog，每行一次连接取用 + 一次 autocommit（WAL 下即一次 fsync）。
    /// </summary>
    public void Write(IEnumerable<LogRow> rows)
    {
        var list = rows as IReadOnlyList<LogRow> ?? rows.ToList();
        _db.InsertLogs(list);
    }

    /// <summary>清空过期节流键，避免字典无界增长。</summary>
    public void PruneThrottle()
    {
        var nowSec = DateTimeOffset.Now.ToUnixTimeSeconds();
        var window = (long)ThrottleWindow.TotalSeconds;
        foreach (var kv in _throttle)
        {
            if (nowSec - kv.Value.Since >= window) _throttle.TryRemove(kv.Key, out _);
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// 日志落库后台服务：把 AuditLog 队列里的事件批量写入 SQLite，
/// 并按天惰性清理超过保留期的日志与在线采样点。
/// </summary>
public class AuditLogWriter : BackgroundService
{
    private const int LogRetentionDays = 90;      // 操作日志保留天数
    private const int SampleRetentionDays = 7;    // 在线采样点保留天数

    private readonly AuditLog _audit;
    private readonly Database _db;
    private readonly ILogger<AuditLogWriter> _logger;
    private DateTime _lastPurgeDate = DateTime.MinValue;

    public AuditLogWriter(AuditLog audit, Database db, ILogger<AuditLogWriter> logger)
    {
        _audit = audit;
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<LogRow>(256);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 先等一条，再尽量把积压一次取完，减少 SQLite 连接开销
                var first = await _audit.WaitOneAsync(stoppingToken);
                if (first is null) break;
                buffer.Add(first);
                while (buffer.Count < 256 && _audit.TryRead(out var more) && more is not null)
                    buffer.Add(more);

                _audit.Write(buffer);
                buffer.Clear();
                PurgeIfNeeded();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "操作日志写入服务异常退出");
        }
    }

    /// <summary>每天首次写入时清理一次过期数据（惰性，无需定时器）。</summary>
    private void PurgeIfNeeded()
    {
        var today = DateTime.Today;
        if (today == _lastPurgeDate) return;
        _lastPurgeDate = today;
        try
        {
            var logs = _db.PurgeOldLogs(LogRetentionDays);
            var samples = _db.PurgeOldOnlineSamples(SampleRetentionDays);
            // v0.8.2：令牌过期清理从「每个请求都删一次」挪到每天一次兜底
            // （请求路径上已由 Database.PurgeExpiredTokensIfDue 以 10 分钟节流处理）。
            var tokens = _db.PurgeExpiredTokens(DateTimeOffset.Now.ToUnixTimeSeconds());
            _audit.PruneThrottle();
            if (logs > 0 || samples > 0 || tokens > 0)
                _logger.LogInformation("日志清理完成：删除日志 {Logs} 条、采样点 {Samples} 个、过期令牌 {Tokens} 个", logs, samples, tokens);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "日志清理失败（不影响主流程）");
        }
    }
}
