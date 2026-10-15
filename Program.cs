using System.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using CampusBrowser.Server.Models;
using CampusBrowser.Server.Services;

// 启动拍照上传限制（v0.5.0）
const int MaxPhotoBytes = 2 * 1024 * 1024;      // 单张照片上限 2MB
const int PhotoRetentionDays = 7;               // 照片保留天数，上传时惰性清理（按天清理策略）

// PIN 服务端校验限频（v0.7.0）：每设备 60 秒窗口内最多 5 次，防暴力枚举
const int VerifyPinWindowSeconds = 60;
const int VerifyPinMaxHits = 5;

// 管理后台登录限频（v0.7.1，M1）：每 IP 60 秒滑动窗口最多 5 次
const int LoginWindowSeconds = 60;
const int LoginMaxHits = 5;

// 设备注册限频（v0.8.2，L2）：注册接口无鉴权，不限频可被批量伪造设备撑爆设备表与后台列表
const int RegisterWindowSeconds = 60;
const int RegisterMaxHits = 20;

// 登录令牌有效期（v0.7.1，L3）：6 小时，每次有效请求滑动续期
const long TokenTtlSeconds = 6 * 60 * 60;

// APP 接口 UA 校验（v0.7.0 引入开关，v0.8.3 起常开强制）：合法 UA 必须包含的标记
const string AppUaMarker = "OkHttp/";

// 管理后台页面 CSP（v0.8.3）：只加在管理后台自己的 HTML 上。
// /h/{slug} 是教师自编页面，不加 CSP（否则会破坏页面本身）。
// style-src 保留 'unsafe-inline'：现有表格行/内联样式仍靠 style 属性（后续可收紧到纯 'self'）。
const string AdminCsp =
    "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
    "img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

// 在线采样（v0.8.0）：首页全天折线图的数据源，每 5 秒采一次（1 秒前端刷新仍平滑）
const int OnlineSampleSeconds = 5;
// 首页/曲线最多回看天数（与 Database.PurgeOldOnlineSamples 的 7 天保留对齐）
const int OnlineSampleKeepDays = 7;

// 内置 HTML 页限制（v0.8.0）：地址片段白名单正则 + 单页体积上限
const int MaxHtmlChars = 512 * 1024; // 512 KB

var HtmlSlugRegex = new Regex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

// verify-pin 滑动窗口计数：deviceId -> 窗口内请求时间戳队列
var verifyPinHits = new ConcurrentDictionary<string, Queue<long>>();

var builder = WebApplication.CreateBuilder(args);

// 注册为 Windows 服务（Linux 下自动忽略）；数据目录固定在程序目录，避免服务模式下落到 System32。
builder.Host.UseWindowsService(o => o.ServiceName = "CampusBrowserServer");
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
builder.Services.AddSingleton(_ => new Database(dataDir));

// 操作日志（v0.8.0）：内存队列 + 后台消费落库，请求线程不碰磁盘
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddHostedService<AuditLogWriter>();
// 在线采样（v0.8.0）：首页全天折线图数据
builder.Services.AddHostedService<OnlineSampler>();

// 性能打点（v0.8.2）：进程内路由耗时/响应字节计数 + SQLite 访问计数，只观测不改行为。
// 由 GET /api/admin/diag/perf 输出，tools/perf_probe.ps1 用它做预算判定。
builder.Services.AddSingleton<PerfStats>();

// 读路径缓存（v0.8.2）：
//  - ConfigInterval：当前配置的 update_interval_seconds，30 秒 TTL（原来每次请求都读整份 config_json 再解析）
//  - OverviewCache：首页概览「在线数 + 全天采样点 + 中位数间隔」按 5 秒采样周期缓存并预序列化
builder.Services.AddSingleton<ConfigInterval>();
builder.Services.AddSingleton<OverviewCache>();
//  - ConfigCache：APP 下发配置的客户端视图（剥 PIN/正文 + RSA 签名），按版本号缓存
builder.Services.AddSingleton<ConfigCache>();

// 响应压缩（v0.8.2）：后台首页每秒拉一次概览（日终约 760 KB）、设备/日志列表、APP 配置 JSON 都高度可压。
// 用 .NET 内置 ResponseCompression（Brotli + Gzip），不改任何端点逻辑；仅压 text/json 类。
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    o.MimeTypes = new[]
    {
        "application/json", "text/json", "text/plain", "text/csv", "text/html",
        "text/css", "application/javascript", "text/javascript", "image/svg+xml"
    };
});

// 端口优先级：命令行/环境变量显式指定 > 安装目录 campus.port 覆盖文件 > 默认 8080。
// campus.port 为安装器写入的纯文本端口号，升级覆盖安装时该文件保留，自定义端口不丢。
var configuredUrls = builder.Configuration["Urls"];
if (string.IsNullOrWhiteSpace(configuredUrls))
{
    var portFile = Path.Combine(AppContext.BaseDirectory, "campus.port");
    if (File.Exists(portFile))
    {
        var portText = File.ReadAllText(portFile).Trim();
        if (int.TryParse(portText, out var portFromFile) && portFromFile is >= 1 and <= 65535)
            configuredUrls = $"http://0.0.0.0:{portFromFile}";
    }
}
builder.WebHost.UseUrls(configuredUrls ?? "http://0.0.0.0:8080");
// 安全：不向响应暴露 Server 头，减少服务端指纹信息
builder.WebHost.UseKestrel(o =>
{
    o.AddServerHeader = false;
    // v0.8.2：Kestrel 默认允许 30MB 请求体，而照片端点自己限 2MB（Program.cs 里 MaxPhotoBytes）。
    // 原来 2MB-30MB 之间的请求会先完整落到内存/临时文件再被拒 → 直接卡在入口，3MB 兜住合法照片+余量。
    o.Limits.MaxRequestBodySize = 3 * 1024 * 1024;
});

// 裁剪安全：Minimal API 请求体绑定/响应序列化接入 source generator（AppJsonContext），
// 未注册类型（如 Results.Ok(new{...}) 匿名对象）由反射 resolver 兜底。
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolver =
        JsonTypeInfoResolver.Combine(AppJsonContext.Default, new DefaultJsonTypeInfoResolver());
});

// 登录限频（v0.7.1，M1）：.NET 8 内置滑动窗口限频器，按客户端 IP 分区。
// 参考微软官方实现（github.com/dotnet/aspnetcore RateLimiting）。
builder.Services.AddRateLimiter(rateOptions =>
{
    rateOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateOptions.OnRejected = async (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("{\"error\":\"尝试过于频繁，请稍后再试\"}");
    };
    rateOptions.AddPolicy("login", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString()
                         ?? httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                         ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = LoginMaxHits,
                Window = TimeSpan.FromSeconds(LoginWindowSeconds),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));
    // 设备注册限频（v0.8.2，L2）：与登录同一套滑动窗口，按客户端 IP 分区
    rateOptions.AddPolicy("register", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString()
                         ?? httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                         ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = RegisterMaxHits,
                Window = TimeSpan.FromSeconds(RegisterWindowSeconds),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

var db = app.Services.GetRequiredService<Database>();
db.Init();

// 读路径缓存（v0.8.2）：轮询间隔缓存 + 首页概览缓存
var configInterval = app.Services.GetRequiredService<ConfigInterval>();
var overviewCache = app.Services.GetRequiredService<OverviewCache>();
var configCache = app.Services.GetRequiredService<ConfigCache>();

// 操作日志（v0.8.0）：登录 IP/时间、管理操作、设备上报
var audit = app.Services.GetRequiredService<AuditLog>();

// 首次启动生成签名密钥对（私钥不出服务器）。
if (string.IsNullOrEmpty(db.GetSetting("rsa_private")))
{
    var (priv, pub) = Signer.GenerateKeyPair();
    db.SetSetting("rsa_private", priv);
    db.SetSetting("rsa_public", pub);
}

// 平板管理密码迁移（v0.8.3）：v0.8.2 及更早把 admin_pin_hash 存在配置版本 JSON 里，
// 于是发布/回滚配置会连带改动或还原密码。现在改存 settings 表（键 admin_pin_hash），
// 配置发布/回滚完全不碰密码。这里做一次性迁移，幂等，重启不覆盖已有密码。
if (string.IsNullOrEmpty(db.GetSetting("admin_pin_hash")))
{
    try
    {
        var legacy = db.GetCurrentVersion()?.ConfigJson;
        if (legacy is not null)
        {
            using var legacyDoc = JsonDocument.Parse(legacy);
            if (legacyDoc.RootElement.TryGetProperty("admin_pin_hash", out var legacyPin) &&
                !string.IsNullOrWhiteSpace(legacyPin.GetString()))
                db.SetSetting("admin_pin_hash", legacyPin.GetString()!);
        }
    }
    catch
    {
        // 历史配置 JSON 异常不影响启动；密码缺失时校验一律不通过
    }
}

// 性能打点中间件（v0.8.2）：只挂在 /api 下，统计每个路由的耗时与响应字节。
// 用 CountingStream 包一层 Response.Body，不缓冲、不改内容；非 /api 请求直接放行，零额外开销。
var perf = app.Services.GetRequiredService<PerfStats>();

// 响应压缩（v0.8.2）：必须在打点中间件之前，打点记录的字节数才是真正上网的压缩后体积。
app.UseResponseCompression();

app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }
    var originalBody = ctx.Response.Body;
    var counting = new CountingStream(originalBody);
    ctx.Response.Body = counting;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await next();
    }
    finally
    {
        sw.Stop();
        ctx.Response.Body = originalBody;
        perf.Record(PerfStats.KeyOf(ctx), sw.Elapsed.TotalMilliseconds, counting.BytesWritten, ctx.Response.StatusCode);
    }
});

// 性能验收口（v0.8.2，需管理员令牌）：路由耗时/字节 + SQLite 访问计数，供 tools/perf_probe.ps1 判定预算。
// 阈值分两段走：B1 先用「基线宽松值」（保证改造中的旧路径也能跑过），B2 按实测收紧。
app.MapGet("/api/admin/diag/perf", (HttpContext ctx, bool? reset) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var budget = new Dictionary<string, (double AvgMs, long Bytes)>(StringComparer.Ordinal)
    {
        ["GET /api/admin/stats/overview"] = (400, 1_000_000),
        ["GET /api/admin/devices"] = (100, 60_000),
        ["GET /api/admin/config/versions"] = (150, 200_000),
        ["GET /api/v1/config"] = (200, 400_000),
        ["POST /api/v1/devices/heartbeat"] = (150, 2_000)
    };
    var routes = perf.Routes.Select(kv =>
    {
        var stat = kv.Value;
        var hasBudget = budget.TryGetValue(kv.Key, out var b);
        return new
        {
            route = kv.Key,
            count = stat.Count,
            avgMs = stat.AvgMs,
            maxMs = stat.MaxMs,
            avgBytes = stat.AvgBytes,
            maxBytes = stat.MaxBytes,
            errors = stat.Errors,
            budgetAvgMs = hasBudget ? b.AvgMs : 0,
            budgetBytes = hasBudget ? b.Bytes : 0,
            pass = !hasBudget || (stat.AvgMs <= b.AvgMs && stat.AvgBytes <= b.Bytes)
        };
    }).ToList();
    var payload = new
    {
        uptimeSeconds = perf.UptimeSeconds,
        auditQueue = audit.Pending,
        db = new
        {
            connOpens = DbStats.ConnOpens,
            transactions = DbStats.Transactions,
            writes = DbStats.Writes,
            tokenLookups = DbStats.TokenLookups,
            tokenCacheHits = DbStats.TokenCacheHits,
            tokenRenewWrites = DbStats.TokenRenewWrites,
            logBatches = DbStats.LogBatches,
            overviewCacheHits = DbStats.OverviewCacheHits,
            overviewCacheMisses = DbStats.OverviewCacheMisses,
            configCacheHits = DbStats.ConfigCacheHits,
            configCacheMisses = DbStats.ConfigCacheMisses
        },
        routes
    };
    // 压测脚本在测量前清零，保证统计窗口干净（先取快照再清零）
    if (reset == true)
    {
        perf.Reset();
        DbStats.Reset();
    }
    return Results.Ok(payload);
});

/// <summary>
/// 性能验收种子（v0.8.2）：只在设置环境变量 CAMPUS_PERF_SEED=1 的进程上存在（服务/生产模式不会有，
/// 其余情况返回 404，与「接口不存在」不可区分）。用来把空库灌成「日终 17,280 个采样点 + 200 台设备」，
/// 否则测的是一张空表的读路径，量不出真实代价。
/// </summary>
app.MapPost("/api/admin/diag/seed", (HttpContext ctx, int? devices, int? samples) =>
{
    if (Environment.GetEnvironmentVariable("CAMPUS_PERF_SEED") != "1") return Results.NotFound();
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var seededDevices = db.SeedDevices(Math.Clamp(devices ?? 200, 0, 5000));
    var seededSamples = db.SeedOnlineSamples(Math.Clamp(samples ?? 17280, 0, 20000), OnlineSampleSeconds);
    return Results.Ok(new { ok = true, devices = seededDevices, samples = seededSamples });
});

app.UseDefaultFiles();
// 静态资源安全响应头（v0.8.3）：
//  - 管理后台的 HTML/JS 一律 no-store，升级覆盖安装后绝不会用到旧脚本/旧壳
//  - 只给管理后台自己的 .html 加 CSP 与防嵌套/防 Referer 头；
//    /h/{slug}（教师自编页）与图片/CSS 不受影响
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = staticCtx =>
    {
        var headers = staticCtx.Context.Response.Headers;
        var ext = Path.GetExtension(staticCtx.File.Name).ToLowerInvariant();
        headers["Cache-Control"] = ext is ".html" or ".js" ? "no-store" : "no-cache";
        headers["X-Content-Type-Options"] = "nosniff";
        if (ext == ".html")
        {
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = AdminCsp;
        }
    }
});

// 登录审计（v0.8.0）：把 /api/login 的响应体缓冲下来，读取状态码后再原样写回。
// 只缓冲这一条短 JSON 响应，不读取/不记录请求体中的密码。
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.Equals("/api/login", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    var audit = ctx.RequestServices.GetRequiredService<AuditLog>();
    var original = ctx.Response.Body;
    using var buffer = new MemoryStream();
    ctx.Response.Body = buffer;
    await next();
    buffer.Position = 0;
    var payload = buffer.ToArray();
    ctx.Response.Body = original;
    // 命中登录限频时响应体已被限频器写好，这里一并搬移
    ctx.Response.ContentLength = payload.Length;
    await original.WriteAsync(payload);

    var status = ctx.Response.StatusCode;
    var evt = status == StatusCodes.Status200OK
        ? AuditLog.Events.LoginOk
        : status == StatusCodes.Status429TooManyRequests ? AuditLog.Events.LoginBlocked
        : AuditLog.Events.LoginFail;
    var detail = status == StatusCodes.Status200OK
        ? "管理员登录成功"
        : status == StatusCodes.Status429TooManyRequests ? "登录过于频繁，已按 IP 限频"
        : "用户名或密码错误";
    audit.Log(ctx, evt, detail, status);
});

app.UseRateLimiter();

// ============ 工具函数 ============

static bool IsHttpUrl(string? url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

static string? TokenFrom(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header["Bearer ".Length..].Trim()
        : null;
}

bool IsAdmin(HttpContext ctx) => db.TokenValid(TokenFrom(ctx), TokenTtlSeconds);

/// <summary>审计日志里的操作人：管理端操作统一记管理员用户名。</summary>
string AuditActor() => db.GetSetting("admin_user") ?? "";

/// <summary>
/// APP 接口 UA 校验（v0.8.3：常开强制，不再有后台开关）。
/// 请求 UA 必须含 OkHttp/（APP 端用 OkHttp 发请求）；否则 403 非法客户端。
/// 注意：/h/{slug} 是平板 WebView 打开的页面（WebView UA），不受此校验影响。
/// </summary>
bool AppUaValid(HttpContext ctx) =>
    ctx.Request.Headers.UserAgent.ToString().Contains(AppUaMarker, StringComparison.Ordinal);

List<string> ParseRules(string? text) =>
    (text ?? string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

List<BookmarkItem> NormalizeBookmarks(List<BookmarkItem>? bookmarks) =>
    (bookmarks ?? new List<BookmarkItem>())
        .Where(b => !string.IsNullOrWhiteSpace(b.Url))
        .Select(b =>
        {
            var url = b.Url.Trim();
            var title = string.IsNullOrWhiteSpace(b.Title)
                ? (Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url)
                : b.Title.Trim();
            return new BookmarkItem { Title = title, Url = url };
        })
        .ToList();

List<AllowedAppItem> NormalizeAllowedApps(List<AllowedAppItem>? apps) =>
    (apps ?? new List<AllowedAppItem>())
        .Where(a => !string.IsNullOrWhiteSpace(a.Package))
        .Select(a =>
        {
            var pkg = a.Package.Trim();
            var label = string.IsNullOrWhiteSpace(a.Label) ? pkg : a.Label.Trim();
            var scheme = (a.Scheme ?? string.Empty).Trim().TrimEnd(':');
            return new AllowedAppItem { Package = pkg, Label = label, Scheme = scheme };
        })
        .ToList();

/// <summary>
/// 内置 HTML 页（v0.8.0）：管理员在后台编辑的静态页面，随配置一起下发，
/// APP 端只需把 URL 加进书签即可在受管 WebView 中打开（白名单模式下书签自动放行）。
/// </summary>
List<HtmlPageItem> NormalizeHtmlPages(List<HtmlPageItem>? pages) =>
    (pages ?? new List<HtmlPageItem>())
        .Where(p => !string.IsNullOrWhiteSpace(p.Slug))
        .Select(p => new HtmlPageItem
        {
            Slug = p.Slug.Trim(),
            Title = string.IsNullOrWhiteSpace(p.Title) ? p.Slug.Trim() : p.Title.Trim(),
            Html = p.Html ?? ""
        })
        .ToList();

/// <summary>HTML 页字段校验：slug 必须是小写字母/数字/短横线，且不重复。</summary>
IResult? ValidateHtmlPages(List<HtmlPageItem>? pages)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in pages ?? new List<HtmlPageItem>())
    {
        if (string.IsNullOrWhiteSpace(p.Slug) && string.IsNullOrWhiteSpace(p.Html)) continue;
        var slug = (p.Slug ?? "").Trim();
        if (!HtmlSlugRegex.IsMatch(slug))
            return Results.BadRequest(new { error = $"HTML 页地址片段不合法（仅小写字母/数字/短横线，1-64 位）：{slug}" });
        if (!seen.Add(slug))
            return Results.BadRequest(new { error = $"HTML 页地址片段重复：{slug}" });
        if ((p.Html ?? "").Length > MaxHtmlChars)
            return Results.BadRequest(new { error = $"HTML 页过大（上限 {MaxHtmlChars / 1024} KB）：{slug}" });
    }
    return null;
}

/// <summary>解析时间参数：兼容 Unix 秒与 ISO 字符串，无法解析返回 null。</summary>
static DateTimeOffset? ParseTimeParam(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    if (long.TryParse(raw, out var secs) && secs > 0) return DateTimeOffset.FromUnixTimeSeconds(secs);
    return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
}

/// <summary>当前配置的轮询间隔推导出的在线判定阈值：间隔 2 倍，至少 120 秒。</summary>
TimeSpan OnlineThreshold() => TimeSpan.FromSeconds(Math.Max(configInterval.Seconds * 2, 120));

/// <summary>事件名 → 中文展示名（后台筛选与日志表共用）。</summary>
static string EventLabel(string evt) => evt switch
{
    "setup_init" => "系统初始化",
    "login_ok" => "登录成功",
    "login_fail" => "登录失败",
    "login_blocked" => "登录被限频",
    "logout" => "退出登录",
    "config_publish" => "发布配置",
    "config_rollback" => "回滚配置",
    "pin_update" => "修改平板密码",
    "html_save" => "保存 HTML 页",
    "html_delete" => "删除 HTML 页",
    "view_photo" => "查看设备照片",
    "export_csv" => "导出 CSV",
    "pin_verify" => "校验平板密码",
    "device_register" => "设备注册",
    "device_heartbeat" => "设备心跳",
    _ => evt
};

/// <summary>后台日志筛选下拉用的事件清单。</summary>
var EventCatalog = new (string Value, string Label)[]
{
    ("all", "全部事件"),
    ("login_ok", "登录成功"),
    ("login_fail", "登录失败"),
    ("login_blocked", "登录被限频"),
    ("logout", "退出登录"),
    ("setup_init", "系统初始化"),
    ("config_publish", "发布配置"),
    ("config_rollback", "回滚配置"),
    ("pin_update", "修改平板密码"),
    ("html_save", "保存 HTML 页"),
    ("html_delete", "删除 HTML 页"),
    ("view_photo", "查看设备照片"),
    ("export_csv", "导出 CSV"),
    ("pin_verify", "校验平板密码"),
    ("device_register", "设备注册"),
    ("device_heartbeat", "设备心跳"),
};

AppConfig BuildConfig(string title, string homeUrl, string fallbackUrl, string mode, string rules,
    List<BookmarkItem>? bookmarks, int interval, bool kiosk,
    bool hiddenEntryEnabled, bool blockScreenshot, bool requireStartupPhoto,
    List<AllowedAppItem>? allowedApps, List<HtmlPageItem>? htmlPages = null) => new()
{
    Version = "0.0.0", // 由发布流程覆盖
    GeneratedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
    Title = string.IsNullOrWhiteSpace(title) ? "校园门户" : title.Trim(),
    HomeUrl = homeUrl.Trim(),
    FallbackUrl = fallbackUrl.Trim(),
    Mode = mode == "blacklist" ? "blacklist" : "whitelist",
    Rules = ParseRules(rules),
    Bookmarks = NormalizeBookmarks(bookmarks),
    UpdateIntervalSeconds = Math.Clamp(interval <= 0 ? 300 : interval, 30, 86400),
    Kiosk = kiosk,
    HiddenEntryEnabled = hiddenEntryEnabled,
    BlockScreenshot = blockScreenshot,
    RequireStartupPhoto = requireStartupPhoto,
    AllowedApps = NormalizeAllowedApps(allowedApps),
    HtmlPages = NormalizeHtmlPages(htmlPages)
};

(string Json, string Signature) SerializeAndSign(AppConfig config)
{
    // 使用 source generator 序列化，确保 PublishTrimmed 裁剪后元数据不丢失
    var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
    var signature = Signer.Sign(db.GetSetting("rsa_private")!, Encoding.UTF8.GetBytes(json));
    return (json, signature);
}

string NextVersion()
{
    var current = db.GetCurrentVersion()?.Version;
    if (current is null) return "1.0.0";
    var parts = current.Split('.');
    if (parts.Length == 3 && int.TryParse(parts[2], out var patch))
        return $"{parts[0]}.{parts[1]}.{patch + 1}";
    return current + ".1";
}

IResult? ValidateConfigForm(string homeUrl, string? pin, List<BookmarkItem>? bookmarks, string? rules = null)
{
    if (!IsHttpUrl(homeUrl))
        return Results.BadRequest(new { error = "首页网址不正确，需以 http:// 或 https:// 开头" });
    if (!string.IsNullOrWhiteSpace(pin) && (pin.Trim().Length != 6 || !pin.Trim().All(char.IsDigit)))
        return Results.BadRequest(new { error = "管理密码必须是 6 位数字，或留空保持不变" });
    if (bookmarks is not null)
    {
        foreach (var b in bookmarks.Where(b => !string.IsNullOrWhiteSpace(b.Url)))
        {
            if (!IsHttpUrl(b.Url?.Trim()))
                return Results.BadRequest(new { error = $"书签网址不正确（需 http/https 开头）：{b.Url}" });
        }
    }
    // 规则端口合法性校验（v0.7.0）：host:port 中端口必须 1-65535
    if (rules is not null)
    {
        foreach (var raw in ParseRules(rules))
        {
            var hostPart = raw.Contains("://") ? raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..] : raw;
            hostPart = hostPart.Split('/')[0];
            if (hostPart.Contains(':') &&
                (!int.TryParse(hostPart.Split(':')[1], out var p) || p is < 1 or > 65535))
                return Results.BadRequest(new { error = $"规则端口不正确（需 1-65535）：{raw}" });
        }
    }
    return null;
}

// ============ 初始化与登录 ============

app.MapGet("/api/setup/status", () => Results.Ok(new { needsSetup = !db.AdminExists() }));

app.MapPost("/api/setup", (SetupRequest req, HttpContext ctx) =>
{
    if (db.AdminExists()) return Results.BadRequest(new { error = "系统已初始化" });
    if (string.IsNullOrWhiteSpace(req.Username) || req.Password.Length < 6)
        return Results.BadRequest(new { error = "管理员账号必填，密码至少 6 位" });
    IResult? invalid = ValidateConfigForm(req.HomeUrl, req.AdminPin, req.Bookmarks, req.Rules);
    if (invalid is null) invalid = ValidateHtmlPages(req.HtmlPages);
    if (invalid is not null) return invalid;

    db.SetSetting("admin_user", req.Username.Trim());
    db.SetSetting("admin_pwd_hash", AdminPassword.Hash(req.Password));
    // v0.8.3：平板管理密码存 settings 表，不再写进配置 JSON（发布/回滚不会改密码）
    if (!string.IsNullOrWhiteSpace(req.AdminPin))
        db.SetSetting("admin_pin_hash", PinHasher.HashPin(req.AdminPin));

    var config = BuildConfig(req.Title, req.HomeUrl, req.FallbackUrl, req.Mode, req.Rules,
        req.Bookmarks, req.UpdateIntervalSeconds, req.Kiosk,
        req.HiddenEntryEnabled, req.BlockScreenshot, req.RequireStartupPhoto, req.AllowedApps,
        req.HtmlPages);
    config.Version = "1.0.0";
    var (json, signature) = SerializeAndSign(config);
    var id = db.InsertVersion("1.0.0", json, signature, "初始化配置");
    db.SetCurrent(id);
    configInterval.Invalidate();
    configCache.Invalidate();
    // 内置 HTML 页：初始化时提交的页面同时落库，/h/{slug} 才能直接打开
    var createdPages = 0;
    foreach (var p in config.HtmlPages)
    {
        var slug = p.Slug.Trim().ToLowerInvariant();
        if (db.GetHtmlPage(slug) is null && db.SaveHtmlPage(0, slug, p.Title, p.Html)) createdPages++;
    }
    audit.Log(ctx, AuditLog.Events.SetupInit,
        detail: $"初始化管理员 {req.Username.Trim()}，发布首版配置 1.0.0，内置 HTML 页 {createdPages} 个");
    return Results.Ok(new { ok = true, version = "1.0.0" });
});

app.MapPost("/api/login", (LoginRequest req) =>
{
    var user = db.GetSetting("admin_user");
    var hash = db.GetSetting("admin_pwd_hash") ?? "";
    // M1：用户名不存在与密码错误返回完全一致的 401 + 消息，消除用户名枚举
    if (user != req.Username.Trim() || !AdminPassword.Verify(req.Password, hash))
        return Results.Json(new { error = "用户名或密码错误" }, statusCode: StatusCodes.Status401Unauthorized);
    var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    db.InsertToken(token, TokenTtlSeconds);
    return Results.Ok(new { token });
}).RequireRateLimiting("login");

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var actor = AuditActor();
    db.DeleteToken(TokenFrom(ctx));
    audit.Log(ctx, AuditLog.Events.Logout, detail: "管理员退出登录", status: StatusCodes.Status200OK, actor: actor);
    return Results.Ok(new { ok = true });
});

// ============ 管理后台接口 ============

app.MapGet("/api/admin/config", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var current = db.GetCurrentVersion();
    return current is null
        ? Results.Ok(new { })
        : Results.Text(current.ConfigJson, "application/json; charset=utf-8", Encoding.UTF8);
});

app.MapPost("/api/admin/config/publish", (HttpContext ctx, PublishRequest req) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    IResult? invalid = ValidateConfigForm(req.HomeUrl, null, req.Bookmarks, req.Rules);
    if (invalid is null) invalid = ValidateHtmlPages(req.HtmlPages);
    if (invalid is not null) return invalid;

    var version = NextVersion();
    var config = BuildConfig(req.Title, req.HomeUrl, req.FallbackUrl, req.Mode, req.Rules,
        req.Bookmarks, req.UpdateIntervalSeconds, req.Kiosk,
        req.HiddenEntryEnabled, req.BlockScreenshot, req.RequireStartupPhoto, req.AllowedApps,
        req.HtmlPages);
    config.Version = version;
    var (json, signature) = SerializeAndSign(config);
    var id = db.InsertVersion(version, json, signature, string.IsNullOrWhiteSpace(req.Note) ? "后台发布" : req.Note.Trim());
    db.SetCurrent(id);
    configInterval.Invalidate(); // 新配置可能改了 update_interval_seconds，立即生效
    configCache.Invalidate();    // 新版本号 → 旧的下发视图（剥 PIN + 签名）作废
    // 内置 HTML 页：发布时带入的正文同样落库（正文为空的条目只更新标题，避免覆盖已有内容）
    var touched = 0;
    foreach (var p in config.HtmlPages)
    {
        var slug = p.Slug.Trim().ToLowerInvariant();
        var existing = db.GetHtmlPage(slug);
        var html = string.IsNullOrEmpty(p.Html) ? existing?.Html ?? "" : p.Html;
        if (db.SaveHtmlPage(existing?.Id ?? 0, slug, p.Title, html)) touched++;
    }
    audit.Log(ctx, AuditLog.Events.ConfigPublish,
        detail: $"发布配置 {version}：书签 {config.Bookmarks.Count} 个、规则 {config.Rules.Count} 条、HTML 页 {config.HtmlPages.Count} 个（同步 {touched}）" +
                (string.IsNullOrWhiteSpace(req.Note) ? "" : $"，备注：{req.Note.Trim()}"),
        status: StatusCodes.Status200OK, actor: AuditActor());
    return Results.Ok(new { ok = true, version });
});

app.MapGet("/api/admin/config/versions", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    // v0.8.2 修复 N+1：GetCurrentVersion() 原来写在 Select 里，每行都要查一次库并读出
    // 整份 config_json（含 HTML 正文），50 个版本 = 50 次查询 + 50 份 blob；提到外面只查一次。
    var currentVersion = db.GetCurrentVersion()?.Version;
    // v0.8.2 瘦身：只查 version/published_at/note 三列（原来连 config_json 一起读，50 版可能几十 MB）
    return Results.Ok(db.ListVersionSummaries().Select(v => new
    {
        v.Version, v.PublishedAt, v.Note, current = v.Version == currentVersion
    }));
});

/// <summary>回滚 = 用历史版本内容重新发布一个新版本号，保证版本号单调递增，终端一定能收到。</summary>
app.MapPost("/api/admin/config/rollback", (HttpContext ctx, string version) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var target = db.GetVersion(version);
    if (target is null) return Results.NotFound(new { error = "版本不存在" });

    var newVersion = NextVersion();
    var config = JsonSerializer.Deserialize(target.ConfigJson, AppJsonContext.Default.AppConfig)!;
    config.Version = newVersion;
    config.GeneratedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");
    var (json, signature) = SerializeAndSign(config);
    var id = db.InsertVersion(newVersion, json, signature, $"回滚自 {version}");
    db.SetCurrent(id);
    configInterval.Invalidate();
    configCache.Invalidate();
    audit.Log(ctx, AuditLog.Events.ConfigRollback,
        detail: $"回滚 {version} → 新版本 {newVersion}",
        status: StatusCodes.Status200OK, actor: AuditActor());
    return Results.Ok(new { ok = true, version = newVersion });
});

app.MapGet("/api/admin/devices", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    // 在线判定阈值：轮询间隔的 2 倍，至少 120 秒
    var threshold = OnlineThreshold();
    // 分页（v0.8.2，L2）：默认最多 500 台/次，设备再多也不会一次把整表塞给网页；
    // 总数放响应头，前端需要时可以显示“共 N 台”。
    var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l)
        ? Math.Clamp(l, 1, 1000) : 500;
    var offset = int.TryParse(ctx.Request.Query["offset"].FirstOrDefault(), out var o)
        ? Math.Max(o, 0) : 0;
    ctx.Response.Headers["X-Total-Count"] = db.CountDevices().ToString();
    return Results.Ok(db.ListDevices(limit, offset).Select(d => new
    {
        d.DeviceId, d.Name, d.AppVersion, d.ConfigVersion, d.IpAddress,
        d.RegisteredAt, d.LastSeen, d.LastPhotoAt, d.PhotoSkipReason, d.LastWrongPinPhotoAt,
        online = IsOnline(d.LastSeen, threshold)
    }));
});

/// <summary>
/// 首页概览指标（v0.8.0）：当前在线数 / 设备总数 / 在线百分比 + 今日 00:00 起的在线采样曲线。
/// 前端 1 秒刷新只调这一个接口。
/// </summary>
app.MapGet("/api/admin/stats/overview", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var now = DateTimeOffset.Now.ToUnixTimeSeconds();

    // 整份响应（1 秒轮询里 4/5 次直接命中 5 秒缓存，零查询零序列化）
    var build = () =>
    {
        var threshold = OnlineThreshold();
        var lastSeen = db.ListDeviceLastSeen();
        var total = lastSeen.Count;
        var onlineCount = lastSeen.Count(s => IsOnline(s, threshold));

        var todayStart = new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset).ToUnixTimeSeconds();
        var rows = db.ListOnlineSamples(todayStart);
        var samples = rows.Select(s => new OverviewCache.Sample(s.Ts, s.Online, s.Total)).ToList();

        // 采样间隔（秒）：取相邻采样点间隔的中位数，只有 1 个点时用设计值 5
        var sampleSeconds = OnlineSampleSeconds;
        if (rows.Count >= 3)
        {
            var deltas = new List<int>();
            for (var i = 1; i < rows.Count; i++)
            {
                var d = (int)(rows[i].Ts - rows[i - 1].Ts);
                if (d is > 0 and < 600) deltas.Add(d);
            }
            if (deltas.Count > 0)
            {
                deltas.Sort();
                sampleSeconds = deltas[deltas.Count / 2];
            }
        }
        return (onlineCount, total, sampleSeconds, samples);
    };

    // 增量模式（v0.8.2 前端每秒调用）：?since=<最后一点 ts> 只回新增采样点，响应从几百 KB 降到几百字节。
    var sinceRaw = ctx.Request.Query["since"].FirstOrDefault();
    if (long.TryParse(sinceRaw, out var since) && since > 0)
    {
        overviewCache.GetFullJson(now, OnlineSampleSeconds, OnlineSampleKeepDays, build);
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Text(overviewCache.BuildDelta(now, since), "application/json; charset=utf-8");
    }

    var full = overviewCache.GetFullJson(now, OnlineSampleSeconds, OnlineSampleKeepDays, build);
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Bytes(full, "application/json; charset=utf-8");
});

/// <summary>操作日志查询（v0.8.0，需 Bearer Token）。从/到支持 Unix 秒或 ISO 时间。</summary>
app.MapGet("/api/admin/logs", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var from = ParseTimeParam(ctx.Request.Query["from"].FirstOrDefault());
    var to = ParseTimeParam(ctx.Request.Query["to"].FirstOrDefault());
    var evt = ctx.Request.Query["event"].FirstOrDefault();
    var kw = ctx.Request.Query["q"].FirstOrDefault();
    var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 200;
    var offset = int.TryParse(ctx.Request.Query["offset"].FirstOrDefault(), out var o) ? o : 0;
    var rows = db.QueryLogs(from, to, evt, kw, limit, offset);
    var total = db.CountLogs(from, to, evt, kw);
    return Results.Ok(new
    {
        total,
        limit = Math.Clamp(limit, 1, 2000),
        offset = Math.Max(offset, 0),
        events = EventCatalog,
        items = rows.Select(r => new
        {
            r.Id, r.Ts, r.Event, eventLabel = EventLabel(r.Event), r.Ip, r.Actor,
            r.DeviceId, r.Method, r.Path, r.Status, r.Detail, r.UserAgent
        })
    });
});

/// <summary>操作日志 CSV 导出（UTF-8 BOM + RFC4180，与设备导出一致）。</summary>
app.MapGet("/api/admin/logs/export.csv", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var from = ParseTimeParam(ctx.Request.Query["from"].FirstOrDefault());
    var to = ParseTimeParam(ctx.Request.Query["to"].FirstOrDefault());
    var evt = ctx.Request.Query["event"].FirstOrDefault();
    var kw = ctx.Request.Query["q"].FirstOrDefault();
    var rows = db.QueryLogsForExport(from, to, evt, kw);

    var sb = new StringBuilder();
    sb.Append('\uFEFF');
    sb.AppendLine(string.Join(',', new[] { "时间", "事件", "IP", "操作人", "设备", "方法", "路径", "结果", "详情" }.Select(EscapeCsv)));
    foreach (var r in rows)
    {
        sb.AppendLine(string.Join(',', new[]
        {
            r.Ts, EventLabel(r.Event), r.Ip, r.Actor, r.DeviceId, r.Method, r.Path,
            r.Status.ToString(CultureInfo.InvariantCulture), r.Detail
        }.Select(EscapeCsv)));
    }
    audit.Log(ctx, AuditLog.Events.ExportCsv,
        detail: $"导出操作日志 {rows.Count} 条", status: StatusCodes.Status200OK, actor: AuditActor());
    var filename = $"logs_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.csv";
    ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
    return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
});

/// <summary>
/// 设备列表 CSV 导出（需 Bearer Token）。
/// 查询参数：columns=逗号分隔列key（不传=全部）；from/to=最后上线时间范围（ISO，不传=全部设备）。
/// 列 key：online,deviceId,name,appVersion,configVersion,ipAddress,registeredAt,lastSeen,lastPhotoAt,photoSkipReason,lastWrongPinPhotoAt
/// </summary>
app.MapGet("/api/admin/devices/export.csv", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();

    var allColumns = new (string key, string header)[]
    {
        ("online", "在线状态"),
        ("deviceId", "设备识别码"),
        ("name", "名称"),
        ("appVersion", "APP版本"),
        ("configVersion", "配置版本"),
        ("ipAddress", "IP地址"),
        ("registeredAt", "注册时间"),
        ("lastSeen", "最后上线时间"),
        ("lastPhotoAt", "最近启动拍照"),
        ("photoSkipReason", "拍照跳过原因"),
        ("lastWrongPinPhotoAt", "最近输错拍照"),
    };

    var columnsParam = ctx.Request.Query["columns"].FirstOrDefault()?.Trim();
    var selected = string.IsNullOrWhiteSpace(columnsParam)
        ? allColumns
        : columnsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Join(allColumns, k => k, c => c.key, (k, c) => c)
            .ToArray();
    if (selected.Length == 0) selected = allColumns;

    // 时间筛选（按最后上线时间 lastSeen）
    var fromStr = ctx.Request.Query["from"].FirstOrDefault()?.Trim();
    var toStr = ctx.Request.Query["to"].FirstOrDefault()?.Trim();
    DateTimeOffset? fromDt = null, toDt = null;
    if (!string.IsNullOrWhiteSpace(fromStr) && DateTimeOffset.TryParse(fromStr, out var f)) fromDt = f;
    if (!string.IsNullOrWhiteSpace(toStr) && DateTimeOffset.TryParse(toStr, out var t)) toDt = t;

    // 在线判定阈值（与 devices 列表一致）
    var threshold = OnlineThreshold();

    var devices = db.ListDevices().Where(d =>
    {
        if (fromDt is null && toDt is null) return true;
        if (!DateTimeOffset.TryParse(d.LastSeen, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ls)) return false;
        if (fromDt is not null && ls < fromDt) return false;
        if (toDt is not null && ls > toDt) return false;
        return true;
    }).ToList();

    var sb = new StringBuilder();
    sb.Append('\uFEFF'); // UTF-8 BOM，Excel 正确识别中文
    sb.AppendLine(string.Join(',', selected.Select(c => EscapeCsv(c.header))));
    foreach (var d in devices)
    {
        var online = IsOnline(d.LastSeen, threshold) ? "在线" : "离线";
        var values = selected.Select(c => c.key switch
        {
            "online" => online,
            "deviceId" => d.DeviceId,
            "name" => d.Name ?? "",
            "appVersion" => d.AppVersion ?? "",
            "configVersion" => d.ConfigVersion ?? "",
            "ipAddress" => d.IpAddress ?? "",
            "registeredAt" => d.RegisteredAt ?? "",
            "lastSeen" => d.LastSeen ?? "",
            "lastPhotoAt" => d.LastPhotoAt ?? "",
            "photoSkipReason" => d.PhotoSkipReason ?? "",
            "lastWrongPinPhotoAt" => d.LastWrongPinPhotoAt ?? "",
            _ => ""
        });
        sb.AppendLine(string.Join(',', values.Select(EscapeCsv)));
    }

    var filename = $"devices_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.csv";
    ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
    audit.Log(ctx, AuditLog.Events.ExportCsv,
        detail: $"导出设备列表 CSV {devices.Count} 条", status: StatusCodes.Status200OK, actor: AuditActor());
    return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
});

static string EscapeCsv(string? s)
{
    if (s is null) return "";
    if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    return s;
}

/// <summary>管理后台查看设备照片（需 Bearer Token）。type=startup(默认)|wrong_pin；file=latest 取该类型最新一张，否则传精确文件名。</summary>
app.MapGet("/api/admin/photo", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var deviceId = ctx.Request.Query["deviceId"].FirstOrDefault()?.Trim() ?? "";
    var file = ctx.Request.Query["file"].FirstOrDefault()?.Trim() ?? "";
    var type = ctx.Request.Query["type"].FirstOrDefault()?.Trim() ?? "startup";
    if (type != "startup" && type != "wrong_pin") type = "startup";
    if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(file))
        return Results.BadRequest(new { error = "缺少 deviceId 或 file 参数" });
    // 白名单校验：仅允许 latest 或纯 [A-Za-z0-9._-] 的 .jpg 文件名，杜绝路径穿越
    if (file != "latest" && !Regex.IsMatch(file, "^[A-Za-z0-9._-]+\\.jpg$"))
        return Results.BadRequest(new { error = "非法文件名" });

    // 按 type 分子目录；startup 兼容 v0.5.0 旧照片（在设备根目录）
    var dir = Path.Combine(dataDir, "photos", deviceId, type);
    string? full;
    if (file == "latest")
    {
        full = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.jpg").OrderByDescending(f => f).FirstOrDefault()
            : null;
        // startup 类型回退旧目录（v0.5.0 照片直接放在设备根目录）
        if (full is null && type == "startup")
        {
            var legacyDir = Path.Combine(dataDir, "photos", deviceId);
            if (Directory.Exists(legacyDir))
                full = Directory.GetFiles(legacyDir, "*.jpg").OrderByDescending(f => f).FirstOrDefault();
        }
    }
    else
    {
        full = Path.Combine(dir, file);
    }
    if (full is null || !File.Exists(full)) return Results.NotFound(new { error = "照片不存在" });
    audit.Log(ctx, AuditLog.Events.ViewPhoto,
        detail: $"查看设备 {deviceId} 的{(type == "wrong_pin" ? "输错密码" : "启动")}照片：{Path.GetFileName(full)}",
        status: StatusCodes.Status200OK, actor: AuditActor(), deviceId: deviceId);
    return Results.File(full, "image/jpeg");
});

static bool IsOnline(string lastSeen, TimeSpan threshold)
{
    try
    {
        return DateTimeOffset.Now - DateTimeOffset.Parse(lastSeen, CultureInfo.InvariantCulture) < threshold;
    }
    catch
    {
        return false;
    }
}

app.MapGet("/api/admin/public-key", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    return Results.Ok(new { publicKey = db.GetSetting("rsa_public") });
});

// ============ 内置 HTML 页（v0.8.0） ============
// 管理员在后台编辑静态 HTML，平板把 http://<服务端>/h/<slug> 加进书签即可在受管 WebView 中打开。

/// <summary>
/// HTML 页列表（v0.8.2 瘦身：只回标题/大小/时间，不回正文）。
/// 正文由 GET /api/admin/html-pages/{id} 单页取——原来列表把每页正文（单页上限 512 KB）全带上，
/// 10 页就是 5 MB 响应，而首屏并发 8 个接口时纯属浪费。
/// </summary>
app.MapGet("/api/admin/html-pages", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/h/";
    return Results.Ok(db.ListHtmlPageSummaries().Select(p => new
    {
        id = p.Id, slug = p.Slug, title = p.Title, createdAt = p.CreatedAt, updatedAt = p.UpdatedAt,
        url = baseUrl + p.Slug,
        size = p.Size
    }));
});

/// <summary>单页 HTML 正文（v0.8.2，后台编辑器点击时按需拉取）。</summary>
app.MapGet("/api/admin/html-pages/{id:long}", (HttpContext ctx, long id) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var page = db.GetHtmlPageById(id);
    if (page is null) return Results.NotFound(new { error = "HTML 页不存在" });
    return Results.Ok(new { page.Id, page.Slug, page.Title, page.Html, page.CreatedAt, page.UpdatedAt });
});

/// <summary>新建或更新 HTML 页（id=0/缺省为新建）。</summary>
app.MapPost("/api/admin/html-pages", (HttpContext ctx, HtmlPageRequest req) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var slug = (req.Slug ?? "").Trim().ToLowerInvariant();
    if (!HtmlSlugRegex.IsMatch(slug))
        return Results.BadRequest(new { error = "地址片段不合法：仅小写字母/数字/短横线，1-64 位（如 notice-2026）" });
    var html = req.Html ?? "";
    if (html.Length > MaxHtmlChars)
        return Results.BadRequest(new { error = $"HTML 内容过大（上限 {MaxHtmlChars / 1024} KB）" });
    var title = string.IsNullOrWhiteSpace(req.Title) ? slug : req.Title.Trim();

    var existing = req.Id > 0 ? db.GetHtmlPageById(req.Id) : db.GetHtmlPage(slug);
    var newId = existing?.Id ?? 0;
    if (!db.SaveHtmlPage(newId, slug, title, html))
        return Results.BadRequest(new { error = $"地址片段已被占用：{slug}" });
    var saved = db.GetHtmlPage(slug)!;
    audit.Log(ctx, AuditLog.Events.HtmlSave,
        detail: $"保存 HTML 页 /h/{slug}（{title}，{Encoding.UTF8.GetByteCount(html)} 字节）",
        status: StatusCodes.Status200OK, actor: AuditActor());
    return Results.Ok(new { ok = true, id = saved.Id, slug = saved.Slug, url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/h/{saved.Slug}" });
});

app.MapDelete("/api/admin/html-pages/{id:long}", (HttpContext ctx, long id) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var page = db.GetHtmlPageById(id);
    if (page is null) return Results.NotFound(new { error = "HTML 页不存在" });
    db.DeleteHtmlPage(id);
    audit.Log(ctx, AuditLog.Events.HtmlDelete, detail: $"删除 HTML 页 /h/{page.Slug}（{page.Title}）",
        status: StatusCodes.Status200OK, actor: AuditActor());
    return Results.Ok(new { ok = true });
});

/// <summary>
/// HTML 页下发（公开）：平板 WebView 直接打开 http://<服务端>/h/<slug>。
/// 内容原样返回，仅补安全响应头（禁止被嗅探/被 iframe 嵌套）。
/// </summary>
app.MapGet("/h/{slug}", (HttpContext ctx, string slug) =>
{
    var key = (slug ?? "").Trim().ToLowerInvariant();
    if (!HtmlSlugRegex.IsMatch(key)) return Results.NotFound("页面不存在");
    var page = db.GetHtmlPage(key);
    if (page is null) return Results.NotFound("页面不存在");
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    // v0.8.2：原来强制 no-cache, must-revalidate = 平板每次进入书签都完整重下整页，
    // 现在用 updated_at 生成弱 ETag：没改就 304（几十字节），改了才回正文；再给 60 秒本地缓存。
    var etag = "\"h" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
        Encoding.UTF8.GetBytes($"{page.Id}:{page.UpdatedAt}:{page.Html.Length}"))).ToLowerInvariant() + "\"";
    ctx.Response.Headers.ETag = etag;
    ctx.Response.Headers["Cache-Control"] = "private, max-age=60";
    var ifNoneMatch = ctx.Request.Headers.IfNoneMatch.ToString();
    if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Contains(etag)) return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Text(page.Html, "text/html; charset=utf-8", Encoding.UTF8);
});

/// <summary>
/// 平板 6 位管理密码（v0.8.3）：存在 settings 表（键 admin_pin_hash），与配置版本完全解耦——发布/回滚配置都不会改动它。
/// GET 只回「是否已设置」，绝不回哈希（哈希只在服务端 compare，APP 永远拿不到）。
/// </summary>
app.MapGet("/api/admin/pin", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    return Results.Ok(new { pinSet = !string.IsNullOrWhiteSpace(db.GetSetting("admin_pin_hash")) });
});

/// <summary>修改平板管理密码（v0.8.3）：6 位数字，或留空表示保持不变。审计只记「已更新」，绝不记密码。</summary>
app.MapPost("/api/admin/pin", (HttpContext ctx, SetPinRequest req) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var pin = (req.Pin ?? "").Trim();
    var pinSet = !string.IsNullOrWhiteSpace(db.GetSetting("admin_pin_hash"));
    if (pin.Length == 0) return Results.Ok(new { ok = true, changed = false, pinSet });
    if (pin.Length != 6 || !pin.All(char.IsDigit))
        return Results.BadRequest(new { error = "管理密码必须是 6 位数字，或留空保持不变" });

    db.SetSetting("admin_pin_hash", PinHasher.HashPin(pin));
    audit.Log(ctx, AuditLog.Events.PinUpdate,
        detail: "更新平板管理密码（与配置版本解耦，发布/回滚不会改动）",
        status: StatusCodes.Status200OK, actor: AuditActor());
    return Results.Ok(new { ok = true, changed = true, pinSet = true });
});

// ============ APP 端接口（无需登录令牌） ============

/// <summary>拉取当前配置：正文即被签名的原始 JSON，版本与签名放在响应头，保证字节级可验签。</summary>
app.MapGet("/api/v1/config", async (HttpContext ctx) =>
{
    if (!AppUaValid(ctx))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("{\"error\":\"非法客户端\"}");
        return;
    }
    var row = db.GetCurrentVersion();
    if (row is null)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"error\":\"尚未发布配置\"}");
        return;
    }
    var deviceId = ctx.Request.Query["device_id"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(deviceId)) db.TouchDeviceSeen(deviceId!, row.Version);

    // M2：剥离 admin_pin_hash 后用私钥重签，再下发（版本号不变）
    // v0.8.2：同一版本只算一次「解析 + 剥正文 + RSA 重签 + UTF8 编码」，结果按版本号缓存。
    var (bytes, clientSignature) = configCache.GetClientConfig(row.Version, row.ConfigJson);
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    ctx.Response.Headers["X-Config-Version"] = row.Version;
    ctx.Response.Headers["X-Signature"] = clientSignature;
    ctx.Response.ContentLength = bytes.Length;
    await ctx.Response.Body.WriteAsync(bytes);
});

app.MapPost("/api/v1/devices/register", (HttpContext ctx, DeviceRequest req) =>
{
    if (!AppUaValid(ctx)) return Results.Json(new { error = "非法客户端" }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(req.DeviceId)) return Results.BadRequest(new { error = "缺少 device_id" });
    var id = req.DeviceId.Trim();
    db.UpsertDevice(id, req.Name, req.AppVersion, req.IpAddress);
    audit.Log(ctx, AuditLog.Events.DeviceRegister,
        detail: $"设备 {id} 注册：{req.Name}，APP {req.AppVersion}，上报 IP {req.IpAddress}",
        status: StatusCodes.Status200OK, deviceId: id);
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("register");

app.MapPost("/api/v1/devices/heartbeat", (HttpContext ctx, DeviceRequest req) =>
{
    if (!AppUaValid(ctx)) return Results.Json(new { error = "非法客户端" }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(req.DeviceId)) return Results.BadRequest(new { error = "缺少 device_id" });
    var id = req.DeviceId.Trim();
    // v0.8.2：合并成一条 UPSERT（原来 UpsertDevice + TouchDeviceSeen = 两次连接、两次事务、两次 fsync）
    db.TouchDeviceHeartbeat(id, req.Name, req.AppVersion, req.IpAddress, req.ConfigVersion);
    // 心跳量大：按「事件+设备」60 秒合并成一条，避免日志表被写爆
    audit.Log(ctx, AuditLog.Events.DeviceHeartbeat,
        detail: $"设备 {id} 心跳，IP {req.IpAddress}，配置版本 {req.ConfigVersion}",
        status: StatusCodes.Status200OK, deviceId: id, throttleKey: id);
    return Results.Ok(new { ok = true });
});

/// <summary>
/// 设备照片上报（multipart/form-data），按 type 分目录/限频/字段：
/// - type=startup（默认，兼容 v0.5.0）：启动前置拍照，5 分钟限频，记录 last_photo_at
/// - type=wrong_pin：输错密码拍照，1 分钟限频，记录 last_wrong_pin_photo_at
/// - 带 image：JPEG 校验大小/魔数/已注册后落盘 data/photos/{deviceId}/{type}/
/// - 带 skip_reason：仅 startup 记录跳过原因
/// </summary>
app.MapPost("/api/v1/photo", async (HttpContext ctx) =>
{
    if (!AppUaValid(ctx))
        return Results.Json(new { error = "非法客户端" }, statusCode: 403);
    var form = ctx.Request.Form;
    var deviceId = form["device_id"].ToString().Trim();
    var skipReason = form["skip_reason"].ToString().Trim();
    var type = form["type"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(type)) type = "startup";
    if (type != "startup" && type != "wrong_pin")
        return Results.BadRequest(new { error = "非法 type 参数" });
    if (string.IsNullOrWhiteSpace(deviceId))
        return Results.BadRequest(new { error = "缺少 device_id" });
    var device = db.GetDevice(deviceId);
    if (device is null)
        return Results.BadRequest(new { error = "设备未注册" });

    var now = DateTimeOffset.Now;

    var image = form.Files.FirstOrDefault();
    if (image is not null && image.Length > 0)
    {
        if (image.Length > MaxPhotoBytes)
            return Results.BadRequest(new { error = "图片过大（上限 2MB）" });

        await using var inStream = image.OpenReadStream();
        var head = new byte[3];
        var read = await inStream.ReadAsync(head.AsMemory(0, 3));
        if (read < 3 || head[0] != 0xFF || head[1] != 0xD8 || head[2] != 0xFF)
            return Results.BadRequest(new { error = "仅支持 JPEG 图片" });

        // 按 type 分目录：data/photos/{deviceId}/{type}/
        var dir = Path.Combine(dataDir, "photos", deviceId, type);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{now:yyyyMMdd_HHmmssfff}.jpg");
        await using var outStream = File.Create(file);
        await outStream.WriteAsync(head.AsMemory(0, 3)); // 魔数校验已消费前 3 字节，先补写回
        await inStream.CopyToAsync(outStream);

        if (type == "wrong_pin")
            db.SetWrongPinPhotoTaken(deviceId, now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        else
            db.SetPhotoTaken(deviceId, now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        CleanupOldPhotos(dir);
        return Results.Ok(new { ok = true });
    }

    if (!string.IsNullOrWhiteSpace(skipReason))
    {
        if (type == "startup")
            db.SetPhotoSkip(deviceId, skipReason);
        return Results.Ok(new { ok = true, skipped = skipReason });
    }

    return Results.BadRequest(new { error = "缺少照片文件或跳过原因" });
});

/// <summary>删除目录中超过保留天数的照片；清理失败不影响主流程。</summary>
static void CleanupOldPhotos(string dir)
{
    try
    {
        foreach (var f in Directory.GetFiles(dir, "*.jpg"))
        {
            var fi = new FileInfo(f);
            if (DateTimeOffset.Now - fi.LastWriteTime > TimeSpan.FromDays(PhotoRetentionDays))
                fi.Delete();
        }
    }
    catch
    {
        // 忽略清理异常
    }
}

/// <summary>
/// PIN 服务端校验（v0.7.0）：APP 发送 MD5(pin+盐) 哈希，服务端与 settings 表中的 admin_pin_hash 比对。
/// 每设备 60 秒窗口限 5 次（429）；device_id 必须已注册；UA 校验与其他 APP 接口一致。
/// </summary>
app.MapPost("/api/v1/verify-pin", (HttpContext ctx, VerifyPinRequest req) =>
{
    if (!AppUaValid(ctx)) return Results.Json(new { error = "非法客户端" }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(req.DeviceId) || string.IsNullOrWhiteSpace(req.PinHash))
        return Results.BadRequest(new { error = "缺少 device_id 或 pin_hash" });
    var deviceId = req.DeviceId.Trim();
    if (db.GetDevice(deviceId) is null)
        return Results.BadRequest(new { error = "设备未注册" });

    // 滑动窗口限频
    var nowTicks = DateTimeOffset.Now.ToUnixTimeSeconds();
    var q = verifyPinHits.GetOrAdd(deviceId, _ => new Queue<long>());
    lock (q)
    {
        while (q.Count > 0 && nowTicks - q.Peek() > VerifyPinWindowSeconds) q.Dequeue();
        if (q.Count >= VerifyPinMaxHits)
            return Results.Json(new { error = "尝试过于频繁，请稍后再试" }, statusCode: 429);
        q.Enqueue(nowTicks);
    }

    // v0.8.3：密码读 settings（键 admin_pin_hash），不再从配置 JSON 取
    var serverHash = db.GetSetting("admin_pin_hash");
    var ok = !string.IsNullOrWhiteSpace(serverHash) &&
             req.PinHash.Trim().Equals(serverHash, StringComparison.OrdinalIgnoreCase);
    // 只记录结果，绝不记录 PIN 或其哈希
    audit.Log(ctx, AuditLog.Events.PinVerify,
        detail: $"设备 {deviceId} 校验平板管理密码：{(ok ? "通过" : "不通过")}",
        status: StatusCodes.Status200OK, deviceId: deviceId);
    return Results.Ok(new { ok });
});

app.MapGet("/api/v1/public-key", (HttpContext ctx) =>
    AppUaValid(ctx)
        ? Results.Ok(new { publicKey = db.GetSetting("rsa_public") })
        : Results.Json(new { error = "非法客户端" }, statusCode: 403));

app.Run();
