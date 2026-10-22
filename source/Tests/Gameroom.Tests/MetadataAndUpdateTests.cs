using NUnit.Framework;
using Playnite;
using Playnite.Gameroom;
using Playnite.SDK.Models;
using System.Linq;

namespace Gameroom.Tests
{
    [TestFixture]
    public class MetadataAndUpdateTests
    {
        [Test]
        public void ParsesSteamReleaseDatesTest()
        {
            Assert.AreEqual(new ReleaseDate(2025, 12, 3), SteamStoreMetadata.ParseReleaseDate("3 Dec, 2025"));
            Assert.AreEqual(new ReleaseDate(2025, 12, 3), SteamStoreMetadata.ParseReleaseDate("Dec 3, 2025"));
            Assert.AreEqual(new ReleaseDate(2026, 3), SteamStoreMetadata.ParseReleaseDate("Mar 2026"));
            Assert.AreEqual(new ReleaseDate(2026), SteamStoreMetadata.ParseReleaseDate("Q2 2026"));
            Assert.IsNull(SteamStoreMetadata.ParseReleaseDate("Coming soon"));
            Assert.IsNull(SteamStoreMetadata.ParseReleaseDate(null));
        }

        [Test]
        public void MapsSteamLanguagesTest()
        {
            Assert.AreEqual("spanish", SteamStoreMetadata.GetSteamLanguage("es_ES"));
            Assert.AreEqual("brazilian", SteamStoreMetadata.GetSteamLanguage("pt_BR"));
            Assert.AreEqual("schinese", SteamStoreMetadata.GetSteamLanguage("zh_CN"));
            Assert.AreEqual("english", SteamStoreMetadata.GetSteamLanguage("en_US"));
            Assert.AreEqual("english", SteamStoreMetadata.GetSteamLanguage(null));
        }

        [Test]
        public void ParsesReleasesTest()
        {
            // Shape of https://api.github.com/repos/<owner>/<repo>/releases, trimmed.
            var json = @"[
                {""tag_name"":""v1.0.9"",""body"":""- Fix"",""draft"":false,""prerelease"":false,""published_at"":""2026-10-20T10:00:00Z"",""assets"":[
                    {""name"":""GameroomSetup-1.0.9.exe"",""size"":10,""digest"":""sha256:ab"",""browser_download_url"":""https://github.com/o/r/releases/download/v1.0.9/GameroomSetup-1.0.9.exe""}]},
                {""tag_name"":""v1.0.10"",""body"":null,""draft"":false,""prerelease"":false,""published_at"":""2026-10-21T10:00:00Z"",""assets"":[
                    {""name"":""notes.txt"",""size"":1,""digest"":null,""browser_download_url"":""https://github.com/o/r/releases/download/v1.0.10/notes.txt""},
                    {""name"":""GameroomSetup-1.0.10.exe"",""size"":100,""digest"":null,""browser_download_url"":""https://github.com/o/r/releases/download/v1.0.10/GameroomSetup-1.0.10.exe""}]},
                {""tag_name"":""v1.1.0-beta"",""body"":"""",""draft"":false,""prerelease"":true,""published_at"":""2026-10-22T10:00:00Z"",""assets"":[
                    {""name"":""GameroomSetup-1.1.0.exe"",""size"":100,""browser_download_url"":""https://github.com/o/r/releases/download/v1.1.0-beta/GameroomSetup-1.1.0.exe""}]},
                {""tag_name"":""v2.0.0"",""body"":"""",""draft"":true,""prerelease"":false,""assets"":[]}
            ]";

            var releases = Playnite.Gameroom.Updates.GameroomReleaseClient.ParseReleases(json);
            // Versions are compared as numbers, 1.0.10 is newer than 1.0.9. Drafts and pre-releases aren't offered.
            CollectionAssert.AreEqual(new[] { "1.0.10", "1.0.9" }, releases.Select(a => a.VersionString));
            Assert.AreEqual(100, releases[0].TotalSize, "Only the installer is downloaded.");
            Assert.AreEqual("GameroomSetup-1.0.10.exe", releases[0].SetupFile);
            Assert.IsNull(releases[0].Files[0].Sha256);
            Assert.AreEqual("ab", releases[1].Files[0].Sha256);
            Assert.AreEqual("- Fix", releases[1].Notes);
            Assert.AreEqual("https://github.com/o/r/releases/download/v1.0.10/GameroomSetup-1.0.10.exe",
                Playnite.Gameroom.Updates.GameroomReleaseClient.GetFileUrl(releases[0], releases[0].Files[0]));

            var beta = Playnite.Gameroom.Updates.GameroomReleaseClient.ParseReleases(json, Playnite.Gameroom.Updates.ReleaseChannel.BetaChannel);
            Assert.AreEqual("1.1.0", beta[0].VersionString, "The beta channel gets pre-releases too.");
        }

        [Test]
        public void ConvertsReleaseNotesToHtmlTest()
        {
            var html = Updater.NotesToHtml("New features:\n- Live playtime\n- Saves <backed up> while playing\n\nThanks!");
            Assert.AreEqual("<p>New features:</p><ul><li>Live playtime</li><li>Saves &lt;backed up&gt; while playing</li></ul><p>Thanks!</p>", html);
            Assert.AreEqual(string.Empty, Updater.NotesToHtml(null));
        }
    }
}
