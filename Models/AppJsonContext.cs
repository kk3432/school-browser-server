using System.Text.Json.Serialization;

namespace CampusBrowser.Server.Models;

/// <summary>
/// System.Text.Json source generator 上下文。
/// 发布时开启 PublishTrimmed 后，反射式 JSON 序列化可能被裁剪误伤；
/// 使用 source generator 为以下类型预生成序列化元数据，确保裁剪安全。
/// </summary>
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(List<BookmarkItem>))]
[JsonSerializable(typeof(List<AllowedAppItem>))]
[JsonSerializable(typeof(SetupRequest))]
[JsonSerializable(typeof(PublishRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(VerifyPinRequest))]
[JsonSerializable(typeof(SecuritySettingsRequest))]
[JsonSerializable(typeof(DeviceRequest))]
[JsonSerializable(typeof(VersionRow))]
[JsonSerializable(typeof(DeviceRow))]
[JsonSerializable(typeof(Dictionary<string, object>))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
