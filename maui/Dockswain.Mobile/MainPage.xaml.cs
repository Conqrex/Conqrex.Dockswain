using Dockswain.Mobile.Models;
using Dockswain.Mobile.Pages;
using Dockswain.Mobile.Services;
using Microsoft.Maui.Layouts;

namespace Dockswain.Mobile;

public partial class MainPage : ContentPage
{
    private enum Feature
    {
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

    private List<ServerProfile> _servers = [];
    private ServerProfile? _server;
    private Feature _feature = Feature.Containers;
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

        Loaded += async (_, _) => await SafeAsync(LoadServersAsync);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _refreshTimer?.Start();
    }

    protected override void OnDisappearing()
    {
        _refreshTimer?.Stop();
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
