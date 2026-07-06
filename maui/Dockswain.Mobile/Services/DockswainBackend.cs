using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dockswain.Mobile.Models;

namespace Dockswain.Mobile.Services;

public sealed partial class DockswainBackend(RemoteShell shell)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ServerRuntime> RefreshRuntimeAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        var containers = await ListContainersAsync(server, cancellationToken).ConfigureAwait(false);
        var stats = await StatsAsync(server, cancellationToken).ConfigureAwait(false);
        var version = "";
        try
        {
            version = (await shell.RunCheckedAsync(
                server,
                $"{shell.Docker(server)} version --format '{{{{.Server.Version}}}}'",
                cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch
        {
            // Version is decorative; list/status already proves Docker reachability.
        }

        return new ServerRuntime
        {
            DockerVersion = version,
            Containers = containers,
            StatsByContainerId = stats
        };
    }

    public async Task<List<DockerContainer>> ListContainersAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        var output = await RunDockerCheckedAsync(
            server,
            $"{shell.Docker(server)} ps -a --no-trunc --format '{{{{json .}}}}'",
            cancellationToken).ConfigureAwait(false);

        return ParseJsonLines<DockerContainer>(output)
            .OrderByDescending(c => c.IsRunning)
            .ThenBy(c => c.CleanName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<Dictionary<string, ContainerStat>> StatsAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        try
        {
            var output = await RunDockerCheckedAsync(
                server,
                $"{shell.Docker(server)} stats --no-stream --format '{{{{json .}}}}'",
                cancellationToken).ConfigureAwait(false);
            return ParseJsonLines<ContainerStat>(output)
                .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                .GroupBy(s => s.Id.Length <= 12 ? s.Id : s.Id[..12])
                .ToDictionary(g => g.Key, g => g.First());
        }
        catch
        {
            return [];
        }
    }

    public Task ContainerActionAsync(ServerProfile server, string action, string id, CancellationToken cancellationToken = default)
    {
        var dockerAction = action switch
        {
            "start" => "start",
            "stop" => "stop",
            "restart" => "restart",
            "remove" => "rm -f",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported container action.")
        };

        return RunDockerCheckedAsync(server, $"{shell.Docker(server)} {dockerAction} {RemoteShell.Quote(id)}", cancellationToken);
    }

    public async Task<string> LogsAsync(ServerProfile server, string id, int tail = 400, CancellationToken cancellationToken = default)
    {
        var safeTail = Math.Clamp(tail, 50, 5000);
        return await RunDockerCheckedAsync(
            server,
            $"{shell.Docker(server)} logs --tail {safeTail} {RemoteShell.Quote(id)} 2>&1",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExecAsync(ServerProfile server, string id, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            command = "sh -lc 'pwd && ls -la'";
        }

        return await RunDockerCheckedAsync(
            server,
            $"{shell.Docker(server)} exec {RemoteShell.Quote(id)} sh -lc {RemoteShell.Quote(command)} 2>&1",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ComposeProject>> ComposeProjectsAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        var output = await RunDockerCheckedAsync(
            server,
            $"{shell.Docker(server)} compose ls --format json",
            cancellationToken).ConfigureAwait(false);

        return ParseCompose(output)
            .OrderByDescending(p => p.IsRunning)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task ComposeActionAsync(ServerProfile server, ComposeProject project, string action, CancellationToken cancellationToken = default)
    {
        if (project.ConfigFiles.Length == 0)
        {
            throw new RemoteCommandException("compose_missing_files", "Docker did not report compose config files for this project.");
        }

        var files = string.Join(" ", project.ConfigFiles.Select(f => "-f " + RemoteShell.Quote(f)));
        var command = action switch
        {
            "up" => $"{shell.Docker(server)} compose {files} up -d",
            "down" => $"{shell.Docker(server)} compose {files} down",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported compose action.")
        };
        return RunDockerCheckedAsync(server, command, cancellationToken);
    }

    public async Task<DiskSnapshot> DiskAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        var root = (await shell.RunCheckedAsync(
            server,
            $"{shell.Docker(server)} info --format '{{{{.DockerRootDir}}}}' 2>/dev/null || printf /var/lib/docker",
            cancellationToken).ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            root = "/var/lib/docker";
        }

        var dfOutput = await shell.RunCheckedAsync(
            server,
            $"df -PB1 {RemoteShell.Quote(root)} | awk 'NR==2 {{print $2 \"\\t\" $3 \"\\t\" $4 \"\\t\" $5}}'",
            cancellationToken).ConfigureAwait(false);
        var parts = dfOutput.Trim().Split('\t');
        var disk = new DiskInfo
        {
            DockerRoot = root,
            Size = ParseLong(parts.ElementAtOrDefault(0)),
            Used = ParseLong(parts.ElementAtOrDefault(1)),
            Available = ParseLong(parts.ElementAtOrDefault(2)),
            UsePercent = parts.ElementAtOrDefault(3) ?? ""
        };

        var dockerDf = new List<DockerDfEntry>();
        try
        {
            var dfJson = await RunDockerCheckedAsync(
                server,
                $"{shell.Docker(server)} system df --format '{{{{json .}}}}'",
                cancellationToken).ConfigureAwait(false);
            dockerDf = ParseDockerDf(dfJson);
        }
        catch
        {
            // Older Docker builds may not support --format here. Disk info still loads.
        }

        var logs = await ContainerLogFilesAsync(server, cancellationToken).ConfigureAwait(false);
        return new DiskSnapshot { Disk = disk, Df = dockerDf, Logs = logs };
    }

    public async Task<string> PruneAsync(ServerProfile server, string what, CancellationToken cancellationToken = default)
    {
        var command = what switch
        {
            "build" => $"{shell.Docker(server)} builder prune -f",
            "images" => $"{shell.Docker(server)} image prune -f",
            "containers" => $"{shell.Docker(server)} container prune -f",
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "Unsupported prune target.")
        };

        return await RunDockerCheckedAsync(server, command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ContainerLogFile>> ContainerLogFilesAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        var inspect = await RunDockerCheckedAsync(
            server,
            $"ids=$({shell.Docker(server)} ps -aq --no-trunc); [ -z \"$ids\" ] || {shell.Docker(server)} inspect --format '{{{{.Id}}}}\\t{{{{.Name}}}}\\t{{{{.LogPath}}}}' $ids",
            cancellationToken).ConfigureAwait(false);

        var logs = new List<ContainerLogFile>();
        foreach (var line in inspect.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[2]))
            {
                continue;
            }

            var sizeText = await shell.RunCheckedAsync(
                server,
                $"{shell.Sudo(server)}stat -c %s {RemoteShell.Quote(parts[2])} 2>/dev/null || echo -1",
                cancellationToken).ConfigureAwait(false);
            logs.Add(new ContainerLogFile
            {
                Id = parts[0].Length <= 12 ? parts[0] : parts[0][..12],
                Name = parts[1].TrimStart('/'),
                Path = parts[2],
                Size = ParseLong(sizeText.Trim())
            });
        }

        return logs.OrderByDescending(l => l.Size).ToList();
    }

    public async Task TruncateLogAsync(ServerProfile server, ContainerLogFile log, CancellationToken cancellationToken = default)
    {
        await shell.RunCheckedAsync(
            server,
            $"{shell.Sudo(server)}truncate -s 0 {RemoteShell.Quote(log.Path)}",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(ServerProfile server, string path, CancellationToken cancellationToken = default)
    {
        return shell.ListDirectoryAsync(server, path, cancellationToken);
    }

    public async Task<string> HomeDirectoryAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        return (await shell.RunCheckedAsync(server, "printf %s \"$HOME\"", cancellationToken).ConfigureAwait(false)).Trim();
    }

    public Task CreateDirectoryAsync(ServerProfile server, string path, CancellationToken cancellationToken = default)
    {
        return shell.CreateDirectoryAsync(server, path, cancellationToken);
    }

    public Task RenameFileAsync(ServerProfile server, string from, string to, CancellationToken cancellationToken = default)
    {
        return shell.RenameAsync(server, from, to, cancellationToken);
    }

    public Task DeleteFileAsync(ServerProfile server, string path, bool recursive, CancellationToken cancellationToken = default)
    {
        return shell.DeleteAsync(server, path, recursive, cancellationToken);
    }

    public Task<string> ReadFileAsync(ServerProfile server, string path, bool privileged = false, CancellationToken cancellationToken = default)
    {
        return privileged || server.UseSudo
            ? shell.RunCheckedAsync(server, $"{shell.Sudo(server)}cat -- {RemoteShell.Quote(path)}", cancellationToken)
            : shell.ReadTextAsync(server, path, cancellationToken);
    }

    public Task WriteFileAsync(ServerProfile server, string path, string content, bool privileged = false, CancellationToken cancellationToken = default)
    {
        if (!privileged && !server.UseSudo)
        {
            return shell.WriteTextAsync(server, path, content, cancellationToken);
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        return shell.RunCheckedAsync(
            server,
            $"printf %s {RemoteShell.Quote(encoded)} | base64 -d | {shell.Sudo(server)}tee {RemoteShell.Quote(path)} >/dev/null",
            cancellationToken);
    }

    public Task UploadAsync(ServerProfile server, string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        return shell.UploadAsync(server, localPath, remotePath, cancellationToken);
    }

    public Task DownloadAsync(ServerProfile server, string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        return shell.DownloadAsync(server, remotePath, localPath, cancellationToken);
    }

    public async Task<NginxSnapshot> NginxAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        var output = await shell.RunCheckedAsync(server, BuildNginxListCommand(server), cancellationToken).ConfigureAwait(false);
        if (output.Contains("@@ERR@@ no_nginx_dir", StringComparison.Ordinal))
        {
            throw new RemoteCommandException("no_nginx_dir");
        }

        var snapshot = new NginxSnapshot { Directory = NginxDir(server) };
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('\t');
            if (parts.Length == 0)
            {
                continue;
            }

            if (parts[0] == "SITE" && parts.Length >= 6)
            {
                snapshot.Sites.Add(new NginxSite
                {
                    Name = parts[1],
                    Path = parts[2],
                    Enabled = parts[3] == "1",
                    Tls = parts[4] == "1",
                    ServerName = parts[5]
                });
            }
            else if (parts[0] == "CONFD" && parts.Length >= 5)
            {
                snapshot.ConfdFiles.Add(new NginxConfdFile
                {
                    Name = parts[1],
                    Path = parts[2],
                    Enabled = parts[3] == "1",
                    Size = ParseLong(parts[4])
                });
            }
        }

        snapshot.Certificates = await CertbotListAsync(server, cancellationToken).ConfigureAwait(false);
        snapshot.Sites = snapshot.Sites.OrderByDescending(s => s.Enabled).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        snapshot.ConfdFiles = snapshot.ConfdFiles.OrderByDescending(s => s.Enabled).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return snapshot;
    }

    public Task ToggleNginxSiteAsync(ServerProfile server, NginxSite site, CancellationToken cancellationToken = default)
    {
        return shell.RunCheckedAsync(server, BuildNginxToggleCommand(server, site.Name, site.Enabled ? "disable" : "enable", site.Path), cancellationToken);
    }

    public Task NewNginxSiteAsync(ServerProfile server, string name, string config, CancellationToken cancellationToken = default)
    {
        EnsureSafeFileName(name);
        var pathCommand = $"d={RemoteShell.Quote(NginxDir(server))}; if [ -d \"$d/sites-available\" ]; then printf %s \"$d/sites-available/{name}\"; else printf %s \"$d/conf.d/{name}\"; fi";
        return WriteGeneratedNginxFileAsync(server, pathCommand, config, enableSite: true, cancellationToken);
    }

    public Task NewConfdFileAsync(ServerProfile server, string name, string config, CancellationToken cancellationToken = default)
    {
        EnsureSafeFileName(name);
        var pathCommand = $"d={RemoteShell.Quote(NginxDir(server))}; printf %s \"$d/conf.d/{name}\"";
        return WriteGeneratedNginxFileAsync(server, pathCommand, config, enableSite: false, cancellationToken);
    }

    public Task ToggleConfdAsync(ServerProfile server, NginxConfdFile file, CancellationToken cancellationToken = default)
    {
        var target = file.Path;
        var command = file.Enabled
            ? $"{shell.Sudo(server)}mv -- {RemoteShell.Quote(target)} {RemoteShell.Quote(target + ".disabled")}"
            : $"{shell.Sudo(server)}mv -- {RemoteShell.Quote(target)} {RemoteShell.Quote(target.EndsWith(".disabled", StringComparison.Ordinal) ? target[..^9] : target.Replace(".conf.disabled", ".conf", StringComparison.Ordinal))}";
        return shell.RunCheckedAsync(server, command, cancellationToken);
    }

    public Task DeleteConfdAsync(ServerProfile server, NginxConfdFile file, CancellationToken cancellationToken = default)
    {
        return shell.RunCheckedAsync(server, $"{shell.Sudo(server)}rm -f -- {RemoteShell.Quote(file.Path)}", cancellationToken);
    }

    public async Task<(bool Pass, string Output)> NginxTestAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        var result = await shell.RunAsync(server, $"{shell.Sudo(server)}nginx -t 2>&1", cancellationToken).ConfigureAwait(false);
        return (result.ExitStatus == 0, result.Combined.Trim());
    }

    public Task NginxReloadAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        return shell.RunCheckedAsync(server, $"{shell.Sudo(server)}nginx -s reload", cancellationToken);
    }

    public async Task<List<CertbotCertificate>> CertbotListAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        try
        {
            var output = await shell.RunCheckedAsync(server, $"{shell.Sudo(server)}certbot certificates 2>&1", cancellationToken).ConfigureAwait(false);
            return ParseCertbot(output);
        }
        catch
        {
            return [];
        }
    }

    public Task<string> CertbotIssueAsync(ServerProfile server, string domains, bool redirect, CancellationToken cancellationToken = default)
    {
        var domainArgs = domains
            .Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => "-d " + RemoteShell.Quote(d));
        var redirectFlag = redirect ? "--redirect" : "";
        return shell.RunCheckedAsync(
            server,
            $"{shell.Sudo(server)}certbot --nginx -n --agree-tos --register-unsafely-without-email {redirectFlag} {string.Join(" ", domainArgs)} 2>&1",
            cancellationToken);
    }

    public static string BuildReverseProxyConfig(string domains, string target)
    {
        var serverName = string.Join(" ", domains.Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return $$"""
server {
    listen 80;
    listen [::]:80;
    server_name {{serverName}};

    location / {
        proxy_pass {{target}};
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
""";
    }

    public static string BuildStaticSiteConfig(string domains, string root)
    {
        var serverName = string.Join(" ", domains.Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return $$"""
server {
    listen 80;
    listen [::]:80;
    server_name {{serverName}};
    root {{root}};
    index index.html index.htm;

    location / {
        try_files $uri $uri/ =404;
    }
}
""";
    }

    private async Task<string> RunDockerCheckedAsync(ServerProfile server, string command, CancellationToken cancellationToken)
    {
        try
        {
            return await shell.RunCheckedAsync(server, command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw RemoteExceptionMapper.Map(ex);
        }
    }

    private async Task WriteGeneratedNginxFileAsync(ServerProfile server, string remotePathCommand, string config, bool enableSite, CancellationToken cancellationToken)
    {
        var remotePath = (await shell.RunCheckedAsync(server, remotePathCommand, cancellationToken).ConfigureAwait(false)).Trim();
        await WriteFileAsync(server, remotePath, config, privileged: true, cancellationToken).ConfigureAwait(false);

        if (enableSite && remotePath.Contains("/sites-available/", StringComparison.Ordinal))
        {
            var name = RemoteShell.FileNameRemote(remotePath);
            await shell.RunCheckedAsync(
                server,
                $"d={RemoteShell.Quote(NginxDir(server))}; {shell.Sudo(server)}ln -sfn \"../sites-available/{name}\" \"$d/sites-enabled/{name}\"",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private string BuildNginxListCommand(ServerProfile server)
    {
        var dir = RemoteShell.Quote(NginxDir(server));
        var sudo = shell.Sudo(server);
        return $$"""
d={{dir}}
[ -d "$d" ] || { echo '@@ERR@@ no_nginx_dir'; exit 0; }
list_site() {
  f="$1"; [ -f "$f" ] || return 0
  name="$(basename "$f")"; enabled=1
  case "$f" in
    */sites-available/*) [ -e "$d/sites-enabled/$name" ] && enabled=1 || enabled=0 ;;
    *.disabled) enabled=0; name="${name%.disabled}" ;;
  esac
  tls=0; {{sudo}}grep -Eq 'ssl_certificate|listen[[:space:]][^;]*ssl' "$f" 2>/dev/null && tls=1
  sn="$({{sudo}}grep -E '^[[:space:]]*server_name[[:space:]]+' "$f" 2>/dev/null | head -1 | sed -E 's/^[[:space:]]*server_name[[:space:]]+//;s/;.*$//')"
  printf 'SITE\t%s\t%s\t%s\t%s\t%s\n' "$name" "$f" "$enabled" "$tls" "$sn"
}
if [ -d "$d/sites-available" ]; then
  for f in "$d/sites-available"/*; do list_site "$f"; done
else
  for f in "$d/conf.d"/*.conf "$d/conf.d"/*.conf.disabled; do list_site "$f"; done
fi
if [ -d "$d/conf.d" ]; then
  for f in "$d/conf.d"/*.conf "$d/conf.d"/*.conf.disabled; do
    [ -f "$f" ] || continue
    {{sudo}}grep -Eq '^[[:space:]]*server[[:space:]]*\{' "$f" 2>/dev/null && continue
    name="$(basename "$f")"; enabled=1
    case "$name" in *.disabled) enabled=0; name="${name%.disabled}" ;; esac
    size="$({{sudo}}stat -c %s "$f" 2>/dev/null || echo -1)"
    printf 'CONFD\t%s\t%s\t%s\t%s\n' "$name" "$f" "$enabled" "$size"
  done
fi
""";
    }

    private string BuildNginxToggleCommand(ServerProfile server, string name, string action, string path)
    {
        EnsureSafeFileName(name);
        var dir = RemoteShell.Quote(NginxDir(server));
        var sudo = shell.Sudo(server);
        if (path.Contains("/sites-available/", StringComparison.Ordinal))
        {
            return action == "enable"
                ? $"d={dir}; {sudo}ln -sfn \"../sites-available/{name}\" \"$d/sites-enabled/{name}\""
                : $"d={dir}; {sudo}rm -f -- \"$d/sites-enabled/{name}\"";
        }

        var disabled = path.EndsWith(".disabled", StringComparison.Ordinal) ? path : path + ".disabled";
        var enabled = disabled.EndsWith(".disabled", StringComparison.Ordinal) ? disabled[..^9] : path;
        return action == "enable"
            ? $"{sudo}mv -- {RemoteShell.Quote(disabled)} {RemoteShell.Quote(enabled)}"
            : $"{sudo}mv -- {RemoteShell.Quote(enabled)} {RemoteShell.Quote(disabled)}";
    }

    private static List<T> ParseJsonLines<T>(string output)
    {
        var items = new List<T>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, JsonOptions);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            catch
            {
                // Docker can occasionally emit a partial line while containers churn.
            }
        }

        return items;
    }

    private static List<ComposeProject> ParseCompose(string output)
    {
        output = output.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var projects = new List<ComposeProject>();
        if (output.StartsWith("[", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(output);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                projects.Add(ComposeFromElement(element));
            }
        }
        else
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var document = JsonDocument.Parse(line);
                projects.Add(ComposeFromElement(document.RootElement));
            }
        }

        return projects;
    }

    private static ComposeProject ComposeFromElement(JsonElement element)
    {
        return new ComposeProject
        {
            Name = GetJsonString(element, "Name"),
            Status = GetJsonString(element, "Status"),
            ConfigFilesRaw = GetJsonValue(element, "ConfigFiles") switch
            {
                { ValueKind: JsonValueKind.Array } files => string.Join(",", files.EnumerateArray().Select(f => f.GetString()).Where(v => !string.IsNullOrWhiteSpace(v))),
                { ValueKind: JsonValueKind.String } files => files.GetString() ?? "",
                _ => ""
            }
        };
    }

    private static List<DockerDfEntry> ParseDockerDf(string output)
    {
        var list = new List<DockerDfEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            list.Add(new DockerDfEntry
            {
                Type = GetJsonString(root, "Type"),
                Total = GetJsonString(root, "TotalCount", "Count", "Total"),
                Active = GetJsonString(root, "Active"),
                Size = GetJsonString(root, "Size"),
                Reclaimable = GetJsonString(root, "Reclaimable")
            });
        }

        return list;
    }

    private static List<CertbotCertificate> ParseCertbot(string output)
    {
        var certs = new List<CertbotCertificate>();
        CertbotCertificate? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Certificate Name:", StringComparison.OrdinalIgnoreCase))
            {
                current = new CertbotCertificate { Name = line["Certificate Name:".Length..].Trim() };
                certs.Add(current);
            }
            else if (current is not null && line.StartsWith("Domains:", StringComparison.OrdinalIgnoreCase))
            {
                current.Domains = line["Domains:".Length..].Trim();
            }
            else if (current is not null && line.StartsWith("Expiry Date:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["Expiry Date:".Length..].Trim();
                current.Expiry = value;
                var open = value.IndexOf('(', StringComparison.Ordinal);
                var close = value.IndexOf(')', StringComparison.Ordinal);
                if (open >= 0 && close > open)
                {
                    current.Valid = value[(open + 1)..close];
                }
            }
        }

        return certs;
    }

    private static JsonElement GetJsonValue(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) ? value : default;
    }

    private static string GetJsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.ToString()
            };
        }

        return "";
    }

    private static long ParseLong(string? value)
    {
        return long.TryParse(value?.Trim(), out var parsed) ? parsed : 0;
    }

    private static void EnsureSafeFileName(string name)
    {
        if (!SafeFileNameRegex().IsMatch(name))
        {
            throw new RemoteCommandException("bad_name", "Use letters, numbers, dots, dashes, and underscores only.");
        }
    }

    private static string NginxDir(ServerProfile server)
    {
        return string.IsNullOrWhiteSpace(server.NginxDirectory) ? "/etc/nginx" : server.NginxDirectory.TrimEnd('/');
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex SafeFileNameRegex();
}
