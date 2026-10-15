using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CampusBrowser.Server.Services;

/// <summary>
/// APP 下发配置的客户端视图缓存（v0.8.2）。
///
/// 背景（改造前，每个 /api/v1/config 请求都做一遍）：
///   1. JsonNode.Parse 整份 config_json（含所有内置 HTML 页正文，单页上限 512 KB）
///   2. 删掉 admin_pin_hash、删掉 html_pages[].html
///   3. 重新序列化成紧凑 JSON
///   4. 读 rsa_private 私钥（一次 SQLite 查询）
///   5. RSA-2048 签名（每次重新 ImportPkcs8PrivateKey + 一次私钥运算）
/// 设备端每 5 分钟一轮、100 台设备 = 每天约 2.9 万次重复的纯计算。
///
/// 现在：按「配置版本号」缓存剥离后的字节 + 签名。版本号不可变（发布/回滚都会生成新版本号），
/// 所以缓存命中即等价；发布/回滚/初始化后由调用方 Invalidate()。
/// </summary>
public sealed class ConfigCache
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Database _db;
    private readonly object _gate = new();
    private string _version = "";
    private string _signature = "";
    private byte[] _body = Array.Empty<byte>();

    public ConfigCache(Database db) => _db = db;

    /// <summary>返回下发用的 UTF-8 字节与配套签名（同一版本号只计算一次）。</summary>
    public (byte[] Body, string Signature) GetClientConfig(string version, string configJson)
    {
        lock (_gate)
        {
            if (_body.Length > 0 && _version == version)
            {
                Interlocked.Increment(ref DbStats.ConfigCacheHits);
                return (_body, _signature);
            }
            Interlocked.Increment(ref DbStats.ConfigCacheMisses);
            var stripped = Strip(configJson);
            var priv = _db.GetSetting("rsa_private") ?? "";
            _signature = Signer.Sign(priv, Encoding.UTF8.GetBytes(stripped));
            _body = Encoding.UTF8.GetBytes(stripped);
            _version = version;
            return (_body, _signature);
        }
    }

    /// <summary>配置变更（发布/回滚/初始化）后调用。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _version = "";
            _body = Array.Empty<byte>();
            _signature = "";
        }
    }

    /// <summary>
    /// M2（v0.7.1）：下发给 APP 前剥离 admin_pin_hash；HTML 页只留 slug/title，正文走 /h/{slug}。
    /// 剥离改变了字节，必须用私钥对剥离后的内容重新签名，APP 端 RSA 验签才能通过。
    /// </summary>
    private static string Strip(string configJson)
    {
        var obj = JsonNode.Parse(configJson)!.AsObject();
        obj.Remove("admin_pin_hash");
        if (obj["html_pages"] is JsonArray pages)
        {
            foreach (var node in pages)
            {
                if (node is JsonObject page) page.Remove("html");
            }
        }
        return obj.ToJsonString(CompactJson);
    }
}
