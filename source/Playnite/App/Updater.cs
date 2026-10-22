using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Playnite.Common;
using Playnite.Common.Web;
using Playnite.Gameroom.Updates;
using Playnite.SDK;

namespace Playnite
{
    /// <summary>
    /// Checks for new Gameroom releases. Downloading and installing is done by GameroomUpdater.exe,
    /// so files of the running application can be replaced.
    /// </summary>
    public class Updater
    {
        private static ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteApplication playniteApp;
        private readonly GameroomReleaseClient client = new GameroomReleaseClient();
        private List<ReleaseInfo> releases;

        public static string UpdaterExecutablePath => Path.Combine(PlaynitePaths.ProgramPath, Gameroom.Updates.ReleaseChannel.UpdaterExecutableName);

        /// <summary>
        /// Updates are offered only to installed copies, development and portable builds don't update themselves.
        /// </summary>
        public static bool ProgramUpdatesEnabled => !PlayniteSettings.IsPortable && File.Exists(UpdaterExecutablePath);

        public bool IsUpdateAvailable
        {
            get
            {
                if (!ProgramUpdatesEnabled)
                {
                    return false;
                }

                return GetLatestVersion() > GameroomVersion.Current;
            }
        }

        private static Version currentVersion;
        /// <summary>
        /// Version of the Playnite base. Exposed to extensions, which use it to check compatibility.
        /// Gameroom's own version is <see cref="GameroomVersion.Current"/>.
        /// </summary>
        public static Version CurrentVersion
        {
            get
            {
                if (currentVersion == null)
                {
                    currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                }

                return currentVersion;
            }
        }

        public Updater(IPlayniteApplication app)
        {
            playniteApp = app;
        }

        private List<ReleaseInfo> GetReleases()
        {
            if (releases == null)
            {
                releases = client.GetReleases();
            }

            return releases;
        }

        public Version GetLatestVersion()
        {
            return GetReleases().FirstOrDefault()?.Version ?? GameroomVersion.Current;
        }

        public List<ReleaseNoteData> GetReleaseNotes()
        {
            return GetReleases()
                .Where(a => a.Version > GameroomVersion.Current)
                .Select(a => new ReleaseNoteData { Version = a.Version, Note = NotesToHtml(a.Notes) })
                .ToList();
        }

        /// <summary>
        /// Release notes are plain text, lines starting with "-" or "*" are list items.
        /// </summary>
        public static string NotesToHtml(string notes)
        {
            if (notes.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var html = new StringBuilder();
            var inList = false;
            foreach (var rawLine in notes.Replace("\r", string.Empty).Split('\n'))
            {
                var line = rawLine.Trim();
                var isItem = line.StartsWith("- ") || line.StartsWith("* ");
                if (isItem && !inList)
                {
                    html.Append("<ul>");
                    inList = true;
                }
                else if (!isItem && inList)
                {
                    html.Append("</ul>");
                    inList = false;
                }

                if (isItem)
                {
                    html.Append("<li>").Append(WebUtility.HtmlEncode(line.Substring(2).Trim())).Append("</li>");
                }
                else if (!line.IsNullOrEmpty())
                {
                    html.Append("<p>").Append(WebUtility.HtmlEncode(line)).Append("</p>");
                }
            }

            if (inList)
            {
                html.Append("</ul>");
            }

            return html.ToString();
        }

        /// <summary>
        /// GameroomUpdater.exe downloads the release, so there's nothing to do here.
        /// </summary>
        public Task DownloadUpdate(Action<DownloadProgressChangedEventArgs> progressHandler)
        {
            return Task.CompletedTask;
        }

        public void InstallUpdate(ApplicationMode mode)
        {
            var latest = GetLatestVersion();
            var args = $"--install --version {latest} --wait-pid {Process.GetCurrentProcess().Id}";
            if (mode == ApplicationMode.Fullscreen)
            {
                args += " --fullscreen";
            }

            logger.Info($"Starting update to Gameroom {latest}");
            playniteApp.QuitAndStart(UpdaterExecutablePath, args);
        }
    }
}
