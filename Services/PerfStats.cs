using System.Collections.Concurrent;
using System.Diagnostics;

namespace CampusBrowser.Server.Services;

/// <summary>
/// 轻量性能打点（v0.8.2）：按路由累计请求数 / 平均与最大耗时 / 平均与最大响应字节 / 错误数，
/// 由 GET /api/admin/diag/perf 输出，并作为压测脚本（tools/perf_probe.ps1）判定预算阈值的依据。
///
/// 设计：纯内存计数、无锁（ConcurrentDictionary + Interlocked），单次请求只多一次字典查找；
/// 不做直方图/采样，避免为观测引入可观开销；reset 由调用方触发（压测脚本测量前清零）。
/// </summary>
public sealed class PerfStats
{
    /// <summary>单条路由的累计指标。时间单位毫秒，字节为响应体实际写出字节数。</summary>
    public sealed class RouteStat
    {
        private long _count, _totalMs, _maxMs, _bytes, _maxBytes, _errors;

        public void Add(double ms, long bytes, int status)
        {
            var msLong = (long)ms;
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _totalMs, msLong);
            Interlocked.Add(ref _bytes, bytes);
            UpdateMax(ref _maxMs, msLong);
            UpdateMax(ref _maxBytes, bytes);
            if (status >= 400) Interlocked.Increment(ref _errors);
        }

        private static void UpdateMax(ref long target, long value)
        {
            var current = Interlocked.Read(ref target);
            while (value > current)
            {
                var seen = Interlocked.CompareExchange(ref target, value, current);
                if (seen == current) return;
                current = seen;
            }
        }

        public long Count => Interlocked.Read(ref _count);
        public long MaxMs => Interlocked.Read(ref _maxMs);
        public long MaxBytes => Interlocked.Read(ref _maxBytes);
        public long Errors => Interlocked.Read(ref _errors);
        public double AvgMs => Count == 0 ? 0 : Math.Round((double)Interlocked.Read(ref _totalMs) / Count, 2);
        public long AvgBytes => Count == 0 ? 0 : Interlocked.Read(ref _bytes) / Count;
    }

    private readonly ConcurrentDictionary<string, RouteStat> _routes = new();
    private readonly long _startedAt = Environment.TickCount64;

    /// <summary>记录一次请求；[route] 形如 "GET /api/admin/stats/overview"。</summary>
    public void Record(string route, double ms, long bytes, int status) =>
        _routes.GetOrAdd(route, static _ => new RouteStat()).Add(ms, bytes, status);

    public IReadOnlyList<KeyValuePair<string, RouteStat>> Routes =>
        _routes.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

    public void Reset() => _routes.Clear();

    public long UptimeSeconds => (Environment.TickCount64 - _startedAt) / 1000;

    /// <summary>
    /// 路由键：方法 + 路径（不含查询串），避免 ?device_id= 之类把统计打散。
    /// 例外（v0.8.2）：/api/admin/stats/overview 的增量模式（?since=）是另一条路径，
    /// 响应体积差三个数量级，必须单独成键才能各自设预算。
    /// </summary>
    public static string KeyOf(HttpContext ctx) =>
        ctx.Request.Method + " " + ctx.Request.Path.Value +
        (ctx.Request.Query.ContainsKey("since") ? " (delta)" : "");
}

/// <summary>
/// 响应字节计数器：套在 Response.Body 外层统计实际写出的字节，不缓冲、不改动内容。
/// 仅用于 /api 下的接口（含大响应的 overview / 日志查询），以便量化「响应有多大」。
/// </summary>
public sealed class CountingStream : Stream
{
    private readonly Stream _inner;

    public CountingStream(Stream inner) => _inner = inner;

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        BytesWritten += count;
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        BytesWritten += buffer.Length;
        _inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        BytesWritten += count;
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BytesWritten += buffer.Length;
        return _inner.WriteAsync(buffer, cancellationToken);
    }
}

/// <summary>
/// SQLite 访问计数（v0.8.2，仅用于性能验收）：连接打开次数、显式事务数、写入语句数、令牌查询与续期写入数。
/// 这些数字是「写放大」的可观测证据：改造前每个已认证请求 1 连接 + 2 写事务，续期每次都落库。
/// </summary>
public static class DbStats
{
    public static long ConnOpens;
    public static long Transactions;
    public static long Writes;
    public static long TokenLookups;
    public static long TokenCacheHits;
    public static long TokenRenewWrites;

    /// <summary>审计日志批量落库的批次数（v0.8.2 的 InsertLogs 事务路径）。</summary>
    public static long LogBatches;

    /// <summary>首页概览缓存命中次数（1 秒刷新里 4/5 次应命中）。</summary>
    public static long OverviewCacheHits;

    /// <summary>首页概览缓存未命中、真正查库并序列化的次数。</summary>
    public static long OverviewCacheMisses;

    /// <summary>APP 配置客户端视图（剥离 PIN/HTML 正文 + 签名）命中缓存次数。</summary>
    public static long ConfigCacheHits;

    /// <summary>APP 配置客户端视图重新解析 + 重签名的次数。</summary>
    public static long ConfigCacheMisses;

    public static void Reset()
    {
        Interlocked.Exchange(ref ConnOpens, 0);
        Interlocked.Exchange(ref Transactions, 0);
        Interlocked.Exchange(ref Writes, 0);
        Interlocked.Exchange(ref TokenLookups, 0);
        Interlocked.Exchange(ref TokenCacheHits, 0);
        Interlocked.Exchange(ref TokenRenewWrites, 0);
        Interlocked.Exchange(ref LogBatches, 0);
        Interlocked.Exchange(ref OverviewCacheHits, 0);
        Interlocked.Exchange(ref OverviewCacheMisses, 0);
        Interlocked.Exchange(ref ConfigCacheHits, 0);
        Interlocked.Exchange(ref ConfigCacheMisses, 0);
    }
}
