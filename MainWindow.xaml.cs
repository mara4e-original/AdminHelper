using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AdminHelper;

public partial class MainWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private ulong _prevIdle, _prevTotal;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _tick;
    private bool _ready;
    private readonly bool _isAdmin;
    private UIElement[] _panels = Array.Empty<UIElement>();

    private List<ServiceItem> _allServices = new();
    private List<ProcItem> _allProcs = new();
    private List<ConnItem> _allConns = new();
    private List<AutoItem> _allAuto = new();

    // консоль
    private Process? _cmdProc;
    private bool _cmdStoppedByUser;
    private readonly List<string> _history = new();
    private int _histIdx;
    private string _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly Regex CdRegex = new(@"^cd(\s+/d)?\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // быстрая проверка
    private bool _scanning;

    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "system idle process", "registry", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "memory compression", "secure system", "system interrupts"
    };

    public MainWindow()
    {
        InitializeComponent();

        _isAdmin = IsAdministrator();
        _panels = new UIElement[]
        {
            DashboardPanel, ServicesPanel, ProcessesPanel, NetworkPanel, ConsolePanel, StartupPanel, OptimizePanel, SecurityPanel, UpdatePanel
        };
        _ready = true;

        SourceInitialized += (_, _) => Native.StyleTitleBar(this);

        string rights = _isAdmin ? "Администратор" : "Обычные права";
        AdminBadgeText.Text = rights;
        AdminBadgeText.Foreground = (System.Windows.Media.Brush)FindResource(_isAdmin ? "GoodBrush" : "WarnBrush");
        AdminText.Text = rights;

        GreetingText.Text = $"Привет, {Environment.UserName}!";
        CpuDetail.Text = $"Логических ядер: {Environment.ProcessorCount}";
        UpdateConsolePrompt();
        ConsoleOutput.Text = "Admin Helper · консоль готова.\n\n";

        GetCpuUsage();
        LoadDisks();
        ShowPanel(DashboardPanel, animate: false);
        UpdateDashboard();

        InitUpdates();

        Loaded += async (_, _) => await PlayIntroAsync();
        Loaded += async (_, _) => await AutoCheckUpdateAsync();

        _timer.Tick += (_, _) => UpdateDashboard();
        _timer.Start();
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ---------- Дашборд ----------

    private static ulong ToUlong(FILETIME ft) => ((ulong)ft.High << 32) | ft.Low;

    private double GetCpuUsage()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        ulong i = ToUlong(idle);
        ulong t = ToUlong(kernel) + ToUlong(user);
        ulong dIdle = i - _prevIdle;
        ulong dTotal = t - _prevTotal;
        _prevIdle = i;
        _prevTotal = t;
        return dTotal == 0 ? 0 : (1.0 - (double)dIdle / dTotal) * 100;
    }

    private void UpdateDashboard()
    {
        double cpu = GetCpuUsage();
        CpuText.Text = $"{cpu:0}%";
        CpuBar.Value = cpu;

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        double totalGb = mem.ullTotalPhys / 1073741824.0;
        double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
        RamText.Text = $"{mem.dwMemoryLoad}%";
        RamBar.Value = mem.dwMemoryLoad;
        RamDetail.Text = $"Занято {usedGb:0.0} из {totalGb:0.0} ГБ";

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        InfoText.Text = $"Компьютер: {Environment.MachineName}    ·    " +
                        $"Работает: {(int)up.TotalDays} д {up.Hours} ч {up.Minutes} мин";

        // тяжёлые счётчики обновляем раз в 3 секунды и только когда дашборд виден
        if (DashboardPanel.Visibility == Visibility.Visible && _tick++ % 3 == 0)
        {
            var ps = Process.GetProcesses();
            ProcCountText.Text = ps.Length.ToString();
            foreach (var p in ps) p.Dispose();

            ConnCountText.Text = Native.GetTcpTable().Count(r => r.State == 5).ToString();
        }
    }

    private void LoadDisks()
    {
        var list = new List<DiskItem>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            double total = d.TotalSize / 1073741824.0;
            double free = d.AvailableFreeSpace / 1073741824.0;
            double used = total - free;
            list.Add(new DiskItem
            {
                Name = d.Name,
                Info = $"занято {used:0} из {total:0} ГБ, свободно {free:0} ГБ",
                Percent = used / total * 100
            });
        }
        DisksList.ItemsSource = list;
    }

    // ---------- Навигация ----------

    private void ShowPanel(UIElement panel, bool animate = true)
    {
        foreach (var p in _panels) p.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;

        if (animate)
        {
            panel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    // ---------- Заставка при запуске ----------

    private bool _introSkipped;

    private void Intro_MouseDown(object sender, MouseButtonEventArgs e) => _introSkipped = true;

    private async Task PlayIntroAsync()
    {
        string hello = $"Привет, {Environment.UserName}!";
        string pc = $"Компьютер: {Environment.MachineName}";
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        try
        {
            IntroCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = ease });
            IntroShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(700)) { EasingFunction = ease });
            await Task.Delay(450);

            // «печатаем» приветствие
            for (int i = 1; i <= hello.Length && !_introSkipped; i++)
            {
                IntroHello.Text = hello[..i] + "▌";
                await Task.Delay(45);
            }
            IntroHello.Text = hello;

            // затем имя компьютера
            for (int i = 1; i <= pc.Length && !_introSkipped; i++)
            {
                IntroPc.Text = pc[..i];
                await Task.Delay(22);
            }
            IntroPc.Text = pc;

            if (!_introSkipped) await Task.Delay(650);
        }
        catch
        {
            // заставка не должна мешать запуску
        }

        // плавно убираем заставку и показываем программу
        var dur = TimeSpan.FromMilliseconds(650);
        var fadeOut = new DoubleAnimation(1, 0, dur);
        fadeOut.Completed += (_, _) => IntroLayer.Visibility = Visibility.Collapsed;
        IntroLayer.BeginAnimation(OpacityProperty, fadeOut);
        MainUi.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
        MainShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, dur) { EasingFunction = ease });
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton rb) return;

        switch (rb.Tag as string)
        {
            case "Dashboard": ShowPanel(DashboardPanel); _tick = 0; UpdateDashboard(); break;
            case "Services": ShowPanel(ServicesPanel); LoadServices(); break;
            case "Processes": ShowPanel(ProcessesPanel); LoadProcesses(); break;
            case "Network": ShowPanel(NetworkPanel); LoadNetwork(); break;
            case "Console": ShowPanel(ConsolePanel); ConsoleInput.Focus(); break;
            case "Startup": ShowPanel(StartupPanel); LoadStartup(); break;
            case "Optimize": ShowPanel(OptimizePanel); break;
            case "Security": ShowPanel(SecurityPanel); break;
            case "Update": ShowPanel(UpdatePanel); break;
        }
    }

    // ---------- Общие помощники ----------

    private static bool IsDenied(Exception ex) =>
        ex is UnauthorizedAccessException ||
        ex is System.Security.SecurityException ||
        ex is Win32Exception { NativeErrorCode: 5 } ||
        ex.InnerException is Win32Exception { NativeErrorCode: 5 } ||
        ex.InnerException is UnauthorizedAccessException ||
        (ex is AggregateException agg && agg.InnerExceptions.Any(IsDenied));

    private const string NeedAdminText = "Недостаточно прав. Закройте программу и запустите её от имени администратора.";

    private static string FormatBytes(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                MessageBox.Show("Файл не найден на диске.", "Открыть папку", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Завершает процесс (с дочерними) после подтверждения. Критичные системные процессы заблокированы.</summary>
    private async Task<bool> TerminateAsync(int pid, string displayName)
    {
        string bare = Path.GetFileNameWithoutExtension(displayName);
        if (pid <= 4 || pid == Environment.ProcessId || CriticalNames.Contains(bare))
        {
            MessageBox.Show(
                "Этот процесс нужен самой Windows (или это сама программа). Его завершение приведёт к сбою системы, поэтому оно заблокировано.",
                "Защита от ошибки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var answer = MessageBox.Show(
            $"Завершить «{displayName}» (PID {pid}) вместе с дочерними процессами?\nНесохранённые данные в нём будут потеряны.",
            "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;

        try
        {
            await Task.Run(() =>
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(true);
                p.WaitForExit(5000);
            });
            return true;
        }
        catch (ArgumentException)
        {
            return true; // процесса уже нет
        }
        catch (Exception ex)
        {
            MessageBox.Show(IsDenied(ex) ? NeedAdminText : ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    // ---------- Службы ----------

    private static string TranslateStatus(ServiceControllerStatus s) => s switch
    {
        ServiceControllerStatus.Running => "Работает",
        ServiceControllerStatus.Stopped => "Остановлена",
        ServiceControllerStatus.Paused => "Приостановлена",
        ServiceControllerStatus.StartPending => "Запускается",
        ServiceControllerStatus.StopPending => "Останавливается",
        ServiceControllerStatus.ContinuePending => "Возобновляется",
        ServiceControllerStatus.PausePending => "Приостанавливается",
        _ => "?"
    };

    private static string GetStartType(ServiceController s)
    {
        try
        {
            return s.StartType switch
            {
                ServiceStartMode.Automatic => "Автоматически",
                ServiceStartMode.Manual => "Вручную",
                ServiceStartMode.Disabled => "Отключена",
                ServiceStartMode.Boot => "Загрузка",
                ServiceStartMode.System => "Система",
                _ => "?"
            };
        }
        catch
        {
            return "?";
        }
    }

    private async void LoadServices()
    {
        ServicesStatus.Text = "Загрузка списка служб...";
        try
        {
            _allServices = await Task.Run(() =>
            {
                var result = new List<ServiceItem>();
                foreach (var s in ServiceController.GetServices())
                {
                    result.Add(new ServiceItem
                    {
                        Name = s.ServiceName,
                        DisplayName = s.DisplayName,
                        StatusText = TranslateStatus(s.Status),
                        IsRunning = s.Status == ServiceControllerStatus.Running,
                        StartTypeText = GetStartType(s)
                    });
                    s.Dispose();
                }
                return result.OrderBy(x => x.DisplayName).ToList();
            });
        }
        catch (Exception ex)
        {
            ServicesStatus.Text = "Не удалось получить список служб: " + ex.Message;
            return;
        }
        ApplyServiceFilter();
    }

    private void ApplyServiceFilter()
    {
        if (!_ready) return;

        string q = ServiceSearch.Text.Trim();
        var list = string.IsNullOrEmpty(q)
            ? _allServices
            : _allServices.Where(s =>
                s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        ServicesGrid.ItemsSource = list;
        int running = _allServices.Count(s => s.IsRunning);
        ServicesStatus.Text = $"Показано {list.Count} из {_allServices.Count}. Запущено: {running}";
    }

    private void ServiceSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyServiceFilter();
    private void RefreshServices_Click(object sender, RoutedEventArgs e) => LoadServices();
    private async void StartService_Click(object sender, RoutedEventArgs e) => await ChangeService("start");
    private async void StopService_Click(object sender, RoutedEventArgs e) => await ChangeService("stop");
    private async void RestartService_Click(object sender, RoutedEventArgs e) => await ChangeService("restart");

    private async Task ChangeService(string action)
    {
        if (ServicesGrid.SelectedItem is not ServiceItem item)
        {
            MessageBox.Show("Сначала выберите службу в списке.", "Службы",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (action != "start")
        {
            string verb = action == "stop" ? "остановить" : "перезапустить";
            var answer = MessageBox.Show($"Точно {verb} службу «{item.DisplayName}»?", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        ServicesStatus.Text = $"Выполняется: {item.DisplayName}...";
        try
        {
            await Task.Run(() =>
            {
                using var sc = new ServiceController(item.Name);
                var timeout = TimeSpan.FromSeconds(20);

                if (action == "start")
                {
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
                    }
                }
                else
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        if (!sc.CanStop)
                            throw new InvalidOperationException("Эту службу нельзя остановить.");
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    }
                    if (action == "restart")
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(IsDenied(ex) ? NeedAdminText : ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        LoadServices();
    }

    // ---------- Процессы ----------

    private static List<ProcItem> BuildProcList()
    {
        var res = new List<ProcItem>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    string? path = Native.GetProcessPath(p.Id);
                    string name = path != null ? Path.GetFileName(path) : p.ProcessName;

                    double mem = 0;
                    try { mem = p.WorkingSet64 / 1048576.0; } catch { }

                    var v = Analyzer.AnalyzeProcess(name, path);
                    res.Add(new ProcItem
                    {
                        Pid = p.Id,
                        Name = name,
                        MemoryMb = mem,
                        Path = path ?? "",
                        Level = v.Level,
                        Reason = v.Reason
                    });
                }
                catch
                {
                    // процесс успел завершиться — пропускаем
                }
            }
        }
        return res.OrderByDescending(x => x.Level).ThenByDescending(x => x.MemoryMb).ToList();
    }

    private async void LoadProcesses()
    {
        ProcStatus.Text = "Сканирую процессы...";
        try
        {
            _allProcs = await Task.Run(BuildProcList);
        }
        catch (Exception ex)
        {
            ProcStatus.Text = "Не удалось получить список процессов: " + ex.Message;
            return;
        }
        ApplyProcFilter();
    }

    private void ApplyProcFilter()
    {
        if (!_ready) return;

        string q = ProcSearch.Text.Trim();
        bool onlySusp = ProcSuspOnly.IsChecked == true;

        var list = _allProcs.Where(p =>
            (!onlySusp || p.Level >= 1) &&
            (q.Length == 0 ||
             p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             p.Path.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             p.Pid.ToString().Contains(q))).ToList();

        ProcGrid.ItemsSource = list;
        int bad = _allProcs.Count(p => p.Level == 2);
        int warn = _allProcs.Count(p => p.Level == 1);
        ProcStatus.Text = $"Показано {list.Count} из {_allProcs.Count}. Подозрительных: {bad}, стоит проверить: {warn}";
    }

    private void ProcSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyProcFilter();
    private void ProcFilter_Changed(object sender, RoutedEventArgs e) => ApplyProcFilter();
    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => LoadProcesses();

    private async void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ProcGrid.SelectedItem is not ProcItem item)
        {
            MessageBox.Show("Сначала выберите процесс в списке.", "Процессы",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await TerminateAsync(item.Pid, item.Name);
        LoadProcesses();
    }

    private void OpenProcFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ProcGrid.SelectedItem is not ProcItem item)
        {
            MessageBox.Show("Сначала выберите процесс в списке.", "Процессы",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrEmpty(item.Path))
        {
            MessageBox.Show("Путь к файлу этого процесса недоступен (нужны права администратора или это системный процесс).",
                "Процессы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenInExplorer(item.Path);
    }

    // ---------- Сеть ----------

    private static string TcpState(uint s) => s switch
    {
        1 => "Закрыто",
        2 => "Слушает",
        3 => "SYN отправлен",
        4 => "SYN получен",
        5 => "Соединено",
        6 => "FIN-WAIT-1",
        7 => "FIN-WAIT-2",
        8 => "CLOSE-WAIT",
        9 => "Закрывается",
        10 => "LAST-ACK",
        11 => "TIME-WAIT",
        12 => "Удалено",
        _ => "?"
    };

    private static (List<ConnItem> Conns, List<AdapterItem> Adapters) BuildNetwork()
    {
        var names = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { names[p.Id] = p.ProcessName; } catch { }
            }
        }

        var conns = new List<ConnItem>();
        foreach (var r in Native.GetTcpTable())
        {
            bool listen = r.State == 2;
            names.TryGetValue(r.Pid, out string? pn);
            conns.Add(new ConnItem
            {
                Process = pn ?? (r.Pid == 0 ? "System Idle" : "?"),
                Pid = r.Pid,
                Local = $"{r.Local}:{r.LocalPort}",
                Remote = listen ? "—" : $"{r.Remote}:{r.RemotePort}",
                State = TcpState(r.State),
                IsEstablished = r.State == 5,
                IsListening = listen
            });
        }
        conns = conns
            .OrderBy(c => c.IsEstablished ? 0 : c.IsListening ? 1 : 2)
            .ThenBy(c => c.Process, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var adapters = new List<AdapterItem>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var ip = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;

                string traffic = "";
                try
                {
                    var st = ni.GetIPStatistics();
                    traffic = $"↓ {FormatBytes(st.BytesReceived)}    ↑ {FormatBytes(st.BytesSent)}";
                }
                catch { }

                string speed = ni.Speed > 0 ? $" · {ni.Speed / 1_000_000} Мбит/с" : "";
                adapters.Add(new AdapterItem
                {
                    Name = ni.Name,
                    Info = (ip?.ToString() ?? "нет IPv4") + speed,
                    Traffic = traffic
                });
            }
        }
        catch
        {
        }

        return (conns, adapters);
    }

    private async void LoadNetwork()
    {
        NetStatus.Text = "Читаю сетевые соединения...";
        try
        {
            var (conns, adapters) = await Task.Run(BuildNetwork);
            _allConns = conns;
            AdaptersList.ItemsSource = adapters;
        }
        catch (Exception ex)
        {
            NetStatus.Text = "Не удалось получить данные сети: " + ex.Message;
            return;
        }
        ApplyNetFilter();
    }

    private void ApplyNetFilter()
    {
        if (!_ready) return;

        string q = NetSearch.Text.Trim();
        bool onlyEst = NetEstOnly.IsChecked == true;

        var list = _allConns.Where(c =>
            (!onlyEst || c.IsEstablished) &&
            (q.Length == 0 ||
             c.Process.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             c.Local.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             c.Remote.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             c.Pid.ToString().Contains(q))).ToList();

        NetGrid.ItemsSource = list;
        NetStatus.Text = $"Показано {list.Count} из {_allConns.Count}. Установленных: {_allConns.Count(c => c.IsEstablished)}, " +
                         $"слушающих портов: {_allConns.Count(c => c.IsListening)} (IPv4)";
    }

    private void NetSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyNetFilter();
    private void NetFilter_Changed(object sender, RoutedEventArgs e) => ApplyNetFilter();
    private void RefreshNetwork_Click(object sender, RoutedEventArgs e) => LoadNetwork();

    private async void KillNetProcess_Click(object sender, RoutedEventArgs e)
    {
        if (NetGrid.SelectedItem is not ConnItem item)
        {
            MessageBox.Show("Сначала выберите соединение в списке.", "Сеть",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await TerminateAsync(item.Pid, item.Process);
        LoadNetwork();
    }

    // ---------- Консоль ----------

    private void UpdateConsolePrompt() => ConsolePrompt.Text = _cwd + ">";

    private void AppendConsole(string text)
    {
        ConsoleOutput.AppendText(text);
        if (ConsoleOutput.Text.Length > 200_000)
            ConsoleOutput.Text = ConsoleOutput.Text[^150_000..];
        ConsoleOutput.ScrollToEnd();
    }

    private void ConsoleInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                RunCommand();
                break;
            case Key.Up:
                e.Handled = true;
                if (_history.Count > 0)
                {
                    _histIdx = Math.Max(0, _histIdx - 1);
                    ConsoleInput.Text = _history[_histIdx];
                    ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
                }
                break;
            case Key.Down:
                e.Handled = true;
                if (_history.Count > 0)
                {
                    _histIdx = Math.Min(_history.Count, _histIdx + 1);
                    ConsoleInput.Text = _histIdx < _history.Count ? _history[_histIdx] : "";
                    ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
                }
                break;
        }
    }

    private void ConsoleRun_Click(object sender, RoutedEventArgs e) => RunCommand();

    private void ConsoleClear_Click(object sender, RoutedEventArgs e)
    {
        ConsoleOutput.Clear();
        ConsoleInput.Focus();
    }

    private void ConsoleStop_Click(object sender, RoutedEventArgs e)
    {
        if (_cmdProc is { HasExited: false })
        {
            _cmdStoppedByUser = true;
            try { _cmdProc.Kill(true); } catch { }
        }
    }

    /// <summary>Команды, которые должны менять состояние самой консоли (cls, cd, смена диска).</summary>
    private bool TryHandleBuiltin(string cmd)
    {
        if (cmd.Equals("cls", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleOutput.Clear();
            return true;
        }

        if (cmd.Length == 2 && cmd[1] == ':' && char.IsLetter(cmd[0]))
        {
            string root = char.ToUpperInvariant(cmd[0]) + @":\";
            AppendConsole($"{_cwd}> {cmd}\n");
            if (Directory.Exists(root)) { _cwd = root; UpdateConsolePrompt(); }
            else AppendConsole("Не удаётся найти указанный диск.\n");
            AppendConsole("\n");
            return true;
        }

        if (!cmd.Contains('&') && !cmd.Contains('|'))
        {
            var m = CdRegex.Match(cmd);
            if (m.Success)
            {
                AppendConsole($"{_cwd}> {cmd}\n");
                string target = m.Groups[2].Value.Trim().Trim('"');
                try
                {
                    string full = Path.GetFullPath(Path.Combine(_cwd, target));
                    if (Directory.Exists(full)) { _cwd = full; UpdateConsolePrompt(); }
                    else AppendConsole("Системе не удаётся найти указанный путь.\n");
                }
                catch
                {
                    AppendConsole("Неверный путь.\n");
                }
                AppendConsole("\n");
                return true;
            }

            if (cmd.Equals("cd", StringComparison.OrdinalIgnoreCase))
            {
                AppendConsole($"{_cwd}> cd\n{_cwd}\n\n");
                return true;
            }
        }

        return false;
    }

    private async void RunCommand()
    {
        string cmd = ConsoleInput.Text.Trim();
        if (cmd.Length == 0) return;

        if (_cmdProc is { HasExited: false })
        {
            AppendConsole("[предыдущая команда ещё выполняется — дождитесь или нажмите «Стоп»]\n");
            return;
        }

        _history.Add(cmd);
        _histIdx = _history.Count;
        ConsoleInput.Clear();

        if (TryHandleBuiltin(cmd)) return;

        AppendConsole($"{_cwd}> {cmd}\n");

        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /s /c \"chcp 65001>nul & {cmd}\"",
            WorkingDirectory = _cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, a) => { if (a.Data != null) Dispatcher.BeginInvoke(new Action(() => AppendConsole(a.Data + "\n"))); };
        proc.ErrorDataReceived += (_, a) => { if (a.Data != null) Dispatcher.BeginInvoke(new Action(() => AppendConsole(a.Data + "\n"))); };

        try
        {
            _cmdStoppedByUser = false;
            proc.Start();
            _cmdProc = proc;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.StandardInput.Close(); // чтобы команды вроде pause не зависали в ожидании ввода

            ConsoleStopBtn.IsEnabled = true;
            await proc.WaitForExitAsync();

            AppendConsole(_cmdStoppedByUser ? "[остановлено]\n\n" : $"[код выхода: {proc.ExitCode}]\n\n");
        }
        catch (Exception ex)
        {
            AppendConsole($"[ошибка запуска: {ex.Message}]\n\n");
        }
        finally
        {
            ConsoleStopBtn.IsEnabled = false;
            _cmdProc = null;
            proc.Dispose();
            ConsoleInput.Focus();
        }
    }

    // ---------- Автозагрузка ----------

    private async void LoadStartup()
    {
        StartupStatus.Text = "Ищу записи автозагрузки...";
        try
        {
            _allAuto = await Task.Run(Analyzer.ScanStartup);
        }
        catch (Exception ex)
        {
            StartupStatus.Text = "Не удалось прочитать автозагрузку: " + ex.Message;
            return;
        }
        ApplyStartupFilter();
    }

    private bool OnRegistryTab => StartupTabReg.IsChecked == true;

    private void ApplyStartupFilter()
    {
        if (!_ready) return;

        StartupTabReg.Content = $"Реестр ({_allAuto.Count(a => a.Kind != AutoKind.StartupFile)})";
        StartupTabFolder.Content = $"Папка автозагрузки ({_allAuto.Count(a => a.Kind == AutoKind.StartupFile)})";

        var scope = _allAuto.Where(a => OnRegistryTab ? a.Kind != AutoKind.StartupFile : a.Kind == AutoKind.StartupFile).ToList();

        string q = StartupSearch.Text.Trim();
        bool onlySusp = StartupSuspOnly.IsChecked == true;

        var list = scope.Where(a =>
            (!onlySusp || a.Level >= 1) &&
            (q.Length == 0 ||
             a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             a.Command.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             a.Target.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        StartupGrid.ItemsSource = list;

        int missing = scope.Count(a => a.IsMissing && a.Kind != AutoKind.Winlogon);
        StartupCleanBtn.Content = missing > 0 ? $"Удалить без файла ({missing})" : "Удалить без файла";
        StartupCleanBtn.IsEnabled = missing > 0;

        StartupStatus.Text = $"Показано {list.Count} из {scope.Count}. Подозрительных: {scope.Count(a => a.Level == 2)}, " +
                             $"стоит проверить: {scope.Count(a => a.Level == 1)}, без файла: {missing}";
    }

    private void StartupTab_Changed(object sender, RoutedEventArgs e) => ApplyStartupFilter();
    private void StartupSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyStartupFilter();
    private void StartupFilter_Changed(object sender, RoutedEventArgs e) => ApplyStartupFilter();
    private void RefreshStartup_Click(object sender, RoutedEventArgs e) => LoadStartup();

    private void OpenStartupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (StartupGrid.SelectedItem is not AutoItem item)
        {
            MessageBox.Show("Сначала выберите запись в списке.", "Автозагрузка",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenInExplorer(item.ExePath);
    }

    /// <summary>Удаляет одну запись автозагрузки (из реестра или файл из папки).</summary>
    private static void RemoveAutoEntry(AutoItem item)
    {
        if (item.Kind == AutoKind.Registry)
        {
            var root = item.IsMachine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
            using var key = root.OpenSubKey(item.RegSubKey, writable: true);
            if (key == null) throw new InvalidOperationException("Раздел реестра не найден.");
            key.DeleteValue(item.ValueName, throwOnMissingValue: false);
        }
        else if (item.Kind == AutoKind.StartupFile)
        {
            File.Delete(item.Command);
        }
    }

    private void RemoveStartup_Click(object sender, RoutedEventArgs e)
    {
        if (StartupGrid.SelectedItem is not AutoItem item)
        {
            MessageBox.Show("Сначала выберите запись в списке.", "Автозагрузка",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (item.Kind == AutoKind.Winlogon)
        {
            MessageBox.Show(
                "Записи Winlogon (Shell и Userinit) нельзя удалять автоматически: ошибка может сломать вход в систему. " +
                "Исправьте значение вручную в реестре или проверьте систему антивирусом.",
                "Автозагрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string what = item.Kind == AutoKind.Registry
            ? "Запись будет удалена из реестра."
            : "Файл будет удалён из папки автозагрузки.";
        string shown = string.IsNullOrEmpty(item.Target) ? item.Command : item.Target;
        var answer = MessageBox.Show(
            $"Убрать «{item.Name}» из автозагрузки?\n\n{shown}\n\n{what} Запомните команду выше, если захотите вернуть её.",
            "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            RemoveAutoEntry(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show(IsDenied(ex) ? NeedAdminText : ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        LoadStartup();
    }

    /// <summary>Убирает все записи текущей вкладки, у которых нет файла на диске.</summary>
    private void CleanMissingStartup_Click(object sender, RoutedEventArgs e)
    {
        var missing = _allAuto.Where(a =>
            a.IsMissing && a.Kind != AutoKind.Winlogon &&
            (OnRegistryTab ? a.Kind == AutoKind.Registry : a.Kind == AutoKind.StartupFile)).ToList();

        if (missing.Count == 0)
        {
            MessageBox.Show("Записей без файла на этой вкладке нет.", "Автозагрузка",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string names = string.Join("\n", missing.Take(15).Select(m => "• " + m.Name));
        if (missing.Count > 15) names += $"\n…и ещё {missing.Count - 15}";

        var answer = MessageBox.Show(
            $"Удалить записи, у которых программа уже не существует ({missing.Count} шт.)?\n\n{names}\n\n" +
            "Они всё равно не запускаются и только засоряют список.",
            "Удалить записи без файла", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        int done = 0;
        bool denied = false;
        var errors = new List<string>();
        foreach (var m in missing)
        {
            try
            {
                RemoveAutoEntry(m);
                done++;
            }
            catch (Exception ex)
            {
                if (IsDenied(ex)) denied = true;
                else errors.Add($"{m.Name}: {ex.Message}");
            }
        }

        string report = $"Удалено записей: {done} из {missing.Count}.";
        if (denied) report += "\n\n" + NeedAdminText;
        if (errors.Count > 0) report += "\n\n" + string.Join("\n", errors.Take(5));
        MessageBox.Show(report, "Автозагрузка", MessageBoxButton.OK,
            denied || errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        LoadStartup();
    }

    // ---------- Оптимизация (шутка: только текст, ничего реального не выполняется) ----------

    private bool _optimizing;

    private async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        if (_optimizing) return;
        _optimizing = true;
        OptimizeBtn.IsEnabled = false;
        OptLog.Clear();
        OptBar.BeginAnimation(ProgressBar.ValueProperty, null);
        OptBar.Value = 0;
        OptStatusText.Text = "Оптимизация...";

        string[] steps =
        {
            "Запускаю оптимизацию системы...",
            "Оптимизирую систему...",
            "Удаляю System32...",
            "Удаляю папку Windows...",
            "Удаляю локальный диск C:...",
            "Нажимаю Alt+F4...",
            "Закрываю все приложения..."
        };

        var rnd = new Random();
        for (int i = 0; i < steps.Length; i++)
        {
            OptLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {steps[i]}\n");
            OptLog.ScrollToEnd();

            double target = (i + 1) * 100.0 / (steps.Length + 1);
            OptBar.BeginAnimation(ProgressBar.ValueProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(450)));

            await Task.Delay(rnd.Next(700, 1300));
        }

        OptBar.BeginAnimation(ProgressBar.ValueProperty,
            new DoubleAnimation(100, TimeSpan.FromMilliseconds(450)));
        await Task.Delay(500);

        OptLog.AppendText($"[{DateTime.Now:HH:mm:ss}] Готово! Оптимизация завершена.\n");
        OptLog.AppendText("\n(Это шутка: на самом деле ничего не удалено и ничего не закрыто.)\n");
        OptLog.ScrollToEnd();

        OptStatusText.Text = "Оптимизация завершена";
        OptimizeBtn.IsEnabled = true;
        _optimizing = false;
    }

    // ---------- Защита ----------

    private async void QuickScan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        _scanning = true;
        QuickScanBtn.IsEnabled = false;
        ScanBar.Value = 0;

        try
        {
            ScanText.Text = "Проверяю процессы...";
            var procs = await Task.Run(BuildProcList);
            ScanBar.Value = 40;

            ScanText.Text = "Проверяю автозагрузку...";
            var auto = await Task.Run(Analyzer.ScanStartup);
            ScanBar.Value = 75;

            ScanText.Text = "Проверяю сетевые соединения...";
            int established = await Task.Run(() => Native.GetTcpTable().Count(r => r.State == 5));
            ScanBar.Value = 100;

            _allProcs = procs;
            _allAuto = auto;
            ApplyProcFilter();
            ApplyStartupFilter();

            int badProcs = procs.Count(p => p.Level >= 1);
            int badAuto = auto.Count(a => a.Level >= 1);
            SecProcText.Text = badProcs.ToString();
            SecAutoText.Text = badAuto.ToString();
            SecNetText.Text = established.ToString();

            SecStatusText.Text = badProcs + badAuto == 0
                ? "Явных признаков проблем не найдено."
                : $"Найдено мест, на которые стоит взглянуть: {badProcs + badAuto}.";
            ScanText.Text = $"Проверено процессов: {procs.Count}, записей автозагрузки: {auto.Count}. Открываю средство проверки Windows (MRT)...";

            await Task.Delay(900);
            LaunchMrt();
        }
        catch (Exception ex)
        {
            ScanText.Text = "Ошибка проверки: " + ex.Message;
        }
        finally
        {
            _scanning = false;
            QuickScanBtn.IsEnabled = true;
        }
    }

    private void LaunchMrt()
    {
        try
        {
            // то же, что Win+R → mrt: официальное средство удаления вредоносных программ Microsoft
            Process.Start(new ProcessStartInfo("mrt.exe") { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ScanText.Text = "Запрос прав администратора отменён — MRT не запущен.";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить mrt.exe: " + ex.Message, "Защита",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenDefender_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось открыть «Безопасность Windows»: " + ex.Message, "Защита",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SecShowProcs_Click(object sender, RoutedEventArgs e)
    {
        ProcSuspOnly.IsChecked = true;
        NavProcesses.IsChecked = true;
    }

    private void SecShowStartup_Click(object sender, RoutedEventArgs e)
    {
        StartupSuspOnly.IsChecked = true;
        NavStartup.IsChecked = true;
    }

    private void SecShowNet_Click(object sender, RoutedEventArgs e)
    {
        NetEstOnly.IsChecked = true;
        NavNetwork.IsChecked = true;
    }
}
