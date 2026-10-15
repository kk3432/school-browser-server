using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CampusBrowser.Server.Services;

/// <summary>
/// 首页概览缓存（v0.8.2 读路径优化）。
///
/// 背景：前台首页默认 1 秒刷新 GET /api/admin/stats/overview，而采样点每 5 秒才落一个，
/// 改造前每次请求都要：查 devices 全表算在线数、把当天所有采样点（一天最多 17,280 个，日终约 760 KB JSON）
/// 从 SQLite 读出来、排序求间隔中位数、再整份序列化。1 秒一次 = 每秒重复做一遍全天工作。
///
/// 现在：把「在线数 + 全天采样点 + 中位数间隔」按采样周期（5 秒）缓存，
/// 并把整份响应 JSON 预先序列化成 byte[]。1 秒轮询里 4/5 次直接返回缓存字节（零查询零序列化）。
/// 前端（v0.8.2）改成增量拉取 ?since=&lt;最后一点的时间戳&gt; 后，每秒只回传新增的 0-2 个点。
/// </summary>
public sealed class OverviewCache
{
    /// <summary>缓存有效期：与采样间隔一致（采样间隔更小时取采样间隔）。</summary>
    private const int MaxTtlSeconds = 5;

    private readonly object _gate = new();
    private readonly List<Sample> _samples = new();
    private byte[] _fullJson = Array.Empty<byte>();
    private long _builtAtUnix;
    private int _ttlSeconds = MaxTtlSeconds;
    private int _keepDays = 7;
    private int _online;
    private int _total;

    public readonly record struct Sample(long Ts, int Online, int Total);

    /// <summary>命中缓存则直接复用，过期则用 [rebuild] 重新取数并序列化整份响应。</summary>
    public byte[] GetFullJson(long nowUnix, int sampleSeconds, int keepDays, Func<(int Online, int Total, int SampleSeconds, List<Sample> Samples)> rebuild)
    {
        lock (_gate)
        {
            _ttlSeconds = sampleSeconds > 0 ? sampleSeconds : MaxTtlSeconds;
            _keepDays = keepDays;
            if (_fullJson.Length > 0 && nowUnix - _builtAtUnix < _ttlSeconds)
            {
                Interlocked.Increment(ref DbStats.OverviewCacheHits);
                return _fullJson;
            }
            Interlocked.Increment(ref DbStats.OverviewCacheMisses);
            var (online, total, granularity, samples) = rebuild();
            _online = online;
            _total = total;
            _samples.Clear();
            _samples.AddRange(samples);
            _fullJson = BuildFull(nowUnix, online, total, granularity);
            _builtAtUnix = nowUnix;
            return _fullJson;
        }
    }

    /// <summary>
    /// 增量拉取：只回传 ts &gt; since 的采样点（前端追加到已有曲线上）。
    /// 指标（online/total/百分比）也一并回传，前端可以直接整包合并，不必保留上一次的字段。
    /// </summary>
    public string BuildDelta(long nowUnix, long since)
    {
        List<Sample> newPoints;
        int online, total, keepDays, sampleSeconds, dayStart;
        lock (_gate)
        {
            newPoints = _samples.Where(s => s.Ts > since).ToList();
            online = _online;
            total = _total;
            keepDays = _keepDays;
            sampleSeconds = _ttlSeconds;
            dayStart = DayStart();
        }
        var buffer = new ArrayBufferWriter<byte>(256 + newPoints.Count * 40);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("mode", "delta");
            w.WriteNumber("now", nowUnix);
            w.WriteNumber("since", since);
            w.WriteNumber("online", online);
            w.WriteNumber("offline", total - online);
            w.WriteNumber("total", total);
            w.WriteNumber("percent", total == 0 ? 0 : Math.Round(online * 100.0 / total, 1));
            w.WriteNumber("todayStart", dayStart);
            w.WriteString("dayStart", DateTimeOffset.Now.Date.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            w.WriteNumber("sampleSeconds", sampleSeconds);
            w.WriteNumber("keepDays", keepDays);
            w.WriteStartArray("samples");
            foreach (var s in newPoints)
            {
                w.WriteStartObject();
                w.WriteNumber("ts", s.Ts);
                w.WriteNumber("online", s.Online);
                w.WriteNumber("total", s.Total);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static int DayStart() =>
        (int)new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset).ToUnixTimeSeconds();

    /// <summary>整份响应：字段与原 /api/admin/stats/overview 完全一致（前端无需改动也能用）。</summary>
    private byte[] BuildFull(long nowUnix, int online, int total, int sampleSeconds)
    {
        var dayStart = new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset).ToUnixTimeSeconds();
        var buffer = new ArrayBufferWriter<byte>(1024 + _samples.Count * 40);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("now", nowUnix);
            w.WriteNumber("online", online);
            w.WriteNumber("offline", total - online);
            w.WriteNumber("total", total);
            w.WriteNumber("percent", total == 0 ? 0 : Math.Round(online * 100.0 / total, 1));
            w.WriteNumber("todayStart", dayStart);
            w.WriteString("dayStart", DateTimeOffset.Now.Date.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            w.WriteNumber("sampleSeconds", sampleSeconds);
            w.WriteNumber("keepDays", _keepDays);
            w.WriteStartArray("samples");
            foreach (var s in _samples)
            {
                w.WriteStartObject();
                w.WriteNumber("ts", s.Ts);
                w.WriteNumber("online", s.Online);
                w.WriteNumber("total", s.Total);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
