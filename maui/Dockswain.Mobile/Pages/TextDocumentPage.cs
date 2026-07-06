using Dockswain.Mobile;

namespace Dockswain.Mobile.Pages;

public sealed class TextDocumentPage : ContentPage
{
    private readonly Editor _editor;
    private readonly Func<string, Task>? _save;

    public TextDocumentPage(string title, string content, bool readOnly = false, Func<string, Task>? save = null)
    {
        Title = title;
        _save = save;
        _editor = new Editor
        {
            Text = content,
            IsReadOnly = readOnly,
            AutoSize = EditorAutoSizeOption.Disabled,
            FontFamily = "monospace",
            FontSize = 13,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };

        var toolbar = new HorizontalStackLayout
        {
            Padding = new Thickness(12, 8),
            Spacing = 8
        };

        if (!readOnly && save is not null)
        {
            toolbar.Add(ActionButton("Save", async () =>
            {
                await save(_editor.Text ?? "");
                await this.ThemedAlert("Saved", "Remote file updated.", "OK");
            }));
        }

        toolbar.Add(ActionButton("Share", async () =>
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = title,
                Text = _editor.Text ?? ""
            });
        }));
        toolbar.Add(ActionButton("Close", async () => await Navigation.PopAsync()));

        Grid.SetRow(_editor, 1);
        Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            Children =
            {
                toolbar,
                _editor
            }
        };
    }

    private static Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button
        {
            Text = text,
            Padding = new Thickness(12, 6),
            MinimumHeightRequest = 36
        };
        button.Clicked += async (_, _) => await action();
        return button;
    }
}
