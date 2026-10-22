using Newtonsoft.Json.Linq;
using Playnite.Database;
using Playnite.Gameroom.Achievements;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;

namespace Playnite.Gameroom.Media
{
    /// <summary>
    /// Official artwork for the start page and game pages: transparent logos and wide hero images from Steam,
    /// cached in the library folder.
    /// </summary>
    public class GameMediaService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string cdn = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/";
        // Games without artwork are checked again after a while, it may be added later.
        private static readonly TimeSpan missingRetry = TimeSpan.FromDays(7);
        private readonly IGameDatabaseMain database;
        private readonly Func<Game, string> getAppId;
        private readonly string mediaDir;
        private readonly ConcurrentDictionary<string, object> locks = new ConcurrentDictionary<string, object>();

        public GameMediaService(IGameDatabaseMain database, string libraryDir, Func<Game, string> getAppId)
        {
            this.database = database;
            this.getAppId = getAppId;
            mediaDir = Path.Combine(libraryDir, "gameroom-media");
        }

        /// <summary>
        /// Transparent logo of the game, null when there's none.
        /// </summary>
        public string GetLogoPath(Game game, bool allowDownload = true)
        {
            var appId = game == null ? null : getAppId(game);
            if (appId.IsNullOrWhiteSpace())
            {
                return null;
            }

            return GetAsset(appId, "logo.png", allowDownload, target =>
                CopyFromSteamCache(appId, "logo.png", target) ||
                Download(cdn + appId + "/logo.png", target) ||
                Download(GetAppInfoLogoUrl(appId), target));
        }

        /// <summary>
        /// Wide artwork of the game for full screen pages: the Steam library hero, made for that and without text,
        /// then the game's own background image.
        /// </summary>
        public string GetHeroPath(Game game, bool allowDownload = true)
        {
            if (game == null)
            {
                return null;
            }

            var appId = getAppId(game);
            if (!appId.IsNullOrWhiteSpace())
            {
                var hero = GetAsset(appId, "library_hero.jpg", allowDownload, target =>
                    CopyFromSteamCache(appId, "library_hero.jpg", target) ||
                    Download(cdn + appId + "/library_hero.jpg", target) ||
                    Download(SafeGet(() => SteamStoreMetadata.GetLibraryHeroUrl(appId)), target));
                if (hero != null)
                {
                    return hero;
                }
            }

            if (!game.BackgroundImage.IsNullOrWhiteSpace())
            {
                var background = game.BackgroundImage.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? game.BackgroundImage
                    : database.GetFullFilePath(game.BackgroundImage);
                if (background.StartsWith("http", StringComparison.OrdinalIgnoreCase) || File.Exists(background))
                {
                    return background;
                }
            }

            return game.CoverImage.IsNullOrWhiteSpace() ? null : database.GetFullFilePath(game.CoverImage);
        }

        private string GetAsset(string appId, string fileName, bool allowDownload, Func<string, bool> fetch)
        {
            var dir = Path.Combine(mediaDir, appId);
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path))
            {
                return path;
            }

            var missing = path + ".missing";
            if (!allowDownload || (File.Exists(missing) && DateTime.Now - File.GetLastWriteTime(missing) < missingRetry))
            {
                return null;
            }

            lock (locks.GetOrAdd(appId + fileName, _ => new object()))
            {
                if (File.Exists(path))
                {
                    return path;
                }

                try
                {
                    Directory.CreateDirectory(dir);
                    if (fetch(path))
                    {
                        File.Delete(missing);
                        return path;
                    }

                    File.WriteAllText(missing, string.Empty);
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to get {fileName} of Steam app {appId}");
                }

                return null;
            }
        }

        /// <summary>
        /// Artwork the Steam client already downloaded for games in the user's Steam library.
        /// </summary>
        private static bool CopyFromSteamCache(string appId, string fileName, string target)
        {
            var steamPath = AchievementReaders.FindSteamPath();
            if (steamPath == null)
            {
                return false;
            }

            var cache = Path.Combine(steamPath, "appcache", "librarycache");
            var found = new[]
            {
                // Older Steam clients keep flat files.
                Path.Combine(cache, $"{appId}_{fileName}"),
                Path.Combine(cache, appId, fileName)
            }.FirstOrDefault(File.Exists);

            var appDir = Path.Combine(cache, appId);
            if (found == null && Directory.Exists(appDir))
            {
                found = Directory.EnumerateFiles(appDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
            }

            if (found == null)
            {
                return false;
            }

            File.Copy(found, target, true);
            return true;
        }

        /// <summary>
        /// Newer games keep their logo under a content hash, published in the app info.
        /// </summary>
        private static string GetAppInfoLogoUrl(string appId)
        {
            return SafeGet(() =>
            {
                var json = JObject.Parse(SteamStoreMetadata.Download($"https://api.steamcmd.net/v1/info/{appId}"));
                var logo = json["data"]?[appId]?["common"]?["library_assets_full"]?["library_logo"]?["image"];
                var file = (logo?["english"] ?? (logo as JObject)?.Properties().FirstOrDefault()?.Value)?.ToString();
                return file.IsNullOrWhiteSpace() ? null : cdn + appId + "/" + file;
            });
        }

        private static string SafeGet(Func<string> get)
        {
            try
            {
                return get();
            }
            catch (Exception e)
            {
                logger.Debug(e, "Failed to look up Steam artwork.");
                return null;
            }
        }

        private static bool Download(string url, string target)
        {
            if (url.IsNullOrWhiteSpace())
            {
                return false;
            }

            var temp = target + ".download";
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add(HttpRequestHeader.UserAgent, "Gameroom");
                    client.DownloadFile(url, temp);
                }

                if (new FileInfo(temp).Length == 0)
                {
                    File.Delete(temp);
                    return false;
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(temp, target);
                return true;
            }
            catch (WebException)
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }

                return false;
            }
        }
    }
}
