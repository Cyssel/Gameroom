using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Playnite.Gameroom.Achievements
{
    /// <summary>
    /// Reads achievement unlocks stored locally by the Steam client and by Steam emulators.
    /// </summary>
    public static class AchievementReaders
    {
        // Sections of emulator .ini files that aren't achievements.
        private static readonly HashSet<string> ignoredIniSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SteamAchievements", "Stats", "Steam"
        };

        private static readonly Regex steamStatsFileRegex = new Regex(@"^UserGameStats_(\d+)_(\d+)\.bin$", RegexOptions.IgnoreCase);

        private static string PublicDocuments => Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "Documents");
        private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        /// <summary>
        /// Folders where emulators keep achievements of all games, one subfolder per Steam app id.
        /// </summary>
        public static List<string> GetEmulatorRoots()
        {
            return new List<string>
            {
                Path.Combine(PublicDocuments, "Steam"),
                Path.Combine(AppData, "Steam", "CODEX"),
                Path.Combine(AppData, "Goldberg SteamEmu Saves"),
                Path.Combine(AppData, "GSE Saves"),
                Path.Combine(PublicDocuments, "EMPRESS"),
                Path.Combine(AppData, "EMPRESS"),
                Path.Combine(PublicDocuments, "OnlineFix")
            };
        }

        public static List<AchievementSourceFile> GetEmulatorFiles(string appId)
        {
            return new List<AchievementSourceFile>
            {
                new AchievementSourceFile(Path.Combine(PublicDocuments, "Steam", "RUNE", appId, "achievements.ini"), AchievementSource.Rune),
                new AchievementSourceFile(Path.Combine(PublicDocuments, "Steam", "CODEX", appId, "achievements.ini"), AchievementSource.Codex),
                new AchievementSourceFile(Path.Combine(AppData, "Steam", "CODEX", appId, "achievements.ini"), AchievementSource.Codex),
                new AchievementSourceFile(Path.Combine(AppData, "Goldberg SteamEmu Saves", appId, "achievements.json"), AchievementSource.Goldberg),
                new AchievementSourceFile(Path.Combine(AppData, "GSE Saves", appId, "achievements.json"), AchievementSource.Goldberg),
                new AchievementSourceFile(Path.Combine(PublicDocuments, "EMPRESS", appId, "remote", appId, "achievements.json"), AchievementSource.Empress),
                new AchievementSourceFile(Path.Combine(AppData, "EMPRESS", appId, "remote", appId, "achievements.json"), AchievementSource.Empress),
                new AchievementSourceFile(Path.Combine(PublicDocuments, "OnlineFix", appId, "Stats", "Achievements.ini"), AchievementSource.OnlineFix)
            };
        }

        public static bool IsAchievementFile(string path)
        {
            var name = Path.GetFileName(path);
            return name.Equals("achievements.ini", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("achievements.json", StringComparison.OrdinalIgnoreCase);
        }

        public static AchievementSource GetSource(string path)
        {
            if (path.IndexOf(@"\RUNE\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AchievementSource.Rune;
            }

            if (path.IndexOf(@"\CODEX\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AchievementSource.Codex;
            }

            if (path.IndexOf(@"\EMPRESS\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AchievementSource.Empress;
            }

            if (path.IndexOf(@"\OnlineFix\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AchievementSource.OnlineFix;
            }

            return AchievementSource.Goldberg;
        }

        /// <summary>
        /// App id of an emulator achievement file: the first numeric folder below the root.
        /// </summary>
        public static string GetAppId(string root, string path)
        {
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return path.Substring(root.Length)
                .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(a => a.Length >= 2 && a.All(char.IsDigit));
        }

        public static Dictionary<string, AchievementProgress> ReadEmulatorFile(string path)
        {
            var text = ReadShared(path);
            return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? ParseGoldbergJson(text) : ParseIni(text);
        }

        /// <summary>
        /// Reads a file that the game may be writing at the same time.
        /// </summary>
        public static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        public static byte[] ReadSharedBytes(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        public static DateTime? FromUnixTime(long? value)
        {
            if (value == null || value <= 0)
            {
                return null;
            }

            // Some emulators store milliseconds.
            var seconds = value.Value > 100_000_000_000 ? value.Value / 1000 : value.Value;
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static long? ParseLong(string value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : (long?)null;
        }

        private static bool ParseBool(string value)
        {
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// achievements.ini of CODEX, RUNE and OnlineFix: one section per achievement.
        /// </summary>
        public static Dictionary<string, AchievementProgress> ParseIni(string text)
        {
            var result = new Dictionary<string, AchievementProgress>(StringComparer.Ordinal);
            AchievementProgress current = null;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                {
                    continue;
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    var name = line.Substring(1, line.Length - 2).Trim();
                    if (name.Length == 0 || ignoredIniSections.Contains(name))
                    {
                        current = null;
                    }
                    else if (!result.TryGetValue(name, out current))
                    {
                        current = new AchievementProgress { ApiName = name };
                        result.Add(name, current);
                    }

                    continue;
                }

                var separator = line.IndexOf('=');
                if (current == null || separator <= 0)
                {
                    continue;
                }

                var value = line.Substring(separator + 1).Trim();
                switch (line.Substring(0, separator).Trim().ToLowerInvariant())
                {
                    case "achieved":
                    case "unlocked":
                        current.Unlocked = ParseBool(value);
                        break;
                    case "unlocktime":
                    case "timestamp":
                    case "time":
                        current.UnlockTime = FromUnixTime(ParseLong(value));
                        break;
                    case "curprogress":
                        current.CurrentProgress = (int)(ParseLong(value) ?? 0);
                        break;
                    case "maxprogress":
                        current.MaxProgress = (int)(ParseLong(value) ?? 0);
                        break;
                }
            }

            foreach (var achievement in result.Values.Where(a => !a.Unlocked))
            {
                achievement.UnlockTime = null;
            }

            return result;
        }

        /// <summary>
        /// achievements.json of Goldberg, GSE and EMPRESS: {"NAME": {"earned": true, "earned_time": 0}}, or a list of those.
        /// </summary>
        public static Dictionary<string, AchievementProgress> ParseGoldbergJson(string text)
        {
            var result = new Dictionary<string, AchievementProgress>(StringComparer.Ordinal);
            var token = JToken.Parse(text);
            IEnumerable<(string Name, JToken Data)> entries;
            if (token is JObject obj)
            {
                entries = obj.Properties().Select(a => (a.Name, a.Value));
            }
            else if (token is JArray array)
            {
                entries = array.OfType<JObject>().Select(a => (a["name"]?.ToString(), (JToken)a));
            }
            else
            {
                return result;
            }

            foreach (var (name, data) in entries)
            {
                if (name.IsNullOrWhiteSpace() || !(data is JObject values))
                {
                    continue;
                }

                var earned = values["earned"] ?? values["achieved"] ?? values["unlocked"];
                var unlocked = earned != null && (earned.Type == JTokenType.Boolean ? earned.Value<bool>() : ParseBool(earned.ToString()));
                var time = values["earned_time"] ?? values["unlock_time"] ?? values["time"];
                result[name] = new AchievementProgress
                {
                    ApiName = name,
                    Unlocked = unlocked,
                    UnlockTime = unlocked ? FromUnixTime(ParseLong(time?.ToString())) : null,
                    CurrentProgress = (int)(ParseLong(values["progress"]?.ToString()) ?? 0),
                    MaxProgress = (int)(ParseLong(values["max_progress"]?.ToString()) ?? 0)
                };
            }

            return result;
        }

        #region Steam client

        /// <summary>
        /// Steam\appcache\stats, where the Steam client caches achievements of the games played on this PC.
        /// </summary>
        public static string FindSteamStatsDirectory()
        {
            var steamPath = FindSteamPath();
            var stats = steamPath == null ? null : Path.Combine(steamPath, "appcache", "stats");
            return stats != null && Directory.Exists(stats) ? stats : null;
        }

        /// <summary>
        /// Folder of the Steam client, null when it isn't installed.
        /// </summary>
        public static string FindSteamPath()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var steamPath = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
                    return steamPath.IsNullOrWhiteSpace() || !Directory.Exists(steamPath) ? null : steamPath;
                }
            }
            catch
            {
                return null;
            }
        }

        public static string GetAppIdFromSteamStatsFile(string path)
        {
            var match = steamStatsFileRegex.Match(Path.GetFileName(path));
            return match.Success ? match.Groups[2].Value : null;
        }

        /// <summary>
        /// Stats file of the Steam account that played the game most recently.
        /// </summary>
        public static string FindSteamUserStatsFile(string statsDirectory, string appId)
        {
            if (statsDirectory == null || !Directory.Exists(statsDirectory))
            {
                return null;
            }

            return new DirectoryInfo(statsDirectory).EnumerateFiles($"UserGameStats_*_{appId}.bin")
                .Where(a => GetAppIdFromSteamStatsFile(a.Name) == appId)
                .OrderByDescending(a => a.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }

        public static string GetSteamSchemaFile(string statsDirectory, string appId)
        {
            return Path.Combine(statsDirectory, $"UserGameStatsSchema_{appId}.bin");
        }

        private static IEnumerable<KeyValue> GetAchievementStats(KeyValue schema)
        {
            var stats = schema.Children.Select(a => a["stats"]).FirstOrDefault(a => a != null);
            if (stats == null)
            {
                yield break;
            }

            foreach (var stat in stats.Children)
            {
                var type = stat["type"];
                var typeName = type?.AsString();
                var isAchievements = type?.AsLong() == 4 ||
                    string.Equals(typeName, "ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeName, "4", StringComparison.Ordinal);
                if (isAchievements && stat["bits"] != null)
                {
                    yield return stat;
                }
            }
        }

        private static IEnumerable<(KeyValue Stat, int Bit, KeyValue Node)> GetAchievementBits(KeyValue schema)
        {
            foreach (var stat in GetAchievementStats(schema))
            {
                foreach (var node in stat["bits"].Children)
                {
                    var bit = int.TryParse(node.Name, out var parsed) ? parsed : (int)(node["bit"]?.AsLong() ?? -1);
                    if (bit >= 0 && bit < 64 && !node["name"]?.AsString().IsNullOrWhiteSpace() == true)
                    {
                        yield return (stat, bit, node);
                    }
                }
            }
        }

        /// <summary>
        /// Unlocks in UserGameStats_[account]_[app].bin. Achievements are bits of stats defined in the schema file.
        /// </summary>
        public static Dictionary<string, AchievementProgress> ParseSteamStats(KeyValue stats, KeyValue schema)
        {
            var result = new Dictionary<string, AchievementProgress>(StringComparer.Ordinal);
            var cache = stats["cache"] ?? stats;
            foreach (var (stat, bit, node) in GetAchievementBits(schema))
            {
                var userStat = cache[stat.Name];
                var data = userStat?["data"]?.AsLong() ?? 0;
                var unlocked = (data & (1L << bit)) != 0;
                var apiName = node["name"].AsString();
                result[apiName] = new AchievementProgress
                {
                    ApiName = apiName,
                    Unlocked = unlocked,
                    UnlockTime = unlocked ? FromUnixTime(userStat?["AchievementTimes"]?[bit.ToString(CultureInfo.InvariantCulture)]?.AsLong()) : null
                };
            }

            return result;
        }

        /// <summary>
        /// Achievement definitions from the Steam schema file, used when the Steam web API can't be reached.
        /// </summary>
        public static List<AchievementDefinition> ParseSteamSchema(KeyValue schema, string language)
        {
            string localized(KeyValue node)
            {
                if (node == null)
                {
                    return null;
                }

                if (node.Children.Count == 0)
                {
                    return node.AsString();
                }

                return (node[language] ?? node["english"] ?? node.Children.FirstOrDefault(a => a.Name != "token"))?.AsString();
            }

            return GetAchievementBits(schema).Select(a =>
            {
                var display = a.Node["display"];
                var hidden = display?["hidden"];
                return new AchievementDefinition
                {
                    ApiName = a.Node["name"].AsString(),
                    Name = localized(display?["name"]),
                    Description = localized(display?["desc"]),
                    Hidden = hidden?.AsLong() == 1 || hidden?.AsString() == "1",
                    Icon = display?["icon"]?.AsString(),
                    IconGray = display?["icon_gray"]?.AsString()
                };
            }).ToList();
        }

        #endregion Steam client
    }
}
