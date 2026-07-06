using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dockswain.Mobile.Models;

namespace Dockswain.Mobile.Services;

public sealed class MobileImportService(SettingsStore settings)
{
    private const string PayloadType = "com.conqrex.dockswain.servers";
    private const string CompactPrefix = "DSWAIN1:";
    private const string ParserVersion = "Dockswain Mobile 1.0.4 QR parser";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<MobileImportResult> ImportAsync(string raw, IList<ServerProfile> existing)
    {
        var payload = Parse(raw);
        var added = 0;
        var updated = 0;
        var secrets = 0;

        foreach (var incoming in payload.Servers.Where(s => !string.IsNullOrWhiteSpace(s.Host)))
        {
            var match = existing.FirstOrDefault(s =>
                s.Host.Equals(incoming.Host, StringComparison.OrdinalIgnoreCase)
                && s.Port == (incoming.Port <= 0 ? 22 : incoming.Port)
                && s.User.Equals(incoming.User ?? "", StringComparison.OrdinalIgnoreCase));

            var profile = match ?? new ServerProfile();
            if (match is null)
            {
                added++;
                existing.Add(profile);
            }
            else
            {
                updated++;
            }

            profile.Label = incoming.Label ?? "";
            profile.Host = incoming.Host.Trim();
            profile.User = string.IsNullOrWhiteSpace(incoming.User) ? "root" : incoming.User.Trim();
            profile.Port = incoming.Port <= 0 ? 22 : incoming.Port;
            profile.AuthMode = ParseAuth(incoming.Auth);
            profile.UseSudo = incoming.UseSudo;
            profile.DockerCommand = string.IsNullOrWhiteSpace(incoming.DockerCommand) ? "docker" : incoming.DockerCommand.Trim();
            profile.NginxDirectory = string.IsNullOrWhiteSpace(incoming.NginxDirectory) ? "/etc/nginx" : incoming.NginxDirectory.Trim();
            profile.ConnectTimeoutSeconds = incoming.ConnectTimeoutSeconds <= 0 ? 8 : Math.Clamp(incoming.ConnectTimeoutSeconds, 3, 60);

            if (!string.IsNullOrEmpty(incoming.Password))
            {
                await settings.SetPasswordAsync(profile, incoming.Password);
                await settings.SetPrivateKeyAsync(profile, "");
                await settings.SetPrivateKeyPassphraseAsync(profile, "");
                secrets++;
            }

            if (!string.IsNullOrEmpty(incoming.PrivateKey))
            {
                await settings.SetPrivateKeyAsync(profile, incoming.PrivateKey);
                await settings.SetPrivateKeyPassphraseAsync(profile, incoming.PrivateKeyPassphrase ?? "");
                await settings.SetPasswordAsync(profile, "");
                secrets++;
            }
        }

        return new MobileImportResult(added, updated, secrets);
    }

    public static MobileImportPayload Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidDataException("QR code is empty.");
        }

        raw = UnwrapJsonString(raw.Trim());
        string json;
        try
        {
            json = DecodeImportText(raw);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"{ParserVersion}: QR payload is not valid DSWAIN1 base64. Raw: {Preview(raw)}", ex);
        }

        var payload = DeserializePayload(json);
        if (payload.Servers.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(payload.Type))
            {
                payload.Type = PayloadType;
            }

            if (payload.Version == 0)
            {
                payload.Version = 1;
            }

            return payload;
        }

        throw new InvalidDataException($"{ParserVersion}: QR payload did not contain any Dockswain servers. Decoded: {Preview(json)}");
    }

    public static bool LooksLikeImportText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        raw = UnwrapJsonString(raw.Trim());
        return raw.StartsWith(CompactPrefix, StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("dockswain://", StringComparison.OrdinalIgnoreCase)
            || raw.Contains(PayloadType, StringComparison.Ordinal)
            || raw.Contains("\"t\":\"dsw\"", StringComparison.Ordinal)
            || raw.Contains("\"payload\"", StringComparison.Ordinal);
    }

    private static string DecodeImportText(string raw)
    {
        if (TryExtractDataParameter(raw, out var data))
        {
            return DecodeBase64Url(data);
        }

        if (raw.StartsWith(CompactPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return DecodeBase64Url(raw[CompactPrefix.Length..]);
        }

        return raw;
    }

    private static MobileImportPayload DeserializePayload(string json)
    {
        json = UnwrapJsonString(json.Trim());
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && TryGetProperty(root, "payload", out var nestedPayload)
                && nestedPayload.ValueKind == JsonValueKind.String)
            {
                return Parse(nestedPayload.GetString() ?? "");
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                var servers = JsonSerializer.Deserialize<List<MobileImportServer>>(json, JsonOptions) ?? [];
                return new MobileImportPayload
                {
                    Type = PayloadType,
                    Version = 1,
                    Servers = servers
                };
            }

            if (root.ValueKind == JsonValueKind.Object
                && (TryGetProperty(root, "s", out _) || TryGetProperty(root, "t", out _)))
            {
                return CompactPayload(root);
            }

            var payload = JsonSerializer.Deserialize<MobileImportPayload>(json, JsonOptions)
                ?? throw new InvalidDataException("QR code is not a Dockswain import payload.");

            if (string.IsNullOrWhiteSpace(payload.Type) && payload.Servers.Count > 0)
            {
                payload.Type = PayloadType;
                payload.Version = payload.Version == 0 ? 1 : payload.Version;
            }

            return payload;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{ParserVersion}: QR payload is not valid JSON after decoding. Decoded: {Preview(json)}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException($"{ParserVersion}: QR payload could not be deserialized. Decoded: {Preview(json)}", ex);
        }
    }

    private static bool TryExtractDataParameter(string raw, out string data)
    {
        data = "";
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals("dockswain", StringComparison.OrdinalIgnoreCase))
        {
            data = QueryValue(uri.Query, "data");
            if (!string.IsNullOrWhiteSpace(data))
            {
                return true;
            }
        }

        var question = raw.IndexOf('?', StringComparison.Ordinal);
        var query = question >= 0 ? raw[(question + 1)..] : raw;
        data = QueryValue(query, "data");
        return !string.IsNullOrWhiteSpace(data);
    }

    private static string QueryValue(string query, string key)
    {
        query = query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(part[..equals]);
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(part[(equals + 1)..]);
            }
        }

        return "";
    }

    private static string UnwrapJsonString(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value) ?? value;
            }
            catch
            {
                return value;
            }
        }

        return value;
    }

    private static string Preview(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 140 ? value : value[..140] + "...";
    }

    private static ServerAuthMode ParseAuth(string? auth)
    {
        return auth?.Equals("key", StringComparison.OrdinalIgnoreCase) == true
            || auth?.Equals("privatekey", StringComparison.OrdinalIgnoreCase) == true
            || auth?.Equals("private_key", StringComparison.OrdinalIgnoreCase) == true
            || auth?.Equals("k", StringComparison.OrdinalIgnoreCase) == true
            ? ServerAuthMode.PrivateKey
            : ServerAuthMode.Password;
    }

    private static MobileImportPayload CompactPayload(JsonElement root)
    {
        var version = JsonInt(root, "v", 1);
        var servers = new List<MobileImportServer>();
        if (TryGetProperty(root, "s", out var serverArray) && serverArray.ValueKind == JsonValueKind.Array)
        {
            servers.AddRange(serverArray.EnumerateArray().Select(CompactServer));
        }

        return new MobileImportPayload
        {
            Type = PayloadType,
            Version = version,
            Servers = servers
        };
    }

    private static MobileImportServer CompactServer(JsonElement element)
    {
        return new MobileImportServer
        {
            Label = JsonString(element, "l"),
            User = JsonString(element, "u"),
            Host = JsonString(element, "h"),
            Port = JsonInt(element, "p", 22),
            Auth = JsonString(element, "a"),
            UseSudo = JsonBool(element, "su"),
            DockerCommand = JsonString(element, "dc"),
            NginxDirectory = JsonString(element, "nd"),
            ConnectTimeoutSeconds = JsonInt(element, "to", 8),
            Password = JsonString(element, "pw"),
            PrivateKey = JsonString(element, "pk"),
            PrivateKeyPassphrase = JsonString(element, "kp")
        };
    }

    private static string JsonString(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static int JsonInt(JsonElement element, string name, int fallback)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
        {
            return n;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n) ? n : fallback;
    }

    private static bool JsonBool(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var b) && b,
            _ => false
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string DecodeBase64Url(string value)
    {
        value = value.Trim().ReplaceLineEndings("").Replace(" ", "");
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}

public sealed record MobileImportResult(int Added, int Updated, int Secrets);

public sealed class MobileImportPayload
{
    public string Type { get; set; } = "";
    public int Version { get; set; }
    public List<MobileImportServer> Servers { get; set; } = [];
}

public sealed class MobileImportServer
{
    public string? Label { get; set; }
    public string? User { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string? Auth { get; set; }
    public bool UseSudo { get; set; }
    public string? DockerCommand { get; set; }
    public string? NginxDirectory { get; set; }
    public int ConnectTimeoutSeconds { get; set; } = 8;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Password { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? PrivateKey { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? PrivateKeyPassphrase { get; set; }
}
