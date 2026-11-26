using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NumaLink.App.Services;

public static class LinkThemeManager
{
    public static void Apply(LinkColorMode mode)
    {
        var wpfTheme = mode == LinkColorMode.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(wpfTheme, WindowBackdropType.None, updateAccent: false);

        var res = System.Windows.Application.Current?.Resources;
        if (res is null) return;

        if (mode == LinkColorMode.Dark)
        {
            // Numa Dark (Azul Noche — never pure black)
            SetBrush(res, "LinkBgBrush", "#0E1324");
            SetBrush(res, "LinkSurfaceBrush", "#141B2F");
            SetBrush(res, "LinkElevatedBrush", "#1B2440");
            SetBrush(res, "LinkBorderBrush", "#2A3656");
            SetBrush(res, "LinkTextPrimaryBrush", "#EEF2FF");
            SetBrush(res, "LinkTextSecondaryBrush", "#94A3B8");
            SetBrush(res, "LinkPrimaryBrush", "#2563FF");
            SetBrush(res, "LinkPrimaryContentBrush", "#EEF2FF");
            SetBrush(res, "LinkSuccessBrush", "#2EE6C5");
            SetBrush(res, "LinkWarmBrush", "#FFB347");
            SetBrush(res, "LinkHoverBrush", "#1F2B4D");
        }
        else
        {
            // Numa Light
            SetBrush(res, "LinkBgBrush", "#F7FAFF");
            SetBrush(res, "LinkSurfaceBrush", "#FFFFFF");
            SetBrush(res, "LinkElevatedBrush", "#EEF4FF");
            SetBrush(res, "LinkBorderBrush", "#CBD5E1");
            SetBrush(res, "LinkTextPrimaryBrush", "#111827");
            SetBrush(res, "LinkTextSecondaryBrush", "#475569");
            SetBrush(res, "LinkPrimaryBrush", "#2563FF");
            SetBrush(res, "LinkPrimaryContentBrush", "#EEF2FF");
            SetBrush(res, "LinkSuccessBrush", "#0D9488");
            SetBrush(res, "LinkWarmBrush", "#D97706");
            SetBrush(res, "LinkHoverBrush", "#E2ECFF");
        }
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        res[key] = new SolidColorBrush(color);
    }
}
