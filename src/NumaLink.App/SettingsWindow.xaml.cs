using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NumaFiles.Core.Models;
using NumaFiles.LanShare;
using NumaFiles.VirtualDrive;
using NumaLink.App.Services;
using Wpf.Ui.Controls;

namespace NumaLink.App;

public partial class SettingsWindow : FluentWindow
{
    private readonly LinkSettings _settings;
    private readonly LocStrings _loc;
    private readonly ILanShareCoordinator _lan;
    private readonly VirtualDriveManager _driveManager;
    private readonly Action _onChanged;
    private readonly Action _onRequestExit;
    private bool _initializing = true;
    private int _activeTab;

    private sealed record RowItem(string Id, string DeviceName, string Summary, string ActionLabel);

    public SettingsWindow(
        LinkSettings settings,
        LocStrings loc,
        ILanShareCoordinator lan,
        VirtualDriveManager driveManager,
        Action onChanged,
        Action onRequestExit)
    {
        InitializeComponent();
        _settings = settings;
        _loc = loc;
        _lan = lan;
        _driveManager = driveManager;
        _onChanged = onChanged;
        _onRequestExit = onRequestExit;

        AliasTextBox.Text = _lan.LocalDeviceName;

        LayoutCombo.SelectedIndex = _settings.Layout switch
        {
            LinkLayoutStyle.TwoColumnHorizontal => 1,
            LinkLayoutStyle.BentoGrid => 2,
            _ => 0
        };

        StartupCheck.IsChecked = StartupManager.IsEnabled();
        AutoCheckUpdatesCheck.IsChecked = _settings.AutoCheckUpdates;
        ApplyLocalization();
        SelectTab(0);
        _initializing = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void TabGeneral_Click(object sender, RoutedEventArgs e) => SelectTab(0);
    private void TabConnections_Click(object sender, RoutedEventArgs e) => SelectTab(1);
    private void TabSystem_Click(object sender, RoutedEventArgs e) => SelectTab(2);

    private void SelectTab(int index)
    {
        _activeTab = index;
        GeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SystemPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        StyleTabButton(TabGeneralBtn, index == 0);
        StyleTabButton(TabConnectionsBtn, index == 1);
        StyleTabButton(TabSystemBtn, index == 2);

        if (index != 0)
            RefreshSessionsAndCache();
    }

    private void StyleTabButton(System.Windows.Controls.Button btn, bool active)
    {
        btn.Background = (System.Windows.Media.Brush)FindResource(active ? "LinkPrimaryBrush" : "LinkSurfaceBrush");
        btn.Foreground = (System.Windows.Media.Brush)FindResource(active ? "LinkPrimaryContentBrush" : "LinkTextSecondaryBrush");
    }

    private void ApplyLocalization()
    {
        Title = $"{_loc.Settings} — Numa Link";
        WindowTitleText.Text = _loc.Settings;
        TabGeneralBtn.Content = _loc.TabGeneral;
        TabConnectionsBtn.Content = _loc.TabConnections;
        TabSystemBtn.Content = _loc.TabSystem;

        AliasSectionLabel.Text = _loc.AliasSection;
        SaveAliasButton.Content = _loc.SaveAliasBtn;
        LayoutSectionLabel.Text = _loc.LayoutSection;
        LayoutItemQuickShare.Content = _loc.LayoutQuickShare;
        LayoutItemTwoCol.Content = _loc.LayoutTwoCol;
        LayoutItemBento.Content = _loc.LayoutBento;
        LanguageSectionLabel.Text = _loc.LanguageSection;

        SessionsSectionLabel.Text = _settings.Language == LinkLanguage.En ? "ACTIVE SESSIONS" : "SESIONES ACTIVAS";
        TrustedSectionLabel.Text = _settings.Language == LinkLanguage.En ? "TRUSTED DEVICES" : "EQUIPOS DE CONFIANZA";
        NoSessionsText.Text = _loc.NoActiveSessions;
        NoTrustedText.Text = _loc.NoTrustedDevices;

        StartupCheck.Content = _loc.StartWithWindows;
        AutoCheckUpdatesCheck.Content = _loc.AutoCheckUpdates;
        ClearCacheButton.Content = _loc.ClearCacheBtn;
        CheckUpdatesButton.Content = _loc.CheckUpdatesBtn;

        var ver = UpdateCheckerService.CurrentVersion;
        AboutVersionBadge.Text = $"v{ver}";
        var winfspStatus = WinFspBootstrapper.IsInstalled() ? "WinFsp OK" : "WinFsp pendiente";
        AboutTechText.Text = $"Numa · GPLv3 · TLS 1.3 · {winfspStatus}";

        UpdateLanguageButtonsVisual();
        StyleTabButton(TabGeneralBtn, _activeTab == 0);
        StyleTabButton(TabConnectionsBtn, _activeTab == 1);
        StyleTabButton(TabSystemBtn, _activeTab == 2);
        RefreshSessionsAndCache();
    }

    private void RefreshSessionsAndCache()
    {
        var sessions = _lan.ActiveSessions
            .Select(s => new RowItem(
                s.SessionId,
                s.Peer.DisplayName,
                $"{s.Peer.HostAddress} · {FormatPolicy(s.Policy)}",
                _loc.DisconnectBtn))
            .ToList();
        SessionsList.ItemsSource = sessions;
        NoSessionsText.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var trusted = _lan.TrustedDevices
            .Select(t => new RowItem(
                t.DeviceId,
                t.DisplayName,
                FormatPolicy(t.Policy),
                _loc.RevokeBtn))
            .ToList();
        TrustedList.ItemsSource = trusted;
        NoTrustedText.Visibility = trusted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var bytes = CacheCleanerService.GetCacheSizeBytes();
        CacheSizeText.Text = _loc.CacheLabel(CacheCleanerService.FormatSize(bytes));
    }

    private string FormatPolicy(AccessPolicy p)
    {
        var en = _settings.Language == LinkLanguage.En;
        var level = p.Level switch
        {
            AccessLevel.Full => en ? "Full" : "Completo",
            AccessLevel.Media => en ? "Media" : "Multimedia",
            AccessLevel.Custom => en ? "Custom" : "Personalizado",
            _ => p.Level.ToString()
        };
        var perm = p.Permission == AccessPermission.ReadWrite
            ? (en ? "Read/Write" : "Lectura/Escritura")
            : (en ? "Read-only" : "Solo lectura");
        return $"{level} · {perm}";
    }

    private void UpdateLanguageButtonsVisual()
    {
        var isEs = _settings.Language == LinkLanguage.Es;

        LangEsButton.Background = (System.Windows.Media.Brush)FindResource(isEs ? "LinkPrimaryBrush" : "LinkSurfaceBrush");
        LangEsButton.Foreground = (System.Windows.Media.Brush)FindResource(isEs ? "LinkPrimaryContentBrush" : "LinkTextPrimaryBrush");
        LangEsButton.BorderBrush = (System.Windows.Media.Brush)FindResource(isEs ? "LinkPrimaryBrush" : "LinkBorderBrush");

        LangEnButton.Background = (System.Windows.Media.Brush)FindResource(!isEs ? "LinkPrimaryBrush" : "LinkSurfaceBrush");
        LangEnButton.Foreground = (System.Windows.Media.Brush)FindResource(!isEs ? "LinkPrimaryContentBrush" : "LinkTextPrimaryBrush");
        LangEnButton.BorderBrush = (System.Windows.Media.Brush)FindResource(!isEs ? "LinkPrimaryBrush" : "LinkBorderBrush");
    }

    private void SaveAlias_Click(object sender, RoutedEventArgs e)
    {
        _lan.SetLocalDeviceName(AliasTextBox.Text);
        AliasTextBox.Text = _lan.LocalDeviceName;
        _onChanged();
    }

    private void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var tag = (LayoutCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        _settings.Layout = tag switch
        {
            "TwoColumnHorizontal" => LinkLayoutStyle.TwoColumnHorizontal,
            "BentoGrid" => LinkLayoutStyle.BentoGrid,
            _ => LinkLayoutStyle.QuickShareTabs
        };
        LinkSettingsStore.Save(_settings);
        _onChanged();
    }

    private void LangEs_Click(object sender, RoutedEventArgs e) => SetLanguage(LinkLanguage.Es);
    private void LangEn_Click(object sender, RoutedEventArgs e) => SetLanguage(LinkLanguage.En);

    private void SetLanguage(LinkLanguage lang)
    {
        _settings.Language = lang;
        _loc.SetLanguage(lang);
        LinkSettingsStore.Save(_settings);
        ApplyLocalization();
        _onChanged();
    }

    private void DisconnectSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string sessionId })
        {
            _driveManager.Unmount(sessionId);
            _lan.DisconnectSession(sessionId);
            RefreshSessionsAndCache();
            _onChanged();
        }
    }

    private void RevokeTrusted_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string deviceId })
        {
            _lan.RevokeTrustedDevice(deviceId);
            RefreshSessionsAndCache();
            _onChanged();
        }
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var enabled = StartupCheck.IsChecked == true;
        _settings.StartWithWindows = enabled;
        StartupManager.SetEnabled(enabled);
        LinkSettingsStore.Save(_settings);
    }

    private void AutoCheckUpdates_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.AutoCheckUpdates = AutoCheckUpdatesCheck.IsChecked == true;
        LinkSettingsStore.Save(_settings);
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        CacheCleanerService.ClearCache();
        RefreshSessionsAndCache();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = _loc.CheckingUpdates;
        UpdateStatusText.Visibility = Visibility.Visible;

        var res = await UpdateCheckerService.CheckAsync(_settings.UpdateRepo, _settings.Language);
        UpdateStatusText.Text = res.Message;

        if (res.UpdateAvailable)
        {
            var prompt = !string.IsNullOrWhiteSpace(res.DirectExeUrl)
                ? (_settings.Language == LinkLanguage.En
                    ? "Download and install automatically now?"
                    : "¿Descargar e instalar automáticamente ahora?")
                : (_settings.Language == LinkLanguage.En
                    ? "Open download page?"
                    : "¿Abrir página de descarga?");

            var confirm = System.Windows.MessageBox.Show(
                this,
                $"{res.Message}\n\n{prompt}",
                "Numa Link",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information);

            if (confirm == System.Windows.MessageBoxResult.Yes)
            {
                if (!string.IsNullOrWhiteSpace(res.DirectExeUrl))
                {
                    try
                    {
                        var progress = new Progress<string>(msg => UpdateStatusText.Text = msg);
                        var applied = await UpdateCheckerService.DownloadAndApplyUpdateAsync(res.DirectExeUrl, _settings.Language, progress);
                        if (applied)
                        {
                            Close();
                            _onRequestExit();
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        UpdateStatusText.Text = $"Error: {ex.Message}";
                    }
                }
                else if (!string.IsNullOrWhiteSpace(res.DownloadUrl))
                {
                    Process.Start(new ProcessStartInfo { FileName = res.DownloadUrl, UseShellExecute = true });
                }
            }
        }

        CheckUpdatesButton.IsEnabled = true;
    }
}
