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
/// AI** chats with a model running on this PC (nothing leaves the PC);
/// **Lumen AI** is CyberNet Labs' own cloud AI (a monthly plan or API access,
/// with a Lumen key; it says so while it isn't open yet).
internal sealed class AssistantPage : IPage
{
    public UIElement View => root;
    readonly Grid root = new() { Padding = new Thickness(28, 16, 28, 16), RowSpacing = 12 };
    readonly StackPanel messages = new() { Spacing = 12, MaxWidth = 760 };
    readonly ScrollViewer scroll = new();
    readonly TextBox input = new() { PlaceholderText = "Ask about your PC, or say what to do (“close everything except Spotify”, “steady 60 FPS in my game”)", AcceptsReturn = false };
    readonly Button send = new() { Content = Theme.Icon("", 16), Padding = new Thickness(14, 8, 14, 8) };
    readonly RadioButtons mode = new() { MaxColumns = 4 };
    readonly HyperlinkButton cloudChange = new() { Content = "Change Key", Visibility = Visibility.Collapsed };
    UIElement? cloudPanel;
    readonly HyperlinkButton lumenSignOut = new() { Content = "Sign Out of Lumen AI", Visibility = Visibility.Collapsed };
    UIElement? lumenPanel;
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
        titles.Children.Add(Theme.Text("Answers about this PC, in plain words.", 14, Theme.Secondary));
        head.Children.Add(titles);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        mode.Items.Add("Apex Agent"); mode.Items.Add("Local AI"); mode.Items.Add("Lumen AI"); mode.Items.Add("Cloud AI");
        mode.SelectedIndex = Settings.Get("assistantMode", 0);
        // --assistant=N opens on that mode (the build's screenshots).
        if (Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--assistant=")) is { } am && int.TryParse(am[12..], out var mi)) mode.SelectedIndex = mi;
        right.Children.Add(lumenSignOut); right.Children.Add(cloudChange); right.Children.Add(model); right.Children.Add(mode);
        cloudChange.Click += (_, _) => { CloudKeys.Forget(); _ = ModeChanged(); };
        model.SelectionChanged += (_, _) =>
        {
            if (model.SelectedItem is not string m) return;
            if (IsCloud) CloudKeys.Model = m; else Settings.Set("assistantModel", m);
        };
        lumenSignOut.Click += (_, _) => { LumenAccount.SignOut(); _ = ModeChanged(); };
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
    bool IsLumen => mode.SelectedIndex == 2;
    bool IsCloud => mode.SelectedIndex == 3;
    bool LumenReady => Lumen.IsLive && LumenAccount.HasKey;

    async Task ModeChanged()
    {
        model.Visibility = Visibility.Collapsed;
        lumenSignOut.Visibility = Visibility.Collapsed;
        cloudChange.Visibility = Visibility.Collapsed;
        if (lumenPanel != null) { messages.Children.Remove(lumenPanel); lumenPanel = null; if (messages.Children.Count == 0) Welcome(); }
        if (cloudPanel != null) { messages.Children.Remove(cloudPanel); cloudPanel = null; if (messages.Children.Count == 0) Welcome(); }
        if (IsCloud)
        {
            if (!CloudKeys.Ready)
            {
                footnote.Text = "Use your own key from an AI provider. Your question and this PC’s readings go to that provider to answer it.";
                messages.Children.Clear();
                cloudPanel = CloudPanel();
                messages.Children.Add(cloudPanel);
                return;
            }
            var service = CloudProviders.Title(CloudKeys.Provider);
            footnote.Text = $"Asking {service} for its models…";
            cloudChange.Visibility = Visibility.Visible;
            try
            {
                var offered = await CloudChat.Models(CloudKeys.Provider, CloudKeys.Base, CloudKeys.Key!);
                if (!IsCloud) return;
                model.Items.Clear();
                foreach (var name in offered) model.Items.Add(name);
                model.SelectedIndex = offered.Count == 0 ? -1 : Math.Max(0, offered.IndexOf(CloudKeys.Model));
                model.Visibility = offered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                footnote.Text = offered.Count == 0 ? $"{service} didn’t offer any chat models for this key."
                    : $"{service} answers with your key. Your question and this PC’s readings are sent to {service}.";
            }
            catch (CloudChatException e) { footnote.Text = e.Message; }
            return;
        }
        if (IsLumen)
        {
            if (LumenReady)
            {
                lumenSignOut.Visibility = Visibility.Visible;
                footnote.Text = "Lumen AI answers, by CyberNet Labs. Your question and this PC’s readings are sent to Lumen to answer it.";
            }
            else
            {
                footnote.Text = Lumen.IsLive ? "Connect Lumen AI with the key from your CyberNet Labs account." : "Lumen AI opens soon.";
                messages.Children.Clear();
                lumenPanel = LumenPanel();
                messages.Children.Add(lumenPanel);
            }
            return;
        }
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
        if (IsLumen && !LumenReady) { _ = ModeChanged(); return; }    // the connect screen says what to do
        if (IsCloud && !CloudKeys.Ready) { _ = ModeChanged(); return; }
        Bubble(text, mine: true);
        if (IsCloud) _ = AskCloud(text);
        else if (IsLumen) _ = AskLumen(text);
        else if (Local) _ = AskLocal(text);
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

    async Task AskCloud(string text)
    {
        if (model.SelectedItem is not string m) { Bubble("Pick a model first (top right).", mine: false); return; }
        history.Add(("user", text));
        var bubble = Bubble("…", mine: false);
        var block = (RichTextBlock)bubble.Children[0];
        var answer = new System.Text.StringBuilder();
        answering = new CancellationTokenSource();
        send.IsEnabled = false;
        try
        {
            var system = LocalChat.Instructions + "\n\nThis PC right now:\n" + AgentBridge.ReadingsText();
            await CloudChat.Ask(CloudKeys.Provider, CloudKeys.Base, CloudKeys.Key!, m, history, system, piece => MainWindow.Ui(() =>
            {
                answer.Append(piece);
                Render(block, answer.ToString());
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            }), answering.Token);
            history.Add(("assistant", answer.ToString()));
        }
        catch (CloudChatException e) { Render(block, e.Message); history.RemoveAt(history.Count - 1); }
        catch (OperationCanceledException) { history.RemoveAt(history.Count - 1); }
        finally { answering = null; send.IsEnabled = true; }
    }

    /// Cloud AI before a key is saved: pick the provider, paste the key, Connect.
    UIElement CloudPanel()
    {
        var p = new StackPanel { Spacing = 12, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
        var title = Theme.Text("Cloud AI with your own key", 26, bold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        p.Children.Add(title);
        var blurb = Theme.Text("Ask a provider’s models about this PC. You pay the provider for what you use; the key stays in Windows’ Credential Manager.", 14, Theme.Secondary);
        blurb.TextWrapping = TextWrapping.Wrap; blurb.TextAlignment = TextAlignment.Center;
        p.Children.Add(blurb);
        var provider = new ComboBox { Header = "Provider", MinWidth = 300 };
        foreach (var x in CloudProviders.All) provider.Items.Add(CloudProviders.Title(x));
        provider.SelectedIndex = Array.IndexOf(CloudProviders.All, CloudKeys.Provider);
        var address = new TextBox { Header = "Address", PlaceholderText = "https://…/v1", Text = CloudKeys.CustomBase };
        var key = new PasswordBox { Header = "API key", PlaceholderText = "Paste your key" };
        var getKey = new HyperlinkButton { Content = "Get a key", Padding = new Thickness(0) };
        var problem = Theme.Text("", 13, Theme.Solid(Theme.Red)); problem.TextWrapping = TextWrapping.Wrap;
        var connect = new Button { Content = "Connect", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        void Changed()
        {
            var x = CloudProviders.All[Math.Max(0, provider.SelectedIndex)];
            address.Visibility = x == CloudProvider.Custom ? Visibility.Visible : Visibility.Collapsed;
            getKey.Visibility = CloudProviders.KeyPage(x) is { } k ? Visibility.Visible : Visibility.Collapsed;
            if (CloudProviders.KeyPage(x) is { } page) getKey.NavigateUri = new Uri(page);
        }
        provider.SelectionChanged += (_, _) => Changed();
        Changed();
        connect.Click += async (_, _) =>
        {
            var x = CloudProviders.All[Math.Max(0, provider.SelectedIndex)];
            var b = (CloudProviders.Base(x) ?? address.Text).Trim().TrimEnd('/');
            if (x == CloudProvider.Custom && CloudProviders.CheckBase(b) is { } bad) { problem.Text = bad; return; }
            if (key.Password.Trim().Length == 0) { problem.Text = "Paste your key first."; return; }
            connect.IsEnabled = false; problem.Text = "";
            try
            {
                await CloudChat.Models(x, b, key.Password.Trim());     // checks the key; nothing is spent
                CloudKeys.Provider = x; if (x == CloudProvider.Custom) CloudKeys.CustomBase = b;
                if (!CloudKeys.Save(key.Password)) { problem.Text = "Windows didn’t let Apex keep the key in Credential Manager."; return; }
                await ModeChanged();
            }
            catch (CloudChatException e) { problem.Text = e.Message; }
            finally { connect.IsEnabled = true; }
        };
        foreach (var e in new UIElement[] { provider, address, key, getKey, connect, problem }) p.Children.Add(e);
        return p;
    }

    async Task AskLumen(string text)
    {
        history.Add(("user", text));
        var bubble = Bubble("…", mine: false);
        var block = (RichTextBlock)bubble.Children[0];
        var answer = new System.Text.StringBuilder();
        answering = new CancellationTokenSource();
        send.IsEnabled = false;
        try
        {
            await LocalChat.Ask(new LocalChat.Server("Lumen AI", Lumen.Base!.TrimEnd('/')), Lumen.Model, history, AgentBridge.ReadingsText(),
                piece => MainWindow.Ui(() =>
                {
                    answer.Append(piece);
                    Render(block, answer.ToString());
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                }), answering.Token, LumenAccount.Key);
            history.Add(("assistant", answer.ToString()));
        }
        catch (LumenRefused e) { Render(block, e.Message); }
        catch (Exception) { Render(block, "Couldn’t reach Lumen AI. Check the internet connection."); }
        finally { answering = null; send.IsEnabled = true; }
    }

    /// Lumen AI before it's connected: what it is, the two ways to pay, and
    /// (once CyberNet Labs opens it) the key; until then, where to go instead.
    UIElement LumenPanel()
    {
        var p = new StackPanel { Spacing = 14, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
        var title = Theme.Text(Lumen.IsLive ? "Lumen AI" : "Lumen AI is coming soon", 26, bold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        p.Children.Add(title);
        var blurb = Theme.Text(Lumen.Blurb, 14, Theme.Secondary);
        blurb.TextWrapping = TextWrapping.Wrap; blurb.TextAlignment = TextAlignment.Center;
        p.Children.Add(blurb);

        var chosen = LumenAccount.Access ?? Lumen.Access.Plan;
        var cards = new Grid { ColumnSpacing = 12 };
        cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition());
        var borders = new List<(Border Card, Lumen.Access Access)>();
        void Mark() { foreach (var (c, a) in borders) c.BorderBrush = a == chosen && Lumen.IsLive ? Theme.Accent : Theme.CardStroke; }
        foreach (var (a, i) in new[] { (Lumen.Access.Plan, 0), (Lumen.Access.Api, 1) })
        {
            var inner = new StackPanel { Spacing = 4 };
            inner.Children.Add(Theme.Text(Lumen.Title(a), 14, bold: true));
            var d = Theme.Text(Lumen.Detail(a), 12, Theme.Secondary); d.TextWrapping = TextWrapping.Wrap;
            inner.Children.Add(d);
            var card = new Border { Child = inner, Padding = new Thickness(14), CornerRadius = new CornerRadius(12), Background = Theme.CardFill,
                                    BorderThickness = new Thickness(2), Opacity = Lumen.IsLive ? 1 : 0.6 };
            ToolTipService.SetToolTip(card, Lumen.IsLive ? Lumen.Detail(a) : "Opens when CyberNet Labs launches Lumen AI");
            var access = a;
            card.Tapped += (_, _) => { if (Lumen.IsLive) { chosen = access; Mark(); } };
            Grid.SetColumn(card, i); cards.Children.Add(card);
            borders.Add((card, a));
        }
        Mark();
        p.Children.Add(cards);

        if (Lumen.IsLive)
        {
            if (Lumen.Page(chosen) is { } page)
            {
                var link = new HyperlinkButton { Content = "Choose a plan or buy API access at CyberNet Labs", NavigateUri = new Uri(page), HorizontalAlignment = HorizontalAlignment.Center };
                p.Children.Add(link);
            }
            var key = new PasswordBox { PlaceholderText = "Paste the Lumen key from your CyberNet Labs account" };
            var problem = Theme.Text("", 13, Theme.Secondary); problem.TextWrapping = TextWrapping.Wrap;
            var connect = new Button { Content = "Connect", HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            connect.Click += async (_, _) =>
            {
                connect.IsEnabled = false; connect.Content = "Checking…"; problem.Text = "";
                var why = await LumenAccount.Connect(key.Password, chosen);
                connect.IsEnabled = true; connect.Content = "Connect";
                if (why != null) problem.Text = why; else _ = ModeChanged();
            };
            p.Children.Add(key); p.Children.Add(connect); p.Children.Add(problem);
        }
        else
        {
            var not = Theme.Text(Lumen.NotYet, 12, Theme.Tertiary);
            not.TextWrapping = TextWrapping.Wrap; not.TextAlignment = TextAlignment.Center;
            p.Children.Add(not);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            var agent = new Button { Content = "Ask Apex Agent", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            agent.Click += (_, _) => mode.SelectedIndex = 0;
            var local = new Button { Content = "Use Local AI" };
            local.Click += (_, _) => mode.SelectedIndex = 1;
            row.Children.Add(agent); row.Children.Add(local);
            p.Children.Add(row);
        }
        return p;
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
