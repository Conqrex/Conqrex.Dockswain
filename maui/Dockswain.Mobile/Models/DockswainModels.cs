using System.Text.Json.Serialization;

namespace Dockswain.Mobile.Models;

public enum ServerAuthMode
{
    Password,
    PrivateKey
}

public sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string Host { get; set; } = "";
    public string User { get; set; } = "root";
    public int Port { get; set; } = 22;
    public ServerAuthMode AuthMode { get; set; } = ServerAuthMode.Password;
    public bool UseSudo { get; set; }
    public string DockerCommand { get; set; } = "docker";
    public string NginxDirectory { get; set; } = "/etc/nginx";
    public int ConnectTimeoutSeconds { get; set; } = 8;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Target : Label;

    [JsonIgnore]
    public string Target => string.IsNullOrWhiteSpace(User) ? Host : $"{User}@{Host}";
}

public sealed class DockerContainer
{
    [JsonPropertyName("ID")]
    public string Id { get; set; } = "";

    [JsonPropertyName("Names")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Image")]
    public string Image { get; set; } = "";

    [JsonPropertyName("State")]
    public string State { get; set; } = "";

    [JsonPropertyName("Status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("Ports")]
    public string Ports { get; set; } = "";

    [JsonPropertyName("Networks")]
    public string Networks { get; set; } = "";

    [JsonPropertyName("CreatedAt")]
    public string CreatedAt { get; set; } = "";

    [JsonIgnore]
    public string ShortId => Id.Length <= 12 ? Id : Id[..12];

    [JsonIgnore]
    public string CleanName => Name.TrimStart('/');

    [JsonIgnore]
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsLive => IsRunning
        || State.Equals("paused", StringComparison.OrdinalIgnoreCase)
        || State.Equals("restarting", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string PrimaryNetwork
    {
        get
        {
            var networks = Networks.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (networks.Length == 0)
            {
                return "-";
            }

            var nonSystem = networks.FirstOrDefault(n => n is not "bridge" and not "host" and not "none" and not "ingress" and not "docker_gwbridge");
            return string.IsNullOrWhiteSpace(nonSystem) ? networks[0] : nonSystem;
        }
    }
}

public sealed class ContainerStat
{
    [JsonPropertyName("ID")]
    public string Id { get; set; } = "";

    [JsonPropertyName("CPUPerc")]
    public string Cpu { get; set; } = "";

    [JsonPropertyName("MemPerc")]
    public string Memory { get; set; } = "";

    [JsonPropertyName("MemUsage")]
    public string MemoryUsage { get; set; } = "";
}

public sealed class ComposeProject
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("ConfigFiles")]
    public string ConfigFilesRaw { get; set; } = "";

    [JsonIgnore]
    public string[] ConfigFiles => ConfigFilesRaw
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    [JsonIgnore]
    public bool IsRunning => Status.Contains("running", StringComparison.OrdinalIgnoreCase);
}

public sealed class DiskInfo
{
    public long Size { get; set; }
    public long Used { get; set; }
    public long Available { get; set; }
    public string UsePercent { get; set; } = "";
    public string DockerRoot { get; set; } = "";
}

public sealed class DockerDfEntry
{
    public string Type { get; set; } = "";
    public string Total { get; set; } = "";
    public string Active { get; set; } = "";
    public string Size { get; set; } = "";
    public string Reclaimable { get; set; } = "";
}

public sealed class ContainerLogFile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Path { get; set; } = "";
}

public sealed class RemoteFileEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "file";
    public long Size { get; set; }
    public DateTimeOffset Modified { get; set; }

    [JsonIgnore]
    public bool IsDirectory => Type == "dir";
}

public sealed class NginxSite
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Tls { get; set; }
    public string ServerName { get; set; } = "";
}

public sealed class NginxConfdFile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; }
    public long Size { get; set; }
}

public sealed class CertbotCertificate
{
    public string Name { get; set; } = "";
    public string Domains { get; set; } = "";
    public string Expiry { get; set; } = "";
    public string Valid { get; set; } = "";
}

public sealed class NginxSnapshot
{
    public string Directory { get; set; } = "/etc/nginx";
    public List<NginxSite> Sites { get; set; } = [];
    public List<NginxConfdFile> ConfdFiles { get; set; } = [];
    public List<CertbotCertificate> Certificates { get; set; } = [];
}

public sealed class DiskSnapshot
{
    public DiskInfo Disk { get; set; } = new();
    public List<DockerDfEntry> Df { get; set; } = [];
    public List<ContainerLogFile> Logs { get; set; } = [];
}

public sealed class ServerRuntime
{
    public string DockerVersion { get; set; } = "";
    public List<DockerContainer> Containers { get; set; } = [];
    public Dictionary<string, ContainerStat> StatsByContainerId { get; set; } = [];
}

public sealed class FleetContainerSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string ImageReference { get; set; } = "";
    public string ImageId { get; set; } = "";
    public string CurrentImageId { get; set; } = "";
    public bool ImageUpdate { get; set; }
    public bool ImagePinned { get; set; }
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public string Health { get; set; } = "";
    public int RestartCount { get; set; }
    public int ExitCode { get; set; }
    public string FinishedAt { get; set; } = "";
    public string Cpu { get; set; } = "";
    public string Memory { get; set; } = "";
    public string MemoryUsage { get; set; } = "";

    [JsonIgnore]
    public string ShortId => Id.Length <= 12 ? Id : Id[..12];
    [JsonIgnore]
    public string CleanName => Name.TrimStart('/');
    [JsonIgnore]
    public string StateValue => State.ToLowerInvariant();
    [JsonIgnore]
    public bool IsRunning => StateValue == "running";
    [JsonIgnore]
    public bool IsLive => StateValue is "running" or "restarting" or "paused";
    [JsonIgnore]
    public string HealthValue
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Health)) return Health.ToLowerInvariant();
            if (Status.Contains("unhealthy", StringComparison.OrdinalIgnoreCase)) return "unhealthy";
            if (Status.Contains("health: starting", StringComparison.OrdinalIgnoreCase)) return "starting";
            if (Status.Contains("healthy", StringComparison.OrdinalIgnoreCase)) return "healthy";
            return "";
        }
    }
    [JsonIgnore]
    public double CpuPercent => Percent(Cpu);
    [JsonIgnore]
    public double MemoryPercent => Percent(Memory);

    private static double Percent(string value) => double.TryParse(
        value.Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;
}

public sealed class FleetHostResources
{
    public int Cpus { get; set; }
    public double Load1 { get; set; }
    public long MemoryTotal { get; set; }
    public long MemoryUsed { get; set; }
    public double MemoryPercent { get; set; }
}

public sealed class FleetHostSnapshot
{
    public string ServerId { get; set; } = "";
    public string ServerLabel { get; set; } = "";
    public DateTimeOffset SampledAt { get; set; }
    public DateTimeOffset? MetaSampledAt { get; set; }
    public bool Reachable { get; set; }
    public bool DockerOk { get; set; }
    public string Reason { get; set; } = "";
    public string DockerVersion { get; set; } = "";
    public List<FleetContainerSnapshot> Containers { get; set; } = [];
    public FleetHostResources Resources { get; set; } = new();
    public DiskSnapshot? Disk { get; set; }
    public List<CertbotCertificate> Certificates { get; set; } = [];
}

public sealed class FleetEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "info";
    public string ServerId { get; set; } = "";
    public string ServerLabel { get; set; } = "";
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public int Count { get; set; } = 1;
}

public sealed class FleetIssue
{
    public string Severity { get; set; } = "warning";
    public string Kind { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerLabel { get; set; } = "";
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Feature { get; set; } = "";
}

public static class ByteFormatter
{
    public static string Human(long bytes)
    {
        if (bytes < 0)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }
}
