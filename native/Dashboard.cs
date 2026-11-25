using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("GPU 空闲卡监控")]
[assembly: AssemblyDescription("通过 SSH 查看 NVIDIA 显卡、计算进程和磁盘余量")]
[assembly: AssemblyProduct("GPU 空闲卡监控")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace GpuWatch
{
    internal static class AppIcon
    {
        internal static readonly BitmapFrame Image = Load();

        private static BitmapFrame Load()
        {
            using (var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("GpuWatch.ico"))
            {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                frame.Freeze();
                return frame;
            }
        }
    }

    internal static class Palette
    {
        public static readonly Brush Ink = Make("#ECF2FC");
        public static readonly Brush Dim = Make("#8494B1");
        public static readonly Brush Mint = Make("#63E3B8");
        public static readonly Brush Amber = Make("#F6BE78");
        public static readonly Brush Blue = Make("#9DBBFF");
        public static readonly Brush Red = Make("#F8A4AA");
        public static readonly Brush Normal = Make("#172133");
        public static readonly Brush Line = Make("#2A354A");
        public static readonly Brush GreenSurface = Make("#182F2C");
        public static readonly Brush GoldSurface = Make("#332A2D");
        public static readonly Brush GreenLine = Make("#345F53");
        public static readonly Brush GoldLine = Make("#654F40");
        public static readonly Brush BlueSurface = Make("#243353");
        public static readonly Brush BlueLine = Make("#516A9C");
        public static readonly Brush Slate = Make("#283349");

        private static Brush Make(string hex)
        {
            var result = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            result.Freeze();
            return result;
        }
    }

    internal sealed class CardView
    {
        public readonly string Host;
        public readonly Gpu Gpu;
        public readonly CardState State;
        public readonly List<GpuProcess> Apps;
        private readonly bool processListKnown;

        public CardView(HostReading host, Gpu gpu, bool live, int limit)
        {
            Host = host.Id;
            Gpu = gpu;
            processListKnown = host.Data.processStatus == "ok";
            Apps = host.Data.processes.Where(app => app.gpu == gpu.index)
                .OrderByDescending(app => app.memoryUsed ?? -1).ToList();
            State = Availability.Classify(gpu, live, processListKnown, Apps.Count, limit);
        }

        public bool Candidate { get { return State == CardState.Idle || State == CardState.LowMemory; } }
        public double? RemainingMiB { get { return Gpu.memoryTotal.HasValue && Gpu.memoryUsed.HasValue ? Math.Max(0, Gpu.memoryTotal.Value - Gpu.memoryUsed.Value) : (double?)null; } }
        public string Device { get { return Host + " · GPU " + Gpu.index; } }
        public int GpuIndex { get { return Gpu.index; } }
        public string Model { get { return (Gpu.name ?? "未知型号").Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", ""); } }
        public string SlotHint { get { return Device + " · " + StateLabel + " · 剩余 " + FreeMemory; } }
        public string FreeMemory { get { return Format.GiB(RemainingMiB); } }
        public string UsedMemory { get { return Format.ValueGiB(Gpu.memoryUsed) + " / " + Format.GiB(Gpu.memoryTotal) + " 已用"; } }
        public double MemoryPercent { get { return Gpu.memoryTotal > 0 ? Math.Min(100, 100 * (Gpu.memoryUsed ?? 0) / Gpu.memoryTotal.Value) : 0; } }
        public string Utilization { get { return Gpu.utilization.HasValue ? Math.Round(Gpu.utilization.Value) + "%" : "—"; } }
        public string Temperature { get { return Gpu.temperature.HasValue ? Math.Round(Gpu.temperature.Value) + "°C" + (Gpu.power.HasValue ? "  ·  " + Math.Round(Gpu.power.Value) + " W" : "") : "—"; } }
        public string Project { get { return Apps.Count > 0 ? (Apps[0].project ?? "未知项目") : "—"; } }
        public string ProjectTooltip { get { return Apps.Count > 0 ? string.Join("、", Apps.Select(app => app.project ?? "未知项目").Distinct()) : null; } }
        public string ProcessesLabel { get { return Apps.Count + " 个进程"; } }
        public Visibility ProcessesVisibility { get { return Apps.Count > 0 && State != CardState.Unavailable ? Visibility.Visible : Visibility.Collapsed; } }
        public string NoProcessesLabel { get { return Apps.Count > 0 ? (State == CardState.Unavailable ? "上次读数" : "") : processListKnown ? "无计算进程" : "进程信息未知"; } }
        public string StateLabel
        {
            get
            {
                switch (State)
                {
                    case CardState.Idle: return "空闲";
                    case CardState.LowMemory: return "低显存";
                    case CardState.Active: return "计算活跃";
                    case CardState.Occupied: return "显存已占用";
                    default: return "读数不可用";
                }
            }
        }
        public Brush StateColor { get { return State == CardState.Idle ? Palette.Mint : State == CardState.LowMemory ? Palette.Amber : State == CardState.Active ? Palette.Blue : Palette.Dim; } }
        public Brush FreeColor { get { return Candidate ? StateColor : Palette.Ink; } }
        public Brush BadgeBackground { get { return State == CardState.Idle ? Palette.GreenSurface : State == CardState.LowMemory ? Palette.GoldSurface : State == CardState.Active ? Palette.BlueSurface : Palette.Slate; } }
        public Brush Background { get { return State == CardState.Idle ? Palette.GreenSurface : Palette.Normal; } }
        public Brush Border { get { return State == CardState.Idle ? Palette.GreenLine : State == CardState.LowMemory ? Palette.GoldLine : Palette.Line; } }
    }

    internal static class Format
    {
        public static string ValueGiB(double? mib) { return mib.HasValue ? (mib.Value / 1024).ToString("0.0") : "—"; }
        public static string GiB(double? mib) { return mib.HasValue ? ValueGiB(mib) + " GiB" : "—"; }
        public static string Capacity(long bytes)
        {
            double gib = bytes / 1073741824.0;
            if (gib >= 1024) return (gib / 1024).ToString("0.00") + " TiB";
            return gib >= 1 ? gib.ToString("0.0") + " GiB" : (bytes / 1048576.0).ToString("0.#") + " MiB";
        }

        public static string Runtime(double? seconds)
        {
            if (!seconds.HasValue) return "—";
            var duration = TimeSpan.FromSeconds(seconds.Value);
            if (duration.Days > 0) return duration.Days + " 天 " + duration.Hours + " 小时";
            if (duration.Hours > 0) return duration.Hours + " 小时 " + duration.Minutes + " 分";
            return duration.Minutes > 0 ? duration.Minutes + " 分钟" : duration.Seconds + " 秒";
        }

        public static string ProjectHint(GpuProcess app)
        {
            switch (app.projectSource)
            {
                case "cwd": return "按工作目录推断";
                case "script": return "按启动脚本推断";
                case "config": return "按启动配置路径推断";
                case "executable": return "按程序路径推断";
                default: return "当前没有可用的项目线索";
            }
        }
    }

    internal sealed class Dashboard
    {
        private readonly List<string> hostIds;
        private readonly Dictionary<string, ServerDefinition> servers;
        internal static readonly string ConfigDirectory = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "servers.local.json"))
            ? AppDomain.CurrentDomain.BaseDirectory : Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "GpuWatch");
        private static readonly string RuntimeDirectory = Path.Combine(ConfigDirectory, ".runtime");
        private static readonly string SettingsFile = Path.Combine(RuntimeDirectory, "desktop-preferences.txt");
        private readonly Window window;
        private readonly string collector;
        private readonly Dictionary<string, HostReading> readings = new Dictionary<string, HostReading>();
        private readonly DispatcherTimer renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private readonly DispatcherTimer noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly IdleNotice idleNotice;
        private TrayController tray;
        private bool exiting;
        private WindowState lastVisibleState = WindowState.Normal;
        private readonly HashSet<string> announcedIdle = new HashSet<string>();
        private readonly HashSet<string> displayedIdle = new HashSet<string>();
        private bool idleAlertsEnabled = true;
        private CancellationTokenSource session;
        private DateTime frozenAt;
        private bool paused;
        private bool dirty = true;
        private int lowLimit = 8;
        private string view = "candidates";
        private string filter;
        private int? deviceFilter;
        private string liveSignature;

        public Dashboard(Window window, string collector, List<ServerDefinition> hosts)
        {
            this.window = window;
            this.collector = collector;
            servers = hosts.ToDictionary(h => h.name);
            hostIds = hosts.Select(h => h.name).ToList();
            UpdateTileColumns();
            window.Icon = AppIcon.Image;
            Find<Image>("AppMark").Source = AppIcon.Image;
            idleNotice = new IdleNotice(window, OpenCandidates);
            window.SourceInitialized += (sender, args) => ApplyDarkTitleBar(new WindowInteropHelper(window).Handle);
            foreach (var id in hostIds) readings[id] = new HostReading { Id = id, State = ConnectionState.Connecting };
            RestorePreferences();
            var limitOptions = Find<ComboBox>("MemoryLimit");
            limitOptions.SelectedItem = limitOptions.Items.Cast<ComboBoxItem>().First(option => (string)option.Tag == lowLimit.ToString());
            limitOptions.SelectionChanged += (sender, args) =>
            {
                var item = limitOptions.SelectedItem as ComboBoxItem;
                if (item != null) { lowLimit = int.Parse((string)item.Tag); Render(); }
            };
            Find<Button>("Pause").Click += (sender, args) => TogglePause();
            Find<Button>("AddServer").Click += (sender, args) => AddServer();
            Find<Button>("IdleAlertsToggle").Click += (sender, args) => ToggleIdleAlerts();
            Find<Button>("Pin").Click += (sender, args) => { window.Topmost = !window.Topmost; RenderControls(); };
            Find<Button>("Candidates").Click += (sender, args) => SwitchView("candidates");
            Find<Button>("AllCards").Click += (sender, args) => SwitchView("all");
            Find<Button>("Processes").Click += (sender, args) => SwitchView("processes");
            Find<Button>("Disks").Click += (sender, args) => SwitchView("disks");
            Find<Button>("ClearServer").Click += (sender, args) => { filter = null; deviceFilter = null; Render(); };
            Find<Button>("ClearDevice").Click += (sender, args) => { deviceFilter = null; Render(); };
            Find<TextBox>("Search").TextChanged += (sender, args) => Render();
            Find<Grid>("LayoutRoot").SizeChanged += (sender, args) =>
            {
                bool compact = args.NewSize.Height < 550;
                Find<TextBlock>("RecommendationDetail").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                Find<TextBlock>("RecommendationDevice").FontSize = compact ? 22 : 27;
                Find<Border>("Recommendation").Padding = compact ? new Thickness(13, 6, 13, 6) : new Thickness(16, 11, 16, 11);
            };
            window.AddHandler(Button.ClickEvent, new RoutedEventHandler(HandleRowClick));
            noticeTimer.Tick += (sender, args) => CheckIdleNotices();
            window.Loaded += OnFirstLoaded;
            window.StateChanged += (sender, args) =>
            {
                if (window.WindowState != WindowState.Minimized) lastVisibleState = window.WindowState;
            };
            window.Closed += (sender, args) =>
            {
                exiting = true;
                noticeTimer.Stop();
                renderTimer.Stop();
                idleNotice.Dismiss();
                StopMonitoring();
                if (tray != null) { tray.Dispose(); tray = null; }
                SavePreferences();
            };
            renderTimer.Tick += (sender, args) =>
            {
                if (!window.IsVisible || window.WindowState == WindowState.Minimized ||
                    System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;
                var current = string.Concat(hostIds.Select(id => IsLive(readings[id]) ? '1' : '0'));
                if (dirty || current != liveSignature) Render();
            };
        }

        private T Find<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }

        private void OnFirstLoaded(object sender, RoutedEventArgs args)
        {
            window.Loaded -= OnFirstLoaded;
            StartMonitoring();
            Render();
            renderTimer.Start();
            noticeTimer.Start();
            if (hostIds.Count == 0) window.Dispatcher.BeginInvoke(new Action(AddServer), DispatcherPriority.ContextIdle);
        }

        private void UpdateTileColumns()
        {
            Find<ItemsControl>("ServerTiles").Tag = Math.Max(1, Math.Min(3, hostIds.Count));
        }

        private void AddServer()
        {
            if (exiting || !window.IsVisible) return;
            var host = AddServerWindow.Prompt(window, ConfigDirectory);
            if (host == null) return;
            hostIds.Add(host.name);
            servers.Add(host.name, host);
            readings.Add(host.name, new HostReading { Id = host.name, State = ConnectionState.Connecting });
            UpdateTileColumns();
            if (session != null) StartHost(host.name, session.Token);
            Render();
        }

        public void EnableTray()
        {
            if (tray != null) return;
            tray = new TrayController(ShowDashboard, ToggleIdleAlerts,
                () => window.Dispatcher.BeginInvoke(new Action(ExitApplication)));
            tray.SetAlertsEnabled(idleAlertsEnabled);
            window.Closing += (sender, args) =>
            {
                if (exiting) return;
                args.Cancel = true;
                window.Hide();
                SavePreferences();
            };
            Application.Current.SessionEnding += (sender, args) => exiting = true;
        }

        public void ShowDashboard()
        {
            if (exiting || window.Dispatcher.HasShutdownStarted) return;
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = lastVisibleState;
            window.Activate();
            Render();
        }

        public void ExitApplication()
        {
            if (exiting) return;
            exiting = true;
            window.Close();
        }

        private void ToggleIdleAlerts()
        {
            idleAlertsEnabled = !idleAlertsEnabled;
            if (!idleAlertsEnabled) announcedIdle.Clear();
            if (tray != null) tray.SetAlertsEnabled(idleAlertsEnabled);
            CheckIdleNotices();
            RenderControls();
            SavePreferences();
        }

        private void HandleRowClick(object sender, RoutedEventArgs args)
        {
            var button = args.OriginalSource as Button;
            if (button == null) return;
            if (button.Tag as string == "server")
            {
                var id = button.CommandParameter as string;
                filter = id == filter ? null : id;
                deviceFilter = null;
                Render();
                args.Handled = true;
            }
            else if (button.Tag as string == "processes")
            {
                var row = button.CommandParameter as CardView;
                if (row == null) return;
                filter = row.Host;
                deviceFilter = row.Gpu.index;
                view = "processes";
                Find<TextBox>("Search").Clear();
                Render();
                args.Handled = true;
            }
        }

        private void SwitchView(string next)
        {
            view = next;
            deviceFilter = null;
            Render();
        }

        private void TogglePause()
        {
            paused = !paused;
            if (paused) { frozenAt = DateTime.Now; StopMonitoring(); }
            else StartMonitoring();
            CheckIdleNotices();
            Render();
        }

        private void OpenCandidates()
        {
            filter = null;
            view = "candidates";
            deviceFilter = null;
            ShowDashboard();
        }

        private void CheckIdleNotices()
        {
            if (paused || !idleAlertsEnabled)
            {
                displayedIdle.Clear();
                idleNotice.Dismiss();
                return;
            }

            if (!idleNotice.IsVisible) displayedIdle.Clear();
            var current = new List<CardView>();
            foreach (var host in readings.Values.Where(h => IsLive(h) && h.Data != null && h.Data.error == null))
            {
                foreach (var gpu in host.Data.gpus)
                {
                    var card = new CardView(host, gpu, true, lowLimit);
                    if (card.State == CardState.Idle) current.Add(card);
                    // Missing process data or a reconnect does not start a new idle period.
                    else if (gpu.utilization > Availability.MaxUtilization || gpu.memoryUsed > Availability.IdleMiB || card.Apps.Count > 0)
                        announcedIdle.Remove(card.Device);
                }
            }
            var rows = current.Where(card => displayedIdle.Contains(card.Device) || !announcedIdle.Contains(card.Device))
                .OrderByDescending(card => card.RemainingMiB).ThenBy(card => card.Host).ThenBy(card => card.Gpu.index).ToList();
            if (rows.Count == 0)
            {
                if (displayedIdle.Count > 0) idleNotice.Dismiss();
                displayedIdle.Clear();
                return;
            }

            var keys = rows.Select(card => card.Device).ToArray();
            if (displayedIdle.SetEquals(keys)) return;
            if (rows.Any(card => !announcedIdle.Contains(card.Device))) idleNotice.Show(rows);
            else idleNotice.Update(rows);
            displayedIdle.Clear();
            displayedIdle.UnionWith(keys);
            announcedIdle.UnionWith(keys);
        }

        private void StartMonitoring()
        {
            session = new CancellationTokenSource();
            foreach (var id in hostIds) StartHost(id, session.Token);
        }

        private void StartHost(string id, CancellationToken token)
        {
            readings[id].State = ConnectionState.Connecting;
            var monitor = new SshMonitor(servers[id], collector);
            Task.Run(() => monitor.RunAsync(reading =>
            {
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (reading.Data == null)
                    {
                        reading.Data = readings[reading.Id].Data;
                        reading.SampledAt = readings[reading.Id].SampledAt;
                    }
                    readings[reading.Id] = reading;
                    dirty = true;
                }));
            }, token));
        }

        private void StopMonitoring()
        {
            if (session == null) return;
            session.Cancel();
            session = null;
        }

        private bool IsLive(HostReading host)
        {
            return host.State == ConnectionState.Online && host.SampledAt.HasValue &&
                ((paused ? frozenAt : DateTime.Now) - host.SampledAt.Value).TotalSeconds <= 7;
        }

        private void RenderControls()
        {
            var reminder = Find<Button>("IdleAlertsToggle");
            reminder.Content = idleAlertsEnabled ? "空闲提醒 · 开" : "空闲提醒 · 关";
            reminder.Foreground = idleAlertsEnabled ? Palette.Mint : Palette.Dim;
            reminder.BorderBrush = idleAlertsEnabled ? Palette.GreenLine : Palette.Line;
            var pin = Find<Button>("Pin");
            pin.Content = window.Topmost ? "已置顶" : "置顶";
            pin.BorderBrush = window.Topmost ? Palette.BlueLine : Palette.Line;
            var pause = Find<Button>("Pause");
            pause.Content = paused ? "继续" : "暂停";
            pause.ToolTip = paused ? "恢复实时刷新" : "暂停刷新";
        }

        private void Render()
        {
            dirty = false;
            liveSignature = string.Concat(hostIds.Select(id => IsLive(readings[id]) ? '1' : '0'));
            var liveHosts = hostIds.Where(id => IsLive(readings[id])).ToList();
            var allRows = hostIds.Where(id => readings[id].Data != null)
                .SelectMany(id => readings[id].Data.gpus.Select(gpu => new CardView(readings[id], gpu, liveHosts.Contains(id), lowLimit))).ToList();
            var candidates = allRows.Where(row => row.Candidate).OrderBy(row => row.State).ThenByDescending(row => row.RemainingMiB).ThenBy(row => row.Host).ThenBy(row => row.Gpu.index).ToList();
            var idle = candidates.Count(row => row.State == CardState.Idle);
            var low = candidates.Count - idle;
            var recent = readings.Values.Max(host => host.SampledAt);
            var online = liveHosts.Count;
            Find<TextBlock>("Summary").Text = online + " / " + hostIds.Count + " 台在线  ·  " + allRows.Count(row => liveHosts.Contains(row.Host)) +
                " 张 GPU  ·  " + idle + " 张空闲  ·  " + low + " 张低显存";
            Find<TextBlock>("LastSample").Text = recent.HasValue ? (paused ? "暂停于 " : "更新 ") + recent.Value.ToString("HH:mm:ss") : "等待首次采样";
            var badge = Find<Border>("LiveBadge");
            var liveText = Find<TextBlock>("LiveText");
            var joining = readings.Values.Any(host => host.State == ConnectionState.Connecting && !host.SampledAt.HasValue);
            var healthy = hostIds.Count > 0 && online == hostIds.Count && liveHosts.All(id => readings[id].Data.error == null);
            liveText.Text = hostIds.Count == 0 ? "待添加" : paused ? "已暂停" : healthy ? "实时 · 2 秒" : online == hostIds.Count ? "部分读数异常" : online > 0 ? "部分在线" : joining ? "连接中" : "采样中断";
            liveText.Foreground = !paused && healthy ? Palette.Mint : Palette.Amber;
            Find<System.Windows.Shapes.Ellipse>("LiveDot").Fill = liveText.Foreground;
            badge.Background = !paused && healthy ? Palette.GreenSurface : Palette.GoldSurface;
            badge.BorderBrush = !paused && healthy ? Palette.GreenLine : Palette.GoldLine;
            RenderControls();
            RenderRecommendation(candidates, allRows, online);
            RenderHostTiles(allRows, candidates);
            RenderList(allRows, candidates);
            if (hostIds.Count == 0)
            {
                Find<TextBlock>("RecommendationDevice").Text = "添加第一台服务器";
                Find<TextBlock>("RecommendationDetail").Text = "点击「添加服务器」填写 SSH 连接信息";
                ShowEmpty(true, "还没有服务器", "添加后即可查看空闲显卡、进程和磁盘余量");
                Find<Grid>("DiskPane").Visibility = Visibility.Collapsed;
            }
        }

        private void RenderRecommendation(List<CardView> candidates, List<CardView> allRows, int online)
        {
            var best = candidates.FirstOrDefault();
            var isIdle = best != null && best.State == CardState.Idle;
            var accent = best == null ? Palette.Dim : best.StateColor;
            var band = Find<Border>("Recommendation");
            band.Background = (Brush)window.FindResource(best == null ? "WatchSurface" : isIdle ? "AvailableSurface" : "CandidateSurface");
            band.BorderBrush = best == null ? Palette.Line : isIdle ? Palette.GreenLine : Palette.GoldLine;
            Find<Border>("RecommendationLine").Background = accent;
            var label = Find<TextBlock>("RecommendationLabel");
            label.Text = best == null ? "显卡可用情况" : isIdle ? "首选空闲卡" : "低显存候选";
            label.Foreground = accent;
            var reference = best ?? allRows.Where(row => row.State != CardState.Unavailable)
                .OrderByDescending(row => row.RemainingMiB).FirstOrDefault();
            Find<TextBlock>("RecommendationDevice").Text = best != null ? best.Device :
                reference != null ? "暂无空闲或低显存卡" : online == 0 ? "等待读数" : "显卡读数不可用";
            Find<TextBlock>("RecommendationDetail").Text = reference == null ? "等待有效的显存和利用率读数"
                : (best == null ? "余量最多的卡已用 " + Format.GiB(reference.Gpu.memoryUsed) : best.Model)
                    + "   ·   利用率 " + reference.Utilization + "   ·   " +
                    (reference.Apps.Count > 0 ? reference.ProcessesLabel : reference.NoProcessesLabel);
            Find<TextBlock>("RecommendationMemory").Text = reference == null ? "—" : reference.FreeMemory;
            Find<TextBlock>("RecommendationMemory").Foreground = accent;
            Find<TextBlock>("RecommendationMemoryLabel").Text = best == null && reference != null
                ? "余量最多 · " + reference.Device : "剩余显存";
        }

        private DiskVolume MonitoredDisk(HostReading host)
        {
            if (!IsLive(host) || host.Data == null || host.Data.disks == null || (host.Data.diskStatus != "ok" && host.Data.diskStatus != "partial"))
                return null;
            var mount = servers[host.Id].mount;
            return host.Data.disks.FirstOrDefault(d => d != null && d.mount == mount && d.totalBytes > 0 && d.availableBytes >= 0);
        }

        private static string DiskIssue(Sample sample)
        {
            if (sample == null || sample.diskStatus == "pending") return "等待磁盘读数";
            if (sample.diskStatus == "stale") return "磁盘读数过期";
            return "磁盘读数不可用";
        }

        private static string DiskTime(Sample sample)
        {
            if (sample == null || !sample.diskSampledAt.HasValue) return "—";
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(sample.diskSampledAt.Value).ToLocalTime().ToString("HH:mm:ss");
        }

        private void RenderHostTiles(List<CardView> allRows, List<CardView> candidates)
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement as Button;
            var focusedId = focused != null && focused.Tag as string == "server" ? focused.CommandParameter as string : null;
            var tiles = Find<ItemsControl>("ServerTiles");
            tiles.ItemsSource = hostIds.Select(id =>
            {
                var host = readings[id];
                var live = IsLive(host);
                var gpuReady = live && host.Data.error == null;
                var available = candidates.Where(row => row.Host == id).ToList();
                var idle = available.Count(row => row.State == CardState.Idle);
                var low = available.Count - idle;
                var disk = MonitoredDisk(host);
                var connection = live ? host.Issue ?? "在线" : host.State == ConnectionState.Offline ? "离线" : host.State == ConnectionState.Online ? "读数过期" : "连接中";
                var slots = allRows.Where(row => row.Host == id).OrderBy(row => row.Gpu.index).ToList();
                return new
                {
                    Id = id, Title = id, Idle = gpuReady ? idle.ToString() : "—", Low = gpuReady ? low.ToString() : "—",
                    IdleColor = idle > 0 ? Palette.Mint : Palette.Dim, LowColor = low > 0 ? Palette.Amber : Palette.Dim,
                    Slots = slots, DiskPath = servers[id].mount,
                    DiskFree = disk == null ? "暂无读数" : Format.Capacity(disk.availableBytes) + " 可用",
                    DiskColor = disk == null ? Palette.Dim : disk.availableBytes < disk.totalBytes * 0.1 ? Palette.Amber : Palette.Ink,
                    DiskHint = disk == null ? (live ? DiskIssue(host.Data) : connection) : "磁盘采样 " + DiskTime(host.Data),
                    ConnectionLabel = paused && live ? "已暂停" : connection, ConnectionColor = gpuReady ? Palette.Mint : host.State == ConnectionState.Offline ? Palette.Red : Palette.Amber,
                    Border = filter == id ? Palette.BlueLine : idle > 0 ? Palette.GreenLine : low > 0 ? Palette.GoldLine : Palette.Line,
                    Background = filter == id ? Palette.BlueSurface : idle > 0 ? Palette.GreenSurface : Palette.Normal,
                    Tooltip = "筛选服务器 " + id + (host.Issue == null ? "" : " · " + host.Issue) + (host.SampledAt.HasValue ? " · 读数 " + host.SampledAt.Value.ToString("HH:mm:ss") : ""),
                    AccessibleName = "查看服务器 " + id + "，" + (live ? idle + " 张空闲，" + low + " 张低显存" : connection),
                };
            }).ToList();
            if (focusedId != null)
            {
                tiles.UpdateLayout();
                var item = tiles.ItemContainerGenerator.ContainerFromIndex(hostIds.IndexOf(focusedId)) as DependencyObject;
                if (item != null) FocusFirstButton(item);
            }
        }

        private static bool FocusFirstButton(DependencyObject root)
        {
            var button = root as Button;
            if (button != null) return button.Focus();
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                if (FocusFirstButton(VisualTreeHelper.GetChild(root, index))) return true;
            return false;
        }

        private void RenderList(List<CardView> allRows, List<CardView> candidates)
        {
            var showProcesses = view == "processes";
            var showDisks = view == "disks";
            Find<Button>("Candidates").Tag = view == "candidates" ? "True" : "False";
            Find<Button>("AllCards").Tag = view == "all" ? "True" : "False";
            Find<Button>("Processes").Tag = showProcesses ? "True" : "False";
            Find<Button>("Disks").Tag = showDisks ? "True" : "False";
            Find<Grid>("DiskPane").Visibility = showDisks ? Visibility.Visible : Visibility.Collapsed;
            Find<Grid>("CardsPane").Visibility = showProcesses || showDisks ? Visibility.Collapsed : Visibility.Visible;
            Find<Grid>("ProcessPane").Visibility = showProcesses ? Visibility.Visible : Visibility.Collapsed;
            Find<StackPanel>("LimitSettings").Visibility = showProcesses || showDisks ? Visibility.Collapsed : Visibility.Visible;
            Find<Grid>("SearchBox").Visibility = showProcesses ? Visibility.Visible : Visibility.Collapsed;
            var search = Find<TextBox>("Search");
            Find<TextBlock>("SearchHint").Visibility = string.IsNullOrEmpty(search.Text) ? Visibility.Visible : Visibility.Collapsed;
            var chip = Find<Button>("ClearServer");
            chip.Visibility = filter == null ? Visibility.Collapsed : Visibility.Visible;
            chip.Content = filter + "  ×";
            chip.ToolTip = "清除服务器筛选";
            var device = Find<Button>("ClearDevice");
            device.Visibility = showProcesses && deviceFilter.HasValue ? Visibility.Visible : Visibility.Collapsed;
            device.Content = "GPU " + deviceFilter + "  ×";
            device.ToolTip = "清除显卡筛选";
            var rules = Find<TextBlock>("Rules");
            if (showDisks)
            {
                var diskRows = new List<object>();
                foreach (var id in hostIds.Where(id => filter == null || filter == id))
                {
                    var host = readings[id];
                    var live = IsLive(host);
                    var d = MonitoredDisk(host);
                    if (d != null)
                    {
                        var percent = Math.Min(100, 100.0 * d.availableBytes / d.totalBytes);
                        diskRows.Add(new { Host = id, Mount = d.mount, SampleTime = DiskTime(host.Data), Free = Format.Capacity(d.availableBytes),
                            Total = Format.Capacity(d.totalBytes), Percent = percent, Ratio = percent > 0 && percent < 0.1 ? "<0.1%" : percent.ToString("0.#") + "%",
                            Color = percent < 10 ? Palette.Amber : Palette.Mint });
                    }
                    else
                    {
                        var reason = !live ? (host.Issue ?? (host.State == ConnectionState.Connecting ? "等待连接" : "读数不可用"))
                            : DiskIssue(host.Data);
                        diskRows.Add(new { Host = id, Mount = servers[id].mount, SampleTime = "—", Free = reason, Total = "—", Percent = 0.0, Ratio = "—", Color = Palette.Dim });
                    }
                }
                Find<ItemsControl>("DiskRows").ItemsSource = diskRows;
                rules.Text = "本地磁盘约每 30 秒更新，余量按当前账号可用空间计算。";
                ShowEmpty(false, "", "");
            }
            else if (showProcesses)
            {
                var query = search.Text.Trim();
                var rows = readings.Values.Where(host => IsLive(host) && host.Data != null && (filter == null || filter == host.Id))
                    .SelectMany(host => host.Data.processes.Select(app => new { Host = host.Id, App = app }))
                    .Where(item => !deviceFilter.HasValue || deviceFilter.Value == item.App.gpu)
                    .Where(item => new[] { item.App.project ?? "未知项目", item.App.pid.ToString(), item.App.user ?? "", item.App.program ?? "" }
                        .Any(value => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderByDescending(item => item.App.memoryUsed).Select(item => new
                    {
                        Device = item.Host + " · GPU " + item.App.gpu, Project = item.App.project ?? "未知项目", Program = item.App.program ?? "—",
                        Pid = item.App.pid, User = item.App.user ?? "—", Memory = Format.GiB(item.App.memoryUsed),
                        Runtime = Format.Runtime(item.App.runningSeconds), Tooltip = Format.ProjectHint(item.App),
                    }).ToList();
                Find<ItemsControl>("ProcessRows").ItemsSource = rows;
                rules.Text = "项目名根据进程目录或启动路径推断。跨卡进程按显卡分别列出。";
                ShowEmpty(rows.Count == 0, query.Length > 0 ? "找不到匹配的进程" : "当前没有可显示的计算进程", "可切换服务器或清除筛选");
            }
            else
            {
                var rows = (view == "all" ? allRows : candidates).Where(row => filter == null || row.Host == filter).ToList();
                var fallback = false;
                if (view == "candidates" && rows.Count == 0)
                {
                    rows = allRows.Where(row => (row.State == CardState.Occupied || row.State == CardState.Active) && (filter == null || row.Host == filter))
                        .OrderByDescending(row => row.RemainingMiB).Take(3).ToList();
                    fallback = rows.Count > 0;
                }
                Find<TextBlock>("BusyCaption").Visibility = fallback ? Visibility.Visible : Visibility.Collapsed;
                Find<ItemsControl>("CardRows").ItemsSource = rows;
                var hasLive = readings.Values.Any(host => (filter == null || filter == host.Id) && IsLive(host));
                rules.Text = "两档均要求利用率 ≤5%。空闲卡另需已用 ≤1 GiB 且无计算进程。";
                ShowEmpty(rows.Count == 0, hasLive ? "当前没有空闲或低显存卡" : "等待实时显卡读数",
                    hasLive ? "切换到「全部显卡」查看剩余显存" : "每台服务器独立重连，离线读数不会列为候选");
            }
        }

        private void ShowEmpty(bool visible, string caption, string detail)
        {
            Find<StackPanel>("Empty").Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                Find<Grid>("CardsPane").Visibility = Visibility.Collapsed;
                Find<Grid>("ProcessPane").Visibility = Visibility.Collapsed;
            }
            Find<TextBlock>("EmptyTitle").Text = caption;
            Find<TextBlock>("EmptyDetail").Text = detail;
        }

        private void RestorePreferences()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                var values = File.ReadAllLines(SettingsFile);
                int value;
                if (values.Length > 0 && int.TryParse(values[0], out value) && new[] { 2, 4, 8, 12, 16, 24 }.Contains(value)) lowLimit = value;
                window.Topmost = values.Length > 1 && values[1] == "True";
                bool enabled;
                if (values.Length > 2 && bool.TryParse(values[2], out enabled)) idleAlertsEnabled = enabled;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void SavePreferences()
        {
            try
            {
                Directory.CreateDirectory(RuntimeDirectory);
                File.WriteAllLines(SettingsFile, new[] { lowLimit.ToString(), window.Topmost.ToString(), idleAlertsEnabled.ToString() });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int size);

        private static void ApplyDarkTitleBar(IntPtr handle)
        {
            var enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

        [STAThread]
        public static int Main(string[] args)
        {
            if (AskPass.IsRequested) return AskPass.Reply(args);
            bool first;
            using (var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\GpuIdleWatch.Public.Show"))
            using (var instance = new Mutex(true, "Local\\GpuIdleWatch.Public", out first))
            {
                if (!first)
                {
                    using (var self = Process.GetCurrentProcess())
                        foreach (var peer in Process.GetProcessesByName(self.ProcessName))
                            using (peer)
                                if (peer.Id != self.Id && peer.SessionId == self.SessionId) AllowSetForegroundWindow(peer.Id);
                    showRequest.Set();
                    return 0;
                }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var assembly = Assembly.GetExecutingAssembly();
                    Window window;
                    string collector;
                    using (var resource = assembly.GetManifestResourceStream("Dashboard.xaml")) window = (Window)XamlReader.Load(resource);
                    using (var resource = new StreamReader(assembly.GetManifestResourceStream("collect.py"))) collector = resource.ReadToEnd();
                    var dashboard = new Dashboard(window, collector, ServerConfig.Load(Dashboard.ConfigDirectory));
                    app.MainWindow = window;
                    dashboard.EnableTray();
                    var activation = ThreadPool.RegisterWaitForSingleObject(showRequest, (state, timedOut) =>
                    {
                        if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new Action(dashboard.ShowDashboard));
                    }, null, Timeout.Infinite, false);
                    try { return app.Run(window); }
                    finally { activation.Unregister(null); }
                }
                catch (Exception error)
                {
                    MessageBox.Show(error.GetBaseException().Message, "GPU 空闲卡监控启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally { instance.ReleaseMutex(); }
            }
        }

    }
}
