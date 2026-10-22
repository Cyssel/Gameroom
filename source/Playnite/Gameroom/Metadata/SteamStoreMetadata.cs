using Newtonsoft.Json.Linq;
using Playnite.Database;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace Playnite.Gameroom
{
    public class SteamStoreGame
    {
        public string AppId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> Developers { get; set; } = new List<string>();
        public List<string> Publishers { get; set; } = new List<string>();
        public List<string> Genres { get; set; } = new List<string>();
        public ReleaseDate? ReleaseDate { get; set; }
        public string HeaderImage { get; set; }
        public string Background { get; set; }
        public string Website { get; set; }
        /// <summary>Vertical library cover, from the store browse API.</summary>
        public string LibraryCover { get; set; }
        /// <summary>Wide library background, from the store browse API.</summary>
        public string LibraryHero { get; set; }
    }

    /// <summary>
    /// Game information and artwork from the public Steam store API. Doesn't need any extension or API key,
    /// so detected games (also ones running through Steam emulators) get covers right away.
    /// </summary>
    public class SteamStoreMetadata
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string assetsHost = "https://shared.cloudflare.steamstatic.com/store_item_assets/";

        private static readonly Dictionary<string, string> steamLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "es", "spanish" }, { "pt_BR", "brazilian" }, { "pt", "portuguese" }, { "fr", "french" }, { "de", "german" },
            { "it", "italian" }, { "ru", "russian" }, { "ja", "japanese" }, { "ko", "koreana" }, { "pl", "polish" },
            { "zh_CN", "schinese" }, { "zh_TW", "tchinese" }, { "tr", "turkish" }, { "uk", "ukrainian" }, { "nl", "dutch" }
        };

        static SteamStoreMetadata()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        internal static string Download(string url)
        {
            using (var client = new WebClient { Encoding = Encoding.UTF8 })
            {
                client.Headers.Add(HttpRequestHeader.UserAgent, "Gameroom");
                return client.DownloadString(url);
            }
        }

        public static string GetSteamLanguage(string playniteLanguage)
        {
            if (playniteLanguage.IsNullOrWhiteSpace())
            {
                return "english";
            }

            if (steamLanguages.TryGetValue(playniteLanguage, out var exact))
            {
                return exact;
            }

            var prefix = playniteLanguage.Split('_')[0];
            return steamLanguages.TryGetValue(prefix, out var language) ? language : "english";
        }

        /// <summary>
        /// Finds the Steam app of a game by name. Only close matches are accepted.
        /// </summary>
        public string FindAppId(string gameName)
        {
            var query = SavePaths.NormalizeName(gameName);
            if (query.Length < 3)
            {
                return null;
            }

            var json = JObject.Parse(Download($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(gameName)}&l=english&cc=US"));
            var items = (json["items"] as JArray)?.Select(a => new { Id = a["id"]?.ToString(), Name = a["name"]?.ToString() }).ToList();
            if (items == null || items.Count == 0)
            {
                return null;
            }

            var exact = items.FirstOrDefault(a => SavePaths.NormalizeName(a.Name) == query);
            if (exact != null)
            {
                return exact.Id;
            }

            // "Game" vs "Game: Definitive Edition" and similar.
            var close = items.FirstOrDefault(a =>
            {
                var name = SavePaths.NormalizeName(a.Name);
                var shorter = Math.Min(name.Length, query.Length);
                var longer = Math.Max(name.Length, query.Length);
                return shorter > 0 && (name.StartsWith(query) || query.StartsWith(name)) && shorter >= longer * 0.6;
            });

            return close?.Id;
        }

        public SteamStoreGame GetGame(string appId, string language)
        {
            var english = GetDetails(appId, "english");
            if (english == null)
            {
                return null;
            }

            var game = new SteamStoreGame
            {
                AppId = appId,
                Name = english["name"]?.ToString(),
                Developers = english["developers"]?.Select(a => a.ToString()).ToList() ?? new List<string>(),
                Publishers = english["publishers"]?.Select(a => a.ToString()).ToList() ?? new List<string>(),
                HeaderImage = english["header_image"]?.ToString(),
                Background = english["background_raw"]?.ToString() ?? english["background"]?.ToString(),
                Website = english["website"]?.ToString(),
                ReleaseDate = ParseReleaseDate(english["release_date"]?["date"]?.ToString())
            };

            // Texts in the user's language when Steam has them.
            var localized = language == "english" ? english : GetDetails(appId, language) ?? english;
            game.Description = localized["about_the_game"]?.ToString();
            if (game.Description.IsNullOrWhiteSpace())
            {
                game.Description = localized["short_description"]?.ToString();
            }

            game.Genres = localized["genres"]?.Select(a => a["description"]?.ToString()).Where(a => !a.IsNullOrWhiteSpace()).ToList() ?? new List<string>();
            try
            {
                LoadLibraryAssets(game);
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to get library artwork of Steam app {appId}");
            }

            return game;
        }

        /// <summary>
        /// Wide library artwork of the app, null when Steam has none.
        /// </summary>
        public static string GetLibraryHeroUrl(string appId)
        {
            var game = new SteamStoreGame { AppId = appId };
            LoadLibraryAssets(game);
            return game.LibraryHero;
        }

        /// <summary>
        /// Library artwork paths contain a content hash on newer games, the store browse API provides them.
        /// </summary>
        private static void LoadLibraryAssets(SteamStoreGame game)
        {
            var input = "{\"ids\":[{\"appid\":" + game.AppId + "}],\"context\":{\"language\":\"english\",\"country_code\":\"US\"},\"data_request\":{\"include_assets\":true}}";
            var json = JObject.Parse(Download("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input)));
            var assets = json["response"]?["store_items"]?.FirstOrDefault()?["assets"];
            var format = assets?["asset_url_format"]?.ToString();
            if (format.IsNullOrWhiteSpace())
            {
                return;
            }

            string url(string key)
            {
                var file = assets[key]?.ToString();
                return file.IsNullOrWhiteSpace() ? null : assetsHost + format.Replace("${FILENAME}", file);
            }

            game.LibraryCover = url("library_capsule_2x") ?? url("library_capsule");
            game.LibraryHero = url("library_hero") ?? url("raw_page_background");
        }

        private static JToken GetDetails(string appId, string language)
        {
            var json = JObject.Parse(Download($"https://store.steampowered.com/api/appdetails?appids={appId}&l={language}"));
            var app = json[appId];
            return app?["success"]?.Value<bool>() == true ? app["data"] : null;
        }

        public static ReleaseDate? ParseReleaseDate(string date)
        {
            if (date.IsNullOrWhiteSpace())
            {
                return null;
            }

            date = date.Trim();
            var dayFormats = new[] { "d MMM, yyyy", "MMM d, yyyy", "d MMMM, yyyy", "MMMM d, yyyy", "d MMM yyyy" };
            if (DateTime.TryParseExact(date, dayFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return new ReleaseDate(day);
            }

            if (DateTime.TryParseExact(date, new[] { "MMM yyyy", "MMMM yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
            {
                return new ReleaseDate(month.Year, month.Month);
            }

            // "Q1 2026", "Coming 2026"...
            var year = System.Text.RegularExpressions.Regex.Match(date, @"\b(19|20)\d{2}\b");
            return year.Success ? new ReleaseDate(int.Parse(year.Value)) : (ReleaseDate?)null;
        }

        private static bool UrlExists(string url)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";
                request.UserAgent = "Gameroom";
                request.Timeout = 10000;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        public static string GetCoverUrl(SteamStoreGame game)
        {
            if (!game.LibraryCover.IsNullOrEmpty() && UrlExists(game.LibraryCover))
            {
                return game.LibraryCover;
            }

            // Older games without the store browse data still have covers on the classic path.
            var classic = $"{assetsHost}steam/apps/{game.AppId}/library_600x900_2x.jpg";
            return UrlExists(classic) ? classic : game.HeaderImage;
        }

        public static string GetBackgroundUrl(SteamStoreGame game)
        {
            if (!game.LibraryHero.IsNullOrEmpty() && UrlExists(game.LibraryHero))
            {
                return game.LibraryHero;
            }

            return game.Background;
        }

        /// <summary>
        /// Fills missing information and artwork of a game. Existing data is never replaced, except a name that
        /// only differs from Steam's in punctuation (detected names come from folder names).
        /// </summary>
        public bool Apply(IGameDatabaseMain database, GameroomDatabase gameroomDb, Game game, CancellationToken cancelToken)
        {
            var data = gameroomDb.GetOrCreateGameData(game.Id);
            var appId = SaveLocationResolver.GetSteamAppId(game, data) ?? FindAppId(game.Name);
            data.SteamMetadataFetched = true;
            if (appId.IsNullOrWhiteSpace())
            {
                gameroomDb.SaveGameData(data);
                logger.Info($"No Steam store entry found for {game.Name}");
                return false;
            }

            var steam = GetGame(appId, GetSteamLanguage(Localization.CurrentLanguage));
            if (steam == null)
            {
                gameroomDb.SaveGameData(data);
                return false;
            }

            data.SteamAppId = appId;
            gameroomDb.SaveGameData(data);
            if (!steam.Name.IsNullOrWhiteSpace() && SavePaths.NormalizeName(steam.Name) == SavePaths.NormalizeName(game.Name))
            {
                game.Name = steam.Name;
            }

            if (game.Description.IsNullOrWhiteSpace() && !steam.Description.IsNullOrWhiteSpace())
            {
                game.Description = steam.Description;
            }

            if (!game.DeveloperIds.HasItems() && steam.Developers.Count > 0)
            {
                game.DeveloperIds = database.Companies.Add(steam.Developers).Select(a => a.Id).ToList();
            }

            if (!game.PublisherIds.HasItems() && steam.Publishers.Count > 0)
            {
                game.PublisherIds = database.Companies.Add(steam.Publishers).Select(a => a.Id).ToList();
            }

            if (!game.GenreIds.HasItems() && steam.Genres.Count > 0)
            {
                game.GenreIds = database.Genres.Add(steam.Genres).Select(a => a.Id).ToList();
            }

            if (!game.PlatformIds.HasItems())
            {
                game.PlatformIds = new List<Guid> { database.Platforms.Add(new MetadataSpecProperty("pc_windows")).Id };
            }

            if (game.ReleaseDate == null && steam.ReleaseDate != null)
            {
                game.ReleaseDate = steam.ReleaseDate;
            }

            var storeUrl = $"https://store.steampowered.com/app/{appId}/";
            if (game.Links == null)
            {
                game.Links = new ObservableCollection<Link>();
            }

            if (!game.Links.Any(a => a.Url == storeUrl))
            {
                game.Links.Add(new Link("Steam", storeUrl));
            }

            if (!steam.Website.IsNullOrWhiteSpace() && !game.Links.Any(a => a.Url == steam.Website))
            {
                game.Links.Add(new Link(ResourceProvider.GetString("LOCWebsiteLabel"), steam.Website));
            }

            if (game.CoverImage.IsNullOrEmpty())
            {
                var cover = GetCoverUrl(steam);
                if (!cover.IsNullOrEmpty())
                {
                    game.CoverImage = database.AddFile(cover, game.Id, true, cancelToken);
                }
            }

            if (game.BackgroundImage.IsNullOrEmpty())
            {
                var background = GetBackgroundUrl(steam);
                if (!background.IsNullOrEmpty())
                {
                    game.BackgroundImage = database.AddFile(background, game.Id, true, cancelToken);
                }
            }

            if (game.Icon.IsNullOrEmpty())
            {
                var exe = data.KnownExecutables.LastOrDefault(File.Exists) ?? GetPlayActionExecutable(game);
                if (exe != null)
                {
                    game.Icon = database.AddFile(exe, game.Id, true, cancelToken);
                }
            }

            database.Games.Update(game);
            logger.Info($"Applied Steam store data of app {appId} to {game.Name}");
            return true;
        }

        private static string GetPlayActionExecutable(Game game)
        {
            var action = game.GameActions?.FirstOrDefault(a => a.IsPlayAction && a.Type == GameActionType.File && !a.Path.IsNullOrWhiteSpace());
            if (action == null)
            {
                return null;
            }

            var path = game.ExpandVariables(action.Path, true);
            return Path.IsPathRooted(path) && File.Exists(path) ? path : null;
        }
    }
}
