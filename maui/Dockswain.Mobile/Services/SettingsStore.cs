using System.Text.Json;
using Dockswain.Mobile.Models;

namespace Dockswain.Mobile.Services;

public sealed class SettingsStore
{
    private const string ServersKey = "dockswain.servers.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public Task<List<ServerProfile>> LoadServersAsync()
    {
        var json = Preferences.Default.Get(ServersKey, "[]");
        try
        {
            return Task.FromResult(JsonSerializer.Deserialize<List<ServerProfile>>(json, JsonOptions) ?? []);
        }
        catch
        {
            return Task.FromResult<List<ServerProfile>>([]);
        }
    }

    public Task SaveServersAsync(IReadOnlyCollection<ServerProfile> servers)
    {
        Preferences.Default.Set(ServersKey, JsonSerializer.Serialize(servers, JsonOptions));
        return Task.CompletedTask;
    }

    public Task<string> GetPasswordAsync(ServerProfile server)
    {
        return GetSecretAsync(SecretKey(server, "password"));
    }

    public Task SetPasswordAsync(ServerProfile server, string value)
    {
        return SetSecretAsync(SecretKey(server, "password"), value);
    }

    public Task<string> GetPrivateKeyAsync(ServerProfile server)
    {
        return GetSecretAsync(SecretKey(server, "private-key"));
    }

    public Task SetPrivateKeyAsync(ServerProfile server, string value)
    {
        return SetSecretAsync(SecretKey(server, "private-key"), value);
    }

    public Task<string> GetPrivateKeyPassphraseAsync(ServerProfile server)
    {
        return GetSecretAsync(SecretKey(server, "private-key-passphrase"));
    }

    public Task SetPrivateKeyPassphraseAsync(ServerProfile server, string value)
    {
        return SetSecretAsync(SecretKey(server, "private-key-passphrase"), value);
    }

    public Task DeleteSecretsAsync(ServerProfile server)
    {
        SecureStorage.Default.Remove(SecretKey(server, "password"));
        SecureStorage.Default.Remove(SecretKey(server, "private-key"));
        SecureStorage.Default.Remove(SecretKey(server, "private-key-passphrase"));
        return Task.CompletedTask;
    }

    private static string SecretKey(ServerProfile server, string kind)
    {
        return $"dockswain.{server.Id}.{kind}";
    }

    private static async Task<string> GetSecretAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key).ConfigureAwait(false) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static async Task SetSecretAsync(string key, string value)
    {
        try
        {
            if (string.IsNullOrEmpty(value))
            {
                SecureStorage.Default.Remove(key);
            }
            else
            {
                await SecureStorage.Default.SetAsync(key, value).ConfigureAwait(false);
            }
        }
        catch
        {
            // Some desktop debug targets may not have a secure store. Keep the
            // failure local so server metadata can still be edited.
        }
    }
}
