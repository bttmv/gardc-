using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WifiManager;

public class Config
{
    public string Host { get; set; } = "192.168.88.1";
    public string Port { get; set; } = "8728";
    public string User { get; set; } = "admin";
    public string PasswordEnc { get; set; } = "";
    public string Shop { get; set; } = "شبكة الواي فاي";
    public string WanInterface { get; set; } = "ether1";
    public int CodeLen { get; set; } = 8;
    public string AppPassHash { get; set; } = "";

    [JsonIgnore] public string Password { get; set; } = "";

    static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WifiManagerCS", "config.json");

    public static Config Load()
    {
        try
        {
            var c = JsonSerializer.Deserialize<Config>(System.IO.File.ReadAllText(FilePath)) ?? new Config();
            if (!string.IsNullOrEmpty(c.PasswordEnc))
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(c.PasswordEnc), null, DataProtectionScope.CurrentUser);
                c.Password = Encoding.UTF8.GetString(raw);
            }
            return c;
        }
        catch { return new Config(); }
    }

    public void Save()
    {
        PasswordEnc = Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(Password), null, DataProtectionScope.CurrentUser));
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(this, opts));
    }

    public static string MakeHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(h);
    }

    public static bool VerifyHash(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var h = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, expected);
    }
}
