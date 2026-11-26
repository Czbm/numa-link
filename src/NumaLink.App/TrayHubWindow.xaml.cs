using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NumaFiles.Core.Models;
using NumaFiles.LanShare;
using NumaFiles.VirtualDrive;
using NumaLink.App.Services;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;

namespace NumaLink.App;

public partial class TrayHubWindow : FluentWindow
{
    private readonly LinkSettings _settings;
    private readonly LocStrings _loc = new();
    private readonly LanShareCoordinator _lan;
    private readonly VirtualDriveManager _driveManager;
    private readonly DispatcherTimer _conflictTimer;
    private readonly DispatcherTimer _updateTimer;
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;
    private bool _qsShowingMounted;
    private string? _pendingUpdateUrl;
    private string? _pendingDirectExeUrl;
    private string? _notifiedUpdateVersion;

    private sealed record PeerRow(LanPeer Peer, string ConnectLabel)
    {
        public string DisplayName => Peer.DisplayName;
        public string HostAddress => Peer.HostAddress;
        public SymbolRegular DeviceIcon =>
            Peer.DeviceType.Equals("android", StringComparison.OrdinalIgnoreCase) ||
            Peer.DeviceType.Equals("ios", StringComparison.OrdinalIgnoreCase)
                ? SymbolRegular.Phone24
                : SymbolRegular.Laptop24;
    }

    private sealed record DriveRow(MountedDriveInfo Info, string OpenLabel, string UnmountLabel)
    {
        public string SessionId => Info.SessionId;
        public string DeviceName => Info.DeviceName;
        public string DriveLetter => Info.DriveLetter;
        public string MountPoint => Info.MountPoint;
        public string AccessSummary => Info.AccessSummary;
    }

    public TrayHubWindow()
    {
        InitializeComponent();

        _settings = LinkSettingsStore.Load();
        _loc.SetLanguage(_settings.Language);
        LinkThemeManager.Apply(_settings.ColorMode);

        _lan = new LanShareCoordinator { Visibility = _settings.Visibility };
        _lan.IncomingRequestHandler = HandleIncomingRequestAsync;
        _lan.StateChanged += (_, _) => Dispatcher.Invoke(RefreshUi);

        _driveManager = new VirtualDriveManager();
        _driveManager.MountedDrivesChanged += (_, _) => Dispatcher.Invoke(RefreshUi);

        _conflictTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _conflictTimer.Tick += (_, _) => UpdateConflictBanner();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesSilentlyAsync();

        InitializeTray();

        Loaded += (_, _) =>
        {
            _lan.Start();
            _conflictTimer.Start();
            _updateTimer.Start();
            ApplyLayoutAndLocalization();
            RefreshUi();

            if (App.StartMinimized)
                Hide();

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                await Dispatcher.InvokeAsync(async () => await CheckForUpdatesSilentlyAsync());
            });
        };

        Closing += TrayHubWindow_Closing;
        Closed += (_, _) => Cleanup();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void ApplyLayoutAndLocalization()
    {
        var ver = $"v{UpdateCheckerService.CurrentVersion}";
        Title = $"Numa Link {ver}";
        TitleVersionText.Text = ver;

        // Resize width cleanly per layout while letting height hug content dynamically
        LayoutQuickShareRoot.Visibility = _settings.Layout == LinkLayoutStyle.QuickShareTabs ? Visibility.Visible : Visibility.Collapsed;
        LayoutTwoColRoot.Visibility = _settings.Layout == LinkLayoutStyle.TwoColumnHorizontal ? Visibility.Visible : Visibility.Collapsed;
        LayoutBentoRoot.Visibility = _settings.Layout == LinkLayoutStyle.BentoGrid ? Visibility.Visible : Visibility.Collapsed;

        switch (_settings.Layout)
        {
            case LinkLayoutStyle.QuickShareTabs:
                MinWidth = 460;
                Width = 490;
                break;
            case LinkLayoutStyle.TwoColumnHorizontal:
                MinWidth = 740;
                Width = 820;
                break;
            case LinkLayoutStyle.BentoGrid:
                MinWidth = 540;
                Width = 580;
                break;
        }
        SizeToContent = SizeToContent.Height;

        // Theme icon
        ThemeToggleIcon.Symbol = _settings.ColorMode == LinkColorMode.Dark
            ? SymbolRegular.WeatherSunny20
            : SymbolRegular.WeatherMoon20;
        ThemeToggleButton.ToolTip = _loc.ThemeTooltip;
        SettingsButton.ToolTip = _loc.SettingsTooltip;

        // Shared banners
        UpdateBannerTitle.Text = _loc.UpdateAvailableTitle;
        DownloadUpdateButton.Content = _loc.DownloadUpdateBtn;
        ConflictTitleText.Text = _loc.ConflictTitle;
        ConflictBodyText.Text = _loc.ConflictBody;
        CloseConflictButton.Content = _loc.Close;
        WinFspTitleText.Text = _loc.WinFspMissingTitle;
        WinFspBannerText.Text = _loc.WinFspMissingBody;
        InstallWinFspButton.Content = _loc.InstallWinFspBtn;

        // Layout-specific static headers
        QsSearchingText.Text = _loc.SearchingPeers;
        QsNoDrivesText.Text = _loc.NoDrives;

        TcThisDeviceLabel.Text = _loc.ThisDevice;
        TcNearbyHeader.Text = _loc.NearbyDevices;
        TcMountedHeader.Text = _loc.MountedDrives;
        TcNoPeersText.Text = _loc.SearchingPeers;
        TcNoDrivesText.Text = _loc.NoDrives;

        BentoDeviceLabel.Text = _loc.ThisDevice;
        BentoVisLabel.Text = _loc.VisibilityTitle;
        BentoMountedHeader.Text = _loc.MountedDrives;
        BentoNearbyHeader.Text = _loc.NearbyDevices;
        BentoNoPeersText.Text = _loc.SearchingPeers;

        UpdateQsTabVisuals();
        RefreshUi();
    }

    private void UpdateQsTabVisuals()
    {
        QsNearbyPanel.Visibility = !_qsShowingMounted ? Visibility.Visible : Visibility.Collapsed;
        QsMountedPanel.Visibility = _qsShowingMounted ? Visibility.Visible : Visibility.Collapsed;

        QsTabNearbyButton.Background = (System.Windows.Media.Brush)FindResource(!_qsShowingMounted ? "LinkPrimaryBrush" : "LinkSurfaceBrush");
        QsTabNearbyButton.Foreground = (System.Windows.Media.Brush)FindResource(!_qsShowingMounted ? "LinkPrimaryContentBrush" : "LinkTextSecondaryBrush");

        QsTabMountedButton.Background = (System.Windows.Media.Brush)FindResource(_qsShowingMounted ? "LinkPrimaryBrush" : "LinkSurfaceBrush");
        QsTabMountedButton.Foreground = (System.Windows.Media.Brush)FindResource(_qsShowingMounted ? "LinkPrimaryContentBrush" : "LinkTextSecondaryBrush");
    }

    private void QsTabNearby_Click(object sender, RoutedEventArgs e)
    {
        _qsShowingMounted = false;
        UpdateQsTabVisuals();
    }

    private void QsTabMounted_Click(object sender, RoutedEventArgs e)
    {
        _qsShowingMounted = true;
        UpdateQsTabVisuals();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ColorMode = _settings.ColorMode == LinkColorMode.Dark ? LinkColorMode.Light : LinkColorMode.Dark;
        LinkThemeManager.Apply(_settings.ColorMode);
        LinkSettingsStore.Save(_settings);
        ApplyLayoutAndLocalization();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings, _loc, _lan, _driveManager,
            () => ApplyLayoutAndLocalization(),
            () =>
            {
                _isExiting = true;
                Close();
            })
        { Owner = this };
        dlg.ShowDialog();
    }

    private static bool IsNumaFilesRunning() =>
        Process.GetProcessesByName("NumaFiles").Length > 0;

    private void UpdateConflictBanner()
    {
        ConflictBanner.Visibility = IsNumaFilesRunning() ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool TryCloseNumaFilesInstances()
    {
        var procs = Process.GetProcessesByName("NumaFiles");
        if (procs.Length == 0) return true;

        try
        {
            foreach (var p in procs)
            {
                using (p)
                {
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus($"Error: {ex.Message}");
            return false;
        }
    }

    private void InitializeTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Numa Link", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
        menu.Items.Add("Cambiar visibilidad LAN", null, (_, _) => Dispatcher.Invoke(CycleVisibility));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir y desmontar unidades", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _isExiting = true;
            Close();
        }));

        Icon? icon = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        }
        catch { }

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = "Numa Link",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWindow);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(_pendingUpdateUrl))
                OpenUrl(_pendingUpdateUrl);
            else
                RestoreWindow();
        });
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        if (!_settings.AutoCheckUpdates)
            return;

        var res = await UpdateCheckerService.CheckAsync(_settings.UpdateRepo, _settings.Language);
        if (!res.Success || !res.UpdateAvailable || string.IsNullOrWhiteSpace(res.LatestVersion))
            return;

        _pendingUpdateUrl = res.DownloadUrl;
        _pendingDirectExeUrl = res.DirectExeUrl;
        UpdateBannerTitle.Text = _loc.UpdateAvailableTitle;
        UpdateBannerText.Text = res.Message;
        DownloadUpdateButton.Content = _loc.DownloadUpdateBtn;
        UpdateBanner.Visibility = Visibility.Visible;

        if (_notifiedUpdateVersion != res.LatestVersion)
        {
            _notifiedUpdateVersion = res.LatestVersion;
            if (!IsVisible && _trayIcon is not null)
            {
                _trayIcon.ShowBalloonTip(
                    5000,
                    $"NUMA Link — {_loc.UpdateAvailableTitle}",
                    res.Message,
                    Forms.ToolTipIcon.Info);
            }
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_pendingDirectExeUrl))
        {
            DownloadUpdateButton.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(msg => UpdateBannerText.Text = msg);
                var applied = await UpdateCheckerService.DownloadAndApplyUpdateAsync(_pendingDirectExeUrl, _settings.Language, progress);
                if (applied)
                {
                    _isExiting = true;
                    Close();
                    return;
                }
            }
            catch (Exception ex)
            {
                UpdateBannerText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                DownloadUpdateButton.IsEnabled = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(_pendingUpdateUrl))
            OpenUrl(_pendingUpdateUrl);
    }

    private void DismissUpdate_Click(object sender, RoutedEventArgs e) =>
        UpdateBanner.Visibility = Visibility.Collapsed;

    private void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        UpdateConflictBanner();
    }

    private void TrayHubWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void Cleanup()
    {
        _conflictTimer.Stop();
        _updateTimer.Stop();
        _driveManager.Dispose();
        _lan.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    private Task<PairDecision> HandleIncomingRequestAsync(IncomingPairRequest request)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            var dlg = new LinkRequestWindow(request);
            dlg.ShowDialog();
            return dlg.Decision;
        }).Task;
    }

    private void RefreshUi()
    {
        var devName = _lan.LocalDeviceName;
        var visText = _loc.VisibilityLabel(_lan.Visibility);

        QsDeviceNameText.Text = devName;
        TcDeviceNameText.Text = devName;
        BentoDeviceNameText.Text = devName;

        QsVisibilityButton.Content = visText;
        TcVisibilityButton.Content = visText;
        BentoVisibilityButton.Content = visText;

        UpdateConflictBanner();
        WinFspBanner.Visibility = WinFspBootstrapper.IsInstalled() ? Visibility.Collapsed : Visibility.Visible;

        var drives = _driveManager.GetMountedDrives()
            .Select(d => new DriveRow(d, _loc.Open, _loc.Unmount))
            .ToList();

        var peers = _lan.NearbyPeers
            .Select(p => new PeerRow(p, _loc.Connect))
            .ToList();

        // Quick Share tabs
        QsTabNearbyButton.Content = $"{_loc.TabNearby} ({peers.Count})";
        QsTabMountedButton.Content = $"{_loc.TabMounted} ({drives.Count})";
        QsPeersList.ItemsSource = peers;
        QsNoPeersState.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QsDrivesList.ItemsSource = drives;
        QsNoDrivesState.Visibility = drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Two-Column layout
        TcPeersList.ItemsSource = peers;
        TcNoPeersText.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TcDrivesList.ItemsSource = drives;
        TcNoDrivesText.Visibility = drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Bento Grid layout
        BentoDrivesList.ItemsSource = drives;
        BentoMountedStrip.Visibility = drives.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BentoPeersList.ItemsSource = peers;
        BentoNoPeersText.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string message)
    {
        StatusBannerText.Text = message;
        StatusBanner.Visibility = Visibility.Visible;
    }

    private void DismissStatus_Click(object sender, RoutedEventArgs e) =>
        StatusBanner.Visibility = Visibility.Collapsed;

    private void CloseNumaFiles_Click(object sender, RoutedEventArgs e)
    {
        if (TryCloseNumaFilesInstances())
        {
            UpdateConflictBanner();
            ShowStatus(_settings.Language == LinkLanguage.En
                ? "Numa Files closed."
                : "Numa Files cerrado.");
        }
    }

    private void CycleVisibility()
    {
        _lan.Visibility = _lan.Visibility switch
        {
            VisibilityMode.EveryoneOnLan => VisibilityMode.TrustedOnly,
            VisibilityMode.TrustedOnly => VisibilityMode.Hidden,
            _ => VisibilityMode.EveryoneOnLan
        };
        _settings.Visibility = _lan.Visibility;
        LinkSettingsStore.Save(_settings);
        RefreshUi();
    }

    private void VisibilityButton_Click(object sender, RoutedEventArgs e) => CycleVisibility();

    private async void InstallWinFsp_Click(object sender, RoutedEventArgs e)
    {
        InstallWinFspButton.IsEnabled = false;
        var progress = new Progress<string>(msg => WinFspBannerText.Text = msg);
        try
        {
            var ok = await WinFspBootstrapper.InstallAsync(progress);
            if (ok)
                ShowStatus(_settings.Language == LinkLanguage.En ? "WinFsp installed." : "WinFsp instalado.");
            else
                ShowStatus(_settings.Language == LinkLanguage.En ? "WinFsp installation canceled." : "Instalación de WinFsp cancelada.");
        }
        catch (Exception ex)
        {
            var root = ex.InnerException ?? ex;
            ShowStatus($"Error: {root.Message}");
        }
        finally
        {
            InstallWinFspButton.IsEnabled = true;
            RefreshUi();
        }
    }

    private async void ConnectPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: LanPeer peer })
            return;

        if (IsNumaFilesRunning())
        {
            var msg = _settings.Language == LinkLanguage.En
                ? "Numa Files is running and may conflict with LAN connections.\nClose it before connecting?"
                : "Numa Files está abierto y puede interferir con la conexión LAN.\n¿Deseas cerrarlo antes de conectar?";

            var ans = System.Windows.MessageBox.Show(
                this, msg, "Numa Link",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (ans == System.Windows.MessageBoxResult.Yes)
            {
                TryCloseNumaFilesInstances();
                UpdateConflictBanner();
            }
        }

        if (!WinFspBootstrapper.IsInstalled())
        {
            ShowStatus(_loc.WinFspMissingBody);
            return;
        }

        var code = LanPairingService.GenerateVerificationCode();
        ShowStatus(_settings.Language == LinkLanguage.En
            ? $"Connecting to {peer.DisplayName} · Code {code}..."
            : $"Conectando a {peer.DisplayName} · Código {code}...");

        try
        {
            var resp = await _lan.RequestConnectionAsync(peer, code);
            if (!resp.Accepted)
            {
                ShowStatus($"{peer.DisplayName}: {resp.Reason ?? "Rejected"}");
                return;
            }

            var endpoint = _lan.ConnectedEndpoints.FirstOrDefault(ep => ep.SessionId == resp.SessionId);
            if (endpoint is null) return;

            var mounted = _driveManager.Mount(endpoint);
            ShowStatus($"{peer.DisplayName} → {mounted.MountPoint}");
            _qsShowingMounted = true;
            UpdateQsTabVisuals();
            RefreshUi();

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = mounted.MountPoint,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var root = ex.InnerException ?? ex;
            ShowStatus($"Error: {root.Message}");
        }
    }

    private void OpenDrive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string mountPoint })
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = mountPoint,
                UseShellExecute = true
            });
        }
    }

    private void UnmountDrive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string sessionId })
        {
            _driveManager.Unmount(sessionId);
            _lan.DisconnectSession(sessionId);
            ShowStatus(_settings.Language == LinkLanguage.En ? "Drive unmounted." : "Unidad desmontada.");
            RefreshUi();
        }
    }
}
