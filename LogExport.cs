using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace QarttzVPN;

/// <summary>
/// Шифрует лог клиента НАШИМ публичным ключом для отправки в поддержку. Расшифровать может только
/// держатель приватного ключа (мы, оффлайн, инструментом qartzlog). Формат байт-в-байт совпадает с
/// Android (LogCrypto) и qartzlog:
///   "QLOG1\n" | u16be(wkLen) | RSA-OAEP(SHA-256)(aesKey32) | nonce12 | AES-256-GCM(gzip(plaintext))
/// (в GCM сначала шифртекст, затем 16-байтный тег — как у Go crypto/cipher Seal).
/// </summary>
internal static class LogExport
{
    private const string Magic = "QLOG1\n";

    private static RSA PublicKey()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("qartz-log-public.pem"));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        var rsa = RSA.Create();
        rsa.ImportFromPem(r.ReadToEnd());
        return rsa;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    public static byte[] Seal(byte[] plain)
    {
        var aesKey = RandomNumberGenerator.GetBytes(32);
        byte[] wk;
        using (var rsa = PublicKey())
            wk = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var gz = Gzip(plain);
        var ct = new byte[gz.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(aesKey, 16))
            gcm.Encrypt(nonce, gz, ct, tag);

        using var outMs = new MemoryStream();
        outMs.Write(Encoding.ASCII.GetBytes(Magic));
        outMs.WriteByte((byte)((wk.Length >> 8) & 0xFF));
        outMs.WriteByte((byte)(wk.Length & 0xFF));
        outMs.Write(wk);
        outMs.Write(nonce);
        outMs.Write(ct);
        outMs.Write(tag);
        return outMs.ToArray();
    }

    /// <summary>Шифрует текст лога и сохраняет .enc на Рабочий стол; возвращает путь.</summary>
    public static string ExportToDesktop(string logText)
    {
        var blob = Seal(Encoding.UTF8.GetBytes(logText));
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(dir, $"qartz-log-{DateTime.Now:yyyyMMdd-HHmmss}.enc");
        File.WriteAllBytes(path, blob);
        return path;
    }
}
