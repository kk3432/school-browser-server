using System.Security.Cryptography;
using System.Text;

namespace CampusBrowser.Server.Services;

/// <summary>Web 后台登录密码哈希（PBKDF2-SHA256），与平板端 6 位 PIN 的 MD5 不是一回事。</summary>
public static class AdminPassword
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 平板端 6 位管理密码：MD5(密码 + 固定盐)，小写十六进制。
/// 注意：6 位数字空间小，仅用于现场临时管理，APP 端需配合错误次数限制。
/// 盐值必须与 APP 端 SecurityConfig.PIN_SALT 完全一致。
/// </summary>
public static class PinHasher
{
    public const string Salt = "campus-browser-pin-v1";

    public static string HashPin(string pin)
    {
        var bytes = Encoding.UTF8.GetBytes(pin.Trim() + Salt);
        return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    }
}
