using Microsoft.Maui.Controls.Shapes;

namespace Dockswain.Mobile;

/// <summary>
/// Central dark palette + small view factories, matching the Plasma widget
/// (Breeze-dark neutrals with the Dockswain teal accent). Used from the
/// code-behind builders; the XAML side mirrors these in Colors.xaml/Styles.xaml.
/// The look is a flat desktop-widget panel: hairline borders, 4px corners,
/// accent-tinted header strips, and monospace value columns.
/// </summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb("#16191C");
    public static readonly Color Surface = Color.FromArgb("#21262B");
    public static readonly Color SurfaceAlt = Color.FromArgb("#2B3138");
    public static readonly Color Border = Color.FromArgb("#333B42");
    public static readonly Color TextPrimary = Color.FromArgb("#E6EBEF");
    public static readonly Color TextMuted = Color.FromArgb("#9AA7B0");
    public static readonly Color Accent = Color.FromArgb("#1F6F8B");
    public static readonly Color AccentText = Color.FromArgb("#FFFFFF");
    public static readonly Color Positive = Color.FromArgb("#3FB950");
    public static readonly Color Warning = Color.FromArgb("#D29922");
    public static readonly Color Negative = Color.FromArgb("#E05561");

    // Flat panel geometry.
    public const int CornerRadius = 4;

    /// <summary>Platform default monospace family for aligned value columns.</summary>
    public static readonly string Mono =
        DeviceInfo.Platform == DevicePlatform.Android ? "monospace" :
        DeviceInfo.Platform == DevicePlatform.WinUI ? "Consolas" :
        "Menlo"; // iOS / MacCatalyst

    /// <summary>Plain flat card: hairline border, 4px corners, no glow.</summary>
    public static Border Card(View content, double padding = 10) => new()
    {
        Padding = padding,
        StrokeThickness = 1,
        Stroke = Border,
        BackgroundColor = Surface,
        StrokeShape = new RoundRectangle { CornerRadius = CornerRadius },
        Content = content
    };

    /// <summary>
    /// Plasma-widget panel: an accent-ticked <paramref name="header"/> strip over
    /// a hairline, with <paramref name="body"/> below. Replaces the "bold title
    /// floating inside a card" pattern.
    /// </summary>
    public static Border PanelCard(string header, View body, View? headerTrailing = null)
    {
        var bar = new Grid
        {
            BackgroundColor = SurfaceAlt,
            Padding = new Thickness(10, 6),
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(3),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            Children =
            {
                new BoxView { Color = Accent, WidthRequest = 3, HorizontalOptions = LayoutOptions.Start },
                new Label
                {
                    Text = header,
                    FontAttributes = FontAttributes.Bold,
                    FontSize = 13,
                    TextColor = TextPrimary,
                    VerticalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.TailTruncation
                }.AtColumn(1)
            }
        };

        if (headerTrailing is not null)
        {
            bar.Children.Add(headerTrailing.AtColumn(2));
        }

        return new Border
        {
            Padding = 0,
            StrokeThickness = 1,
            Stroke = Border,
            BackgroundColor = Surface,
            StrokeShape = new RoundRectangle { CornerRadius = CornerRadius },
            Content = new VerticalStackLayout
            {
                Children =
                {
                    bar,
                    new BoxView { HeightRequest = 1, Color = Border },
                    new VerticalStackLayout { Padding = 10, Spacing = 4, Children = { body } }
                }
            }
        };
    }

    /// <summary>Label-left, monospace-value-right data row for a panel body.</summary>
    public static Grid DataRow(string label, string value) => new()
    {
        ColumnSpacing = 8,
        ColumnDefinitions =
        {
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(GridLength.Star)
        },
        Children =
        {
            new Label
            {
                Text = label,
                FontSize = 12,
                TextColor = TextMuted,
                VerticalTextAlignment = TextAlignment.Center
            },
            new Label
            {
                Text = value,
                FontSize = 12,
                FontFamily = Mono,
                TextColor = TextPrimary,
                HorizontalTextAlignment = TextAlignment.End,
                HorizontalOptions = LayoutOptions.End,
                LineBreakMode = LineBreakMode.TailTruncation
            }.AtColumn(1)
        }
    };

    public static Button PrimaryButton(string text) => new()
    {
        Text = text,
        BackgroundColor = Accent,
        TextColor = AccentText
    };

    public static Button DangerButton(string text) => new()
    {
        Text = text,
        BackgroundColor = Negative,
        TextColor = AccentText
    };

    public static Label Muted(string text, double size = 12) => new()
    {
        Text = text,
        FontSize = size,
        TextColor = TextMuted
    };
}
