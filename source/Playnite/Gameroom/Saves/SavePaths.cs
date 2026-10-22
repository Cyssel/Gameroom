using Microsoft.Win32;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.Gameroom
{
    /// <summary>
    /// Save location path templates. Templates use environment variables (%APPDATA%...) and
    /// {InstallDir}, {SteamAppId} placeholders, so backups can be restored on another PC or after reinstalling.
    /// </summary>
    public static class SavePaths
    {
        public const string InstallDirToken = "{InstallDir}";
        public const string SteamAppIdToken = "{SteamAppId}";

        private static readonly string[] contractVariables = { "APPDATA", "LOCALAPPDATA", "PUBLIC", "PROGRAMDATA", "USERPROFILE" };

        public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static string LocalLow => Path.Combine(UserProfile, "AppData", "LocalLow");
        public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        public static string SavedGames => Path.Combine(UserProfile, "Saved Games");
        public static string PublicDocuments => Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);

        /// <summary>
        /// Expands a template to an absolute path. Returns null if a required placeholder can't be filled.
        /// </summary>
        public static string Expand(string template, string installDir, string steamAppId)
        {
            if (template.IsNullOrWhiteSpace())
            {
                return null;
            }

            var result = template;
            if (result.Contains(InstallDirToken))
            {
                if (installDir.IsNullOrWhiteSpace())
                {
                    return null;
                }

                result = result.Replace(InstallDirToken, installDir.TrimEnd('\\'));
            }

            if (result.Contains(SteamAppIdToken))
            {
                if (steamAppId.IsNullOrWhiteSpace())
                {
                    return null;
                }

                result = result.Replace(SteamAppIdToken, steamAppId);
            }

            result = Environment.ExpandEnvironmentVariables(result);
            return GameMatcher.NormalizePath(result);
        }

        /// <summary>
        /// Converts an absolute path to a portable template.
        /// </summary>
        public static string Contract(string path, string installDir)
        {
            var normalized = GameMatcher.NormalizePath(path);
            if (normalized == null)
            {
                return path;
            }

            var install = GameMatcher.NormalizePath(installDir);
            if (install != null && IsSubPathOrEqual(normalized, install))
            {
                return InstallDirToken + normalized.Substring(install.Length);
            }

            var candidates = contractVariables
                .Select(a => new { Name = a, Value = GameMatcher.NormalizePath(Environment.GetEnvironmentVariable(a)) })
                .Where(a => a.Value != null)
                .OrderByDescending(a => a.Value.Length);
            foreach (var candidate in candidates)
            {
                if (IsSubPathOrEqual(normalized, candidate.Value))
                {
                    return $"%{candidate.Name}%" + normalized.Substring(candidate.Value.Length);
                }
            }

            return normalized;
        }

        public static bool IsSubPathOrEqual(string path, string parent)
        {
            if (path == null || parent == null)
            {
                return false;
            }

            path = path.TrimEnd('\\');
            parent = parent.TrimEnd('\\');
            return path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Expands '*' path segments to existing directories.
        /// </summary>
        public static IEnumerable<string> ExpandWildcards(string path)
        {
            if (path == null)
            {
                yield break;
            }

            var index = path.IndexOf('*');
            if (index < 0)
            {
                yield return path;
                yield break;
            }

            var parent = path.Substring(0, path.LastIndexOf('\\', index));
            var rest = path.Substring(index);
            var segmentEnd = rest.IndexOf('\\');
            var pattern = segmentEnd < 0 ? rest : rest.Substring(0, segmentEnd);
            var remaining = segmentEnd < 0 ? string.Empty : rest.Substring(segmentEnd);
            string[] dirs;
            try
            {
                dirs = Directory.Exists(parent) ? Directory.GetDirectories(parent, pattern) : new string[0];
            }
            catch
            {
                dirs = new string[0];
            }

            foreach (var dir in dirs)
            {
                foreach (var expanded in ExpandWildcards(dir + remaining))
                {
                    yield return expanded;
                }
            }
        }

        public static string GetSteamPath()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var path = key?.GetValue("SteamPath")?.ToString();
                    return path.IsNullOrWhiteSpace() ? null : Path.GetFullPath(path);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Name normalized for loose comparisons: lowercase letters and digits only.
        /// </summary>
        public static string NormalizeName(string name)
        {
            if (name.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            return Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
        }

        /// <summary>
        /// Name usable as a folder name.
        /// </summary>
        public static string ToFolderName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((name ?? "Game").Select(a => invalid.Contains(a) ? '_' : a).ToArray()).Trim().TrimEnd('.');
            return clean.IsNullOrWhiteSpace() ? "Game" : clean;
        }
    }
}
