using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodeUsageMonit {
    // The taskbar ticker: a small borderless window that sits on the taskbar and shows the
    // quota summary. Windows 11 no longer hosts third-party desk bands, so like
    // TrafficMonitor's Win11 mode this is an overlay pinned over the taskbar's free space: it
    // tracks the Shell_TrayWnd position each second, hides when a full-screen app owns that
    // monitor, and never takes focus. Two scroll directions (right-click to switch): 上下
    // rolls one provider per line upwards (default, keeps the strip short); 左右 is the
    // classic single-line marquee. Optional segments (today's cost, tokens, output speed,
    // data freshness) are appended / get their own line. Independent of the four display
    // sizes: the desktop widget and the panel can be open or hidden while it runs.
    public sealed partial class MonitorPanel {
        private Window stripWindow;
        private Border stripRoot;
        private Grid stripViewport;
        private StackPanel stripTrack;
        private StackPanel stripCopyA, stripCopyB;
        private TranslateTransform stripShift;
        private bool stripHover, stripDragging, stripLeftDown;
        private int stripDragAnchor, stripDragLeft;
        private string lastStripText;
        // Vertical mode is a one-line carousel: the pill is exactly one line tall and every
        // few seconds slides up to the next provider; horizontal is the classic marquee.
        private double stripPitch;
        private int stripLineCount;
        // Width the lines were laid out for (a wheel resize re-lays them out).
        private double stripLaidWidth;
        // Vertical carousel state: the content, the line being shown, and the hold timer.
        private List<StripLine> stripLines;
        private int stripIndex;
        private DispatcherTimer stripHoldTimer;
        // The tint colour the glass layer uses (kept for ApplyBackdrop after the hwnd exists).
        private Color stripTint;
        // A light tint flips the whole text palette to dark ink (readable on pale glass).
        private bool stripLight;
        private static readonly Brush StripInkLight = Brush("#1D2129"), StripDimLight = Brush("#565D69"), StripFaintLight = Brush("#808895"), StripWarnLight = Brush("#9E6416");
        private static double Luma(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255; }
        private Color ResolveStripTint() {
            string value = config.TaskbarTint;
            if (value != null && value.Length == 7 && value[0] == '#') { Color parsed; if (TryParseHex(value, out parsed)) return parsed; }
            return WindowFrame.ParseTint(config.SurfaceColor);
        }
        private Brush StripInk { get { return stripLight ? StripInkLight : Ink; } }
        private Brush StripDim { get { return stripLight ? StripDimLight : InkDim; } }
        private Brush StripFaint { get { return stripLight ? StripFaintLight : InkFaint; } }
        private Brush StripWarn { get { return stripLight ? StripWarnLight : WarnBrush; } }
        // Provider brand colour, deepened on light glass so it still reads.
        private Brush StripBrand(string id) {
            Color c; if (!TryParseHex(ProviderCatalog.Color(id), out c)) return Ink;
            if (stripLight) c = Color.FromRgb((byte)Math.Round(c.R * .60), (byte)Math.Round(c.G * .60), (byte)Math.Round(c.B * .60));
            return new SolidColorBrush(c);
        }
        private DispatcherTimer stripPlaceTimer, stripMinuteTimer;
        private const double HorizontalWidth = 380, VerticalWidth = 280;

        // On / off (tray menu, settings, the ticker's own menu). Always persists.
        private void SetTaskbarStrip(bool on) {
            config.TaskbarStrip = on; SaveConfig();
            if (on) EnableStrip(); else CloseStrip();
        }
        private void EnableStrip() {
            if (quitting || stripWindow != null) return;
            BuildStrip();
            UpdateStripContent();
            PlaceStrip();
        }
        private void CloseStrip() {
            if (stripWindow == null) return;
            if (stripStepTimer != null) stripStepTimer.Stop();
            if (stripPlaceTimer != null) stripPlaceTimer.Stop();
            if (stripMinuteTimer != null) stripMinuteTimer.Stop();
            Window closing = stripWindow; stripWindow = null; lastStripText = null;
            closing.Close();
        }

        // ── Window ────────────────────────────────────────────────────────
        private void BuildStrip() {
            bool vertical = config.TaskbarVertical;
            var shift = new TranslateTransform();
            stripShift = shift;
            stripCopyA = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal };
            stripCopyB = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal };
            stripTrack = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = vertical ? VerticalAlignment.Top : VerticalAlignment.Center, RenderTransform = shift };
            stripTrack.Children.Add(stripCopyA); stripTrack.Children.Add(stripCopyB);
            stripViewport = new Grid { ClipToBounds = true };
            stripViewport.Children.Add(stripTrack);
            Color tint = ResolveStripTint();
            stripTint = tint; stripLight = Luma(tint) > 0.52;
            // Smoked glass: the DWM Acrylic blur does the frosting, the tint layer is thin
            // enough to let the taskbar through (not a black slab), with a hairline rim.
            stripRoot = new Border {
                CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(Color.FromArgb(stripLight ? (byte)0x99 : (byte)0x26, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = vertical ? new Thickness(10, 1.5, 10, 1.5) : new Thickness(11, 0, 11, 0),
                Child = stripViewport
            };
            stripWindow = new Window {
                Title = "codeusagemonit", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, Focusable = false,
                Background = Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.Manual,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
                FontSize = 12.5, Foreground = Ink, UseLayoutRounding = true, SnapsToDevicePixels = true,
                Content = stripRoot, ContextMenu = StripMenu()
            };
            // No AllowsTransparency: a per-pixel layered window renders on the CPU and the
            // slide stutters. Like the panel, transparency comes from DWM (GPU-composited).
            WindowChrome.SetWindowChrome(stripWindow, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(-1), CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = false });
            System.Windows.Automation.AutomationProperties.SetName(stripRoot, I18n.T("任务栏滚动显示"));
            // Hover pauses the scroll; a drag moves along the taskbar; a plain click toggles
            // the panel; the wheel widens / narrows; right-click opens the menu.
            // Hover freezes the carousel (hold timer stops; a running slide finishes) and
            // the horizontal drift (the Stopwatch pauses).
            stripRoot.MouseEnter += delegate { stripHover = true; stripWatch.Stop(); if (stripHoldTimer != null) stripHoldTimer.Stop(); };
            stripRoot.MouseLeave += delegate { stripHover = false; stripWatch.Start(); RestartStripHold(); };
            stripHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            stripHoldTimer.Tick += delegate { SlideStripOnce(); };
            stripStepTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
            stripStepTimer.Tick += delegate { StripStep(); };
            stripRoot.MouseWheel += delegate(object sender, MouseWheelEventArgs e) {
                config.TaskbarWidth = Math.Max(180, Math.Min(720, config.TaskbarWidth + (e.Delta > 0 ? 20 : -20)));
                PlaceStrip(); SaveConfig(); e.Handled = true;
            };
            stripRoot.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                stripLeftDown = true; stripDragging = false;
                stripDragAnchor = Forms.Cursor.Position.X;
                // The offset the strip has inside the taskbar right now (device pixels).
                RECT bar; stripDragLeft = TaskbarRect(out bar) && bar.Width > 0 ? StripLeftDevice(bar) : -1;
                stripRoot.CaptureMouse();
            };
            stripRoot.MouseMove += delegate(object sender, MouseEventArgs e) {
                if (!stripLeftDown) return;
                int dx = Forms.Cursor.Position.X - stripDragAnchor;
                if (!stripDragging && Math.Abs(dx) < 4) return;
                stripDragging = true;
                RECT bar; if (stripDragLeft >= 0 && TaskbarRect(out bar)) {
                    int width = (int)Math.Round(config.TaskbarWidth * SystemDpi());
                    config.TaskbarLeft = Math.Max(4, Math.Min(bar.Width - width - 4, stripDragLeft + dx));
                    PlaceStrip();
                }
            };
            stripRoot.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) {
                stripRoot.ReleaseMouseCapture();
                bool dragged = stripDragging; stripLeftDown = false; stripDragging = false;
                if (dragged) { SaveConfig(); return; }
                ToggleFromTray();
            };
            stripWindow.SourceInitialized += delegate {
                IntPtr hwnd = new WindowInteropHelper(stripWindow).Handle;
                // Tool window: never in Alt+Tab; no-activate: clicks keep the focus where it was.
                IntPtr style = GetWindowLong(hwnd, -20);
                SetWindowLong(hwnd, -20, new IntPtr(style.ToInt64() | 0x80L | 0x08000000L));
                HwndSource.FromHwnd(hwnd).AddHook(StripHook);
                // System blur under a thin tint: the frosted-glass look (GPU-smooth).
                WindowFrame.ApplyBackdrop(stripWindow, stripRoot, "acrylic", .62, stripTint);
            };
            // A wheel resize changes the line width: re-lay the content out (the animation
            // pass picks the new pitch / extent up on its next tick).
            stripWindow.SizeChanged += delegate { UpdateStripContent(); };
            stripWindow.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (stripWindow != null) { e.Cancel = true; stripWindow.Hide(); } };
            // The taskbar can move (resolution, explorer restarting) and full-screen apps come
            // and go; the minute pass keeps reset countdowns fresh while the panel is hidden.
            stripPlaceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            stripPlaceTimer.Tick += delegate { PlaceStrip(); };
            stripMinuteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            stripMinuteTimer.Tick += delegate { UpdateStripContent(); };
            stripPlaceTimer.Start(); stripMinuteTimer.Start();
            stripWindow.Show();
        }
        private IntPtr StripHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
            // WM_MOUSEACTIVATE → MA_NOACTIVATE: clicking the ticker never focuses it.
            if (message == 0x0021) { handled = true; return new IntPtr(3); }
            return IntPtr.Zero;
        }

        // ── Content ───────────────────────────────────────────────────────
        // Typography: provider name 12.5 semibold in its colour, window labels 11 dim,
        // the percentage is the hero (13.5 semibold, warning colour when low), the compact
        // countdown 11 faint. Sizes ride on the runs so both directions share them.
        private sealed class StripRun { public string Text; public Brush Color; public FontWeight Weight = FontWeights.Normal; public double Size = 12.5; }
        // One line per provider (vertical mode); the horizontal marquee flattens them.
        private sealed class StripLine { public List<StripRun> Runs = new List<StripRun>(); }
        // Called from Render() (every refresh) and the strip's own minute timer. The text is
        // compared first: unchanged content must not restart the scroll.
        private void UpdateStripContent() {
            if (stripWindow == null || quitting) return;
            List<StripLine> lines = StripLines();
            string joined = String.Join("\n", lines.Select(l => String.Join("", l.Runs.Select(r => r.Text).ToArray())).ToArray());
            if (joined == lastStripText && stripCopyA.Children.Count > 0 && Math.Abs(config.TaskbarWidth - stripLaidWidth) < 0.5) return;
            lastStripText = joined; stripLaidWidth = config.TaskbarWidth;
            bool vertical = config.TaskbarVertical;
            FillStripCopy(stripCopyA, lines, vertical); FillStripCopy(stripCopyB, lines, vertical);
            stripRoot.ToolTip = joined;
            if (vertical) {
                // One-line carousel: the viewport holds the current line and the next one;
                // a slide swaps them (BuildStripTrack), so the geometry is always exact.
                stripLines = lines;
                stripLineCount = lines.Count;
                if (stripIndex >= stripLineCount) stripIndex = 0;
                BuildStripTrack();
            } else {
                stripLines = null;
                stripCopyB.Visibility = Visibility.Visible; // the seam of the marquee loop
                stripPitch = 0;
                StartStripAnimation();
            }
            PlaceStrip();
        }
        // The vertical track: line[index] on top, line[index + 1] below; top-aligned, the
        // transform 0 shows exactly the current line. Measured here: pitch = one line.
        private void BuildStripTrack() {
            stripTrack.Children.Clear();
            stripTrack.Children.Add(StripBlock(stripLines[stripIndex]));
            stripTrack.Children.Add(StripBlock(stripLines[(stripIndex + 1) % stripLines.Count]));
            stripTrack.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
            stripPitch = stripTrack.DesiredSize.Height / 2;
            stripShift.BeginAnimation(TranslateTransform.YProperty, null);
            stripShift.Y = 0;
            RestartStripHold();
        }
        private TextBlock StripBlock(StripLine line) {
            // Display-mode formatting keeps 11–13.5 px glyphs crisp (greyscale anti-aliasing
            // at that size looks fuzzy on a glass surface); short lines centre in the pill.
            var block = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Math.Max(60, config.TaskbarWidth - 24), Margin = new Thickness(0, 1, 0, 1), TextAlignment = TextAlignment.Center };
            System.Windows.Media.TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
            System.Windows.Media.TextOptions.SetTextRenderingMode(block, TextRenderingMode.ClearType);
            foreach (StripRun run in line.Runs) block.Inlines.Add(new System.Windows.Documents.Run(run.Text) { Foreground = run.Color, FontWeight = run.Weight, FontSize = run.Size });
            return block;
        }
        // Hold for Hold seconds, then slide the next line up (WPF animates the one move);
        // after the slide, rebuild the pair and hold again. Hover stops the hold timer; a
        // slide already running finishes first.
        private void RestartStripHold() {
            if (stripHoldTimer == null) return;
            stripHoldTimer.Stop();
            if (!stripHover && !quitting && stripLineCount > 1 && stripPitch > 0) stripHoldTimer.Start();
        }
        private void SlideStripOnce() {
            if (stripWindow == null || quitting || stripLines == null || stripLineCount <= 1 || stripPitch <= 0) return;
            var slide = new DoubleAnimation(-stripPitch, TimeSpan.FromSeconds(0.5)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            slide.Completed += delegate {
                stripIndex = (stripIndex + 1) % stripLineCount;
                BuildStripTrack(); // resets the transform and restarts the hold
            };
            stripShift.BeginAnimation(TranslateTransform.YProperty, slide);
        }
        private void FillStripCopy(StackPanel copy, List<StripLine> lines, bool vertical) {
            copy.Children.Clear();
            // A vertical StackPanel measures children with infinite width, so TextTrimming
            // never kicks in and long lines get hard-clipped at the border instead of
            // ellipsized. Constrain each line to the pill's text area explicitly.
            double available = vertical ? Math.Max(60, config.TaskbarWidth - 24) : Double.PositiveInfinity;
            foreach (StripLine line in lines) {
                var block = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = available };
                System.Windows.Media.TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
                if (vertical) block.Margin = new Thickness(0, 1, 0, 1);
                foreach (StripRun run in line.Runs) block.Inlines.Add(new System.Windows.Documents.Run(run.Text) { Foreground = run.Color, FontWeight = run.Weight, FontSize = run.Size });
                copy.Children.Add(block);
            }
            copy.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
        }
        // Compact countdown for the narrow pill: "6d16h" / "2h05m" / "45m" / "30s" — no
        // unit words, so the line fits at 250 px. ("6天 23小时后重置" did not.)
        private static string CompactCountdown(string iso) {
            DateTimeOffset date; if (!DateTimeOffset.TryParse(iso, out date)) return "";
            var span = date - DateTimeOffset.UtcNow;
            if (span.TotalSeconds <= 0) return Countdown(iso); // 等待新窗口 / waiting state
            if (span.TotalDays >= 1) return (int)span.TotalDays + "d" + span.Hours + "h";
            if (span.TotalHours >= 1) return (int)span.TotalHours + "h" + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
            if (span.TotalMinutes >= 1) return (int)span.TotalMinutes + "m";
            return (int)span.TotalSeconds + "s";
        }
        private List<StripLine> StripLines() {
            bool vertical = config.TaskbarVertical;
            var lines = new List<StripLine>();
            Func<StripLine> newLine = delegate { var l = new StripLine(); lines.Add(l); return l; };
            Action<StripLine, string, Brush, bool> add = delegate(StripLine l, string text, Brush color, bool strong) {
                if (text.Length > 0) l.Runs.Add(new StripRun { Text = text, Color = color, Weight = strong ? FontWeights.SemiBold : FontWeights.Normal });
            };
            Action<StripLine, string, Brush, bool, double> sized = delegate(StripLine l, string text, Brush color, bool strong, double size) {
                if (text.Length > 0) l.Runs.Add(new StripRun { Text = text, Color = color, Weight = strong ? FontWeights.SemiBold : FontWeights.Normal, Size = size });
            };
            Action<StripLine> sep = delegate(StripLine l) { if (l.Runs.Count > 0) sized(l, "  ·  ", StripFaint, false, 10.5); };
            // Every enabled provider, one line each: name in its colour, then ALL quota
            // windows (not just the primary one), the primary's window also with a countdown.
            foreach (string id in EnabledIds()) {
                ProviderState state = states[id];
                List<DayUsage> days = history.Days.Where(d => d.Agent == id).ToList();
                StripLine line = newLine();
                if (state.Status == "ready") {
                    add(line, ProviderCatalog.Name(id) + "  ", StripBrand(id), true);
                    // Only time-windowed token quotas; windowless rows (Zhipu's monthly MCP
                    // tool allowance) stay on the panel but would just clutter the ticker.
                    List<Quota> quotas = state.Quotas.Where(q => q.WindowSeconds > 0).ToList();
                    bool first = true;
                    foreach (Quota quota in quotas) {
                        if (!first) sized(line, " · ", StripFaint, false, 10.5);
                        sized(line, quota.Label + " ", StripDim, false, 11);
                        sized(line, quota.Remaining.ToString("0", CultureInfo.InvariantCulture) + "%", quota.Remaining < 10 ? StripWarn : StripInk, true, 13.5);
                        sized(line, " " + CompactCountdown(quota.ResetUtc), StripFaint, false, 11);
                        first = false;
                    }
                    if (quotas.Count == 0 && state.Balances.Count > 0)
                        sized(line, state.Balances[0].Currency + " " + state.Balances[0].Amount.ToString("N2", CultureInfo.InvariantCulture), StripInk, true, 13);
                    else if (quotas.Count == 0 && days.Count > 0)
                        sized(line, I18n.T("近 30 天 ") + Compact(days.Sum(d => d.Tokens)) + I18n.T(" Token"), StripInk, false, 11.5);
                    else if (quotas.Count == 0) sized(line, I18n.T("暂无数据"), StripDim, false, 11.5);
                    if (config.TaskbarExtras.Contains("speed")) {
                        double? speed = RecentSpeed(id, days);
                        if (speed.HasValue) { add(line, " ", StripInk, false); add(line, OutputTiming.Text(speed), InkDim, false); }
                    }
                } else { add(line, ProviderCatalog.Name(id) + "  ", StripBrand(id), true); sized(line, I18n.T("未连接"), StripFaint, false, 11.5); }
            }
            // Global segments: their own line when scrolling vertically, trailing segments
            // on the last line when scrolling horizontally.
            List<DayUsage> all = history.Days.Where(d => config.Enabled.Contains(d.Agent)).ToList();
            Action<string, string> extra = delegate(string key, string text) {
                if (!config.TaskbarExtras.Contains(key) || text.Length == 0) return;
                if (vertical) { StripLine l = newLine(); sized(l, text, StripDim, false, 11); }
                else { if (lines.Count == 0) newLine(); sep(lines.Last()); sized(lines.Last(), text, StripDim, false, 11); }
            };
            double todayTokens = all.Where(d => d.Day == Today()).Sum(d => d.Tokens);
            extra("today-cost", I18n.T("今日 ") + TodayMoney(all));
            extra("month-cost", I18n.T("30 天 ") + Money(all));
            extra("today-tokens", I18n.T("今日 ") + Compact(todayTokens) + I18n.T(" Token"));
            string freshest = states.Values.Where(s => s.Status == "ready").Select(s => s.LastSuccess).Where(t => t.Length > 0).OrderByDescending(t => t).FirstOrDefault() ?? "";
            extra("updated", freshest.Length > 0 ? UpdatedAgo(freshest) : "");
            if (lines.Count == 0) { StripLine l = newLine(); add(l, "codeusagemonit", StripFaint, false); }
            return lines;
        }

        // ── Scroll ────────────────────────────────────────────────────────
        // A 15 ms Render-priority timer steps the transform from a Stopwatch (a WPF
        // AnimationClock on the transform made lines 2+ vanish on this window; TickCount
        // quantizes to ~16 ms and judders). Values only change during the 0.5 s slides,
        // so holds invalidate nothing. Vertical: hold Hold seconds, ease up over Slide;
        // the wrap slides into the second copy (identical to line 1). Horizontal: 35 px/s.
        private readonly System.Diagnostics.Stopwatch stripWatch = new System.Diagnostics.Stopwatch();
        private DispatcherTimer stripStepTimer;
        private double stripLastY = double.NaN, stripLastX = double.NaN;
        private void StartStripAnimation() {
            stripWatch.Restart();
            stripLastY = 0; stripLastX = 0; stripShift.Y = 0; stripShift.X = 0;
            if (stripStepTimer != null && !stripStepTimer.IsEnabled) stripStepTimer.Start();
        }
        private void StripStep() {
            if (stripWindow == null || quitting || config.TaskbarVertical) return; // vertical = slide animations
            double t = stripWatch.Elapsed.TotalSeconds;
            double extent = stripCopyA.DesiredSize.Width;
            double viewport = stripViewport.ActualWidth;
            if (viewport <= 0) viewport = Math.Max(0, stripWindow.ActualWidth - 22);
            if (extent <= viewport || extent <= 0) return;
            double x = -(t * 35) % extent;
            if (Math.Abs(x - stripLastX) >= 0.25) { stripShift.X = x; stripLastX = x; }
        }

        // ── Placement ─────────────────────────────────────────────────────
        // Runs every second: follows the taskbar (moved / resized / explorer restarted),
        // re-asserts topmost over it, and hides while a full-screen app owns the monitor.
        private void PlaceStrip() {
            if (stripWindow == null || quitting) return;
            RECT bar;
            if (!TaskbarRect(out bar) || bar.Width < 80 || bar.Height < 20 || bar.Height >= bar.Width) { if (stripWindow.IsVisible) stripWindow.Hide(); return; }
            IntPtr hwnd = new WindowInteropHelper(stripWindow).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (StripBlockedByFullscreen(hwnd)) { if (stripWindow.IsVisible) stripWindow.Hide(); return; }
            double scale = SystemDpi();
            // Vertical: the pill is exactly one line tall, vertically centred in the taskbar;
            // horizontal mode keeps the full in-bar slot. The wheel only ever changes width.
            double inBar = (bar.Height - 12) / scale;
            double want = inBar;
            if (config.TaskbarVertical && stripPitch > 0) want = Math.Min(stripPitch + 8, inBar);
            int height = (int)Math.Round(want * scale);
            int width = (int)Math.Round(config.TaskbarWidth * scale);
            int left = (int)Math.Round(bar.Left + (config.TaskbarLeft ?? 12));
            left = Math.Max(bar.Left + 4, Math.Min(bar.Right - width - 4, left));
            int top = bar.Top + (bar.Height - height) / 2;
            stripWindow.Width = config.TaskbarWidth; stripWindow.Height = want;
            // The taskbar is itself topmost: interacting with it (button clicks, flyouts)
            // raises it above this pill, covering it. Re-assert the top of the topmost band
            // every pass — a z-order-only call, no move, no repaint. Full repositioning
            // still happens only when the geometry actually changed.
            if (left != stripPlacedLeft || top != stripPlacedTop || width != stripPlacedWidth || height != stripPlacedHeight) {
                SetWindowPos(hwnd, new IntPtr(-1), left, top, width, height, 0x0010);
                stripPlacedLeft = left; stripPlacedTop = top; stripPlacedWidth = width; stripPlacedHeight = height; stripPlacedAt = Environment.TickCount;
            } else SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            if (!stripWindow.IsVisible) stripWindow.Show();
        }
        private int stripPlacedLeft, stripPlacedTop, stripPlacedWidth, stripPlacedHeight, stripPlacedAt = -100000;
        private int StripLeftDevice(RECT bar) {
            IntPtr hwnd = stripWindow == null ? IntPtr.Zero : new WindowInteropHelper(stripWindow).Handle;
            RECT self; return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out self) ? self.Left - bar.Left : 0;
        }
        private static double SystemDpi() {
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96;
        }
        // A full-screen foreground window (game, video, F11) hides the taskbar itself, so
        // the pill must hide too. The signal is the foreground window covering the TASKBAR
        // rect — not the whole monitor: frameless or oddly-shaped maximized windows report
        // full-monitor rectangles and a monitor rule misfires on them. The desktop and the
        // shell windows are excluded by class; windows on other monitors never match.
        private bool StripBlockedByFullscreen(IntPtr strip) {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == strip) return false;
            var name = new StringBuilder(64); GetClassName(foreground, name, 64);
            string cls = name.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "SecondaryTrayWnd") return false;
            IntPtr bar; RECT barRect;
            bar = FindWindow("Shell_TrayWnd", null);
            if (bar == IntPtr.Zero || !GetWindowRect(bar, out barRect)) return false;
            if (MonitorFromWindow(foreground, 2) != MonitorFromWindow(bar, 2)) return false;
            RECT rect;
            if (!GetWindowRect(foreground, out rect)) return false;
            bool blocked = rect.Left <= barRect.Left && rect.Top <= barRect.Top && rect.Right >= barRect.Right && rect.Bottom >= barRect.Bottom;
            if (blocked) LogStripHide(cls, rect, barRect);
            return blocked;
        }
        // Every hide is logged with the triggering window, so an unexpected disappearance
        // can be diagnosed from data\strip-hide.log afterwards.
        private string stripHideLastLine;
        private void LogStripHide(string cls, RECT window, RECT bar) {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  hidden by " + cls + "  window=" + window.Left + "," + window.Top + "," + window.Right + "," + window.Bottom + "  taskbar=" + bar.Left + "," + bar.Top + "," + bar.Right + "," + bar.Bottom;
            if (line == stripHideLastLine) return;
            stripHideLastLine = line;
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(Store.Data, "strip-hide.log"), line + "\n"); } catch { }
        }
        private static bool TaskbarRect(out RECT rect) {
            rect = new RECT();
            IntPtr bar = FindWindow("Shell_TrayWnd", null);
            return bar != IntPtr.Zero && GetWindowRect(bar, out rect);
        }

        // ── Menu ──────────────────────────────────────────────────────────
        private ContextMenu StripMenu() {
            var menu = new ContextMenu();
            menu.Opened += delegate {
                menu.Items.Clear();
                var open = new MenuItem { Header = window.IsVisible ? I18n.T("收起到托盘") : I18n.T("打开面板") };
                open.Click += delegate { ToggleFromTray(); };
                var refresh = new MenuItem { Header = I18n.T("刷新全部") };
                refresh.Click += delegate { var ignored = Refresh(); };
                var content = new MenuItem { Header = I18n.T("显示内容") };
                foreach (string pair in new[] { "today-cost|今日费用", "month-cost|30 天费用", "today-tokens|今日 Token", "speed|各平台输出速度", "updated|更新时间" }) {
                    string key = pair.Substring(0, pair.IndexOf('|')), label = pair.Substring(pair.IndexOf('|') + 1);
                    var extra = new MenuItem { Header = I18n.T(label), IsCheckable = true, IsChecked = config.TaskbarExtras.Contains(key) };
                    extra.Click += delegate {
                        if (config.TaskbarExtras.Contains(key)) config.TaskbarExtras.Remove(key);
                        else config.TaskbarExtras.Add(key);
                        SaveConfig(); UpdateStripContent();
                    };
                    content.Items.Add(extra);
                }
                // 上下 keeps the strip short (one provider per line); 左右 is one long line.
                var direction = new MenuItem { Header = I18n.T("滚动方向") };
                foreach (string pair in new[] { "vertical|上下滚动", "horizontal|左右滚动" }) {
                    string key = pair.Substring(0, pair.IndexOf('|')), label = pair.Substring(pair.IndexOf('|') + 1);
                    bool vertical = config.TaskbarVertical;
                    var item = new MenuItem { Header = I18n.T(label), IsCheckable = true, IsChecked = (vertical ? "vertical" : "horizontal") == key };
                    item.Click += delegate {
                        if ((vertical ? "vertical" : "horizontal") == key) return;
                        config.TaskbarVertical = key == "vertical";
                        config.TaskbarWidth = config.TaskbarVertical ? VerticalWidth : HorizontalWidth;
                        config.TaskbarLeft = null;
                        SaveConfig(); CloseStrip(); EnableStrip();
                    };
                    direction.Items.Add(item);
                }
                // Glass tint presets; light tints flip the text palette to dark ink.
                var tintMenu = new MenuItem { Header = I18n.T("底色") };
                foreach (string pair in new[] { "panel|跟随面板", "#10131A|墨黑玻璃", "#1B2A45|深蓝玻璃", "#2A2440|暖紫玻璃", "#F2F3F7|浅白玻璃" }) {
                    string key = pair.Substring(0, pair.IndexOf('|')), label = pair.Substring(pair.IndexOf('|') + 1);
                    string current = String.IsNullOrEmpty(config.TaskbarTint) ? "panel" : config.TaskbarTint;
                    var item = new MenuItem { Header = I18n.T(label), IsCheckable = true, IsChecked = current == key };
                    item.Click += delegate {
                        config.TaskbarTint = key; SaveConfig(); CloseStrip(); EnableStrip();
                    };
                    tintMenu.Items.Add(item);
                }
                var close = new MenuItem { Header = I18n.T("关闭任务栏显示") };
                close.Click += delegate { SetTaskbarStrip(false); };
                menu.Items.Add(open); menu.Items.Add(refresh); menu.Items.Add(new Separator());
                menu.Items.Add(content); menu.Items.Add(direction); menu.Items.Add(tintMenu); menu.Items.Add(new Separator()); menu.Items.Add(close);
            };
            return menu;
        }

        // ── Interop ───────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; public int Width { get { return Right - Left; } } public int Height { get { return Bottom - Top; } } }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RECT Monitor, Work; public uint Flags; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
        private static IntPtr GetWindowLong(IntPtr hwnd, int index) { return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index)); }
        private static void SetWindowLong(IntPtr hwnd, int index, IntPtr value) { if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, index, value); else SetWindowLong32(hwnd, index, value.ToInt32()); }
    }
}
