using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RoboDust;

/// The floating control panel, laid out like the Mac one. Built in code and rebuilt whenever what
/// it shows changes shape; the live numbers update in place.
public sealed class Panel : Window
{
    static readonly Color Accent = Color.FromRgb(92, 214, 250), Amber = Color.FromRgb(250, 184, 51), Orange = Color.FromRgb(255, 159, 10);
    static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(23, 23, 23));
    static readonly Brush Primary = Brushes.White, Secondary = new SolidColorBrush(Color.FromRgb(160, 160, 165));
    static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");

    readonly Model model;
    readonly StackPanel root = new() { Margin = new Thickness(20, 18, 20, 20) };
    string shape = "";
    TextBlock statusText, batteryText, binText, collectedText, bytesText, timeText, questionText;
    Button goButton;
    Border batteryBar, binBar;
    MapView map;
    double anchorBottom = double.NaN;

    public Panel(Model m)
    {
        model = m;
        Title = "RoboDust";
        Width = 372;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        Background = Bg;
        FontFamily = Font;
        Foreground = Primary;
        UseLayoutRounding = true;
        Content = root;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        KeyDown += OnKey;
        // Win+D minimizes everything; the panel comes straight back, since it has no minimize button anyway.
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int round = 2, dark = 1;
            Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
            Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        };
        // Grow and shrink with the content, keeping the bottom edge where it is.
        SizeChanged += (_, _) =>
        {
            if (double.IsNaN(anchorBottom)) return;
            var wa = SystemParameters.WorkArea;
            Top = Math.Max(wa.Top, Math.Min(anchorBottom, wa.Bottom) - ActualHeight);
        };
        LocationChanged += (_, _) => { if (IsLoaded) anchorBottom = Top + ActualHeight; };
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 28;
            Top = wa.Bottom - ActualHeight - 28;
            anchorBottom = Top + ActualHeight;
        };
        Refresh();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (model.Stage != Stage.Running || model.Applying) return;
        if (e.Key == Key.Escape) { model.Stop(); e.Handled = true; }
        if (e.Key == Key.Space && e.OriginalSource is not ButtonBase) { model.TogglePause(); e.Handled = true; }
    }

    // MARK: - Refresh

    string Shape()
    {
        var m = model;
        return string.Join("|", m.Stage, m.Phase is Phase.Done or Phase.Tidying or Phase.Tidied or Phase.PuttingBack or Phase.Leaving or Phase.Finished ? m.Phase.ToString() : "live",
            m.Paused, m.Note, m.Outcome, m.Mode, m.Source, m.ReviewOpen, m.Result?.Title, m.Result?.Detail, m.Applying, m.Support, m.SkipFinalClick, m.ClearWindows,
            m.LastRun?.Id, m.DockScreen, m.Fun, m.Page, System.Text.Json.JsonSerializer.Serialize(m.Settings), m.SortBy, m.Muted, m.Stage == Stage.Preparing ? m.Prepared : 0, m.RealRun);
    }

    public void Refresh()
    {
        var s = Shape();
        if (s != shape) { shape = s; Rebuild(); }
        UpdateLive();
    }

    void UpdateLive()
    {
        var m = model;
        if (statusText != null) statusText.Text = m.Stage == Stage.Running ? m.Status : "Ein Saugroboter für deinen Desktop";
        if (questionText != null) { questionText.Text = Question; goButton.IsEnabled = SelectedCount > 0; }
        if (batteryBar != null)
        {
            batteryText.Text = $"{(int)(m.Battery * 100)} %";
            binText.Text = $"{(int)(Math.Min(m.Bin, 1) * 100)} %";
            SetBar(batteryBar, m.Battery);
            SetBar(binBar, m.Bin);
            binBar.Background = new SolidColorBrush(m.Bin > 0.85 ? Orange : Accent);
            collectedText.Text = m.Collected.ToString();
            bytesText.Text = Fmt.Bytes(m.CollectedBytes);
            var t = (int)m.RunTime; timeText.Text = $"{t / 60}:{t % 60:00}";
            map?.InvalidateVisual();
        }
    }

    static void SetBar(Border bar, float v)
    {
        if (bar.Parent is Grid g && g.ActualWidth > 0) bar.Width = g.ActualWidth * Math.Clamp(v, 0, 1);
        else bar.Tag = v;
    }

    void Rebuild()
    {
        root.Children.Clear();
        statusText = null; batteryBar = null; map = null; questionText = null; goButton = null;
        root.Children.Add(Header());
        var body = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        root.Children.Add(body);
        switch (model.Stage)
        {
            case Stage.Ready:
                if (model.Page == Page.Settings)
                {
                    // Taller than some screens: it scrolls.
                    var inner = new StackPanel();
                    SettingsPage(inner);
                    body.Children.Add(new ScrollViewer { Content = inner, MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 170), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) });
                }
                else if (model.Page == Page.Shelf) ShelfPage(body);
                else Setup(body);
                break;
            case Stage.Preparing: Preparing(body); break;
            case Stage.Running: Running(body); break;
        }
    }

    // MARK: - Pieces

    UIElement Header()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        glyph.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(230, 230, 230)) });
        glyph.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(31, 31, 31)), Margin = new Thickness(4.5) });
        glyph.Children.Add(new Ellipse { Fill = new SolidColorBrush(Accent), Width = 9.6, Height = 9.6, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) });
        g.Children.Add(glyph);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "RoboDust", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Primary });
        statusText = new TextBlock { FontSize = 12, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis };
        titles.Children.Add(statusText);
        Grid.SetColumn(titles, 1);
        g.Children.Add(titles);
        var icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (model.Stage == Stage.Running)
            icons.Children.Add(IconButton(model.Muted ? "" : "", model.Muted ? "Ton an" : "Ton aus", () => { model.Muted = !model.Muted; model.Changed(); }));
        else
        {
            icons.Children.Add(IconButton("", "Über RoboDust", () => model.About()));
            icons.Children.Add(IconButton("", "Beenden", () => model.Quit()));
        }
        Grid.SetColumn(icons, 2);
        g.Children.Add(icons);
        return g;
    }

    static Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = Secondary },
            ToolTip = tip, Width = 28, Height = 28, Cursor = Cursors.Hand, Focusable = false,
        };
        b.Template = Template(new CornerRadius(6), Brushes.Transparent, null, new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)));
        b.Click += (_, _) => click();
        return b;
    }

    static new ControlTemplate Template(CornerRadius radius, Brush bg, Brush border, Brush hover)
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border)) { Name = "bd" };
        bd.SetValue(Border.CornerRadiusProperty, radius);
        bd.SetValue(Border.BackgroundProperty, bg);
        if (border != null) { bd.SetValue(Border.BorderBrushProperty, border); bd.SetValue(Border.BorderThicknessProperty, new Thickness(1)); }
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var over = new Trigger { Property = IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));
        t.Triggers.Add(over);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(OpacityProperty, 0.75, "bd"));
        t.Triggers.Add(pressed);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.4, "bd"));
        t.Triggers.Add(disabled);
        return t;
    }

    enum Kind { Primary, Quiet, Warn }

    /// Colours are set here rather than by the system, so a label can never blend into its button.
    static Button Btn(string text, Kind kind, Action click, string glyph = null)
    {
        Brush fg = kind == Kind.Primary ? Brushes.Black : kind == Kind.Warn ? new SolidColorBrush(Color.FromRgb(255, 140, 133)) : Brushes.White;
        Color bg = kind == Kind.Primary ? Accent : kind == Kind.Warn ? Color.FromArgb(36, 255, 69, 59) : Color.FromArgb(26, 255, 255, 255);
        Color hover = kind == Kind.Primary ? Color.FromRgb(130, 224, 252) : kind == Kind.Warn ? Color.FromArgb(56, 255, 69, 59) : Color.FromArgb(44, 255, 255, 255);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != null) content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = fg, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, Height = 36, HorizontalAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand };
        b.Template = Template(new CornerRadius(9), new SolidColorBrush(bg), kind == Kind.Warn ? new SolidColorBrush(Color.FromArgb(128, 255, 102, 92)) : null, new SolidColorBrush(hover));
        b.Click += (_, _) => click();
        return b;
    }

    static TextBlock Text(string s, double size = 12, Brush color = null, bool bold = false) => new()
    {
        Text = s, FontSize = size, Foreground = color ?? Secondary, TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    };

    static UIElement Field(string title, params UIElement[] content)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        p.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Secondary, Margin = new Thickness(0, 0, 0, 7) });
        foreach (var c in content) p.Children.Add(c);
        return p;
    }

    static UIElement Segmented<T>(IList<(T value, string title)> options, T selected, Action<T> pick, int columns = 0)
    {
        var outer = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), Padding = new Thickness(2) };
        var cols = columns > 0 ? columns : options.Count > 5 ? (options.Count + 1) / 2 : options.Count;
        var g = new UniformGrid { Columns = cols, Rows = (options.Count + cols - 1) / cols };
        foreach (var (value, title) in options)
        {
            var on = EqualityComparer<T>.Default.Equals(value, selected);
            var b = new Button
            {
                Content = new TextBlock { Text = title, FontSize = options.Count > 3 && columns == 0 ? 12 : 13, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = on ? Brushes.White : Secondary },
                Height = 28, Cursor = Cursors.Hand,
            };
            b.Template = Template(new CornerRadius(6), on ? new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)) : Brushes.Transparent, null,
                new SolidColorBrush(on ? Color.FromArgb(56, 255, 255, 255) : Color.FromArgb(18, 255, 255, 255)));
            var v = value;
            b.Click += (_, _) => pick(v);
            g.Children.Add(b);
        }
        outer.Child = g;
        return outer;
    }

    static UIElement Switch(string label, bool on, Action<bool> set)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var pill = new Border
        {
            Width = 32, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = on ? new SolidColorBrush(Accent) : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            Child = new Ellipse { Width = 14, Height = 14, Fill = on ? Brushes.Black : Brushes.White, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2, 0, 2, 0) },
        };
        row.Children.Add(pill);
        var t = new TextBlock { Text = label, FontSize = 13, Foreground = Primary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(t, 1);
        row.Children.Add(t);
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; set(!on); };
        row.MouseLeftButtonDown += (_, e) => e.Handled = true;   // not a window drag
        return row;
    }

    static Border Card(Color tint, bool outlined, params UIElement[] content)
    {
        var p = new StackPanel();
        foreach (var c in content) p.Children.Add(c);
        return new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 16),
            Background = new SolidColorBrush(Color.FromArgb(outlined ? (byte)20 : (byte)16, tint.R, tint.G, tint.B)),
            BorderBrush = outlined ? new SolidColorBrush(Color.FromArgb(190, tint.R, tint.G, tint.B)) : null,
            BorderThickness = new Thickness(outlined ? 1 : 0), Child = p,
        };
    }

    static UIElement Row(double gap, params UIElement[] items)
    {
        var g = new UniformGrid { Rows = 1, Columns = items.Length, Margin = new Thickness(0, 8, 0, 0) };
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is FrameworkElement fe) fe.Margin = new Thickness(i == 0 ? 0 : gap / 2, 0, i == items.Length - 1 ? 0 : gap / 2, 0);
            g.Children.Add(items[i]);
        }
        return g;
    }

    static UIElement Stack(double gap, params UIElement[] items)
    {
        var p = new StackPanel();
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is FrameworkElement fe && i > 0) fe.Margin = new Thickness(fe.Margin.Left, gap, fe.Margin.Right, fe.Margin.Bottom);
            p.Children.Add(items[i]);
        }
        return p;
    }

    // MARK: - Setup

    void Setup(StackPanel body)
    {
        var m = model;
        if (m.Result != null)
        {
            var r = m.Result;
            var items = new List<UIElement> { Text(r.Title, 13, r.IsError ? new SolidColorBrush(Orange) : Primary, true) };
            if (r.Detail != null) { var d = Text(r.Detail); d.Margin = new Thickness(0, 6, 0, 0); items.Add(d); }
            var buttons = new List<UIElement>();
            if (r.CanUndo) buttons.Add(Btn("Rückgängig", Kind.Quiet, () => m.Undo()));
            buttons.Add(Btn("Fertig", Kind.Quiet, () => m.DismissResult()));
            items.Add(Row(8, buttons.ToArray()));
            body.Children.Add(Card(Colors.White, false, items.ToArray()));
        }
        if (m.Support != SupportCard.Hidden) body.Children.Add(SupportCardView());
        if (m.Result != null) return;
        // After a clean, the result is all there is to look at. Done brings the settings back.
        if (m.LastRun is { } last)
        {
            var what = last.Mode switch { "move" => "weggeräumt", "sort" => "in Ordner sortiert", _ => "ausgeblendet" };
            var n = last.Items.Count;
            var items = new List<UIElement>
            {
                Text($"Letzter Durchgang: {n} {(n == 1 ? "Element" : "Elemente")} {what}", 13, Primary, true),
                Text($"am {last.When:dd.MM.yyyy 'um' HH:mm}. Du kannst alles zurückholen."),
            };
            var bs = new List<UIElement> { Btn("Zurückholen", Kind.Quiet, () => m.UndoLast()) };
            if (last.Mode == "move") bs.Add(Btn("Ablage öffnen", Kind.Quiet, () => m.OpenShelf()));
            items.Add(Row(8, bs.ToArray()));
            body.Children.Add(Card(Colors.White, false, items.ToArray()));
        }
        body.Children.Add(Field("Aufräumen", Segmented(new List<(Source, string)> { (Source.Sample, "Beispiel-Chaos"), (Source.Real, "Mein Desktop") }, m.Source, v => { m.Source = v; m.Changed(); })));
        var after = new List<UIElement>
        {
            Segmented(new List<(CleanMode, string)> { (CleanMode.Tidy, "Ordnen"), (CleanMode.Sort, "In Ordner"), (CleanMode.Move, "Wegräumen"), (CleanMode.Hide, "Ausblenden") }, m.Mode, v => { m.Mode = v; m.Changed(); }),
        };
        if (m.Mode == CleanMode.Sort)
        {
            var by = Segmented(new List<(SortBy, string)> { (SortBy.Type, "nach Art"), (SortBy.Alphabet, "nach Alphabet"), (SortBy.Date, "nach Datum") }, m.SortBy, v => { m.SortBy = v; m.Changed(); });
            ((FrameworkElement)by).Margin = new Thickness(0, 6, 0, 0);
            after.Add(by);
        }
        var blurb = Text(ModeBlurb); blurb.Margin = new Thickness(0, 7, 0, 0); after.Add(blurb);
        if (m.Mode != CleanMode.Tidy)
        {
            var sw = Switch("Nicht nachfragen, direkt aufräumen", m.SkipFinalClick, v => { m.SkipFinalClick = v; m.Changed(); });
            ((FrameworkElement)sw).Margin = new Thickness(0, 10, 0, 0);
            after.Add(sw);
        }
        body.Children.Add(Field("Danach", after.ToArray()));
        if (m.Fun != FunMode.Off || m.Settings.RemoteControl)
        {
            var fun = Text("Spaßmodus: " + FunName(m.Fun) + (m.Settings.RemoteControl ? " · Fernsteuerung mit den Pfeiltasten" : ""), 12, new SolidColorBrush(Amber));
            fun.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(fun);
        }
        if (m.Note != null) { var n = Text(m.Note, 12, new SolidColorBrush(Orange)); n.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(n); }
        var start = Btn("Saugen starten", Kind.Primary, () => m.Start());
        start.IsDefault = true;
        start.Margin = new Thickness(0, 4, 0, 14);
        body.Children.Add(start);
        var foot = new Grid();
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.Children.Add(new TextBlock
        {
            Text = m.Source == Source.Real ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13,
            Foreground = new SolidColorBrush(m.Source == Source.Real ? Orange : Accent), Margin = new Thickness(0, 1, 8, 0),
        });
        var ft = Text(Footnote); Grid.SetColumn(ft, 1); foot.Children.Add(ft);
        body.Children.Add(foot);
        // What it has done so far.
        var st = m.Settings.Stats;
        if (st.Runs > 0)
        {
            var parts = new List<string> { $"{st.Runs} {(st.Runs == 1 ? "Durchgang" : "Durchgänge")}" };
            if (st.Items > 0) parts.Add($"{st.Items} aufgeräumt ({Fmt.Bytes(st.Bytes)})");
            parts.Add(st.Meters >= 1000 ? $"{st.Meters / 1000:0.0} km gefahren" : $"{st.Meters:0} m gefahren");
            if (st.FavouriteKind != null) parts.Add("Lieblingsart: " + st.FavouriteKind);
            var stats = Text("Bisher: " + string.Join(" · ", parts));
            stats.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(stats);
        }
        var nav = Row(8, Btn("Einstellungen", Kind.Quiet, () => { m.Page = Page.Settings; m.Changed(); }, ""),
                         Btn("Ablage", Kind.Quiet, () => { m.Page = Page.Shelf; m.Changed(); }, ""));
        ((FrameworkElement)nav).Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(nav);
    }

    static string FunName(FunMode f) => f switch
    {
        FunMode.Flames => "🔥 Feuer", FunMode.Bubbles => "🧼 Blasen", FunMode.Confetti => "🎉 Konfetti", FunMode.Disco => "🕺 Disco",
        FunMode.Laser => "Laser", FunMode.Tornado => "🌪 Tornado", FunMode.Plow => "❄ Schneepflug", FunMode.Race => "🏁 Wettrennen",
        FunMode.All => "Alles!", _ => "Aus",
    };

    // MARK: - Settings page

    void SettingsPage(StackPanel body)
    {
        var m = model; var s = m.Settings;
        body.Children.Add(Btn("Zurück", Kind.Quiet, () => { m.Page = Page.Main; m.Changed(); }, ""));
        body.Children.Add(new Border { Height = 16 });
        // With several displays: which one the dock stands on, which is where the robot starts and empties.
        var displays = Screen.All();
        if (displays.Count > 1)
        {
            Floorplan.Layout(displays);
            var ordered = displays.OrderBy(d => d.WorldOrigin.X).ThenBy(d => d.WorldOrigin.Y).ToList();
            var options = ordered.Select((d, i) => (d.Name, d.IsPrimary ? $"{i + 1} · Haupt" : $"Monitor {i + 1}")).ToList();
            var current = displays.Any(d => d.Name == m.DockScreen) ? m.DockScreen : displays.First(d => d.IsPrimary).Name;
            var dockText = Text("Dort startet der Roboter, und dorthin fährt er zum Entleeren.");
            dockText.Margin = new Thickness(0, 7, 0, 0);
            body.Children.Add(Field("Station steht auf", Segmented(options, current, v => { m.DockScreen = v; m.Changed(); }), dockText));
        }
        var funText = Text(FunBlurb);
        funText.Margin = new Thickness(0, 7, 0, 0);
        var remote = Switch("Fernsteuerung: mit den Pfeiltasten selbst fahren", s.RemoteControl, v => m.Set(() => s.RemoteControl = v));
        ((FrameworkElement)remote).Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(Field("Spaßmodus",
            Segmented(new List<(FunMode, string)> {
                (FunMode.Off, "Aus"), (FunMode.Flames, "🔥 Feuer"), (FunMode.Bubbles, "🧼 Blasen"),
                (FunMode.Confetti, "🎉 Konfetti"), (FunMode.Disco, "🕺 Disco"), (FunMode.Laser, "⚡ Laser"),
                (FunMode.Tornado, "🌪 Tornado"), (FunMode.Plow, "❄ Schnee"), (FunMode.Race, "🏁 Rennen"), (FunMode.All, "Alles!") }, m.Fun, v => { m.Fun = v; m.Changed(); }, 3),
            funText, remote));
        var hat = Switch("Mit Partyhut", s.Hat, v => m.Set(() => s.Hat = v));
        ((FrameworkElement)hat).Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(Field("Aussehen",
            Segmented(new List<(RobotSkin, string)> { (RobotSkin.White, "Weiß"), (RobotSkin.Black, "Schwarz"), (RobotSkin.Gold, "Gold"), (RobotSkin.Retro, "Retro"), (RobotSkin.Pink, "Pink") }, (RobotSkin)s.Skin, v => m.Set(() => s.Skin = (int)v)),
            hat));
        var ruleText = Text(s.NeverTouch.Count == 0
            ? "Rechtsklick auf eine Datei in „Elemente prüfen“ merkt sie dir als „nie anfassen“."
            : $"{s.NeverTouch.Count} {(s.NeverTouch.Count == 1 ? "Datei rührt" : "Dateien rührt")} RoboDust nie an.");
        ruleText.Margin = new Thickness(0, 7, 0, 0);
        var rules = new List<UIElement>
        {
            Segmented(new List<(int, string)> { (0, "Alles"), (1, "ab 1 Tag"), (7, "ab 7 Tagen"), (30, "ab 30 Tagen") }, s.MinAgeDays, v => m.Set(() => s.MinAgeDays = v)),
            ruleText,
        };
        if (s.NeverTouch.Count > 0)
        {
            var clear = Btn("Liste leeren", Kind.Quiet, () => m.Set(() => s.NeverTouch.Clear()));
            clear.Margin = new Thickness(0, 8, 0, 0);
            rules.Add(clear);
        }
        body.Children.Add(Field("Nur aufräumen, was so lange unverändert ist", rules.ToArray()));
        var schedText = Text(s.Schedule == 0
            ? "RoboDust räumt nur auf, wenn du startest."
            : $"Räumt {(s.Schedule == 1 ? "jeden Tag" : "jeden Freitag")} um {s.ScheduleHour} Uhr deinen echten Desktop auf, mit der Einstellung von „Danach“{(m.SkipFinalClick ? "" : ", und wartet am Ende auf deinen Klick")}. Danach kommt eine Benachrichtigung.");
        schedText.Margin = new Thickness(0, 7, 0, 0);
        var sched = new List<UIElement> { Segmented(new List<(int, string)> { (0, "Aus"), (1, "Täglich"), (2, "Freitags") }, s.Schedule, v => m.SetSchedule(v)) };
        if (s.Schedule != 0)
        {
            var hours = Segmented(new List<(int, string)> { (9, "9 Uhr"), (12, "12 Uhr"), (17, "17 Uhr"), (20, "20 Uhr") }, s.ScheduleHour, v => { s.ScheduleHour = v; m.SetSchedule(s.Schedule); });
            ((FrameworkElement)hours).Margin = new Thickness(0, 6, 0, 0);
            sched.Add(hours);
        }
        sched.Add(schedText);
        body.Children.Add(Field("Zeitplan", sched.ToArray()));
        var start = new StackPanel();
        start.Children.Add(Switch("Mit Windows starten (im Infobereich)", s.Autostart, v => m.SetAutostart(v)));
        start.Children.Add(Switch("Strg+Alt+R startet sofort einen Durchgang", s.Hotkey, v => m.SetHotkey(v)));
        start.Children.Add(Switch("Fenster minimieren, damit ich zuschauen kann", m.ClearWindows, v => { m.ClearWindows = v; m.Changed(); }));
        body.Children.Add(Field("Start", start));
    }

    // MARK: - Shelf page

    void ShelfPage(StackPanel body)
    {
        var m = model;
        body.Children.Add(Btn("Zurück", Kind.Quiet, () => { m.Page = Page.Main; m.Changed(); }, ""));
        body.Children.Add(new Border { Height = 16 });
        var shelf = FileOps.ShelfFor(DesktopReader.DesktopFolder);
        var runs = FileOps.ShelfContents(shelf);
        if (runs.Count == 0)
        {
            body.Children.Add(Text("Die Ablage ist leer. Hier landet, was RoboDust wegräumt.", 13, Primary));
            return;
        }
        var total = runs.Sum(r => r.Bytes);
        body.Children.Add(Text($"{runs.Count} {(runs.Count == 1 ? "Durchgang" : "Durchgänge")}, zusammen {Fmt.Bytes(total)}", 13, Primary, true));
        var note = Text("Hier wird nichts gelöscht. Was weg soll, löschst du selbst im Explorer.");
        note.Margin = new Thickness(0, 4, 0, 12);
        body.Children.Add(note);
        var list = new StackPanel();
        foreach (var r in runs.Take(30))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var age = (DateTime.Now - r.When).TotalDays;
            var ageText = age < 1 ? "heute" : age < 2 ? "gestern" : $"vor {(int)age} Tagen";
            var info = new StackPanel();
            info.Children.Add(Text(r.Name, 13, Primary));
            info.Children.Add(Text($"{r.Count} {(r.Count == 1 ? "Element" : "Elemente")} · {Fmt.Bytes(r.Bytes)} · {ageText}"));
            row.Children.Add(info);
            var open = Btn("Öffnen", Kind.Quiet, () => { try { System.Diagnostics.Process.Start("explorer.exe", $"\"{r.Path}\""); } catch { } });
            open.Width = 90; open.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(open, 1);
            row.Children.Add(open);
            list.Children.Add(row);
        }
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var all = Btn("Ablage im Explorer öffnen", Kind.Quiet, () => { try { System.Diagnostics.Process.Start("explorer.exe", $"\"{shelf}\""); } catch { } });
        all.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(all);
    }

    string FunBlurb => model.Fun switch
    {
        FunMode.Flames => "Ein Flammenwerfer vorne: Alles im Weg verbrennt zu Asche, und die saugt er dann auf. Nur zum Anschauen, deinen Dateien passiert nichts.",
        FunMode.Bubbles => "Eine Seifenblasen-Pistole hinten. Die Blasen schweben übers Bild und platzen an deinen Symbolen.",
        FunMode.Confetti => "Konfetti für jedes eingesaugte Symbol, und am Ende feuert die Station eine Konfetti-Kanone ab.",
        FunMode.Disco => "Regenbogen-LEDs und bunte Lichtringe im Takt.",
        FunMode.Laser => "Ein Laser zerlegt alles im Weg in bunte Pixel, und die saugt er dann auf.",
        FunMode.Tornado => "Ein Tornado um den Roboter wirbelt alles in seiner Nähe herum, bevor es im Sog verschwindet.",
        FunMode.Plow => "Es schneit. Mit dem Schneepflug schiebt er erst alles zu einem großen Haufen und saugt dann Haufen und Schnee weg.",
        FunMode.Race => "Zwei Rivalen fahren mit und saugen um die Wette. Am Ende gibt es eine Siegerehrung.",
        FunMode.All => "Flammenwerfer, Seifenblasen, Konfetti und Disco auf einmal. Nur zum Anschauen, deinen Dateien passiert nichts.",
        _ => "Der Roboter saugt ganz normal.",
    };

    string ModeBlurb
    {
        get
        {
            var ask = model.SkipFinalClick ? "" : " RoboDust fragt vorher noch einmal nach.";
            return model.Mode switch
            {
                CleanMode.Tidy => "Sortiert alles nach Art in ordentliche Spalten. Nur die Symbole bewegen sich.",
                CleanMode.Move => "Räumt alles in einen Ordner unter „RoboDust Ablage“. Nichts wird gelöscht." + ask,
                CleanMode.Sort => model.SortBy switch
                {
                    SortBy.Alphabet => "Sortiert alles in Ordner wie „A–E“ und „F–J“ auf deinem Desktop.",
                    SortBy.Date => "Sortiert alles nach dem Monat der letzten Änderung in Ordner wie „2026-10 Oktober“.",
                    _ => "Sortiert alles in Ordner wie „Bilder“, „Dokumente“ und „Videos“ auf deinem Desktop.",
                } + " Gibt es so einen Ordner schon, kommt alles dazu. Nichts wird gelöscht." + ask,
                _ => "Blendet alles aus. Die Dateien bleiben genau, wo sie sind." + ask,
            };
        }
    }

    string Footnote
    {
        get
        {
            if (model.Source != Source.Real) return "Das Beispiel-Chaos ist erfunden, also wird nichts Echtes angefasst.";
            return model.Mode switch
            {
                CleanMode.Tidy => "RoboDust ordnet die Symbole auf deinem echten Desktop neu. Deine Dateien bleiben, wo sie sind.",
                CleanMode.Move => "RoboDust verschiebt deine echten Dateien in die Ablage und löscht nie etwas. Gesperrtes, gerade Geändertes und Code-Projekte bleiben liegen.",
                CleanMode.Sort => "RoboDust sortiert deine echten Dateien in Ordner auf dem Desktop und löscht nie etwas. Gesperrtes, gerade Geändertes und Code-Projekte bleiben liegen.",
                _ => "RoboDust blendet deine echten Dateien aus und löscht nie etwas. Gesperrtes, gerade Geändertes und Code-Projekte bleiben liegen.",
            };
        }
    }

    UIElement SupportCardView()
    {
        var m = model;
        if (m.Support == SupportCard.Thanked)
            return Card(Amber, true, Text("Danke! Das bedeutet einem kleinen Roboter sehr viel.", 13, Primary), Stack(10, new Border { Height = 0 }, Btn("Schließen", Kind.Quiet, () => m.Support_(null))));
        var no = new Button { Content = Text("Nein danke", 13), Height = 36, Cursor = Cursors.Hand };
        no.Template = Template(new CornerRadius(9), Brushes.Transparent, null, new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)));
        no.Click += (_, _) => m.Support_(null);
        return Card(Amber, true,
            Text("Hat’s dich zum Lächeln gebracht? Ein Stern oder ein Kaffee hilft Raufan, mehr lustige Software zu bauen.", 13, Primary),
            Stack(8, new Border { Height = 2 },
                Btn("Stern auf GitHub", Kind.Quiet, () => m.Support_("https://github.com/muhraufan/robodust"), ""),
                Btn("Kauf mir einen Kaffee", Kind.Quiet, () => m.Support_("https://www.buymeacoffee.com/notmegapixels")),
                no));
    }

    // MARK: - Preparing

    void Preparing(StackPanel body)
    {
        body.Children.Add(Text("Kopiere deine Symbole…", 13, Primary));
        var bar = new ProgressBar { Minimum = 0, Maximum = Math.Max(model.PrepareTotal, 1), Value = model.Prepared, Height = 5, Margin = new Thickness(0, 10, 0, 8), Foreground = new SolidColorBrush(Accent), Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), BorderThickness = new Thickness(0) };
        body.Children.Add(bar);
        body.Children.Add(Text($"{model.Prepared} von {model.PrepareTotal}"));
    }

    // MARK: - Running

    void Running(StackPanel body)
    {
        var m = model;
        map = new MapView(m) { Height = 190, Margin = new Thickness(0, 0, 0, 14) };
        body.Children.Add(map);
        var meters = new UniformGrid { Rows = 1, Columns = 2, Margin = new Thickness(0, 0, 0, 14) };
        meters.Children.Add(Meter("Akku", Color.FromRgb(107, 230, 140), out batteryText, out batteryBar, new Thickness(0, 0, 9, 0)));
        meters.Children.Add(Meter("Behälter", Accent, out binText, out binBar, new Thickness(9, 0, 0, 0)));
        body.Children.Add(meters);
        var stats = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stats.Children.Add(Stat(out collectedText, $"von {m.Total} Elementen", 0));
        stats.Children.Add(Stat(out bytesText, "eingesammelt", 1));
        stats.Children.Add(Stat(out timeText, "vergangen", 2));
        body.Children.Add(stats);
        Controls(body);
    }

    static UIElement Meter(string title, Color tint, out TextBlock value, out Border bar, Thickness margin)
    {
        var p = new StackPanel { Margin = margin };
        var head = new Grid();
        head.Children.Add(Text(title));
        value = new TextBlock { FontSize = 12, Foreground = Primary, HorizontalAlignment = HorizontalAlignment.Right };
        head.Children.Add(value);
        p.Children.Add(head);
        var track = new Grid { Height = 5, Margin = new Thickness(0, 5, 0, 0) };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)) });
        bar = new Border { CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(tint), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var b = bar;
        track.SizeChanged += (_, _) => { if (b.Tag is float v) b.Width = track.ActualWidth * Math.Clamp(v, 0, 1); };
        track.Children.Add(bar);
        p.Children.Add(track);
        return p;
    }

    static UIElement Stat(out TextBlock big, string small, int col)
    {
        var p = new StackPanel { HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : col == 2 ? HorizontalAlignment.Right : HorizontalAlignment.Center };
        big = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Primary };
        p.Children.Add(big);
        p.Children.Add(new TextBlock { Text = small, FontSize = 11, Foreground = Secondary });
        Grid.SetColumn(p, col);
        return p;
    }

    bool AwaitingChoice => model.Phase == Phase.Done && model.Mode != CleanMode.Tidy && !model.SkipFinalClick && model.Outcome == null && !model.Applying;
    int SelectedCount => model.Total - model.Excluded.Count;

    void Controls(StackPanel body)
    {
        var m = model;
        if (m.Applying)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 60, Height = 4, Margin = new Thickness(0, 0, 10, 0), Foreground = new SolidColorBrush(Accent), Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), BorderThickness = new Thickness(0) });
            row.Children.Add(Text(m.Outcome switch { CleanMode.Move => "Räumt weg…", CleanMode.Hide => "Blendet aus…", CleanMode.Sort => "Sortiert in Ordner…", _ => "Ordnet deine Symbole…" }, 13, Primary));
            body.Children.Add(row);
        }
        else if (AwaitingChoice) Decision(body);
        else if (m.RealRun && m.Phase is Phase.Done or Phase.Tidying or Phase.Tidied)
        {
            if (m.Phase != Phase.Done || m.Mode == CleanMode.Tidy) body.Children.Add(Text("Sortiert deine Symbole in Spalten…", 13, Primary));
        }
        else if (m.Phase is Phase.Done or Phase.Tidying or Phase.Tidied)
        {
            var title = m.Outcome switch { CleanMode.Move => $"{m.Total} Elemente weggeräumt", CleanMode.Hide => $"{m.Total} Elemente ausgeblendet", CleanMode.Sort => $"{m.Total} Elemente in Ordner sortiert", _ => "In Spalten sortiert" };
            body.Children.Add(Text(title, 13, Primary, true));
            var d = Text("Das war das Beispiel-Chaos, also hat sich nichts Echtes geändert. Leg alles zurück, um fertig zu werden.");
            d.Margin = new Thickness(0, 4, 0, 12);
            body.Children.Add(d);
            body.Children.Add(Btn("Alles zurücklegen", Kind.Primary, () => m.PutBack()));
        }
        else if (m.Phase is Phase.PuttingBack or Phase.Leaving or Phase.Finished) { }
        else
        {
            var g = new UniformGrid { Rows = 1, Columns = 2, Margin = new Thickness(0, 0, 0, 10) };
            var pause = Btn(m.Paused ? "Weiter" : "Pause", Kind.Quiet, () => m.TogglePause(), m.Paused ? "" : ""); pause.Margin = new Thickness(0, 0, 4, 0);
            var stop = Btn("Stopp", Kind.Quiet, () => m.Stop(), ""); stop.Margin = new Thickness(4, 0, 0, 0);
            g.Children.Add(pause); g.Children.Add(stop);
            body.Children.Add(g);
            body.Children.Add(Text(m.RealRun ? "Wenn du vor dem Ende stoppst, ändert sich nichts. Esc geht auch." : "Esc stoppt und bringt deinen Desktop zurück. Heb den Roboter hoch, oder wirf ihm ein Symbol hin."));
        }
    }

    string Question
    {
        get
        {
            var n = SelectedCount;
            var noun = n == 1 ? "Element" : "Elemente";
            return model.Mode switch { CleanMode.Move => $"{n} {noun} in die Ablage räumen?", CleanMode.Sort => $"{n} {noun} in Ordner sortieren?", _ => $"{n} {noun} ausblenden?" };
        }
    }

    void Decision(StackPanel body)
    {
        var m = model;
        var n = SelectedCount;
        questionText = Text(Question, 13, Primary, true);
        body.Children.Add(questionText);
        var d = Text(DecisionBody); d.Margin = new Thickness(0, 4, 0, 0); body.Children.Add(d);
        if (m.RealRun && m.Mode == CleanMode.Move && m.UsesCloud)
        {
            var w = Text("Dein Desktop wird mit OneDrive synchronisiert. Was weggeräumt wird, verschwindet auch auf deinen anderen Geräten und liegt danach nur auf diesem PC.", 12, new SolidColorBrush(Orange));
            w.Margin = new Thickness(0, 4, 0, 0); body.Children.Add(w);
        }
        if (m.RealRun && m.Mode == CleanMode.Hide && m.HiddenShown)
        {
            var w = Text("Bei dir werden ausgeblendete Elemente angezeigt, deshalb siehst du sie danach weiterhin, nur blasser.", 12, new SolidColorBrush(Orange));
            w.Margin = new Thickness(0, 4, 0, 0); body.Children.Add(w);
        }
        if (m.RealRun)
        {
            var toggle = new Button { Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            toggle.Content = new TextBlock { Text = (m.ReviewOpen ? "▾ " : "▸ ") + "Elemente prüfen", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Primary };
            toggle.Template = Template(new CornerRadius(4), Brushes.Transparent, null, Brushes.Transparent);
            toggle.Click += (_, _) => { m.ReviewOpen = !m.ReviewOpen; m.Changed(); };
            body.Children.Add(toggle);
            if (m.ReviewOpen)
            {
                var list = new StackPanel();
                foreach (var (index, name) in m.ReviewRows)
                {
                    var cb = new CheckBox
                    {
                        IsChecked = !m.Excluded.Contains(index), Foreground = Primary, Margin = new Thickness(0, 2, 0, 2),
                        Content = new TextBlock { Text = name, FontSize = 12, Foreground = Primary, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 290 },
                    };
                    var i = index;
                    cb.Checked += (_, _) => { m.Excluded.Remove(i); m.Changed(); };
                    cb.Unchecked += (_, _) => { m.Excluded.Add(i); m.Changed(); };
                    // Rule: right-click to keep this one out of every clean from now on.
                    var never = new MenuItem { Header = "Nie anfassen (für immer merken)" };
                    never.Click += (_, _) => { m.Excluded.Add(i); var p = m.Sim?.Items[i].Entry.Path; if (p != null) m.NeverTouch(p); cb.IsChecked = false; };
                    cb.ContextMenu = new ContextMenu();
                    cb.ContextMenu.Items.Add(never);
                    list.Children.Add(cb);
                }
                body.Children.Add(new ScrollViewer { Content = list, Height = 170, Margin = new Thickness(0, 6, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            }
        }
        var go = Btn(m.Mode switch { CleanMode.Move => "Wegräumen", CleanMode.Sort => "In Ordner sortieren", _ => "Ausblenden" }, Kind.Primary, () => m.Choose(m.Mode));
        go.IsEnabled = n > 0;
        goButton = go;
        go.Margin = new Thickness(0, 12, 0, 8);
        body.Children.Add(go);
        body.Children.Add(Btn("Alles zurücklegen", Kind.Quiet, () => m.PutBack()));
    }

    string DecisionBody
    {
        get
        {
            var m = model;
            var preview = "";
            if (m.Mode == CleanMode.Sort)
            {
                // Which folders it will make, and how full each gets.
                var groups = (m.Sim?.Items ?? new List<Item>()).Where(i => !i.Protected && !m.Excluded.Contains(i.Index))
                    .GroupBy(i => FileOps.GroupOf(i.Entry.Name, i.Entry.Kind, i.Entry.Modified, m.SortBy))
                    .OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Count()})").ToList();
                if (groups.Count > 0) preview = "Ordner: " + string.Join(", ", groups.Take(10)) + (groups.Count > 10 ? " …" : "") + ". ";
            }
            if (!m.RealRun) return preview + "Das ist das Beispiel-Chaos, es ändert sich nichts Echtes.";
            var parts = new List<string>();
            if (m.FileBytes > 0) parts.Add($"{Fmt.Bytes(m.FileBytes)} in Dateien");
            if (m.FolderCount > 0) parts.Add($"{m.FolderCount} {(m.FolderCount == 1 ? "Ordner" : "Ordner")}");
            var text = preview + (parts.Count > 0 ? string.Join(", dazu ", parts) + ". " : "");
            text += "Nichts wird gelöscht, und du kannst alles mit einem Klick zurückholen.";
            if (m.LeftAlone > 0) text += $" {m.LeftAlone} zur Sicherheit liegen gelassen.";
            return text;
        }
    }
}

/// Renders the panel's states to PNGs, to review the layout without touching the screen.
public static class PanelShot
{
    static void One(Model m, string path)
    {
        var p = new Panel(m);
        var content = (FrameworkElement)p.Content;
        p.Content = null;
        var frame = new Border { Background = p.Background, Child = content, Width = 372 };
        TextOptions.SetTextFormattingMode(frame, TextFormattingMode.Display);
        frame.Measure(new Size(372, double.PositiveInfinity));
        frame.Arrange(new Rect(frame.DesiredSize));
        frame.UpdateLayout();
        p.Refresh();
        frame.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(372 * 2), (int)(frame.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(frame);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
        p.Close();
    }

    public static void Render(string dir)
    {
        Settings.Persists = false;
        System.IO.Directory.CreateDirectory(dir);
        Model M() { var m = new Model(); m.Settings.SkipFinalClick = false; m.Settings.SupportCardShown = true; return m; }
        var a = M(); a.Mode = CleanMode.Sort; a.Fun = FunMode.Flames; One(a, System.IO.Path.Combine(dir, "panel_ready.png"));
        var a2 = M(); a2.Source = Source.Real; a2.Mode = CleanMode.Hide; a2.LastRun = new FileOps.Run { Mode = "move", When = DateTime.Now, Items = new() { new(), new(), new() } };
        One(a2, System.IO.Path.Combine(dir, "panel_ready_real.png"));
        var e = DesktopReader.Sample(new V2(1536, 912));
        var s = new Sim(new V2(1536, 912), V2.Zero, new V2(1536, 912), e);
        foreach (var it in s.Items.Take(30)) it.Seen = true;

        var b = M(); b.Stage = Stage.Running; b.Sim = s; b.Status = "Saugt"; b.Phase = Phase.Cleaning; b.Total = 42; b.Collected = 17; b.CollectedBytes = 4_300_000_000; b.Bin = 0.7f; b.Battery = 0.92f; b.RunTime = 74;
        One(b, System.IO.Path.Combine(dir, "panel_running.png"));
        var d = M(); d.Stage = Stage.Running; d.Sim = s; d.Status = "Alles sauber"; d.Phase = Phase.Done; d.Total = 105; d.Collected = 105; d.FileBytes = 2_600_000_000; d.FolderCount = 3; d.LeftAlone = 9; d.RealRun = true; d.UsesCloud = true; d.Mode = CleanMode.Sort; d.Battery = 0.9f; d.RunTime = 196; d.ReviewOpen = true;
        d.ReviewRows = Enumerable.Range(0, 12).Select(i => (i, $"Screenshot 2026-10-{10 + i} 091422.png")).ToList();
        One(d, System.IO.Path.Combine(dir, "panel_decision.png"));
        var f = M(); f.Source = Source.Real; f.Result = new RunResult { Title = "105 Elemente in die Ablage geräumt", Detail = "Sie liegen in „~\\RoboDust Ablage\\2026-11-12 14.30.05“. 9 zur Sicherheit liegen gelassen.", CanUndo = true };
        f.Support = SupportCard.Asking;
        One(f, System.IO.Path.Combine(dir, "panel_result.png"));
        var g = M(); g.Page = Page.Settings; g.Fun = FunMode.Plow; g.Settings.Skin = 2; g.Settings.Hat = true; g.Settings.Schedule = 2; g.Settings.MinAgeDays = 7;
        One(g, System.IO.Path.Combine(dir, "panel_settings.png"));
        var h = M(); h.Settings.Stats = new Stats { Runs = 12, Items = 312, Bytes = 14_200_000_000, Meters = 1234, Kinds = new() { ["Bilder"] = 120, ["Dokumente"] = 80 } };
        One(h, System.IO.Path.Combine(dir, "panel_stats.png"));
    }
}

public static class Fmt
{
    public static string Bytes(long b)
    {
        string[] units = { "Byte", "KB", "MB", "GB", "TB" };
        double v = b; int u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return u == 0 ? $"{b} Byte" : $"{v.ToString(v < 10 ? "0.#" : "0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} {units[u]}";
    }
}

/// The floor plan, like the one in a real robot's phone app. Draws only what lidar has "seen".
public sealed class MapView : FrameworkElement
{
    readonly Model model;
    static readonly Color Accent = Color.FromRgb(92, 214, 250);
    public MapView(Model m) { model = m; }

    protected override void OnRender(DrawingContext c)
    {
        var s = model.Sim;
        if (s == null) return;
        // Every display in its real place: one floor plan with a room per display.
        var b = s.Plan.Bounds;
        double W = ActualWidth, H = ActualHeight;
        var k = Math.Min(W / b.W, H / b.H);
        double ox = (W - b.W * k) / 2, oy = (H - b.H * k) / 2;
        Point P(V2 v) => new(ox + (v.X - b.X) * k, oy + (v.Y - b.Y) * k);
        var rooms = new GeometryGroup();
        var gap = s.Plan.Rooms.Count > 1 ? 1.5 : 0;
        foreach (var r in s.Plan.Rooms)
        {
            var tl = P(new V2(r.X, r.Y));
            rooms.Children.Add(new RectangleGeometry(new Rect(tl.X + gap, tl.Y, Math.Max(0, r.W * k - 2 * gap), r.H * k), 6, 6));
        }
        c.PushClip(rooms);
        c.DrawGeometry(new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)), null, rooms);
        if (s.Trail.Count > 1)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(P(s.Trail[0]), false, false);
                for (int i = 1; i < s.Trail.Count; i++) ctx.LineTo(P(s.Trail[i]), true, true);
            }
            var wide = new Pen(new SolidColorBrush(Color.FromArgb(41, Accent.R, Accent.G, Accent.B)), s.R * 1.7 * k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var thin = new Pen(new SolidColorBrush(Color.FromArgb(166, Accent.R, Accent.G, Accent.B)), 1) { LineJoin = PenLineJoin.Round };
            c.DrawGeometry(null, wide, g);
            c.DrawGeometry(null, thin, g);
        }
        var dock = P(s.DockPos + new V2(0, s.R * 1.2f));
        c.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), null, new Rect(dock.X - 10, dock.Y - 6, 20, 12), 3, 3);
        foreach (var it in s.Items)
        {
            if (!it.Seen || !it.OnFloor) continue;
            c.DrawEllipse(new SolidColorBrush(KindColor(it.Entry.Kind)), null, P(it.Icon.Pos), 1.6, 1.6);
        }
        var rp = P(s.Pos); var rr = Math.Max(s.R * k, 4);
        c.DrawEllipse(Brushes.White, null, rp, rr, rr);
        c.DrawLine(new Pen(Brushes.Black, 1.5), rp, new Point(rp.X + Math.Cos(s.Heading) * rr, rp.Y + Math.Sin(s.Heading) * rr));
        if (model.Phase == Phase.Scanning)
        {
            var ft = new FormattedText("Kartiert", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, new SolidColorBrush(Accent), 1.25);
            c.DrawText(ft, new Point(ox + 8, oy + 6));
        }
        c.Pop();
    }

    static Color KindColor(Kind k) => k switch
    {
        Kind.Folder => Color.FromRgb(102, 178, 255), Kind.Image => Color.FromRgb(255, 184, 102), Kind.Video => Color.FromRgb(255, 115, 128),
        Kind.Document => Color.FromRgb(230, 230, 230), Kind.Archive => Color.FromRgb(204, 153, 255), Kind.Audio => Color.FromRgb(128, 242, 178),
        _ => Color.FromRgb(153, 153, 153),
    };
}
