using NumaFiles.Core.Models;

namespace NumaLink.App.Services;

public sealed class LocStrings
{
    public LinkLanguage Lang { get; private set; } = LinkLanguage.Es;

    public void SetLanguage(LinkLanguage lang) => Lang = lang;

    private bool En => Lang == LinkLanguage.En;

    public string ThisDevice => En ? "THIS DEVICE" : "ESTE EQUIPO";
    public string VisibilityTitle => En ? "VISIBILITY" : "VISIBILIDAD";
    public string MountedDrives => En ? "MOUNTED DRIVES" : "UNIDADES MONTADAS";
    public string NearbyDevices => En ? "NEARBY DEVICES" : "EQUIPOS CERCANOS";
    public string TabNearby => En ? "Nearby" : "Cercanos";
    public string TabMounted => En ? "Mounted" : "Montadas";
    public string TabSharing => En ? "Sharing" : "Compartiendo";
    public string NoDrives => En ? "No connected drives" : "Sin unidades conectadas";
    public string NoSharing => En ? "No devices accessing your files" : "Ningún equipo accede a tus archivos";
    public string SearchingPeers => En ? "Searching for devices on your network..." : "Buscando equipos en tu red...";
    public string Connect => En ? "Connect" : "Conectar";
    public string Open => En ? "Open" : "Abrir";
    public string Unmount => En ? "Unmount" : "Desmontar";
    public string EditPrivileges => En ? "Privileges" : "Privilegios";
    public string CutConnection => En ? "Cut" : "Cortar";
    public string RevokeAccess => En ? "Revoke" : "Revocar";
    public string Close => En ? "Close" : "Cerrar";
    public string Settings => En ? "Settings" : "Configuración";
    public string ThemeTooltip => En ? "Switch Light / Dark mode" : "Cambiar modo Claro / Oscuro";
    public string SettingsTooltip => En ? "Settings" : "Configuración";

    public string ConflictTitle => En ? "Numa Files is running" : "Numa Files está abierto";
    public string ConflictBody => En ? "Close it to avoid LAN connection conflicts." : "Ciérralo para evitar conflictos de conexión.";
    public string WinFspMissingTitle => En ? "WinFsp is missing" : "Falta WinFsp";
    public string WinFspMissingBody => En ? "Required to mount devices as a Windows drive (Z:\\)." : "Necesario para montar equipos como unidad (Z:\\).";
    public string InstallWinFspBtn => En ? "Install WinFsp (2.2 MB)" : "Instalar WinFsp (2.2 MB)";

    public string VisibilityLabel(VisibilityMode m) => m switch
    {
        VisibilityMode.EveryoneOnLan => En ? "Visible: Everyone" : "Visible: Todos",
        VisibilityMode.TrustedOnly => En ? "Visible: Trusted" : "Visible: Confianza",
        VisibilityMode.Hidden => En ? "Hidden" : "Oculto",
        _ => "LAN"
    };

    // Settings window
    public string TabGeneral => En ? "General" : "General";
    public string TabConnections => En ? "Connections" : "Conexiones";
    public string TabSystem => En ? "System" : "Sistema";
    public string LayoutSection => En ? "VISUAL LAYOUT" : "ESTRUCTURA VISUAL";
    public string LayoutQuickShare => En ? "Quick Share — Compact tabs" : "Quick Share — Pestañas compactas";
    public string LayoutTwoCol => En ? "Horizontal — 2 columns" : "Horizontal — 2 columnas";
    public string LayoutBento => En ? "Bento Grid — Modular cards" : "Bento Grid — Tarjetas modulares";
    public string LanguageSection => En ? "LANGUAGE" : "IDIOMA";
    public string StartupSection => En ? "SYSTEM & CACHE" : "SISTEMA Y CACHÉ";
    public string StartWithWindows => En ? "Start minimized when Windows starts" : "Abrir minimizado al iniciar Windows";
    public string AliasSection => En ? "DEVICE ALIAS" : "NOMBRE DEL EQUIPO (ALIAS)";
    public string SaveAliasBtn => En ? "Save" : "Guardar";
    public string SessionsSection => En ? "ACTIVE SESSIONS & TRUSTED DEVICES" : "SESIONES ACTIVAS Y CONFIANZA";
    public string NoActiveSessions => En ? "No active LAN sessions" : "Sin sesiones LAN activas";
    public string NoTrustedDevices => En ? "No trusted devices saved" : "Sin equipos de confianza guardados";
    public string DisconnectBtn => En ? "Disconnect" : "Desconectar";
    public string RevokeBtn => En ? "Revoke" : "Revocar";
    public string ClearCacheBtn => En ? "Clear cache" : "Limpiar caché";
    public string CacheLabel(string size) => En ? $"Temporary cache: {size}" : $"Caché temporal: {size}";
    public string UpdatesSection => En ? "UPDATES" : "ACTUALIZACIONES";
    public string AutoCheckUpdates => En ? "Check for updates automatically" : "Buscar actualizaciones automáticamente";
    public string CheckUpdatesBtn => En ? "Check for updates" : "Buscar actualizaciones";
    public string CheckingUpdates => En ? "Checking for updates..." : "Buscando actualizaciones...";
    public string UpdateAvailableTitle => En ? "Update available" : "Actualización disponible";
    public string DownloadUpdateBtn => En ? "Download" : "Descargar";
    public string AboutSection => En ? "ABOUT" : "ACERCA DE";
    public string AboutDescription => En
        ? "Mount LAN computers as native Windows drives with Quick Share simplicity and TLS encryption."
        : "Monta equipos de tu red local como unidades nativas de Windows con la simplicidad de Quick Share y cifrado TLS.";
    public string SaveAndClose => En ? "Done" : "Listo";
}
