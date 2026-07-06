using Microsoft.Maui.Controls.Shapes;

namespace Dockswain.Mobile;

/// <summary>
/// Themed replacements for the native <c>Display*Async</c> dialogs. Each shows a
/// dark rounded card over a dim scrim (matching the Plasma widget) as a
/// translucent modal page, so it works from any page without touching that
/// page's own content tree.
/// </summary>
internal static class PageDialogExtensions
{
    public static Task ThemedAlert(this Page page, string title, string message, string ok = "OK")
        => page.ShowAsync<object?>(dialog => ThemedDialogPage.Info(dialog, title, message, ok));

    public static Task<bool> ThemedConfirm(this Page page, string title, string message, string accept, string cancel)
        => page.ShowAsync<bool>(dialog => ThemedDialogPage.Confirm(dialog, title, message, accept, cancel));

    public static Task<string?> ThemedPrompt(this Page page, string title, string message,
        string? initialValue = null, string accept = "OK", string cancel = "Cancel")
        => page.ShowAsync<string?>(dialog => ThemedDialogPage.Prompt(dialog, title, message, initialValue, accept, cancel));

    public static Task<string?> ThemedSheet(this Page page, string title, string cancel,
        string? destruction, params string[] buttons)
        => page.ShowAsync<string?>(dialog => ThemedDialogPage.Sheet(dialog, title, cancel, destruction, buttons));

    private static async Task<T> ShowAsync<T>(this Page page, Func<ThemedDialogPage, View> build)
    {
        var tcs = new TaskCompletionSource<object?>();
        var dialog = new ThemedDialogPage(tcs);
        dialog.SetBody(build(dialog));
        await page.Navigation.PushModalAsync(dialog, animated: false);
        var result = await tcs.Task;
        return result is T typed ? typed : default!;
    }
}

internal sealed class ThemedDialogPage : ContentPage
{
    private readonly TaskCompletionSource<object?> _result;
    private readonly VerticalStackLayout _card;

    public ThemedDialogPage(TaskCompletionSource<object?> result)
    {
        _result = result;
        BackgroundColor = Color.FromArgb("#B3000000"); // dim scrim
        NavigationPage.SetHasNavigationBar(this, false);

        _card = new VerticalStackLayout { Spacing = 14 };

        Content = new Grid
        {
            Padding = 24,
            Children =
            {
                new Border
                {
                    Padding = 20,
                    BackgroundColor = Theme.Surface,
                    Stroke = Theme.Border,
                    StrokeThickness = 1,
                    StrokeShape = new RoundRectangle { CornerRadius = 16 },
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    MaximumWidthRequest = 420,
                    Content = _card
                }
            }
        };
    }

    public void SetBody(View body)
    {
        _card.Children.Clear();
        _card.Add(body);
    }

    protected override bool OnBackButtonPressed()
    {
        Complete(null);
        return true;
    }

    private async void Complete(object? value)
    {
        if (_result.Task.IsCompleted)
        {
            return;
        }

        _result.SetResult(value);
        await Navigation.PopModalAsync(animated: false);
    }

    private static Label TitleLabel(string text) => new()
    {
        Text = text,
        FontSize = 18,
        FontAttributes = FontAttributes.Bold,
        TextColor = Theme.TextPrimary
    };

    private static Label Message(string text) => new()
    {
        Text = text,
        FontSize = 14,
        TextColor = Theme.TextMuted
    };

    private Button Primary(string text, object? result)
    {
        var b = Theme.PrimaryButton(text);
        b.Clicked += (_, _) => Complete(result);
        return b;
    }

    private Button Ghost(string text, object? result)
    {
        var b = new Button
        {
            Text = text,
            BackgroundColor = Theme.SurfaceAlt,
            TextColor = Theme.TextPrimary
        };
        b.Clicked += (_, _) => Complete(result);
        return b;
    }

    private static Grid TwoButtons(View left, View right) => new()
    {
        ColumnSpacing = 10,
        ColumnDefinitions =
        {
            new ColumnDefinition(GridLength.Star),
            new ColumnDefinition(GridLength.Star)
        },
        Children = { left.AtColumn(0), right.AtColumn(1) }
    };

    public static View Info(ThemedDialogPage d, string title, string message, string ok)
        => new VerticalStackLayout
        {
            Spacing = 14,
            Children = { TitleLabel(title), Message(message), d.Primary(ok, null) }
        };

    public static View Confirm(ThemedDialogPage d, string title, string message, string accept, string cancel)
        => new VerticalStackLayout
        {
            Spacing = 14,
            Children = { TitleLabel(title), Message(message), TwoButtons(d.Ghost(cancel, false), d.Primary(accept, true)) }
        };

    public static View Prompt(ThemedDialogPage d, string title, string message, string? initialValue, string accept, string cancel)
    {
        var entry = new Entry
        {
            Text = initialValue ?? "",
            Placeholder = message,
            TextColor = Theme.TextPrimary,
            BackgroundColor = Theme.SurfaceAlt
        };
        var ok = new Button { Text = accept, BackgroundColor = Theme.Accent, TextColor = Theme.AccentText };
        ok.Clicked += (_, _) => d.Complete(entry.Text ?? "");
        return new VerticalStackLayout
        {
            Spacing = 14,
            Children =
            {
                TitleLabel(title),
                new Border
                {
                    Padding = new Thickness(10, 2),
                    BackgroundColor = Theme.SurfaceAlt,
                    Stroke = Theme.Border,
                    StrokeThickness = 1,
                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                    Content = entry
                },
                TwoButtons(d.Ghost(cancel, null), ok)
            }
        };
    }

    public static View Sheet(ThemedDialogPage d, string title, string cancel, string? destruction, string[] buttons)
    {
        var stack = new VerticalStackLayout { Spacing = 10, Children = { TitleLabel(title) } };
        if (!string.IsNullOrEmpty(destruction))
        {
            var del = new Button { Text = destruction, BackgroundColor = Theme.Negative, TextColor = Theme.AccentText };
            del.Clicked += (_, _) => d.Complete(destruction);
            stack.Add(del);
        }

        foreach (var label in buttons)
        {
            stack.Add(d.Primary(label, label));
        }

        stack.Add(d.Ghost(cancel, null));
        return stack;
    }
}
