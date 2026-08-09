using Dockswain.Mobile.Models;
using Dockswain.Mobile.Pages;
using Dockswain.Mobile.Services;
using Microsoft.Maui.Layouts;

namespace Dockswain.Mobile;

public partial class MainPage : ContentPage
{
    private enum Feature
    {
        Fleet,
        Containers,
        Compose,
        Disk,
        Files,
        Nginx,
        Settings
    }

    private readonly SettingsStore _settings = new();
    private readonly DockswainBackend _backend;
    private readonly MobileImportService _mobileImport;
    private readonly Picker _serverPicker = new() { Title = "Server" };
    private readonly ActivityIndicator _busy = new() { WidthRequest = 28, HeightRequest = 28 };
    private readonly Label _status = new() { FontSize = 12, TextColor = Theme.TextMuted, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly ContentView _content = new();
    private readonly HorizontalStackLayout _tabs = new() { Spacing = 8, Padding = new Thickness(12, 8) };

    private readonly Dictionary<Feature, Button> _tabButtons = [];
    private readonly IDispatcherTimer? _refreshTimer;
    private readonly IDispatcherTimer? _fleetTimer;

    private List<ServerProfile> _servers = [];
    private ServerProfile? _server;
    private Feature _feature = Feature.Fleet;
    private bool _loadingServers;
    private bool _runningOnly = true;
    private bool _groupByNetwork;
    private string _searchText = "";
    private string _remotePath = "";

    private ServerRuntime _runtime = new();
    private List<ComposeProject> _compose = [];
    private DiskSnapshot? _disk;
    private List<RemoteFileEntry> _files = [];
    private NginxSnapshot? _nginx;
    private Dictionary<string, FleetHostSnapshot> _fleetSnapshots = [];
    private List<FleetEvent> _fleetEvents = [];
    private bool _fleetLoaded;
    private bool _fleetRefreshing;
    private DateTimeOffset _lastFleetDeep = DateTimeOffset.MinValue;

    public MainPage()
    {
        InitializeComponent();
        _backend = new DockswainBackend(new RemoteShell(_settings));
        _mobileImport = new MobileImportService(_settings);
        BuildLayout();

        _refreshTimer = Dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(12);
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_feature == Feature.Containers && _server is not null && !_busy.IsRunning)
            {
                await SafeAsync(RefreshContainersAsync, showBusy: false);
            }
        };
        _fleetTimer = Dispatcher.CreateTimer();
        _fleetTimer.Interval = TimeSpan.FromSeconds(FleetRefreshSeconds);
        _fleetTimer.Tick += async (_, _) =>
        {
            if (!_fleetRefreshing) await SafeAsync(RefreshFleetAsync, showBusy: false);
        };

        Loaded += async (_, _) => await SafeAsync(LoadServersAsync);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _refreshTimer?.Start();
        _fleetTimer?.Start();
    }

    protected override void OnDisappearing()
    {
        _refreshTimer?.Stop();
        _fleetTimer?.Stop();
        base.OnDisappearing();
    }

    private void BuildLayout()
    {
        _serverPicker.SelectedIndexChanged += async (_, _) =>
        {
            if (_loadingServers || _serverPicker.SelectedItem is not ServerProfile selected)
            {
                return;
            }

            _server = selected;
            ResetFeatureData();
            await SafeAsync(RefreshCurrentFeatureAsync);
        };

        var add = HeaderButton("Add", async () => await OpenServerEditorAsync(null));
        var edit = HeaderButton("Edit", async () =>
        {
            if (_server is not null)
            {
                await OpenServerEditorAsync(_server);
            }
        });
        var refresh = HeaderButton("Refresh", async () => await RefreshCurrentFeatureAsync());

        var header = new Grid
        {
            Padding = new Thickness(12, 10, 12, 6),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Children =
            {
                _serverPicker,
                add.AtColumn(1),
                edit.AtColumn(2),
                refresh.AtColumn(3),
                _busy.AtColumn(4)
            }
        };

        foreach (var feature in Enum.GetValues<Feature>())
        {
            var button = new Button
            {
                Text = FeatureTitle(feature),
                Padding = new Thickness(12, 6),
                MinimumHeightRequest = 36,
                FontSize = 13
            };
            button.Clicked += async (_, _) =>
            {
                _feature = feature;
                SyncTabs();
                await SafeAsync(RefreshCurrentFeatureAsync);
            };
            _tabButtons[feature] = button;
            _tabs.Add(button);
        }

        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Children =
            {
                header,
                new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _tabs }.AtRow(1),
                _content.AtRow(2),
                new Border
                {
                    Padding = new Thickness(12, 7),
                    StrokeThickness = 0,
                    BackgroundColor = Theme.SurfaceAlt,
                    Content = _status
                }.AtRow(3)
            }
        };

        Content = root;
        SyncTabs();
    }

    private async Task LoadServersAsync()
    {
        if (!_fleetLoaded)
        {
            _fleetEvents = await _settings.LoadFleetEventsAsync();
            _fleetSnapshots = await _settings.LoadFleetSnapshotsAsync();
            _fleetLoaded = true;
        }
        _loadingServers = true;
        var previousId = _server?.Id;
        _servers = await _settings.LoadServersAsync();
        _serverPicker.ItemsSource = null;
        _serverPicker.ItemDisplayBinding = new Binding(nameof(ServerProfile.DisplayName));
        _serverPicker.ItemsSource = _servers;

        _server = _servers.FirstOrDefault(s => s.Id == previousId) ?? _servers.FirstOrDefault();
        _serverPicker.SelectedItem = _server;
        _loadingServers = false;

        if (_server is null)
        {
            _feature = Feature.Settings;
            SyncTabs();
            RenderSettings();
            SetStatus("Add a server to start controlling Docker over SSH.");
            return;
        }

        await RefreshCurrentFeatureAsync();
    }

    private async Task SaveServerAsync(ServerProfile profile)
    {
        var index = _servers.FindIndex(s => s.Id == profile.Id);
        if (index >= 0)
        {
            _servers[index] = profile;
        }
        else
        {
            _servers.Add(profile);
        }

        await _settings.SaveServersAsync(_servers);
        _server = profile;
        await LoadServersAsync();
    }

    private async Task DeleteServerAsync(ServerProfile profile)
    {
        var confirm = await this.ThemedConfirm("Remove server", $"Remove {profile.DisplayName} from Dockswain Mobile?", "Remove", "Cancel");
        if (!confirm)
        {
            return;
        }

        _servers.RemoveAll(s => s.Id == profile.Id);
        await _settings.DeleteSecretsAsync(profile);
        await _settings.SaveServersAsync(_servers);
        await LoadServersAsync();
    }

    private async Task OpenServerEditorAsync(ServerProfile? profile)
    {
        await Navigation.PushAsync(new ServerEditorPage(_settings, profile, SaveServerAsync));
    }

    private async Task RefreshCurrentFeatureAsync()
    {
        if (_feature == Feature.Settings)
        {
            RenderSettings();
            return;
        }

        if (_feature == Feature.Fleet)
        {
            await RefreshFleetAsync();
            return;
        }

        if (_server is null)
        {
            RenderEmpty("No server configured", "Open Settings and add an SSH target.");
            return;
        }

        switch (_feature)
        {
            case Feature.Containers:
                await RefreshContainersAsync();
                break;
            case Feature.Compose:
                _compose = await _backend.ComposeProjectsAsync(_server);
                RenderCompose();
                break;
            case Feature.Disk:
                _disk = await _backend.DiskAsync(_server);
                RenderDisk();
                break;
            case Feature.Files:
                await RefreshFilesAsync();
                break;
            case Feature.Nginx:
                _nginx = await _backend.NginxAsync(_server);
                RenderNginx();
                break;
            case Feature.Settings:
                RenderSettings();
                break;
        }
    }

    private int FleetRefreshSeconds => Math.Max(10, Preferences.Default.Get("dockswain.fleet.refresh", 30));
    private int FleetDeepSeconds => Math.Max(300, Preferences.Default.Get("dockswain.fleet.deep", 900));
    private int FleetCpuThreshold => Preferences.Default.Get("dockswain.fleet.cpu", 85);
    private int FleetMemoryThreshold => Preferences.Default.Get("dockswain.fleet.memory", 85);
    private int FleetDiskThreshold => Preferences.Default.Get("dockswain.fleet.disk", 85);
    private int FleetSslDays => Preferences.Default.Get("dockswain.fleet.sslDays", 14);
    private int FleetRestartThreshold => Preferences.Default.Get("dockswain.fleet.restartCount", 3);
    private int FleetRestartWindow => Preferences.Default.Get("dockswain.fleet.restartWindow", 60);
    private bool FleetResources => Preferences.Default.Get("dockswain.fleet.resources", true);
    private bool FleetDisk => Preferences.Default.Get("dockswain.fleet.diskEnabled", true);
    private bool FleetSsl => Preferences.Default.Get("dockswain.fleet.sslEnabled", true);
    private bool FleetImages => Preferences.Default.Get("dockswain.fleet.images", true);

    private async Task RefreshFleetAsync()
    {
        if (_fleetRefreshing) return;
        _fleetRefreshing = true;
        try
        {
            var deep = DateTimeOffset.Now - _lastFleetDeep >= TimeSpan.FromSeconds(FleetDeepSeconds);
            var samples = await Task.WhenAll(_servers.Select(s => _backend.FleetHealthAsync(s, FleetResources)));
            if (deep)
            {
                await Task.WhenAll(samples.Select(async snapshot =>
                {
                    if (!snapshot.Reachable) return;
                    var server = _servers.FirstOrDefault(s => s.Id == snapshot.ServerId);
                    if (server is null) return;
                    if (FleetDisk)
                    {
                        try { snapshot.Disk = await _backend.DiskAsync(server); } catch { }
                    }
                    if (FleetSsl)
                    {
                        try { snapshot.Certificates = await _backend.CertbotListAsync(server); } catch { }
                    }
                    snapshot.MetaSampledAt = DateTimeOffset.Now;
                }));
                _lastFleetDeep = DateTimeOffset.Now;
            }

            foreach (var sample in samples)
            {
                _fleetSnapshots.TryGetValue(sample.ServerId, out var old);
                if (!deep && old is not null)
                {
                    sample.Disk = old.Disk;
                    sample.Certificates = old.Certificates;
                    sample.MetaSampledAt = old.MetaSampledAt;
                }
                if (old is not null) DetectFleetTransitions(old, sample, deep && sample.MetaSampledAt.HasValue);
                _fleetSnapshots[sample.ServerId] = sample;
            }
            var valid = _servers.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var removed in _fleetSnapshots.Keys.Where(k => !valid.Contains(k)).ToList()) _fleetSnapshots.Remove(removed);
            await _settings.SaveFleetSnapshotsAsync(_fleetSnapshots);
            if (_feature == Feature.Fleet) RenderFleet();
            var issues = FleetIssues();
            SetStatus($"Fleet: {_fleetSnapshots.Values.Count(s => s.Reachable)}/{_servers.Count} hosts online · {issues.Count} problem(s)");
        }
        finally { _fleetRefreshing = false; }
    }

    private void DetectFleetTransitions(FleetHostSnapshot old, FleetHostSnapshot current, bool metaObserved)
    {
        if (old.Reachable && !current.Reachable) AddFleetEvent("host_offline", "critical", current, null, $"{current.ServerLabel} is offline", current.Reason);
        else if (!old.Reachable && current.Reachable) AddFleetEvent("host_online", "ok", current, null, $"{current.ServerLabel} is back online", "SSH connection recovered");
        if (old.Reachable && old.DockerOk && current.Reachable && !current.DockerOk) AddFleetEvent("docker_unavailable", "critical", current, null, $"Docker unavailable on {current.ServerLabel}", current.Reason);
        else if (old.Reachable && !old.DockerOk && current.DockerOk) AddFleetEvent("docker_recovered", "ok", current, null, $"Docker recovered on {current.ServerLabel}", "Docker is responding again");
        if (metaObserved && FleetDisk && old.Disk is not null && current.Disk is not null)
            DetectFleetThreshold(Percent(old.Disk.Disk.UsePercent), Percent(current.Disk.Disk.UsePercent), FleetDiskThreshold, "disk", "Disk", current, null);
        if (metaObserved && FleetSsl)
        {
            var oldCerts = old.Certificates.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var previousSample = old.MetaSampledAt ?? old.SampledAt;
            foreach (var cert in current.Certificates)
            {
                if (!oldCerts.TryGetValue(cert.Name, out var prior)) continue;
                var beforeDays = CertificateDays(prior.Expiry, previousSample); var days = CertificateDays(cert.Expiry);
                if (beforeDays > FleetSslDays && days <= FleetSslDays) AddFleetEvent("ssl_expiring", days < 0 ? "critical" : "warning", current, null, $"Certificate expiring: {cert.Domains}", days < 0 ? $"Expired {-days} days ago" : $"{days} days remaining");
                else if (beforeDays <= FleetSslDays && days > FleetSslDays) AddFleetEvent("ssl_recovered", "ok", current, null, $"Certificate renewed: {cert.Domains}", $"{days} days remaining");
            }
        }
        if (!old.DockerOk || !current.DockerOk) return;

        var before = old.Containers.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var now = current.Containers.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var (id, container) in now)
        {
            if (!before.TryGetValue(id, out var prior))
            {
                AddFleetEvent("container_created", "info", current, container, $"{container.CleanName} was created", container.ImageReference);
                continue;
            }
            var delta = Math.Max(0, container.RestartCount - prior.RestartCount);
            if (delta > 0) AddFleetEvent("container_restart", "warning", current, container, $"{container.CleanName} restarted", $"{delta} new restart(s)", delta);
            if (container.IsLive && prior.HealthValue != "unhealthy" && container.HealthValue == "unhealthy") AddFleetEvent("container_unhealthy", "critical", current, container, $"{container.CleanName} became unhealthy", container.Status);
            else if (prior.HealthValue == "unhealthy" && container.HealthValue != "unhealthy" && container.IsRunning) AddFleetEvent("container_recovered", "ok", current, container, $"{container.CleanName} recovered", container.Status);
            if (prior.StateValue != "restarting" && container.StateValue == "restarting") AddFleetEvent("container_restarting", "warning", current, container, $"{container.CleanName} is restarting", container.Status);
            if (prior.IsLive && !container.IsLive) AddFleetEvent(container.ExitCode == 0 ? "container_stopped" : "container_crashed", container.ExitCode == 0 ? "warning" : "critical", current, container, container.ExitCode == 0 ? $"{container.CleanName} stopped" : $"{container.CleanName} crashed", container.Status);
            else if (!prior.IsLive && container.IsLive) AddFleetEvent("container_started", "info", current, container, $"{container.CleanName} started", container.Status);
            if (FleetResources)
            {
                if (!string.IsNullOrWhiteSpace(prior.Cpu) && !string.IsNullOrWhiteSpace(container.Cpu))
                    DetectFleetThreshold(prior.CpuPercent, container.CpuPercent, FleetCpuThreshold, "container_cpu", "CPU", current, container);
                if (!string.IsNullOrWhiteSpace(prior.Memory) && !string.IsNullOrWhiteSpace(container.Memory))
                    DetectFleetThreshold(prior.MemoryPercent, container.MemoryPercent, FleetMemoryThreshold, "container_memory", "memory", current, container);
            }
            if (FleetImages && container.IsLive && !container.ImagePinned && !prior.ImageUpdate && container.ImageUpdate) AddFleetEvent("image_update", "warning", current, container, $"Newer local image for {container.CleanName}", container.ImageReference);
            else if (FleetImages && container.IsLive && !container.ImagePinned && prior.ImageUpdate && !container.ImageUpdate) AddFleetEvent("image_updated", "ok", current, container, $"{container.CleanName} now uses the current image", container.ImageReference);
        }
        foreach (var removed in before.Where(pair => !now.ContainsKey(pair.Key)).Select(pair => pair.Value))
            AddFleetEvent("container_removed", "info", current, removed, $"{removed.CleanName} was removed", removed.ImageReference);

        if (FleetResources) DetectFleetThreshold(old.Resources.MemoryPercent, current.Resources.MemoryPercent, FleetMemoryThreshold, "host_memory", "Host memory", current, null);
    }

    private void DetectFleetThreshold(double before, double current, double limit, string kind, string label,
        FleetHostSnapshot host, FleetContainerSnapshot? container)
    {
        if (before < limit && current >= limit) AddFleetEvent(kind + "_high", "warning", host, container, $"High {label}{(container is null ? "" : ": " + container.CleanName)}", $"{current:0.0}%");
        else if (before >= limit && current < limit) AddFleetEvent(kind + "_recovered", "ok", host, container, $"{label} recovered{(container is null ? "" : ": " + container.CleanName)}", $"{current:0.0}%");
    }

    private void AddFleetEvent(string kind, string severity, FleetHostSnapshot host, FleetContainerSnapshot? container,
        string title, string detail, int count = 1)
    {
        _fleetEvents.Insert(0, new FleetEvent
        {
            Kind = kind, Severity = severity, ServerId = host.ServerId, ServerLabel = host.ServerLabel,
            ContainerId = container?.ShortId ?? "", ContainerName = container?.CleanName ?? "",
            Title = title, Detail = detail, Count = count
        });
        if (_fleetEvents.Count > 250) _fleetEvents.RemoveRange(250, _fleetEvents.Count - 250);
        _ = _settings.SaveFleetEventsAsync(_fleetEvents);
    }

    private async Task RefreshContainersAsync()
    {
        if (_server is null)
        {
            return;
        }

        _runtime = await _backend.RefreshRuntimeAsync(_server);
        RenderContainers();
        var running = _runtime.Containers.Count(c => c.IsRunning);
        var version = string.IsNullOrWhiteSpace(_runtime.DockerVersion) ? "" : $" Docker {_runtime.DockerVersion}.";
        SetStatus($"{_server.DisplayName}: {running}/{_runtime.Containers.Count} containers running.{version}");
    }

    private void RenderFleet()
    {
        var stack = PageStack();
        stack.Add(TitleRow("Fleet Health", ActionButton("Refresh all", RefreshFleetAsync)));
        var issues = FleetIssues();
        var online = _fleetSnapshots.Values.Count(s => s.Reachable);
        var healthy = _fleetSnapshots.Values.SelectMany(s => s.Containers)
            .Count(c => c.IsRunning && c.HealthValue != "unhealthy");
        var warnings = issues.Count(i => i.Severity == "warning");
        var critical = issues.Count(i => i.Severity == "critical");
        stack.Add(new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 6,
            Children =
            {
                FleetMetric("Hosts", $"{online}/{_servers.Count}", Theme.Accent).AtColumn(0),
                FleetMetric("Healthy", healthy.ToString(), Theme.Positive).AtColumn(1),
                FleetMetric("Warnings", warnings.ToString(), Theme.Warning).AtColumn(2),
                FleetMetric("Critical", critical.ToString(), Theme.Negative).AtColumn(3)
            }
        });

        stack.Add(SectionLabel($"Problems ({issues.Count})"));
        if (issues.Count == 0) stack.Add(EmptyCard(_servers.Count == 0 ? "Add a server in Settings." : "Fleet looks healthy."));
        foreach (var issue in issues)
        {
            var actions = new FlexLayout { Wrap = FlexWrap.Wrap };
            if (!string.IsNullOrWhiteSpace(issue.ContainerId))
            {
                actions.Add(ActionButton("Restart", async () =>
                {
                    var server = _servers.First(s => s.Id == issue.ServerId);
                    await _backend.ContainerActionAsync(server, "restart", issue.ContainerId);
                    await RefreshFleetAsync();
                }));
            }
            actions.Add(ActionButton(issue.Feature == "disk" ? "Cleanup" : issue.Feature == "nginx" ? "Certificates" : "Open",
                async () => { await OpenFleetTargetAsync(issue.ServerId, issue.Feature); }));
            stack.Add(PanelCard(issue.Title, new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = issue.ServerLabel + (string.IsNullOrWhiteSpace(issue.ContainerName) ? "" : " / " + issue.ContainerName), FontSize = 12, TextColor = Theme.TextMuted },
                    new Label { Text = issue.Detail, FontSize = 12, TextColor = issue.Severity == "critical" ? Theme.Negative : Theme.Warning, IsVisible = !string.IsNullOrWhiteSpace(issue.Detail) },
                    actions
                }
            }));
        }

        stack.Add(SectionLabel("Hosts"));
        foreach (var server in _servers)
        {
            _fleetSnapshots.TryGetValue(server.Id, out var host);
            var detail = host is null ? "waiting for first sample"
                : !host.Reachable ? $"offline · {host.Reason}"
                : !host.DockerOk ? $"SSH online · Docker unavailable ({host.Reason})"
                : $"{host.Containers.Count(c => c.IsRunning)}/{host.Containers.Count} running · memory {host.Resources.MemoryPercent:0}%"
                  + (host.Disk is null ? "" : $" · disk {host.Disk.Disk.UsePercent}");
            stack.Add(PanelCard(server.DisplayName, new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = detail, FontSize = 12, TextColor = host?.Reachable == true && host.DockerOk ? Theme.Positive : Theme.Negative },
                    ActionButton("Open", async () => await OpenFleetTargetAsync(server.Id, "containers"))
                }
            }));
        }

        var certs = _fleetSnapshots.Values.SelectMany(host => host.Certificates.Select(cert => (host, cert, days: CertificateDays(cert.Expiry))))
            .OrderBy(row => row.days).ToList();
        stack.Add(SectionLabel($"SSL certificates ({certs.Count(c => c.days <= FleetSslDays)} expiring)"));
        foreach (var row in certs)
        {
            stack.Add(PanelCard(string.IsNullOrWhiteSpace(row.cert.Domains) ? row.cert.Name : row.cert.Domains,
                new VerticalStackLayout
                {
                    Spacing = 4,
                    Children =
                    {
                        DataRow("host", row.host.ServerLabel), DataRow("expires", row.cert.Expiry),
                        new Label { Text = row.days < 0 ? $"expired {-row.days}d" : $"{row.days} days remaining", FontSize = 12,
                            TextColor = row.days < 0 ? Theme.Negative : row.days <= FleetSslDays ? Theme.Warning : Theme.Positive },
                        ActionButton("Manage", async () => await OpenFleetTargetAsync(row.host.ServerId, "nginx"))
                    }
                }));
        }

        var imageRows = _fleetSnapshots.Values.SelectMany(host => host.Containers.Where(c => c.IsLive)
            .GroupBy(c => c.ImageReference).Select(g => (host, container: g.First())))
            .OrderBy(row => row.container.ImageUpdate ? 0 : 1).ThenBy(row => row.container.ImageReference).ToList();
        stack.Add(SectionLabel($"Images ({imageRows.Count(r => r.container.ImageUpdate)} locally updated)"));
        stack.Add(new Label { Text = "Read-only local tag comparison; Dockswain never pulls images in the background.", FontSize = 12, TextColor = Theme.TextMuted });
        foreach (var row in imageRows)
        {
            stack.Add(PanelCard(row.container.ImageReference, new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    DataRow("host", row.host.ServerLabel),
                    new Label { Text = row.container.ImageUpdate ? "newer local image available" : row.container.ImagePinned ? "digest pinned" : "tag reference",
                        FontSize = 12, TextColor = row.container.ImageUpdate ? Theme.Warning : Theme.TextMuted }
                }
            }));
        }

        stack.Add(SectionLabel("Event history"));
        var clear = ActionButton("Clear history", async () =>
        {
            _fleetEvents = [];
            await _settings.SaveFleetEventsAsync(_fleetEvents);
            RenderFleet();
        });
        stack.Add(clear);
        if (_fleetEvents.Count == 0) stack.Add(EmptyCard("No changes recorded yet. The first poll establishes a silent baseline."));
        foreach (var item in _fleetEvents.Take(100))
        {
            stack.Add(PanelCard(item.Title, new VerticalStackLayout
            {
                Spacing = 3,
                Children =
                {
                    new Label { Text = $"{item.Timestamp:g} · {item.ServerLabel}" + (string.IsNullOrWhiteSpace(item.ContainerName) ? "" : " / " + item.ContainerName), FontSize = 11, TextColor = Theme.TextMuted },
                    new Label { Text = item.Detail, FontSize = 12, TextColor = item.Severity == "critical" ? Theme.Negative : item.Severity == "warning" ? Theme.Warning : Theme.TextMuted }
                }
            }));
        }
        _content.Content = Scroll(stack);
    }

    private async Task OpenFleetTargetAsync(string serverId, string feature)
    {
        var server = _servers.FirstOrDefault(s => s.Id == serverId);
        if (server is null) return;
        _feature = feature switch { "disk" => Feature.Disk, "nginx" => Feature.Nginx, _ => Feature.Containers };
        _server = server;
        _loadingServers = true;
        _serverPicker.SelectedItem = server;
        _loadingServers = false;
        SyncTabs();
        await RefreshCurrentFeatureAsync();
    }

    private List<FleetIssue> FleetIssues()
    {
        var issues = new List<FleetIssue>();
        foreach (var server in _servers)
        {
            if (!_fleetSnapshots.TryGetValue(server.Id, out var host))
            {
                issues.Add(FleetIssue("warning", "checking", server, null, $"Checking {server.DisplayName}", "Waiting for the first fleet sample"));
                continue;
            }
            if (!host.Reachable) { issues.Add(FleetIssue("critical", "host_offline", server, null, "Host offline", host.Reason)); continue; }
            if (!host.DockerOk) { issues.Add(FleetIssue("critical", "docker_unavailable", server, null, "Docker unavailable", host.Reason)); continue; }
            if (FleetResources && host.Resources.MemoryPercent >= FleetMemoryThreshold)
                issues.Add(FleetIssue("warning", "host_memory", server, null, $"Host memory {host.Resources.MemoryPercent:0}%", "Memory threshold exceeded"));
            foreach (var c in host.Containers)
            {
                if ((c.IsLive && c.HealthValue == "unhealthy") || c.StateValue == "dead") issues.Add(FleetIssue("critical", "unhealthy", server, c, $"{c.CleanName} is unhealthy", c.Status, "containers"));
                var recent = RecentRestarts(server.Id, c.ShortId);
                if (recent >= FleetRestartThreshold) issues.Add(FleetIssue("warning", "restart_burst", server, c, $"{c.CleanName} restarted {recent} times", $"within {FleetRestartWindow} minutes", "containers"));
                if (c.StateValue == "restarting") issues.Add(FleetIssue("warning", "restarting", server, c, $"{c.CleanName} is restarting", c.Status, "containers"));
                if (FleetResources && c.IsLive && c.CpuPercent >= FleetCpuThreshold) issues.Add(FleetIssue("warning", "cpu", server, c, $"{c.CleanName} CPU {c.Cpu}", "100% equals one fully used CPU core", "containers"));
                if (FleetResources && c.IsLive && c.MemoryPercent >= FleetMemoryThreshold) issues.Add(FleetIssue("warning", "memory", server, c, $"{c.CleanName} memory {c.Memory}", c.MemoryUsage, "containers"));
                if (FleetImages && c.IsLive && !c.ImagePinned && c.ImageUpdate) issues.Add(FleetIssue("warning", "image", server, c, $"{c.CleanName} uses an older image", c.ImageReference, "containers"));
                if (RecentCrash(c)) issues.Add(FleetIssue("warning", "crashed", server, c, $"{c.CleanName} exited with code {c.ExitCode}", c.Status, "containers"));
            }
            if (FleetDisk && host.Disk is not null && Percent(host.Disk.Disk.UsePercent) >= FleetDiskThreshold)
                issues.Add(FleetIssue(Percent(host.Disk.Disk.UsePercent) >= 95 ? "critical" : "warning", "disk", server, null,
                    $"Disk {host.Disk.Disk.UsePercent}", $"{ByteFormatter.Human(ReclaimableBytes(host.Disk.Df))} reclaimable", "disk"));
            if (FleetSsl)
            {
                foreach (var cert in host.Certificates.Where(c => CertificateDays(c.Expiry) <= FleetSslDays))
                {
                    var days = CertificateDays(cert.Expiry);
                    issues.Add(FleetIssue(days < 0 ? "critical" : "warning", "ssl", server, null,
                        days < 0 ? $"SSL expired: {cert.Domains}" : $"SSL expires in {days}d: {cert.Domains}", cert.Expiry, "nginx"));
                }
            }
        }
        return issues.OrderByDescending(i => i.Severity == "critical" ? 2 : 1).ThenBy(i => i.ServerLabel).ToList();
    }

    private bool RecentCrash(FleetContainerSnapshot container)
    {
        if (container.StateValue != "exited" || container.ExitCode == 0
            || !DateTimeOffset.TryParse(container.FinishedAt, out var finished)) return false;
        var age = DateTimeOffset.Now - finished;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(Math.Max(5, FleetRestartWindow));
    }

    private static FleetIssue FleetIssue(string severity, string kind, ServerProfile server, FleetContainerSnapshot? container,
        string title, string detail, string feature = "") => new()
    {
        Severity = severity, Kind = kind, ServerId = server.Id, ServerLabel = server.DisplayName,
        ContainerId = container?.ShortId ?? "", ContainerName = container?.CleanName ?? "",
        Title = title, Detail = detail, Feature = feature
    };

    private int RecentRestarts(string serverId, string containerId)
    {
        var cutoff = DateTimeOffset.Now.AddMinutes(-FleetRestartWindow);
        return _fleetEvents.Where(e => e.ServerId == serverId && e.ContainerId == containerId
            && e.Kind == "container_restart" && e.Timestamp >= cutoff).Sum(e => e.Count);
    }

    private static double Percent(string value) => double.TryParse(value.Trim().TrimEnd('%'),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    private static long ReclaimableBytes(IEnumerable<DockerDfEntry> rows) => rows.Sum(row => DockerBytes(row.Reclaimable));
    private static long DockerBytes(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value,
            @"([0-9]+(?:\.[0-9]+)?)\s*([KMGTPE]?i?B)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)) return 0;
        var unit = match.Groups[2].Value.ToUpperInvariant();
        var power = unit.Length == 0 ? 0 : "KMGTPE".IndexOf(unit[0]) + 1;
        var radix = unit.Contains('I') ? 1024d : 1000d;
        return (long)(number * Math.Pow(radix, Math.Max(0, power)));
    }
    private static int CertificateDays(string expiry, DateTimeOffset? relativeTo = null)
    {
        var raw = expiry.Split('(', 2)[0].Trim();
        return DateTimeOffset.TryParse(raw, out var date)
            ? (int)Math.Floor((date - (relativeTo ?? DateTimeOffset.Now)).TotalDays) : int.MaxValue;
    }

    private static View FleetMetric(string title, string value, Color color) => new Border
    {
        Padding = new Thickness(5, 8), Stroke = color, StrokeThickness = 1,
        BackgroundColor = color.WithAlpha(0.12f),
        Content = new VerticalStackLayout
        {
            Spacing = 1, HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = value, FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = color, HorizontalTextAlignment = TextAlignment.Center },
                new Label { Text = title, FontSize = 10, TextColor = Theme.TextMuted, HorizontalTextAlignment = TextAlignment.Center }
            }
        }
    };

    private async Task RefreshFilesAsync()
    {
        if (_server is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_remotePath))
        {
            _remotePath = await _backend.HomeDirectoryAsync(_server);
        }

        _files = (await _backend.ListFilesAsync(_server, _remotePath)).ToList();
        RenderFiles();
        SetStatus($"{_server.DisplayName}: {_remotePath}");
    }

    private void ResetFeatureData()
    {
        _runtime = new ServerRuntime();
        _compose = [];
        _disk = null;
        _files = [];
        _nginx = null;
        _remotePath = "";
    }

    private void RenderContainers()
    {
        var stack = PageStack();
        stack.Add(new Label
        {
            Text = "Containers",
            FontSize = 22,
            FontAttributes = FontAttributes.Bold
        });

        var search = new SearchBar { Placeholder = "Filter by name, image, or state", Text = _searchText };
        search.TextChanged += (_, e) =>
        {
            _searchText = e.NewTextValue ?? "";
            RenderContainers();
        };

        var toggles = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 12,
            Children =
            {
                search,
                Toggle("Running", _runningOnly, v => { _runningOnly = v; RenderContainers(); }).AtColumn(1),
                Toggle("Group", _groupByNetwork, v => { _groupByNetwork = v; RenderContainers(); }).AtColumn(2)
            }
        };
        stack.Add(toggles);

        var filtered = _runtime.Containers
            .Where(c => !_runningOnly || c.IsLive)
            .Where(c => string.IsNullOrWhiteSpace(_searchText)
                || c.CleanName.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                || c.Image.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                || c.State.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == 0)
        {
            stack.Add(EmptyCard("No containers match the current filter."));
        }
        else if (_groupByNetwork)
        {
            foreach (var group in filtered.GroupBy(c => c.PrimaryNetwork).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                stack.Add(SectionLabel($"{group.Key} ({group.Count()})"));
                foreach (var container in group)
                {
                    stack.Add(ContainerCard(container));
                }
            }
        }
        else
        {
            foreach (var container in filtered)
            {
                stack.Add(ContainerCard(container));
            }
        }

        _content.Content = Scroll(stack);
    }

    private View ContainerCard(DockerContainer container)
    {
        _runtime.StatsByContainerId.TryGetValue(container.ShortId, out var stat);
        var image = new Label
        {
            Text = container.Image,
            FontSize = 12,
            FontFamily = Theme.Mono,
            TextColor = Theme.TextMuted,
            LineBreakMode = LineBreakMode.TailTruncation
        };
        var state = new Label
        {
            Text = $"{container.State}  {container.Status}",
            FontSize = 12,
            TextColor = container.IsRunning ? Theme.Positive : Theme.TextMuted,
            LineBreakMode = LineBreakMode.TailTruncation
        };

        var body = new VerticalStackLayout { Spacing = 4, Children = { image, state } };
        body.Add(DataRow("net", container.PrimaryNetwork));
        if (stat is not null)
        {
            body.Add(DataRow("cpu", stat.Cpu));
            body.Add(DataRow("mem", stat.Memory));
        }

        var actions = new FlexLayout
        {
            Wrap = FlexWrap.Wrap,
            Direction = FlexDirection.Row,
            AlignItems = FlexAlignItems.Start,
            JustifyContent = FlexJustify.Start
        };
        actions.Add(ActionButton(container.IsRunning ? "Stop" : "Start", async () =>
        {
            await _backend.ContainerActionAsync(CurrentServer(), container.IsRunning ? "stop" : "start", container.ShortId);
            await RefreshContainersAsync();
        }, accent: Theme.Accent));
        actions.Add(ActionButton("Restart", async () =>
        {
            await _backend.ContainerActionAsync(CurrentServer(), "restart", container.ShortId);
            await RefreshContainersAsync();
        }));
        actions.Add(ActionButton("Logs", async () =>
        {
            var logs = await _backend.LogsAsync(CurrentServer(), container.ShortId);
            await Navigation.PushAsync(new TextDocumentPage($"{container.CleanName} logs", logs, readOnly: true));
        }));
        actions.Add(ActionButton("Exec", async () =>
        {
            var command = await this.ThemedPrompt("Exec command", container.CleanName, initialValue: "pwd && ls -la");
            if (command is null)
            {
                return;
            }

            var output = await _backend.ExecAsync(CurrentServer(), container.ShortId, command);
            await Navigation.PushAsync(new TextDocumentPage($"{container.CleanName} exec", output, readOnly: true));
        }));
        actions.Add(ActionButton("Remove", async () =>
        {
            if (await this.ThemedConfirm("Remove container", $"Remove {container.CleanName}?", "Remove", "Cancel"))
            {
                await _backend.ContainerActionAsync(CurrentServer(), "remove", container.ShortId);
                await RefreshContainersAsync();
            }
        }, accent: Theme.Negative));
        body.Add(actions);

        return PanelCard(container.CleanName, body);
    }

    private void RenderCompose()
    {
        var stack = PageStack();
        stack.Add(TitleRow("Compose", ActionButton("Reload", async () => await RefreshCurrentFeatureAsync())));

        if (_compose.Count == 0)
        {
            stack.Add(EmptyCard("No compose projects reported by Docker."));
        }

        foreach (var project in _compose)
        {
            var actions = new FlexLayout { Wrap = FlexWrap.Wrap };
            actions.Add(ActionButton("Up", async () =>
            {
                await _backend.ComposeActionAsync(CurrentServer(), project, "up");
                await RefreshCurrentFeatureAsync();
            }));
            actions.Add(ActionButton("Down", async () =>
            {
                await _backend.ComposeActionAsync(CurrentServer(), project, "down");
                await RefreshCurrentFeatureAsync();
            }));
            actions.Add(ActionButton("Files", async () =>
            {
                var text = project.ConfigFiles.Length == 0 ? "No config files reported." : string.Join(Environment.NewLine, project.ConfigFiles);
                await Navigation.PushAsync(new TextDocumentPage(project.Name, text, readOnly: true));
            }));

            stack.Add(PanelCard(project.Name, new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = project.Status, FontSize = 12, TextColor = project.IsRunning ? Theme.Positive : Theme.TextMuted },
                    new Label { Text = string.Join(", ", project.ConfigFiles), FontSize = 12, FontFamily = Theme.Mono, TextColor = Theme.TextMuted, LineBreakMode = LineBreakMode.TailTruncation },
                    actions
                }
            }));
        }

        _content.Content = Scroll(stack);
    }

    private void RenderDisk()
    {
        var stack = PageStack();
        stack.Add(TitleRow("Disk", ActionButton("Reload", async () => await RefreshCurrentFeatureAsync())));

        if (_disk is null)
        {
            stack.Add(EmptyCard("Disk data has not been loaded."));
            _content.Content = Scroll(stack);
            return;
        }

        var usedRatio = _disk.Disk.Size <= 0 ? 0 : (double)_disk.Disk.Used / _disk.Disk.Size;
        stack.Add(PanelCard(_disk.Disk.DockerRoot, new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                DataRow("used", ByteFormatter.Human(_disk.Disk.Used)),
                DataRow("size", ByteFormatter.Human(_disk.Disk.Size)),
                DataRow("percent", _disk.Disk.UsePercent),
                new ProgressBar { Progress = Math.Clamp(usedRatio, 0, 1) }
            }
        }));

        var prune = new FlexLayout { Wrap = FlexWrap.Wrap };
        prune.Add(ActionButton("Prune build cache", async () => await RunPruneAsync("build")));
        prune.Add(ActionButton("Prune dangling images", async () => await RunPruneAsync("images")));
        prune.Add(ActionButton("Prune stopped containers", async () => await RunPruneAsync("containers")));
        stack.Add(prune);

        stack.Add(SectionLabel("Docker system df"));
        foreach (var row in _disk.Df)
        {
            stack.Add(PanelCard(row.Type, new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    DataRow("total", row.Total),
                    DataRow("active", row.Active),
                    DataRow("size", row.Size),
                    DataRow("reclaimable", row.Reclaimable)
                }
            }));
        }

        stack.Add(SectionLabel("Container JSON logs"));
        foreach (var log in _disk.Logs.Take(60))
        {
            var truncate = ActionButton("Truncate", async () =>
            {
                if (await this.ThemedConfirm("Truncate log", $"Empty {log.Name} ({ByteFormatter.Human(log.Size)})?", "Truncate", "Cancel"))
                {
                    await _backend.TruncateLogAsync(CurrentServer(), log);
                    await RefreshCurrentFeatureAsync();
                }
            }, accent: Theme.Negative);
            truncate.Margin = new Thickness(0);

            stack.Add(PanelCard(log.Name, new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    DataRow("size", ByteFormatter.Human(log.Size)),
                    new Label { Text = log.Path, FontSize = 12, FontFamily = Theme.Mono, TextColor = Theme.TextMuted, LineBreakMode = LineBreakMode.TailTruncation }
                }
            }, headerTrailing: truncate));
        }

        _content.Content = Scroll(stack);
    }

    private async Task RunPruneAsync(string what)
    {
        if (!await this.ThemedConfirm("Confirm cleanup", $"Run docker {what} prune?", "Run", "Cancel"))
        {
            return;
        }

        var output = await _backend.PruneAsync(CurrentServer(), what);
        await this.ThemedAlert("Cleanup complete", string.IsNullOrWhiteSpace(output) ? "Done." : output.Trim(), "OK");
        await RefreshCurrentFeatureAsync();
    }

    private void RenderFiles()
    {
        var stack = PageStack();
        var path = new Entry { Text = _remotePath, HorizontalOptions = LayoutOptions.Fill };
        path.Completed += async (_, _) =>
        {
            _remotePath = path.Text ?? "/";
            await SafeAsync(RefreshFilesAsync);
        };

        var go = ActionButton("Go", async () =>
        {
            _remotePath = path.Text ?? "/";
            await RefreshFilesAsync();
        });
        var up = ActionButton("Up", async () =>
        {
            _remotePath = RemoteShell.ParentRemote(_remotePath);
            await RefreshFilesAsync();
        });
        var home = ActionButton("Home", async () =>
        {
            _remotePath = await _backend.HomeDirectoryAsync(CurrentServer());
            await RefreshFilesAsync();
        });
        var mkdir = ActionButton("New folder", async () =>
        {
            var name = await this.ThemedPrompt("New folder", _remotePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                await _backend.CreateDirectoryAsync(CurrentServer(), RemoteShell.CombineRemote(_remotePath, name.Trim()));
                await RefreshFilesAsync();
            }
        });
        var upload = ActionButton("Upload", UploadFileAsync);

        stack.Add(new Label { Text = "Files", FontAttributes = FontAttributes.Bold, FontSize = 22 });
        stack.Add(new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Children = { path, go.AtColumn(1) }
        });
        stack.Add(new FlexLayout { Wrap = FlexWrap.Wrap, Children = { up, home, mkdir, upload } });

        foreach (var entry in _files)
        {
            stack.Add(FileCard(entry));
        }

        if (_files.Count == 0)
        {
            stack.Add(EmptyCard("This directory is empty."));
        }

        _content.Content = Scroll(stack);
    }

    private View FileCard(RemoteFileEntry entry)
    {
        var actions = new FlexLayout { Wrap = FlexWrap.Wrap };
        if (entry.IsDirectory)
        {
            actions.Add(ActionButton("Open", async () =>
            {
                _remotePath = entry.Path;
                await RefreshFilesAsync();
            }));
        }
        else
        {
            actions.Add(ActionButton("Edit", async () => await OpenRemoteFileAsync(entry.Path, privileged: false)));
            actions.Add(ActionButton("Download", async () => await DownloadFileAsync(entry)));
        }

        actions.Add(ActionButton("Rename", async () =>
        {
            var name = await this.ThemedPrompt("Rename", entry.Name, initialValue: entry.Name);
            if (!string.IsNullOrWhiteSpace(name))
            {
                await _backend.RenameFileAsync(CurrentServer(), entry.Path, RemoteShell.CombineRemote(_remotePath, name.Trim()));
                await RefreshFilesAsync();
            }
        }));
        actions.Add(ActionButton("Delete", async () =>
        {
            if (await this.ThemedConfirm("Delete", $"Delete {entry.Name}?", "Delete", "Cancel"))
            {
                await _backend.DeleteFileAsync(CurrentServer(), entry.Path, entry.IsDirectory);
                await RefreshFilesAsync();
            }
        }, accent: Theme.Negative));

        var body = new VerticalStackLayout { Spacing = 4 };
        if (entry.IsDirectory)
        {
            body.Add(new Label { Text = "Directory", FontSize = 12, TextColor = Theme.TextMuted });
        }
        else
        {
            body.Add(DataRow("size", ByteFormatter.Human(entry.Size)));
            body.Add(DataRow("modified", $"{entry.Modified:g}"));
        }
        body.Add(actions);

        return PanelCard(entry.IsDirectory ? $"{entry.Name}/" : entry.Name, body);
    }

    private async Task UploadFileAsync()
    {
        var file = await FilePicker.Default.PickAsync();
        if (file is null)
        {
            return;
        }

        var local = System.IO.Path.Combine(FileSystem.CacheDirectory, file.FileName);
        await using (var input = await file.OpenReadAsync())
        await using (var output = File.Create(local))
        {
            await input.CopyToAsync(output);
        }

        await _backend.UploadAsync(CurrentServer(), local, RemoteShell.CombineRemote(_remotePath, file.FileName));
        await RefreshFilesAsync();
    }

    private async Task DownloadFileAsync(RemoteFileEntry entry)
    {
        var local = System.IO.Path.Combine(FileSystem.CacheDirectory, entry.Name);
        await _backend.DownloadAsync(CurrentServer(), entry.Path, local);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = entry.Name,
            File = new ShareFile(local)
        });
    }

    private async Task OpenRemoteFileAsync(string path, bool privileged)
    {
        var text = await _backend.ReadFileAsync(CurrentServer(), path, privileged);
        await Navigation.PushAsync(new TextDocumentPage(RemoteShell.FileNameRemote(path), text, readOnly: false, save: async updated =>
        {
            await _backend.WriteFileAsync(CurrentServer(), path, updated, privileged);
        }));
    }

    private void RenderNginx()
    {
        var stack = PageStack();
        stack.Add(TitleRow("Nginx", ActionButton("Reload", async () => await RefreshCurrentFeatureAsync())));

        var tools = new FlexLayout { Wrap = FlexWrap.Wrap };
        tools.Add(ActionButton("Test", async () =>
        {
            var result = await _backend.NginxTestAsync(CurrentServer());
            await Navigation.PushAsync(new TextDocumentPage(result.Pass ? "nginx -t passed" : "nginx -t failed", result.Output, readOnly: true));
        }));
        tools.Add(ActionButton("Reload nginx", async () =>
        {
            await _backend.NginxReloadAsync(CurrentServer());
            await this.ThemedAlert("Reloaded", "nginx reload command completed.", "OK");
        }));
        tools.Add(ActionButton("New proxy", async () => await NewNginxSiteAsync(proxy: true)));
        tools.Add(ActionButton("New static", async () => await NewNginxSiteAsync(proxy: false)));
        tools.Add(ActionButton("Issue SSL", async () => await IssueCertificateAsync("")));
        tools.Add(ActionButton("New conf.d", NewConfdAsync));
        stack.Add(tools);

        if (_nginx is null)
        {
            stack.Add(EmptyCard("Nginx data has not been loaded."));
            _content.Content = Scroll(stack);
            return;
        }

        stack.Add(SectionLabel("Sites"));
        foreach (var site in _nginx.Sites)
        {
            var actions = new FlexLayout { Wrap = FlexWrap.Wrap };
            actions.Add(ActionButton(site.Enabled ? "Disable" : "Enable", async () =>
            {
                await _backend.ToggleNginxSiteAsync(CurrentServer(), site);
                await RefreshCurrentFeatureAsync();
            }));
            actions.Add(ActionButton("Edit", async () => await OpenRemoteFileAsync(site.Path, privileged: true)));
            actions.Add(ActionButton("SSL", async () => await IssueCertificateAsync(site.ServerName)));

            stack.Add(PanelCard(site.Name, new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = $"{(site.Enabled ? "enabled" : "disabled")}  {(site.Tls ? "TLS" : "plain HTTP")}", TextColor = site.Enabled ? Theme.Positive : Theme.TextMuted, FontSize = 12 },
                    new Label { Text = site.ServerName, TextColor = Theme.TextMuted, FontSize = 12, FontFamily = Theme.Mono, LineBreakMode = LineBreakMode.TailTruncation },
                    actions
                }
            }));
        }

        stack.Add(SectionLabel("conf.d snippets"));
        foreach (var file in _nginx.ConfdFiles)
        {
            var actions = new FlexLayout { Wrap = FlexWrap.Wrap };
            actions.Add(ActionButton(file.Enabled ? "Disable" : "Enable", async () =>
            {
                await _backend.ToggleConfdAsync(CurrentServer(), file);
                await RefreshCurrentFeatureAsync();
            }));
            actions.Add(ActionButton("Edit", async () => await OpenRemoteFileAsync(file.Path, privileged: true)));
            actions.Add(ActionButton("Delete", async () =>
            {
                if (await this.ThemedConfirm("Delete snippet", $"Delete {file.Name}?", "Delete", "Cancel"))
                {
                    await _backend.DeleteConfdAsync(CurrentServer(), file);
                    await RefreshCurrentFeatureAsync();
                }
            }, accent: Theme.Negative));
            stack.Add(PanelCard(file.Name, new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = file.Enabled ? "enabled" : "disabled", FontSize = 12, TextColor = file.Enabled ? Theme.Positive : Theme.TextMuted },
                    DataRow("size", ByteFormatter.Human(file.Size)),
                    actions
                }
            }));
        }

        stack.Add(SectionLabel("Certificates"));
        foreach (var cert in _nginx.Certificates)
        {
            stack.Add(PanelCard(cert.Name, new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = cert.Domains, FontSize = 12, FontFamily = Theme.Mono, TextColor = Theme.TextMuted, LineBreakMode = LineBreakMode.TailTruncation },
                    DataRow("expiry", cert.Expiry),
                    new Label { Text = cert.Valid, FontSize = 12, TextColor = Theme.TextMuted, IsVisible = !string.IsNullOrWhiteSpace(cert.Valid) }
                }
            }));
        }

        _content.Content = Scroll(stack);
    }

    private async Task NewNginxSiteAsync(bool proxy)
    {
        var name = await this.ThemedPrompt(proxy ? "New reverse proxy" : "New static site", "Config file name", initialValue: "site.conf");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var domains = await this.ThemedPrompt("Domains", "server_name values, separated by spaces");
        if (string.IsNullOrWhiteSpace(domains))
        {
            return;
        }

        var target = await this.ThemedPrompt(proxy ? "Proxy target" : "Web root", proxy ? "Example: http://127.0.0.1:8080" : "Example: /var/www/site");
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var config = proxy
            ? DockswainBackend.BuildReverseProxyConfig(domains, target)
            : DockswainBackend.BuildStaticSiteConfig(domains, target);
        await Navigation.PushAsync(new TextDocumentPage(name.Trim(), config, save: async text =>
        {
            await _backend.NewNginxSiteAsync(CurrentServer(), name.Trim(), text);
            _nginx = await _backend.NginxAsync(CurrentServer());
            RenderNginx();
        }));
    }

    private async Task NewConfdAsync()
    {
        var name = await this.ThemedPrompt("New conf.d file", "File name", initialValue: "upstream.conf");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await Navigation.PushAsync(new TextDocumentPage(name.Trim(), "", save: async text =>
        {
            await _backend.NewConfdFileAsync(CurrentServer(), name.Trim(), text);
            _nginx = await _backend.NginxAsync(CurrentServer());
            RenderNginx();
        }));
    }

    private async Task IssueCertificateAsync(string prefill)
    {
        var domains = await this.ThemedPrompt("Issue certificate", "Domains", initialValue: prefill);
        if (string.IsNullOrWhiteSpace(domains))
        {
            return;
        }

        var choice = await this.ThemedSheet("HTTP to HTTPS redirect?", "Cancel", null, "Redirect", "No redirect");
        if (choice == "Cancel")
        {
            return;
        }

        var output = await _backend.CertbotIssueAsync(CurrentServer(), domains, choice == "Redirect");
        await Navigation.PushAsync(new TextDocumentPage("certbot output", output, readOnly: true));
        _nginx = await _backend.NginxAsync(CurrentServer());
        RenderNginx();
    }

    private void RenderSettings()
    {
        var stack = PageStack();
        var tools = new FlexLayout { Wrap = FlexWrap.Wrap };
        tools.Add(ActionButton("Add server", async () => await OpenServerEditorAsync(null)));
        tools.Add(ActionButton("Scan QR", async () =>
        {
            await Navigation.PushAsync(new QrScanPage(ImportQrAsync));
        }));
        tools.Add(DirectActionButton("Paste QR", async () =>
        {
            var text = await Clipboard.Default.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                text = await this.ThemedPrompt("Paste QR payload", "Paste dockswain:// import text");
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                await ImportQrWithAlertAsync(text);
            }
        }));
        stack.Add(new Label { Text = "Settings", FontSize = 22, FontAttributes = FontAttributes.Bold });
        stack.Add(tools);

        stack.Add(SectionLabel("Fleet Health"));
        stack.Add(PreferenceToggle("Monitor CPU and memory thresholds", "dockswain.fleet.resources", true));
        stack.Add(PreferenceNumber("Health refresh (seconds)", "dockswain.fleet.refresh", 30, 10, 600,
            value => { if (_fleetTimer is not null) _fleetTimer.Interval = TimeSpan.FromSeconds(value); }));
        stack.Add(PreferenceNumber("Disk / SSL refresh (seconds)", "dockswain.fleet.deep", 900, 300, 21600));
        stack.Add(PreferenceNumber("Container CPU warning (100% = one core)", "dockswain.fleet.cpu", 85, 1, 1000));
        stack.Add(PreferenceNumber("Memory warning (%)", "dockswain.fleet.memory", 85, 1, 100));
        stack.Add(PreferenceToggle("Monitor disk pressure", "dockswain.fleet.diskEnabled", true));
        stack.Add(PreferenceNumber("Disk warning (%)", "dockswain.fleet.disk", 85, 1, 100));
        stack.Add(PreferenceToggle("Monitor certificate expiry", "dockswain.fleet.sslEnabled", true));
        stack.Add(PreferenceNumber("SSL warning window (days)", "dockswain.fleet.sslDays", 14, 1, 180));
        stack.Add(PreferenceToggle("Detect locally updated images", "dockswain.fleet.images", true));
        stack.Add(PreferenceNumber("Restart warning count", "dockswain.fleet.restartCount", 3, 1, 100));
        stack.Add(PreferenceNumber("Restart window (minutes)", "dockswain.fleet.restartWindow", 60, 5, 1440));
        stack.Add(new Label
        {
            Text = "Fleet monitoring covers every configured profile while Dockswain Mobile is active. Mobile operating systems may suspend polling in the background.",
            FontSize = 12, TextColor = Theme.TextMuted
        });

        if (_servers.Count == 0)
        {
            stack.Add(EmptyCard("No servers configured. Add an SSH target with Docker access."));
        }

        foreach (var server in _servers)
        {
            var actions = new FlexLayout { Wrap = FlexWrap.Wrap };
            actions.Add(ActionButton("Edit", async () => await OpenServerEditorAsync(server)));
            actions.Add(ActionButton("Test", async () =>
            {
                var runtime = await _backend.RefreshRuntimeAsync(server);
                await this.ThemedAlert("Connected", $"{runtime.Containers.Count} containers found.", "OK");
            }));
            actions.Add(ActionButton("Remove", async () => await DeleteServerAsync(server), accent: Theme.Negative));
            stack.Add(PanelCard(server.DisplayName, new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    DataRow("host", $"{server.Target}:{server.Port}"),
                    DataRow("auth", $"{server.AuthMode}"),
                    DataRow("docker", server.DockerCommand),
                    DataRow("nginx", server.NginxDirectory),
                    DataRow("sudo", server.UseSudo ? "on" : "off"),
                    actions
                }
            }));
        }

        _content.Content = Scroll(stack);
        SetStatus("Dockswain Mobile 1.0.4. Server metadata is stored in Preferences. Passwords and private keys are stored in SecureStorage.");
    }

    private async Task ImportQrWithAlertAsync(string raw)
    {
        try
        {
            await ImportQrAsync(raw);
        }
        catch (Exception ex)
        {
            var message = ex.Message.Contains("Dockswain Mobile 1.0.4 QR parser", StringComparison.Ordinal)
                ? ex.Message
                : $"Dockswain Mobile 1.0.4 QR parser failed: {ex.Message}";
            SetStatus(message.ReplaceLineEndings(" "));
            await this.ThemedAlert("QR import failed", message, "OK");
        }
    }

    private async Task ImportQrAsync(string raw)
    {
        raw = raw.Trim();
        if (!MobileImportService.LooksLikeImportText(raw))
        {
            await this.ThemedAlert("QR text", $"This does not look like a Dockswain QR:\n{Preview(raw)}", "OK");
        }

        var result = await _mobileImport.ImportAsync(raw, _servers);
        await _settings.SaveServersAsync(_servers);
        await LoadServersAsync();
        await this.ThemedAlert(
            "Imported",
            $"Added {result.Added}, updated {result.Updated}. Stored {result.Secrets} secret(s).",
            "OK");
    }

    private static string Preview(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 160 ? value : value[..160] + "...";
    }

    private ServerProfile CurrentServer()
    {
        return _server ?? throw new InvalidOperationException("No server selected.");
    }

    private async Task SafeAsync(Func<Task> work, bool showBusy = true)
    {
        try
        {
            if (showBusy)
            {
                _busy.IsRunning = true;
            }

            await work();
        }
        catch (Exception ex)
        {
            var mapped = RemoteExceptionMapper.Map(ex);
            SetStatus(mapped.Message.ReplaceLineEndings(" "));
            await this.ThemedAlert("Dockswain", mapped.Message, "OK");
        }
        finally
        {
            if (showBusy)
            {
                _busy.IsRunning = false;
            }
        }
    }

    private Button ActionButton(string text, Func<Task> action, Color? accent = null)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 11,
            Padding = new Thickness(10, 4),
            MinimumHeightRequest = 30,
            Margin = new Thickness(0, 0, 6, 6)
        };
        if (accent is not null)
        {
            button.BackgroundColor = accent;
            button.TextColor = Theme.AccentText;
        }
        button.Clicked += async (_, _) => await SafeAsync(action);
        return button;
    }

    private static Button DirectActionButton(string text, Func<Task> action)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 12,
            Padding = new Thickness(10, 5),
            MinimumHeightRequest = 34,
            Margin = new Thickness(0, 0, 6, 6)
        };
        button.Clicked += async (_, _) => await action();
        return button;
    }

    private static Button HeaderButton(string text, Func<Task> action)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 12,
            Padding = new Thickness(10, 5),
            MinimumHeightRequest = 34
        };
        button.Clicked += async (_, _) => await action();
        return button;
    }

    private static View Toggle(string text, bool value, Action<bool> changed)
    {
        var toggle = new Switch { IsToggled = value, VerticalOptions = LayoutOptions.Center };
        toggle.Toggled += (_, e) => changed(e.Value);
        return new HorizontalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = text, FontSize = 12, VerticalTextAlignment = TextAlignment.Center },
                toggle
            }
        };
    }

    private static View PreferenceToggle(string text, string key, bool defaultValue)
    {
        var toggle = new Switch { IsToggled = Preferences.Default.Get(key, defaultValue), VerticalOptions = LayoutOptions.Center };
        toggle.Toggled += (_, e) => Preferences.Default.Set(key, e.Value);
        return new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            Children =
            {
                new Label { Text = text, FontSize = 12, VerticalTextAlignment = TextAlignment.Center },
                toggle.AtColumn(1)
            }
        };
    }

    private static View PreferenceNumber(string text, string key, int defaultValue, int minimum, int maximum,
        Action<int>? changed = null)
    {
        var entry = new Entry
        {
            Text = Preferences.Default.Get(key, defaultValue).ToString(), Keyboard = Keyboard.Numeric,
            WidthRequest = 90, HorizontalTextAlignment = TextAlignment.End
        };
        entry.Unfocused += (_, _) =>
        {
            var value = int.TryParse(entry.Text, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : defaultValue;
            entry.Text = value.ToString(); Preferences.Default.Set(key, value); changed?.Invoke(value);
        };
        return new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            Children =
            {
                new Label { Text = text, FontSize = 12, VerticalTextAlignment = TextAlignment.Center },
                entry.AtColumn(1)
            }
        };
    }

    private static VerticalStackLayout PageStack()
    {
        return new VerticalStackLayout
        {
            Padding = 12,
            Spacing = 8
        };
    }

    private static ScrollView Scroll(View content)
    {
        return new ScrollView { Content = content };
    }

    private static View Card(View content) => Theme.Card(content);

    private static View PanelCard(string header, View body, View? headerTrailing = null)
        => Theme.PanelCard(header, body, headerTrailing);

    private static View DataRow(string label, string value) => Theme.DataRow(label, value);

    private static View EmptyCard(string text)
    {
        return Card(new Label { Text = text, TextColor = Theme.TextMuted, HorizontalTextAlignment = TextAlignment.Center });
    }

    private static Label SectionLabel(string text)
    {
        return new Label
        {
            Text = text,
            FontAttributes = FontAttributes.Bold,
            TextColor = Theme.TextMuted,
            FontSize = 13,
            Margin = new Thickness(0, 10, 0, 0)
        };
    }

    private static View TitleRow(string title, View action)
    {
        return new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            Children =
            {
                new Label { Text = title, FontSize = 22, FontAttributes = FontAttributes.Bold, VerticalTextAlignment = TextAlignment.Center },
                action.AtColumn(1)
            }
        };
    }

    private void RenderEmpty(string title, string subtitle)
    {
        var stack = PageStack();
        stack.Add(new Label { Text = title, FontAttributes = FontAttributes.Bold, FontSize = 22 });
        stack.Add(EmptyCard(subtitle));
        stack.Add(ActionButton("Open Settings", async () =>
        {
            _feature = Feature.Settings;
            SyncTabs();
            RenderSettings();
            await Task.CompletedTask;
        }));
        _content.Content = Scroll(stack);
    }

    private void SyncTabs()
    {
        foreach (var (feature, button) in _tabButtons)
        {
            var active = feature == _feature;
            button.BackgroundColor = active ? Theme.Accent : Theme.SurfaceAlt;
            button.TextColor = active ? Theme.AccentText : Theme.TextMuted;
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
    }

    private static string FeatureTitle(Feature feature)
    {
        return feature switch
        {
            Feature.Fleet => "Fleet",
            Feature.Containers => "Containers",
            Feature.Compose => "Compose",
            Feature.Disk => "Disk",
            Feature.Files => "Files",
            Feature.Nginx => "Nginx",
            Feature.Settings => "Settings",
            _ => feature.ToString()
        };
    }
}

internal static class MainPageGridExtensions
{
    public static T AtColumn<T>(this T view, int column) where T : View
    {
        Grid.SetColumn(view, column);
        return view;
    }

    public static T AtRow<T>(this T view, int row) where T : View
    {
        Grid.SetRow(view, row);
        return view;
    }
}
