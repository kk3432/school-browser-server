using System.Globalization;
using CampusBrowser.Server.Models;

namespace CampusBrowser.Server.Services;

/// <summary>
/// 在线采样后台服务（v0.8.0）：每 5 秒统计一次「在线设备数 / 设备总数」写入 online_samples，
/// 供后台首页画「今日 00:00 起」的在线折线图（前端 1 秒刷新，曲线每 5 秒前进一格）。
///
/// 在线判定阈值与 /api/admin/devices 保持一致：轮询间隔 2 倍、至少 120 秒。
/// </summary>
public class OnlineSampler : BackgroundService
{
    /// <summary>采样间隔（秒）。前端 1 秒刷新不会造成额外采样。</summary>
    public const int SampleSeconds = 5;

    /// <summary>在线判定下限（与 Program.cs /api/admin/devices 一致）。</summary>
    private const int MinThresholdSeconds = 120;

    private readonly Database _db;
    private readonly ILogger<OnlineSampler> _logger;

    public OnlineSampler(Database db, ILogger<OnlineSampler> logger)
    {
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先等一个周期，避免刚刚建库就采样（也让服务启动更快）
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SampleSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Sample();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "在线采样失败（不影响主流程）");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    /// <summary>立即采样一次并落库。</summary>
    public void Sample()
    {
        var threshold = TimeSpan.FromSeconds(Math.Max(CurrentInterval() * 2, MinThresholdSeconds));
        var devices = _db.ListDevices();
        var online = devices.Count(d => IsOnline(d.LastSeen, threshold));
        _db.InsertOnlineSample(DateTimeOffset.Now.ToUnixTimeSeconds(), online, devices.Count);
    }

    /// <summary>读当前配置的轮询间隔（秒），读不到按 300。</summary>
    private int CurrentInterval()
    {
        var json = _db.GetCurrentVersion()?.ConfigJson;
        if (json is null) return 300;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("update_interval_seconds", out var iv))
                return iv.GetInt32();
        }
        catch
        {
            // 配置损坏时按默认
        }
        return 300;
    }

    private static bool IsOnline(string lastSeen, TimeSpan threshold)
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
}
