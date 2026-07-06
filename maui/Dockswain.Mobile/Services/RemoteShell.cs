using System.Net.Sockets;
using System.Text;
using Dockswain.Mobile.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Dockswain.Mobile.Services;

public sealed class RemoteCommandException(string reason, string detail = "") : Exception(ToMessage(reason, detail))
{
    public string Reason { get; } = reason;
    public string Detail { get; } = detail;

    private static string ToMessage(string reason, string detail)
    {
        var message = reason switch
        {
            "no_password" => "No password is stored for this server.",
            "no_private_key" => "No private key is stored for this server.",
            "ssh_auth" => "SSH authentication failed.",
            "ssh_connect" => "Could not connect to the server.",
            "docker_down" => "Docker is not running on the server.",
            "docker_permission" => "The SSH user cannot access the Docker socket.",
            "docker_missing" => "docker is not installed or is not in PATH.",
            "sudo_password" => "sudo needs a password. Connect as root or configure NOPASSWD for the requested commands.",
            "permission" => "Permission denied.",
            "not_found" => "File or directory not found.",
            "bad_name" => "Invalid file name.",
            _ => reason.Replace('_', ' ')
        };

        return string.IsNullOrWhiteSpace(detail) ? message : $"{message}\n{detail.Trim()}";
    }
}

public sealed record RemoteCommandResult(int ExitStatus, string Output, string Error)
{
    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : $"{Output}\n{Error}";
}

public sealed class RemoteShell(SettingsStore settings)
{
    public async Task<RemoteCommandResult> RunAsync(ServerProfile server, string command, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = await CreateSshClientAsync(server).ConfigureAwait(false);
            client.Connect();
            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(5, server.ConnectTimeoutSeconds) + 45);
            var output = cmd.Execute();
            return new RemoteCommandResult(cmd.ExitStatus ?? -1, output ?? "", cmd.Error ?? "");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RunCheckedAsync(ServerProfile server, string command, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(server, command, cancellationToken).ConfigureAwait(false);
        if (result.ExitStatus == 0)
        {
            return result.Output;
        }

        throw MapCommandFailure(result);
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(ServerProfile server, string path, CancellationToken cancellationToken = default)
    {
        return await WithSftpAsync(server, sftp =>
        {
            if (!sftp.Exists(path))
            {
                throw new RemoteCommandException("not_found");
            }

            return sftp.ListDirectory(path)
                .Where(e => e.Name is not "." and not "..")
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new RemoteFileEntry
                {
                    Name = e.Name,
                    Path = CombineRemote(path, e.Name),
                    Type = e.IsDirectory ? "dir" : e.IsSymbolicLink ? "link" : e.IsRegularFile ? "file" : "other",
                    Size = e.Attributes.Size,
                    Modified = e.Attributes.LastWriteTime
                })
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task CreateDirectoryAsync(ServerProfile server, string path, CancellationToken cancellationToken = default)
    {
        return WithSftpAsync(server, sftp =>
        {
            sftp.CreateDirectory(path);
            return true;
        }, cancellationToken);
    }

    public Task RenameAsync(ServerProfile server, string from, string to, CancellationToken cancellationToken = default)
    {
        return WithSftpAsync(server, sftp =>
        {
            sftp.RenameFile(from, to);
            return true;
        }, cancellationToken);
    }

    public Task DeleteAsync(ServerProfile server, string path, bool recursive, CancellationToken cancellationToken = default)
    {
        return WithSftpAsync(server, sftp =>
        {
            DeleteEntry(sftp, path, recursive);
            return true;
        }, cancellationToken);
    }

    public Task<string> ReadTextAsync(ServerProfile server, string path, CancellationToken cancellationToken = default)
    {
        return WithSftpAsync(server, sftp =>
        {
            using var stream = sftp.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }, cancellationToken);
    }

    public Task WriteTextAsync(ServerProfile server, string path, string content, CancellationToken cancellationToken = default)
    {
        return WithSftpAsync(server, sftp =>
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            sftp.UploadFile(stream, path, true);
            return true;
        }, cancellationToken);
    }

    public Task UploadAsync(ServerProfile server, string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        return WithSftpAsync(server, sftp =>
        {
            using var stream = File.OpenRead(localPath);
            sftp.UploadFile(stream, remotePath, true);
            return true;
        }, cancellationToken);
    }

    public Task DownloadAsync(ServerProfile server, string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        return WithSftpAsync(server, sftp =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? FileSystem.CacheDirectory);
            using var stream = File.Create(localPath);
            sftp.DownloadFile(remotePath, stream);
            return true;
        }, cancellationToken);
    }

    public async Task<T> WithSftpAsync<T>(ServerProfile server, Func<SftpClient, T> work, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sftp = await CreateSftpClientAsync(server).ConfigureAwait(false);
            sftp.Connect();
            return work(sftp);
        }, cancellationToken).ConfigureAwait(false);
    }

    public string Docker(ServerProfile server)
    {
        return string.IsNullOrWhiteSpace(server.DockerCommand) ? "docker" : server.DockerCommand.Trim();
    }

    public string Sudo(ServerProfile server)
    {
        return server.UseSudo ? "sudo -n " : "";
    }

    public static string Quote(string value)
    {
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    public static string CombineRemote(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory == "/")
        {
            return "/" + name.TrimStart('/');
        }

        return directory.TrimEnd('/') + "/" + name.TrimStart('/');
    }

    public static string ParentRemote(string path)
    {
        var normalized = path.TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        if (index <= 0)
        {
            return "/";
        }

        return normalized[..index];
    }

    public static string FileNameRemote(string path)
    {
        return path.TrimEnd('/').Split('/').LastOrDefault() ?? path;
    }

    private async Task<SshClient> CreateSshClientAsync(ServerProfile server)
    {
        return new SshClient(await BuildConnectionInfoAsync(server).ConfigureAwait(false));
    }

    private async Task<SftpClient> CreateSftpClientAsync(ServerProfile server)
    {
        return new SftpClient(await BuildConnectionInfoAsync(server).ConfigureAwait(false));
    }

    private async Task<ConnectionInfo> BuildConnectionInfoAsync(ServerProfile server)
    {
        if (string.IsNullOrWhiteSpace(server.Host))
        {
            throw new RemoteCommandException("ssh_connect", "Host is empty.");
        }

        var user = string.IsNullOrWhiteSpace(server.User) ? "root" : server.User.Trim();
        AuthenticationMethod auth;
        if (server.AuthMode == ServerAuthMode.Password)
        {
            var password = await settings.GetPasswordAsync(server).ConfigureAwait(false);
            if (string.IsNullOrEmpty(password))
            {
                throw new RemoteCommandException("no_password");
            }

            auth = new PasswordAuthenticationMethod(user, password);
        }
        else
        {
            var key = await settings.GetPrivateKeyAsync(server).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new RemoteCommandException("no_private_key");
            }

            var passphrase = await settings.GetPrivateKeyPassphraseAsync(server).ConfigureAwait(false);
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(key));
            var privateKey = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(stream)
                : new PrivateKeyFile(stream, passphrase);
            auth = new PrivateKeyAuthenticationMethod(user, privateKey);
        }

        var connection = new ConnectionInfo(server.Host.Trim(), server.Port <= 0 ? 22 : server.Port, user, auth)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(3, server.ConnectTimeoutSeconds))
        };
        return connection;
    }

    private static RemoteCommandException MapCommandFailure(RemoteCommandResult result)
    {
        var combined = result.Combined;
        if (combined.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteCommandException("permission", combined);
        }

        if (combined.Contains("sudo:", StringComparison.OrdinalIgnoreCase)
            && (combined.Contains("password", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("terminal", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("tty", StringComparison.OrdinalIgnoreCase)))
        {
            return new RemoteCommandException("sudo_password", combined);
        }

        if (combined.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteCommandException("docker_down", combined);
        }

        if (combined.Contains("permission denied while trying to connect", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteCommandException("docker_permission", combined);
        }

        if (combined.Contains("docker: command not found", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteCommandException("docker_missing", combined);
        }

        return new RemoteCommandException("command_failed", combined);
    }

    private static void DeleteEntry(SftpClient sftp, string path, bool recursive)
    {
        var attrs = sftp.GetAttributes(path);
        if (!attrs.IsDirectory)
        {
            sftp.DeleteFile(path);
            return;
        }

        if (!recursive)
        {
            sftp.DeleteDirectory(path);
            return;
        }

        foreach (var entry in sftp.ListDirectory(path).Where(e => e.Name is not "." and not ".."))
        {
            DeleteEntry(sftp, entry.FullName, true);
        }

        sftp.DeleteDirectory(path);
    }
}

public static class RemoteExceptionMapper
{
    public static Exception Map(Exception exception)
    {
        return exception switch
        {
            RemoteCommandException => exception,
            SshAuthenticationException ex => new RemoteCommandException("ssh_auth", ex.Message),
            SshConnectionException ex => new RemoteCommandException("ssh_connect", ex.Message),
            SocketException ex => new RemoteCommandException("ssh_connect", ex.Message),
            SftpPathNotFoundException ex => new RemoteCommandException("not_found", ex.Message),
            SftpPermissionDeniedException ex => new RemoteCommandException("permission", ex.Message),
            _ => exception
        };
    }
}
