using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;
using Dockswain.Mobile;
using Dockswain.Mobile.Services;

namespace Dockswain.Mobile.Pages;

public sealed class QrScanPage : ContentPage
{
    private readonly Func<string, Task> _onScanned;
    private readonly CameraBarcodeReaderView _camera;
    private readonly Label _status;
    private bool _handled;

    public QrScanPage(Func<string, Task> onScanned)
    {
        _onScanned = onScanned;
        Title = "Scan QR";

        _camera = new CameraBarcodeReaderView
        {
            IsDetecting = true,
            CameraLocation = CameraLocation.Rear,
            Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormats.TwoDimensional,
                AutoRotate = true,
                Multiple = false,
                TryHarder = true
            }
        };
        _camera.BarcodesDetected += OnBarcodesDetected;
        _status = new Label
        {
            Text = "Point the camera at a Dockswain QR code.",
            TextColor = Colors.White,
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.Center
        };

        var paste = new Button { Text = "Paste QR payload" };
        paste.Clicked += async (_, _) =>
        {
            var text = await Clipboard.Default.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                text = await this.ThemedPrompt("Paste QR payload", "Paste dockswain:// import text");
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                await HandleAsync(text);
            }
        };

        var torch = new Button { Text = "Torch" };
        torch.Clicked += (_, _) => _camera.IsTorchOn = !_camera.IsTorchOn;

        var close = new Button { Text = "Close" };
        close.Clicked += async (_, _) => await Navigation.PopAsync();

        Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            },
            Children =
            {
                _camera,
                new Border
                {
                    Padding = new Thickness(12, 8),
                    StrokeThickness = 0,
                    BackgroundColor = Color.FromArgb("#99000000"),
                    Content = _status
                }.Row(1),
                new Grid
                {
                    Padding = 12,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Star)
                    },
                    ColumnSpacing = 8,
                    Children =
                    {
                        paste,
                        torch.Column(1),
                        close.Column(2)
                    }
                }.Row(2)
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
        }

        if (status != PermissionStatus.Granted)
        {
            _camera.IsDetecting = false;
            await this.ThemedAlert("Camera permission", "Camera access is required to scan a QR code. You can still paste the payload.", "OK");
        }
    }

    protected override void OnDisappearing()
    {
        _camera.IsDetecting = false;
        _camera.BarcodesDetected -= OnBarcodesDetected;
        base.OnDisappearing();
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var value = e.Results
            .Select(r => r.Value)
            .FirstOrDefault(MobileImportService.LooksLikeImportText);
        if (string.IsNullOrWhiteSpace(value))
        {
            var ignored = e.Results.FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(ignored))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _status.Text = "Ignored a non-Dockswain QR. Move closer and center the Dockswain code.";
                });
            }

            return;
        }

        MainThread.BeginInvokeOnMainThread(async () => await HandleAsync(value));
    }

    private async Task HandleAsync(string value)
    {
        if (_handled)
        {
            return;
        }

        _handled = true;
        _camera.IsDetecting = false;
        try
        {
            await _onScanned(value);
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            _handled = false;
            _camera.IsDetecting = true;
            var message = ex.Message.Contains("Dockswain Mobile 1.0.4 QR parser", StringComparison.Ordinal)
                ? ex.Message
                : $"Dockswain Mobile 1.0.4 QR parser failed: {ex.Message}";
            await this.ThemedAlert("QR import failed", message, "OK");
        }
    }
}
