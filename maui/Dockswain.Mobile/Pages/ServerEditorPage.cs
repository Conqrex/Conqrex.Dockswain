using Dockswain.Mobile;
using Dockswain.Mobile.Models;
using Dockswain.Mobile.Services;

namespace Dockswain.Mobile.Pages;

public sealed class ServerEditorPage : ContentPage
{
    private readonly SettingsStore _settings;
    private readonly ServerProfile _server;
    private readonly Func<ServerProfile, Task> _save;

    private readonly Entry _label = new() { Placeholder = "Label" };
    private readonly Entry _host = new() { Placeholder = "Host or IP" };
    private readonly Entry _user = new() { Placeholder = "User" };
    private readonly Entry _port = new() { Placeholder = "Port", Keyboard = Keyboard.Numeric };
    private readonly Picker _auth = new() { Title = "Authentication" };
    private readonly Editor _secret = new() { AutoSize = EditorAutoSizeOption.TextChanges, HeightRequest = 130 };
    private readonly Entry _passphrase = new() { Placeholder = "Private key passphrase", IsPassword = true };
    private readonly Switch _sudo = new();
    private readonly Entry _docker = new() { Placeholder = "docker command" };
    private readonly Entry _nginx = new() { Placeholder = "nginx directory" };
    private readonly Entry _timeout = new() { Placeholder = "SSH timeout", Keyboard = Keyboard.Numeric };

    public ServerEditorPage(SettingsStore settings, ServerProfile? server, Func<ServerProfile, Task> save)
    {
        _settings = settings;
        _server = Clone(server) ?? new ServerProfile();
        _save = save;
        Title = server is null ? "Add server" : "Edit server";

        _auth.Items.Add("Password");
        _auth.Items.Add("Private key");
        _auth.SelectedIndexChanged += (_, _) => SyncSecretPlaceholder();

        Populate();
        Content = BuildContent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_server.AuthMode == ServerAuthMode.Password)
        {
            _secret.Text = await _settings.GetPasswordAsync(_server);
        }
        else
        {
            _secret.Text = await _settings.GetPrivateKeyAsync(_server);
            _passphrase.Text = await _settings.GetPrivateKeyPassphraseAsync(_server);
        }
    }

    private View BuildContent()
    {
        var save = new Button { Text = "Save", HorizontalOptions = LayoutOptions.Fill };
        save.Clicked += async (_, _) => await SaveAsync();

        var cancel = new Button { Text = "Cancel", HorizontalOptions = LayoutOptions.Fill };
        cancel.Clicked += async (_, _) => await Navigation.PopAsync();

        return new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    Section("Connection"),
                    _label,
                    _host,
                    new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition(GridLength.Star),
                            new ColumnDefinition(new GridLength(110))
                        },
                        ColumnSpacing = 10,
                        Children =
                        {
                            _user,
                            _port.Column(1)
                        }
                    },
                    _auth,
                    _secret,
                    _passphrase,
                    Row("Use sudo for privileged nginx/log operations", _sudo),
                    Section("Remote commands"),
                    _docker,
                    _nginx,
                    _timeout,
                    new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition(GridLength.Star),
                            new ColumnDefinition(GridLength.Star)
                        },
                        ColumnSpacing = 10,
                        Children =
                        {
                            cancel,
                            save.Column(1)
                        }
                    }
                }
            }
        };
    }

    private void Populate()
    {
        _label.Text = _server.Label;
        _host.Text = _server.Host;
        _user.Text = _server.User;
        _port.Text = _server.Port.ToString();
        _auth.SelectedIndex = _server.AuthMode == ServerAuthMode.Password ? 0 : 1;
        _sudo.IsToggled = _server.UseSudo;
        _docker.Text = string.IsNullOrWhiteSpace(_server.DockerCommand) ? "docker" : _server.DockerCommand;
        _nginx.Text = string.IsNullOrWhiteSpace(_server.NginxDirectory) ? "/etc/nginx" : _server.NginxDirectory;
        _timeout.Text = _server.ConnectTimeoutSeconds.ToString();
        SyncSecretPlaceholder();
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_host.Text))
        {
            await this.ThemedAlert("Host required", "Enter the SSH host or IP address.", "OK");
            return;
        }

        _server.Label = _label.Text?.Trim() ?? "";
        _server.Host = _host.Text?.Trim() ?? "";
        _server.User = string.IsNullOrWhiteSpace(_user.Text) ? "root" : _user.Text.Trim();
        _server.Port = int.TryParse(_port.Text, out var port) ? port : 22;
        _server.AuthMode = _auth.SelectedIndex == 1 ? ServerAuthMode.PrivateKey : ServerAuthMode.Password;
        _server.UseSudo = _sudo.IsToggled;
        _server.DockerCommand = string.IsNullOrWhiteSpace(_docker.Text) ? "docker" : _docker.Text.Trim();
        _server.NginxDirectory = string.IsNullOrWhiteSpace(_nginx.Text) ? "/etc/nginx" : _nginx.Text.Trim();
        _server.ConnectTimeoutSeconds = int.TryParse(_timeout.Text, out var timeout) ? Math.Clamp(timeout, 3, 60) : 8;

        if (_server.AuthMode == ServerAuthMode.Password)
        {
            await _settings.SetPasswordAsync(_server, _secret.Text ?? "");
            await _settings.SetPrivateKeyAsync(_server, "");
            await _settings.SetPrivateKeyPassphraseAsync(_server, "");
        }
        else
        {
            await _settings.SetPrivateKeyAsync(_server, _secret.Text ?? "");
            await _settings.SetPrivateKeyPassphraseAsync(_server, _passphrase.Text ?? "");
            await _settings.SetPasswordAsync(_server, "");
        }

        await _save(_server);
        await Navigation.PopAsync();
    }

    private void SyncSecretPlaceholder()
    {
        var key = _auth.SelectedIndex == 1;
        _secret.Placeholder = key ? "Paste private key" : "Password";
        _passphrase.IsVisible = key;
    }

    private static ServerProfile? Clone(ServerProfile? source)
    {
        if (source is null)
        {
            return null;
        }

        return new ServerProfile
        {
            Id = source.Id,
            Label = source.Label,
            Host = source.Host,
            User = source.User,
            Port = source.Port,
            AuthMode = source.AuthMode,
            UseSudo = source.UseSudo,
            DockerCommand = source.DockerCommand,
            NginxDirectory = source.NginxDirectory,
            ConnectTimeoutSeconds = source.ConnectTimeoutSeconds
        };
    }

    private static Label Section(string text)
    {
        return new Label
        {
            Text = text,
            FontAttributes = FontAttributes.Bold,
            FontSize = 13,
            TextColor = Theme.TextMuted,
            Margin = new Thickness(0, 8, 0, 0)
        };
    }

    private static Grid Row(string text, View control)
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
                new Label { Text = text, VerticalTextAlignment = TextAlignment.Center },
                control.Column(1)
            }
        };
    }
}

internal static class ViewGridExtensions
{
    public static T Column<T>(this T view, int column) where T : View
    {
        Grid.SetColumn(view, column);
        return view;
    }

    public static T Row<T>(this T view, int row) where T : View
    {
        Grid.SetRow(view, row);
        return view;
    }
}
