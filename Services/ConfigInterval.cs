namespace CampusBrowser.Server.Services;

/// <summary>
/// 当前配置里的轮询间隔（秒）内存缓存（v0.8.2）。
///
/// 背景：update_interval_seconds 决定「设备多久算离线」（间隔 2 倍、下限 120 秒），
/// 用到它的地方有 /api/admin/devices、/api/admin/stats/overview、设备 CSV 导出、OnlineSampler。
/// 改造前每次都要 GetCurrentVersion() 把整份 config_json（含内置 HTML 正文，可达数百 KB）
/// 读出来再 JsonDocument.Parse 一遍——首页 1 秒刷新时等于每秒解析一次整份配置。
///
/// 现在：30 秒 TTL 缓存解析结果；管理员改了间隔最多 30 秒后生效（前端本地也会各自定时）。
/// </summary>
public sealed class ConfigInterval
{
    private const int TtlSeconds = 30;
    private const int DefaultSeconds = 300;

    private readonly Database _db;
    private int _seconds = DefaultSeconds;
    private long _readAtUnix;

    public ConfigInterval(Database db) => _db = db;

    /// <summary>轮询间隔（秒）；读取失败或未发布配置时按 300。</summary>
    public int Seconds
    {
        get
        {
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (now - Interlocked.Read(ref _readAtUnix) < TtlSeconds) return _seconds;
            Interlocked.Exchange(ref _readAtUnix, now);
            var json = _db.GetCurrentVersion()?.ConfigJson;
            if (json is not null)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("update_interval_seconds", out var iv))
                    {
                        var value = iv.GetInt32();
                        if (value > 0) _seconds = value;
                    }
                }
                catch
                {
                    // 配置损坏时保留上一次的值
                }
            }
            return _seconds;
        }
    }

    /// <summary>发布/回滚配置后立即失效，不用等 TTL。</summary>
    public void Invalidate() => Interlocked.Exchange(ref _readAtUnix, 0);
}
