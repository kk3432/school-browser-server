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
const int PhotoRateSeconds = 300;               // 启动拍照：每设备 5 分钟内仅收 1 张（防刷）
const int WrongPinPhotoRateSeconds = 60;        // 输错密码拍照：每设备 1 分钟内仅收 1 张
const int PhotoRetentionDays = 7;               // 照片保留天数，上传时惰性清理（按天清理策略）

// PIN 服务端校验限频（v0.7.0）：每设备 60 秒窗口内最多 5 次，防暴力枚举
const int VerifyPinWindowSeconds = 60;
const int VerifyPinMaxHits = 5;

// 管理后台登录限频（v0.7.1，M1）：每 IP 60 秒滑动窗口最多 5 次
const int LoginWindowSeconds = 60;
const int LoginMaxHits = 5;

// 登录令牌有效期（v0.7.1，L3）：6 小时，每次有效请求滑动续期
const long TokenTtlSeconds = 6 * 60 * 60;

// APP 接口 UA 校验（v0.7.0）：合法 UA 必须包含的标记
const string AppUaMarker = "OkHttp/";

// verify-pin 滑动窗口计数：deviceId -> 窗口内请求时间戳队列
var verifyPinHits = new ConcurrentDictionary<string, Queue<long>>();

var builder = WebApplication.CreateBuilder(args);

// 注册为 Windows 服务（Linux 下自动忽略）；数据目录固定在程序目录，避免服务模式下落到 System32。
builder.Host.UseWindowsService(o => o.ServiceName = "CampusBrowserServer");
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
builder.Services.AddSingleton(_ => new Database(dataDir));

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
builder.WebHost.UseKestrel(o => o.AddServerHeader = false);

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
});

var app = builder.Build();

var db = app.Services.GetRequiredService<Database>();
db.Init();

// 首次启动生成签名密钥对（私钥不出服务器）。
if (string.IsNullOrEmpty(db.GetSetting("rsa_private")))
{
    var (priv, pub) = Signer.GenerateKeyPair();
    db.SetSetting("rsa_private", priv);
    db.SetSetting("rsa_public", pub);
}

app.UseDefaultFiles();
app.UseStaticFiles();
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

/// <summary>
/// M2（v0.7.1）：下发给 APP 前从配置 JSON 中剥离 admin_pin_hash。
/// 版本库里仍保留该字段（供 verify-pin 读取、回滚跟随）；剥离改变了字节，
/// 必须用私钥对剥离后的内容重新签名，APP 端 RSA 验签才能通过。
/// </summary>
(string Json, string Signature) StripPinForClient(string configJson)
{
    var obj = JsonNode.Parse(configJson)!.AsObject();
    obj.Remove("admin_pin_hash");
    var stripped = obj.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
    var priv = db.GetSetting("rsa_private") ?? "";
    var signature = Signer.Sign(priv, Encoding.UTF8.GetBytes(stripped));
    return (stripped, signature);
}

/// <summary>
/// APP 接口 UA 校验（v0.7.0）：后台开关 ua_check_enabled 开启时，请求 UA 必须含 OkHttp/，否则 403。
/// 开关默认关闭，等所有终端升级到 v0.7.0 后再由管理员在后台开启。
/// </summary>
bool AppUaValid(HttpContext ctx)
{
    var enabled = db.GetSetting("ua_check_enabled") == "true";
    if (!enabled) return true;
    return ctx.Request.Headers.UserAgent.ToString().Contains(AppUaMarker, StringComparison.Ordinal);
}

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

AppConfig BuildConfig(string title, string homeUrl, string fallbackUrl, string mode, string rules,
    List<BookmarkItem>? bookmarks, int interval, bool kiosk, string? pinHash,
    bool hiddenEntryEnabled, bool blockScreenshot, bool requireStartupPhoto,
    List<AllowedAppItem>? allowedApps) => new()
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
    AdminPinHash = pinHash ?? ""
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

app.MapPost("/api/setup", (SetupRequest req) =>
{
    if (db.AdminExists()) return Results.BadRequest(new { error = "系统已初始化" });
    if (string.IsNullOrWhiteSpace(req.Username) || req.Password.Length < 6)
        return Results.BadRequest(new { error = "管理员账号必填，密码至少 6 位" });
    var invalid = ValidateConfigForm(req.HomeUrl, req.AdminPin, req.Bookmarks, req.Rules);
    if (invalid is not null) return invalid;

    db.SetSetting("admin_user", req.Username.Trim());
    db.SetSetting("admin_pwd_hash", AdminPassword.Hash(req.Password));

    var config = BuildConfig(req.Title, req.HomeUrl, req.FallbackUrl, req.Mode, req.Rules,
        req.Bookmarks, req.UpdateIntervalSeconds, req.Kiosk,
        string.IsNullOrWhiteSpace(req.AdminPin) ? null : PinHasher.HashPin(req.AdminPin),
        req.HiddenEntryEnabled, req.BlockScreenshot, req.RequireStartupPhoto, req.AllowedApps);
    config.Version = "1.0.0";
    var (json, signature) = SerializeAndSign(config);
    var id = db.InsertVersion("1.0.0", json, signature, "初始化配置");
    db.SetCurrent(id);
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
    db.DeleteToken(TokenFrom(ctx));
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
    var invalid = ValidateConfigForm(req.HomeUrl, req.AdminPin, req.Bookmarks, req.Rules);
    if (invalid is not null) return invalid;

    var current = db.GetCurrentVersion();
    string? pinHash = null;
    if (current is not null)
    {
        using var doc = JsonDocument.Parse(current.ConfigJson);
        if (doc.RootElement.TryGetProperty("admin_pin_hash", out var h)) pinHash = h.GetString();
    }
    if (!string.IsNullOrWhiteSpace(req.AdminPin)) pinHash = PinHasher.HashPin(req.AdminPin);

    var version = NextVersion();
    var config = BuildConfig(req.Title, req.HomeUrl, req.FallbackUrl, req.Mode, req.Rules,
        req.Bookmarks, req.UpdateIntervalSeconds, req.Kiosk, pinHash,
        req.HiddenEntryEnabled, req.BlockScreenshot, req.RequireStartupPhoto, req.AllowedApps);
    config.Version = version;
    var (json, signature) = SerializeAndSign(config);
    var id = db.InsertVersion(version, json, signature, string.IsNullOrWhiteSpace(req.Note) ? "后台发布" : req.Note.Trim());
    db.SetCurrent(id);
    return Results.Ok(new { ok = true, version });
});

app.MapGet("/api/admin/config/versions", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    return Results.Ok(db.ListVersions().Select(v => new
    {
        v.Version, v.PublishedAt, v.Note, current = v.Version == db.GetCurrentVersion()?.Version
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
    return Results.Ok(new { ok = true, version = newVersion });
});

app.MapGet("/api/admin/devices", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    // 在线判定阈值：轮询间隔的 2 倍，至少 120 秒
    var interval = 300;
    var currentJson = db.GetCurrentVersion()?.ConfigJson;
    if (currentJson is not null)
    {
        using var doc = JsonDocument.Parse(currentJson);
        if (doc.RootElement.TryGetProperty("update_interval_seconds", out var iv))
            interval = iv.GetInt32();
    }
    var threshold = TimeSpan.FromSeconds(Math.Max(interval * 2, 120));
    return Results.Ok(db.ListDevices().Select(d => new
    {
        d.DeviceId, d.Name, d.AppVersion, d.ConfigVersion, d.IpAddress,
        d.RegisteredAt, d.LastSeen, d.LastPhotoAt, d.PhotoSkipReason, d.LastWrongPinPhotoAt,
        online = IsOnline(d.LastSeen, threshold)
    }));
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
    var interval = 300;
    var currentJson = db.GetCurrentVersion()?.ConfigJson;
    if (currentJson is not null)
    {
        using var doc = JsonDocument.Parse(currentJson);
        if (doc.RootElement.TryGetProperty("update_interval_seconds", out var iv))
            interval = iv.GetInt32();
    }
    var threshold = TimeSpan.FromSeconds(Math.Max(interval * 2, 120));

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

/// <summary>读取安全设置（UA 校验开关状态）。</summary>
app.MapGet("/api/admin/security", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    return Results.Ok(new { uaCheckEnabled = db.GetSetting("ua_check_enabled") == "true" });
});

/// <summary>更新安全设置（开启/关闭 APP 请求 UA 强制校验）。</summary>
app.MapPost("/api/admin/security", (HttpContext ctx, SecuritySettingsRequest req) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    db.SetSetting("ua_check_enabled", req.UaCheckEnabled ? "true" : "false");
    return Results.Ok(new { ok = true, uaCheckEnabled = req.UaCheckEnabled });
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
    var (clientJson, clientSignature) = StripPinForClient(row.ConfigJson);
    var bytes = Encoding.UTF8.GetBytes(clientJson);
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
    db.UpsertDevice(req.DeviceId.Trim(), req.Name, req.AppVersion, req.IpAddress);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/v1/devices/heartbeat", (HttpContext ctx, DeviceRequest req) =>
{
    if (!AppUaValid(ctx)) return Results.Json(new { error = "非法客户端" }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(req.DeviceId)) return Results.BadRequest(new { error = "缺少 device_id" });
    db.UpsertDevice(req.DeviceId.Trim(), req.Name, req.AppVersion, req.IpAddress);
    db.TouchDeviceSeen(req.DeviceId.Trim(), req.ConfigVersion);
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

    // 按 type 分别限频
    var lastPhotoStr = type == "wrong_pin" ? device.LastWrongPinPhotoAt : device.LastPhotoAt;
    var rateSeconds = type == "wrong_pin" ? WrongPinPhotoRateSeconds : PhotoRateSeconds;
    if (lastPhotoStr is not null &&
        DateTimeOffset.TryParse(lastPhotoStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var last) &&
        now - last < TimeSpan.FromSeconds(rateSeconds))
        return Results.Json(new { error = "上传过于频繁，请稍后再试" }, statusCode: 429);

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
/// PIN 服务端校验（v0.7.0）：APP 发送 MD5(pin+盐) 哈希，服务端比对当前配置的 admin_pin_hash。
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

    var currentJson = db.GetCurrentVersion()?.ConfigJson;
    string? serverHash = null;
    if (currentJson is not null)
    {
        using var doc = JsonDocument.Parse(currentJson);
        if (doc.RootElement.TryGetProperty("admin_pin_hash", out var h)) serverHash = h.GetString();
    }
    var ok = !string.IsNullOrWhiteSpace(serverHash) &&
             req.PinHash.Trim().Equals(serverHash, StringComparison.OrdinalIgnoreCase);
    return Results.Ok(new { ok });
});

app.MapGet("/api/v1/public-key", () => Results.Ok(new { publicKey = db.GetSetting("rsa_public") }));

app.Run();
