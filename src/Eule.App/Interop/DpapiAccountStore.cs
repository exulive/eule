using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eule.Core.Cloud;
using Serilog;

namespace Eule.App.Interop;

/// <summary>
/// Keeps the signed-in account in a file encrypted with Windows DPAPI for the current user:
/// the file is useless on another computer or under another Windows account.
/// </summary>
public sealed class DpapiAccountStore(string path) : IAccountStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Eule.Account.v1");

    public Account? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Account>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            Log.Warning(ex, "Hesap dosyası okunamadı, yeniden giriş gerekecek");
            Clear();
            return null;
        }
    }

    public void Save(Account account)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(account), Entropy, DataProtectionScope.CurrentUser);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, path, overwrite: true);
    }

    public void Clear()
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
