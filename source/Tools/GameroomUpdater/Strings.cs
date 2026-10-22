using System.Globalization;

namespace Gameroom.Updater
{
    /// <summary>
    /// Texts of the updater, in Spanish when Windows is in Spanish and in English otherwise.
    /// </summary>
    internal static class Strings
    {
        private static readonly bool spanish = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";

        private static string T(string english, string spanishText) => spanish ? spanishText : english;

        public static string Title => T("Gameroom Updater", "Actualizador de Gameroom");
        public static string Checking => T("Checking for updates…", "Buscando actualizaciones…");
        public static string UpToDate => T("Gameroom is up to date (version {0}).", "Gameroom está al día (versión {0}).");
        public static string UpdateAvailable => T("Gameroom {0} is available. You have version {1}.", "Ya está disponible Gameroom {0}. Tienes la versión {1}.");
        public static string NotInstalled => T("Gameroom {0} is ready to be installed.", "Gameroom {0} está listo para instalarse.");
        public static string NoReleases => T("No Gameroom release has been published yet.", "Todavía no se ha publicado ninguna versión de Gameroom.");
        public static string Update => T("Update", "Actualizar");
        public static string Install => T("Install", "Instalar");
        public static string Close => T("Close", "Cerrar");
        public static string Retry => T("Retry", "Reintentar");
        public static string Downloading => T("Downloading Gameroom {0}… {1:0.0} / {2:0.0} MB", "Descargando Gameroom {0}… {1:0.0} / {2:0.0} MB");
        public static string WaitingForGameroom => T("Waiting for Gameroom to close…", "Esperando a que se cierre Gameroom…");
        public static string CloseGameroom => T("Close Gameroom to continue the update. Any game you're playing keeps running.", "Cierra Gameroom para continuar con la actualización. Los juegos que estés jugando seguirán abiertos.");
        public static string Installing => T("Installing Gameroom {0}…", "Instalando Gameroom {0}…");
        public static string Updated => T("Gameroom was updated to version {0}.", "Gameroom se actualizó a la versión {0}.");
        public static string Failed => T("The update failed: {0}", "No se pudo actualizar: {0}");
        public static string SetupFailed => T("The installer ended with code {0}. Details are in {1}", "El instalador terminó con el código {0}. Los detalles están en {1}");
        public static string ConnectionFailed => T("Couldn't reach the update server: {0}", "No se pudo conectar con el servidor de actualizaciones: {0}");
        public static string WhatsNew => T("What's new", "Novedades");
    }
}
