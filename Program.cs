using System.Text;
using System.Globalization;
using System.Text.Json;
using CampusBrowser.Server.Models;
using CampusBrowser.Server.Services;

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

bool IsAdmin(HttpContext ctx) => db.TokenValid(TokenFrom(ctx));

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
    bool hiddenEntryEnabled, bool blockScreenshot, List<AllowedAppItem>? allowedApps) => new()
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

IResult? ValidateConfigForm(string homeUrl, string? pin, List<BookmarkItem>? bookmarks)
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
    return null;
}

// ============ 初始化与登录 ============

app.MapGet("/api/setup/status", () => Results.Ok(new { needsSetup = !db.AdminExists() }));

app.MapPost("/api/setup", (SetupRequest req) =>
{
    if (db.AdminExists()) return Results.BadRequest(new { error = "系统已初始化" });
    if (string.IsNullOrWhiteSpace(req.Username) || req.Password.Length < 6)
        return Results.BadRequest(new { error = "管理员账号必填，密码至少 6 位" });
    var invalid = ValidateConfigForm(req.HomeUrl, req.AdminPin, req.Bookmarks);
    if (invalid is not null) return invalid;

    db.SetSetting("admin_user", req.Username.Trim());
    db.SetSetting("admin_pwd_hash", AdminPassword.Hash(req.Password));

    var config = BuildConfig(req.Title, req.HomeUrl, req.FallbackUrl, req.Mode, req.Rules,
        req.Bookmarks, req.UpdateIntervalSeconds, req.Kiosk,
        string.IsNullOrWhiteSpace(req.AdminPin) ? null : PinHasher.HashPin(req.AdminPin),
        req.HiddenEntryEnabled, req.BlockScreenshot, req.AllowedApps);
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
    if (user != req.Username.Trim() || !AdminPassword.Verify(req.Password, hash))
        return Results.Unauthorized();
    var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    db.InsertToken(token);
    return Results.Ok(new { token });
});

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
    var invalid = ValidateConfigForm(req.HomeUrl, req.AdminPin, req.Bookmarks);
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
        req.HiddenEntryEnabled, req.BlockScreenshot, req.AllowedApps);
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
        d.RegisteredAt, d.LastSeen,
        online = IsOnline(d.LastSeen, threshold)
    }));
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

// ============ APP 端接口（无需登录令牌） ============

/// <summary>拉取当前配置：正文即被签名的原始 JSON，版本与签名放在响应头，保证字节级可验签。</summary>
app.MapGet("/api/v1/config", async (HttpContext ctx) =>
{
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

    var bytes = Encoding.UTF8.GetBytes(row.ConfigJson);
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    ctx.Response.Headers["X-Config-Version"] = row.Version;
    ctx.Response.Headers["X-Signature"] = row.Signature;
    ctx.Response.ContentLength = bytes.Length;
    await ctx.Response.Body.WriteAsync(bytes);
});

app.MapPost("/api/v1/devices/register", (DeviceRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.DeviceId)) return Results.BadRequest(new { error = "缺少 device_id" });
    db.UpsertDevice(req.DeviceId.Trim(), req.Name, req.AppVersion, req.IpAddress);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/v1/devices/heartbeat", (DeviceRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.DeviceId)) return Results.BadRequest(new { error = "缺少 device_id" });
    db.UpsertDevice(req.DeviceId.Trim(), req.Name, req.AppVersion, req.IpAddress);
    db.TouchDeviceSeen(req.DeviceId.Trim(), req.ConfigVersion);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/v1/public-key", () => Results.Ok(new { publicKey = db.GetSetting("rsa_public") }));

app.Run();
