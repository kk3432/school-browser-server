using System.Security.Cryptography;

namespace CampusBrowser.Server.Services;

/// <summary>RSA-2048 + SHA256 配置签名。私钥仅存服务端，公钥可下发/内置到 APP。</summary>
public static class Signer
{
    public static (string PrivatePem, string PublicPem) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        var priv = PemEncoding.Write("PRIVATE KEY", rsa.ExportPkcs8PrivateKey());
        var pub = PemEncoding.Write("PUBLIC KEY", rsa.ExportSubjectPublicKeyInfo());
        return (new string(priv), new string(pub));
    }

    public static string Sign(string privatePem, byte[] data)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(DecodePem(privatePem, "PRIVATE KEY"), out _);
        var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(sig);
    }

    public static bool Verify(string publicPem, byte[] data, string signatureBase64)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(DecodePem(publicPem, "PUBLIC KEY"), out _);
            return rsa.VerifyData(data, Convert.FromBase64String(signatureBase64),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodePem(string pem, string label)
    {
        var base64 = string.Concat(pem
            .Split('\n')
            .Where(line => !line.StartsWith("-----")));
        return Convert.FromBase64String(base64.Trim());
    }
}
