using System.Text.Json.Serialization;

namespace CampusBrowser.Server.Models;

/// <summary>
/// 下发给 APP 端的配置文件结构（字段名即线上 JSON 名称，勿随意改动）。
/// </summary>
public class AppConfig
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("config_id")]
    public string ConfigId { get; set; } = "campus-default";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("generated_at")]
    public string GeneratedAt { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "校园门户";

    [JsonPropertyName("home_url")]
    public string HomeUrl { get; set; } = "";

    [JsonPropertyName("fallback_url")]
    public string FallbackUrl { get; set; } = "";

    /// <summary>whitelist=只放行规则内网址；blacklist=默认全放行、仅拦截规则内网址。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "whitelist";

    /// <summary>规则，支持 *.example.edu.cn 域名通配、https://host/* 路径通配、精确网址。</summary>
    [JsonPropertyName("rules")]
    public List<string> Rules { get; set; } = new();

    /// <summary>教师快捷书签，APP 工具栏书签按钮中展示；白名单模式下书签网址自动放行。</summary>
    [JsonPropertyName("bookmarks")]
    public List<BookmarkItem> Bookmarks { get; set; } = new();

    [JsonPropertyName("update_interval_seconds")]
    public int UpdateIntervalSeconds { get; set; } = 300;

    [JsonPropertyName("kiosk")]
    public bool Kiosk { get; set; }

    /// <summary>是否允许平板打开隐藏管理入口（Logo 连点 5 次）；默认开启，关闭后仅能由服务器重新开启。</summary>
    [JsonPropertyName("hidden_entry_enabled")]
    public bool HiddenEntryEnabled { get; set; } = true;

    /// <summary>是否禁止截屏与录屏（FLAG_SECURE）；默认开启，由服务端控制，开启时 APP 全局强制。</summary>
    [JsonPropertyName("block_screenshot")]
    public bool BlockScreenshot { get; set; } = true;

    /// <summary>是否要求平板每次启动时用前置摄像头拍照并上传（v0.5.0 新增）；开启后未授权相机权限无法进入浏览器。</summary>
    [JsonPropertyName("require_startup_photo")]
    public bool RequireStartupPhoto { get; set; }

    /// <summary>允许从浏览器唤醒的其他应用白名单（WebView scheme 链接与工具栏应用按钮）。</summary>
    [JsonPropertyName("allowed_apps")]
    public List<AllowedAppItem> AllowedApps { get; set; } = new();

    /// <summary>6 位管理密码的 MD5(密码+盐) 小写十六进制；APP 本地比对。</summary>
    [JsonPropertyName("admin_pin_hash")]
    public string AdminPinHash { get; set; } = "";
}

/// <summary>允许唤醒的其他应用条目。</summary>
public class AllowedAppItem
{
    /// <summary>目标应用包名，如 com.example.learning。</summary>
    [JsonPropertyName("package")]
    public string Package { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>WebView 中触发该应用的自定义 scheme，如 learning://；可留空。</summary>
    [JsonPropertyName("scheme")]
    public string Scheme { get; set; } = "";
}

/// <summary>首次启动初始化向导请求。</summary>
public class SetupRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Title { get; set; } = "";
    public string HomeUrl { get; set; } = "";
    public string FallbackUrl { get; set; } = "";
    public string Mode { get; set; } = "whitelist";
    public string Rules { get; set; } = "";
    public List<BookmarkItem> Bookmarks { get; set; } = new();
    public int UpdateIntervalSeconds { get; set; } = 300;
    public bool Kiosk { get; set; }
    public bool HiddenEntryEnabled { get; set; } = true;
    public bool BlockScreenshot { get; set; } = true;
    public bool RequireStartupPhoto { get; set; }
    public List<AllowedAppItem> AllowedApps { get; set; } = new();
    public string AdminPin { get; set; } = "";
}

/// <summary>发布配置请求（Web 表单）。</summary>
public class PublishRequest
{
    public string Title { get; set; } = "";
    public string HomeUrl { get; set; } = "";
    public string FallbackUrl { get; set; } = "";
    public string Mode { get; set; } = "whitelist";
    public string Rules { get; set; } = "";
    public List<BookmarkItem> Bookmarks { get; set; } = new();
    public int UpdateIntervalSeconds { get; set; } = 300;
    public bool Kiosk { get; set; }
    public bool HiddenEntryEnabled { get; set; } = true;
    public bool BlockScreenshot { get; set; } = true;
    public bool RequireStartupPhoto { get; set; }
    public List<AllowedAppItem> AllowedApps { get; set; } = new();
    /// <summary>6 位管理密码；留空表示沿用原密码。</summary>
    public string AdminPin { get; set; } = "";
    public string Note { get; set; } = "";
}

public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class DeviceRequest
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string ConfigVersion { get; set; } = "";
    /// <summary>平板当前 IPv4 地址，随注册/心跳上报。</summary>
    public string IpAddress { get; set; } = "";
}

public record VersionRow(string Version, string ConfigJson, string Signature, string PublishedAt, string? Note);

public record DeviceRow(string DeviceId, string Name, string AppVersion, string ConfigVersion, string IpAddress, string RegisteredAt, string LastSeen, string? LastPhotoAt, string? PhotoSkipReason, string? LastWrongPinPhotoAt);

/// <summary>教师快捷书签名目。</summary>
public class BookmarkItem
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}
