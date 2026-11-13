using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RustCompanion.Core;

namespace RustCompanion.App.Services;

/// <summary>Секреты шифруются Windows DPAPI (привязка к учётной записи пользователя) и не попадают в базу, логи и экспорт.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _dir;
    public DpapiSecretStore(string dir) { _dir = dir; Directory.CreateDirectory(dir); }
    private string PathFor(string name) => Path.Combine(_dir, Uri.EscapeDataString(name) + ".bin");

    public string? Get(string name)
    {
        var p = PathFor(name);
        if (!File.Exists(p)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(p), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }

    public void Set(string name, string value) =>
        File.WriteAllBytes(PathFor(name), ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    public void Delete(string name) { var p = PathFor(name); if (File.Exists(p)) File.Delete(p); }
}
