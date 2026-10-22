using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// This file is compiled into Playnite.dll and linked into GameroomUpdater.exe,
// so it must only use the .NET Framework base libraries.
namespace Playnite.Gameroom.Updates
{
    /// <summary>
    /// Where Gameroom releases are published: the releases of its GitHub repository. Forks publishing their own
    /// builds change <see cref="GitHubRepository"/>.
    /// </summary>
    public static class ReleaseChannel
    {
        public const string GitHubRepository = "Cyssel/Gameroom";
        public const string DefaultChannel = "stable";
        /// <summary>Also offers pre-releases.</summary>
        public const string BetaChannel = "beta";

        /// <summary>AppId of the Inno Setup installer (build/installer/Gameroom.iss).</summary>
        public const string InstallerAppId = "{3BDEDB77-2B61-494C-AC81-AA56250648D3}";
        public const string UpdaterExecutableName = "GameroomUpdater.exe";
        public const string DesktopExecutableName = "Gameroom.exe";
        public const string FullscreenExecutableName = "Gameroom.Fullscreen.exe";
    }

    [DataContract]
    public class ReleaseFile
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "size")]
        public long Size { get; set; }

        /// <summary>Checksum published with the file, null when the server doesn't provide one.</summary>
        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; }

        [DataMember(Name = "url")]
        public string Url { get; set; }
    }

    [DataContract]
    internal class GitHubRelease
    {
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }

        [DataMember(Name = "body")]
        public string Body { get; set; }

        [DataMember(Name = "draft")]
        public bool Draft { get; set; }

        [DataMember(Name = "prerelease")]
        public bool Prerelease { get; set; }

        [DataMember(Name = "published_at")]
        public string PublishedAt { get; set; }

        [DataMember(Name = "assets")]
        public List<GitHubAsset> Assets { get; set; }
    }

    [DataContract]
    internal class GitHubAsset
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "size")]
        public long Size { get; set; }

        /// <summary>"sha256:&lt;hex&gt;", computed by GitHub when the file is uploaded.</summary>
        [DataMember(Name = "digest")]
        public string Digest { get; set; }

        [DataMember(Name = "browser_download_url")]
        public string DownloadUrl { get; set; }
    }

    [DataContract]
    public class ReleaseInfo
    {
        [DataMember(Name = "version")]
        public string VersionString { get; set; }

        [DataMember(Name = "channel")]
        public string Channel { get; set; }

        [DataMember(Name = "notes")]
        public string Notes { get; set; }

        [DataMember(Name = "files")]
        public List<ReleaseFile> Files { get; set; } = new List<ReleaseFile>();

        [DataMember(Name = "setup_file")]
        public string SetupFile { get; set; }

        [DataMember(Name = "created_at")]
        public string CreatedAt { get; set; }

        public Version Version => Version.TryParse(VersionString, out var version) ? version : new Version(0, 0, 0);

        public long TotalSize => Files.Sum(a => a.Size);

        public override string ToString()
        {
            return VersionString;
        }
    }

    public class ReleaseDownloadProgress
    {
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public string CurrentFile { get; set; }
        public double Percent => TotalBytes > 0 ? DownloadedBytes * 100.0 / TotalBytes : 0;
    }

    /// <summary>
    /// Reads published releases and downloads their installer files.
    /// </summary>
    public class GameroomReleaseClient
    {
        static GameroomReleaseClient()
        {
            // .NET Framework 4.6 doesn't enable TLS 1.2 by default on every system.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private static WebClient CreateClient()
        {
            var client = new WebClient { Encoding = Encoding.UTF8 };
            // GitHub's API rejects requests without a user agent.
            client.Headers.Add(HttpRequestHeader.UserAgent, "Gameroom");
            client.Headers.Add(HttpRequestHeader.Accept, "application/vnd.github+json");
            return client;
        }

        /// <summary>
        /// Published releases of a channel, newest version first. The beta channel also gets pre-releases.
        /// </summary>
        public List<ReleaseInfo> GetReleases(string channel = ReleaseChannel.DefaultChannel)
        {
            var url = $"https://api.github.com/repos/{ReleaseChannel.GitHubRepository}/releases?per_page=30";
            using (var client = CreateClient())
            {
                return ParseReleases(client.DownloadString(url), channel);
            }
        }

        /// <summary>
        /// Releases from the GitHub releases API. Only releases with a Gameroom installer count.
        /// </summary>
        public static List<ReleaseInfo> ParseReleases(string json, string channel = ReleaseChannel.DefaultChannel)
        {
            List<GitHubRelease> releases;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                releases = (List<GitHubRelease>)new DataContractJsonSerializer(typeof(List<GitHubRelease>)).ReadObject(stream);
            }

            return releases
                .Where(a => !a.Draft && (!a.Prerelease || channel == ReleaseChannel.BetaChannel))
                .Select(ToRelease)
                .Where(a => a != null)
                .OrderByDescending(a => a.Version)
                .ToList();
        }

        private static ReleaseInfo ToRelease(GitHubRelease release)
        {
            // The installer is the only file an update needs.
            var setup = release.Assets?.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.StartsWith("GameroomSetup", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            // Tags are "v1.2.3", pre-releases may add a suffix like "v1.2.3-beta".
            var version = System.Text.RegularExpressions.Regex.Match(release.TagName ?? string.Empty, @"\d+(\.\d+){1,3}");
            if (setup == null || !version.Success)
            {
                return null;
            }

            var digest = setup.Digest ?? string.Empty;
            var info = new ReleaseInfo
            {
                VersionString = version.Value,
                Channel = release.Prerelease ? ReleaseChannel.BetaChannel : ReleaseChannel.DefaultChannel,
                Notes = release.Body,
                SetupFile = setup.Name,
                CreatedAt = release.PublishedAt
            };

            info.Files.Add(new ReleaseFile
            {
                Name = setup.Name,
                Size = setup.Size,
                Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7) : null,
                Url = setup.DownloadUrl
            });

            return info;
        }

        public ReleaseInfo GetLatest(string channel = ReleaseChannel.DefaultChannel)
        {
            return GetReleases(channel).FirstOrDefault();
        }

        public static string GetFileUrl(ReleaseInfo release, ReleaseFile file)
        {
            return file.Url;
        }

        private static bool ChecksumMatches(string path, ReleaseFile file)
        {
            return string.IsNullOrEmpty(file.Sha256) || string.Equals(GetSha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Downloads all files of a release to a folder, verifying their checksums.
        /// Files already downloaded with a matching checksum are reused. Returns path of the setup file.
        /// </summary>
        public string Download(ReleaseInfo release, string targetDir, Action<ReleaseDownloadProgress> progress, CancellationToken cancelToken)
        {
            Directory.CreateDirectory(targetDir);
            var state = new ReleaseDownloadProgress { TotalBytes = release.TotalSize };
            foreach (var file in release.Files)
            {
                cancelToken.ThrowIfCancellationRequested();
                state.CurrentFile = file.Name;
                var targetPath = Path.Combine(targetDir, Path.GetFileName(file.Name));
                if (File.Exists(targetPath) && !string.IsNullOrEmpty(file.Sha256) && ChecksumMatches(targetPath, file))
                {
                    state.DownloadedBytes += file.Size;
                    progress?.Invoke(state);
                    continue;
                }

                var tempPath = targetPath + ".part";
                var request = (HttpWebRequest)WebRequest.Create(GetFileUrl(release, file));
                request.UserAgent = "Gameroom";
                var fileStart = state.DownloadedBytes;
                using (var response = request.GetResponse())
                using (var source = response.GetResponseStream())
                using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancelToken.ThrowIfCancellationRequested();
                        target.Write(buffer, 0, read);
                        state.DownloadedBytes += read;
                        progress?.Invoke(state);
                    }
                }

                if (!ChecksumMatches(tempPath, file))
                {
                    File.Delete(tempPath);
                    throw new InvalidDataException($"Checksum of {file.Name} doesn't match the published one ({file.Sha256}).");
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(tempPath, targetPath);
                state.DownloadedBytes = fileStart + file.Size;
                progress?.Invoke(state);
            }

            return Path.Combine(targetDir, release.SetupFile);
        }

        public static string GetSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// Information about the installed copy of Gameroom, read from the installer's uninstall registration.
    /// </summary>
    public class GameroomInstallation
    {
        public string Directory { get; private set; }
        public Version Version { get; private set; }

        public string DesktopExecutable => Path.Combine(Directory, ReleaseChannel.DesktopExecutableName);
        public string FullscreenExecutable => Path.Combine(Directory, ReleaseChannel.FullscreenExecutableName);

        public static GameroomInstallation Find()
        {
            var keyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ReleaseChannel.InstallerAppId}_is1";
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var key = hive.OpenSubKey(keyPath))
                    {
                        var dir = key?.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, ReleaseChannel.DesktopExecutableName)))
                        {
                            continue;
                        }

                        Version.TryParse(key.GetValue("DisplayVersion") as string, out var version);
                        return new GameroomInstallation { Directory = dir.TrimEnd('\\'), Version = version ?? new Version(0, 0, 0) };
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        /// <summary>
        /// Running Gameroom processes. Copies started from other folders count too: all of them share
        /// the single instance mutex the installer checks before replacing files.
        /// </summary>
        public static List<Process> GetRunningProcesses()
        {
            return Process.GetProcessesByName("Gameroom").Concat(Process.GetProcessesByName("Gameroom.Fullscreen")).ToList();
        }
    }

    public static class GameroomVersion
    {
        private static Version current;

        /// <summary>
        /// Version of Gameroom (not of the Playnite base), from GameroomVersion in source/Directory.Build.props.
        /// </summary>
        public static Version Current
        {
            get
            {
                if (current == null)
                {
                    var value = typeof(GameroomVersion).Assembly
                        .GetCustomAttributes<AssemblyMetadataAttribute>()
                        .FirstOrDefault(a => a.Key == "GameroomVersion")?.Value;
                    current = Version.TryParse(value, out var version) ? version : new Version(0, 0, 0);
                }

                return current;
            }
        }
    }
}
