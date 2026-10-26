using Apex.Core;
using Apex.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Apex.UI;

/// Assistant, as on the Mac: **Apex Agent** answers the common questions with
/// this PC's readings and carries out commands (end apps, stop processes,
/// Game mode with a frame-rate target) after you press its button; **Local
/// AI** chats with a model running on this PC. Nothing leaves the PC.
internal sealed class AssistantPage : IPage
{
    public UIElement View => root;
    readonly Grid root = new() { Padding = new Thickness(28, 16, 28, 16), RowSpacing = 12 };
    readonly StackPanel messages = new() { Spacing = 12, MaxWidth = 760 };
    readonly ScrollViewer scroll = new();
    readonly TextBox input = new() { PlaceholderText = "Ask about your PC, or say what to do (“close everything except Spotify”, “steady 60 FPS in my game”)", AcceptsReturn = false };
    readonly Button send = new() { Content = Theme.Icon("", 16), Padding = new Thickness(14, 8, 14, 8) };
    readonly RadioButtons mode = new() { MaxColumns = 2 };
    readonly TextBlock footnote = Theme.Text("", 12, Theme.Secondary);
    readonly ComboBox model = new() { MinWidth = 220, Visibility = Visibility.Collapsed };
    readonly List<(string Role, string Text)> history = new();
    LocalChat.Server? server;
    CancellationTokenSource? answering;

    public AssistantPage()
    {
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(Theme.Title("Assistant"));
        titles.Children.Add(Theme.Text("Answers about this PC, worked out on this PC.", 14, Theme.Secondary));
        head.Children.Add(titles);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        mode.Items.Add("Apex Agent"); mode.Items.Add("Local AI");
        mode.SelectedIndex = Settings.Get("assistantMode", 0);
        right.Children.Add(model); right.Children.Add(mode);
        Grid.SetColumn(right, 1); head.Children.Add(right);
        root.Children.Add(head);

        scroll.Content = messages;
        messages.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        var bottom = new StackPanel { Spacing = 4, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.CornerRadius = new CornerRadius(18); input.Padding = new Thickness(14, 8, 14, 8);
        bar.Children.Add(input);
        send.CornerRadius = new CornerRadius(18);
        Grid.SetColumn(send, 1); bar.Children.Add(send);
        bottom.Children.Add(bar);
        bottom.Children.Add(footnote);
        Grid.SetRow(bottom, 2); root.Children.Add(bottom);

        send.Click += (_, _) => Send();
        input.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Send(); e.Handled = true; } };
        mode.SelectionChanged += (_, _) => { Settings.Set("assistantMode", mode.SelectedIndex); _ = ModeChanged(); };
        Welcome();
        _ = ModeChanged();
    }

    public void Update(Snapshot s) { }
    public void Shown() => input.Focus(FocusState.Programmatic);

    bool Local => mode.SelectedIndex == 1;

    async Task ModeChanged()
    {
        model.Visibility = Visibility.Collapsed;
        if (!Local) { footnote.Text = "Apex Agent answers on this PC. Nothing you ask is sent anywhere."; return; }
        footnote.Text = "Looking for a local AI server…";
        var (s, models) = await LocalChat.Find();
        server = s;
        if (s == null || models.Count == 0)
        {
            footnote.Text = "No local AI server is running. Start LM Studio’s server (Developer › Start Server) or Ollama, then switch to Local AI again.";
            return;
        }
        model.Items.Clear();
        foreach (var m in models) model.Items.Add(m);
        var saved = Settings.Get("assistantModel", "");
        model.SelectedIndex = Math.Max(0, models.IndexOf(saved));
        model.SelectionChanged += (_, _) => Settings.Set("assistantModel", model.SelectedItem as string ?? "");
        model.Visibility = Visibility.Visible;
        footnote.Text = $"{model.SelectedItem} answers on this PC, through {s.Name}. Nothing leaves the PC.";
    }

    void Welcome()
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Theme.Text("Try asking:", 13, Theme.Secondary));
        var chips = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 250, ItemHeight = 40 };
        foreach (var q in new[] { "Why is my PC slow?", "What’s filling my disk?", "Steady 60 FPS in my game", "Close everything except Spotify", "Is my memory full?", "My Wi-Fi keeps disconnecting" })
        {
            var b = new Button { Content = q, HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) => { input.Text = q; Send(); };
            chips.Children.Add(b);
        }
        p.Children.Add(chips);
        messages.Children.Add(p);
    }

    void Send()
    {
        var text = input.Text.Trim();
        if (text.Length == 0 || answering != null) return;
        input.Text = "";
        Bubble(text, mine: true);
        if (Local) _ = AskLocal(text);
        else Agent(text);
    }

    void Agent(string text)
    {
        var a = ApexAgent.Ask(text, AgentBridge.Readings());
        var bubble = Bubble(a.Text, mine: false);
        if (a.Actions.Count == 0) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var act in a.Actions)
        {
            var b = new Button { Content = act.Title };
            if (act.Acts) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            b.Click += (_, _) =>
            {
                if (act.Acts) foreach (var other in row.Children.OfType<Button>()) other.IsEnabled = false;     // one press each
                var said = AgentBridge.Perform(act, root);
                if (said != null) Bubble(said, mine: false);
            };
            row.Children.Add(b);
        }
        bubble.Children.Add(row);
    }

    async Task AskLocal(string text)
    {
        if (server == null || model.SelectedItem is not string m) { Bubble("No local AI server is running. Start LM Studio’s server or Ollama, then try again.", mine: false); return; }
        history.Add(("user", text));
        var bubble = Bubble("…", mine: false);
        var block = (RichTextBlock)bubble.Children[0];
        var answer = new System.Text.StringBuilder();
        answering = new CancellationTokenSource();
        send.IsEnabled = false;
        try
        {
            var readings = AgentBridge.ReadingsText();
            await LocalChat.Ask(server, m, history, readings, piece => MainWindow.Ui(() =>
            {
                answer.Append(piece);
                Render(block, answer.ToString());
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            }), answering.Token);
            history.Add(("assistant", answer.ToString()));
        }
        catch (Exception e) { Render(block, "The local model didn’t answer: " + e.Message); }
        finally { answering = null; send.IsEnabled = true; }
    }

    /// A message bubble; Apex's on the left, yours on the right.
    StackPanel Bubble(string text, bool mine)
    {
        var block = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 14 };
        Render(block, text);
        var inner = new StackPanel { Spacing = 10 };
        inner.Children.Add(block);
        var card = new Border
        {
            Child = inner, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(16), MaxWidth = 640,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = mine ? Theme.Accent : Theme.CardFill, BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(mine ? 0 : 1),
        };
        if (mine) block.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        messages.Children.Add(card);
        DispatcherQueueExtensions(() => scroll.ChangeView(null, scroll.ScrollableHeight, null));
        return inner;
    }

    void DispatcherQueueExtensions(Action a) => root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => a());

    /// Plain text with **bold**, line breaks and "- " / "1. " lists.
    static void Render(RichTextBlock block, string text)
    {
        block.Blocks.Clear();
        foreach (var line in text.Split('\n'))
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, line.Length == 0 ? 4 : 2) };
            var parts = line.Split("**");
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                var run = new Run { Text = parts[i] };
                if (i % 2 == 1) p.Inlines.Add(new Bold { Inlines = { run } });
                else p.Inlines.Add(run);
            }
            block.Blocks.Add(p);
        }
    }
}
