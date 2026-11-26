using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NumaFiles.Core.Models;
using NumaFiles.LanShare;
using Wpf.Ui.Controls;

namespace NumaLink.App;

public partial class LinkRequestWindow : FluentWindow
{
    private readonly ObservableCollection<string> _customFolders = new();

    public LinkRequestWindow(IncomingPairRequest request)
    {
        InitializeComponent();
        DeviceNameText.Text = request.FromDisplayName;
        DeviceSubtitleText.Text = $"Desde {request.RemoteAddress} · Quiere montar tus archivos";
        CodeText.Text = request.VerificationCode;
        CustomFoldersList.ItemsSource = _customFolders;
    }

    public PairDecision Decision { get; private set; } = PairDecision.Reject("Cancelada.");

    private void AccessLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomFoldersPanel is null) return;
        var tag = (AccessLevelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        CustomFoldersPanel.Visibility = tag == "Custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddCustomFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecciona una carpeta", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
        {
            foreach (var f in dialog.FolderNames)
                if (!_customFolders.Contains(f, StringComparer.OrdinalIgnoreCase))
                    _customFolders.Add(f);
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        var levelTag = (AccessLevelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var level = levelTag switch
        {
            "Full" => AccessLevel.Full,
            "Custom" => AccessLevel.Custom,
            _ => AccessLevel.Media
        };

        if (level == AccessLevel.Custom && _customFolders.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Agrega al menos una carpeta.", "Numa Link");
            return;
        }

        var permTag = (PermissionCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var perm = permTag == "ReadWrite" ? AccessPermission.ReadWrite : AccessPermission.ReadOnly;

        Decision = PairDecision.Accept(new AccessPolicy
        {
            Level = level,
            Permission = perm,
            CustomRoots = _customFolders.ToList(),
            RememberDevice = RememberCheck.IsChecked == true
        });
        DialogResult = true;
        Close();
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        Decision = PairDecision.Reject("Solicitud rechazada.");
        DialogResult = false;
        Close();
    }
}
