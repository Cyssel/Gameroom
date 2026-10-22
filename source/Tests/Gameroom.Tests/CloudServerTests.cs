using NUnit.Framework;
using Playnite.Gameroom.Cloud;
using System;
using System.Text;

namespace Gameroom.Tests
{
    [TestFixture]
    public class CloudServerTests
    {
        private static string Jwt(string payload)
        {
            string part(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return $"{part("{\"alg\":\"HS256\"}")}.{part(payload)}.signature";
        }

        [Test]
        public void NormalizesServerUrlsTest()
        {
            Assert.AreEqual("https://abcd1234.supabase.co", GameroomCloud.NormalizeServerUrl(" https://abcd1234.supabase.co/ "));
            Assert.AreEqual("https://abcd1234.supabase.co", GameroomCloud.NormalizeServerUrl("https://abcd1234.supabase.co/rest/v1/"), "Pasted API addresses keep only the project.");
            Assert.AreEqual("http://127.0.0.1:54321", GameroomCloud.NormalizeServerUrl("http://127.0.0.1:54321"), "Local Supabase.");
            Assert.IsNull(GameroomCloud.NormalizeServerUrl("http://abcd1234.supabase.co"), "Plain http only for a local server.");
            Assert.IsNull(GameroomCloud.NormalizeServerUrl("abcd1234"));
            Assert.IsNull(GameroomCloud.NormalizeServerUrl(""));
        }

        [Test]
        public void RejectsSecretKeysTest()
        {
            Assert.IsTrue(GameroomCloud.IsSecretKey("sb_secret_abc123"));
            Assert.IsTrue(GameroomCloud.IsSecretKey(Jwt("{\"role\":\"service_role\"}")), "Legacy service role key.");
            Assert.IsFalse(GameroomCloud.IsSecretKey(Jwt("{\"role\":\"anon\"}")), "Legacy anon key.");
            Assert.IsFalse(GameroomCloud.IsSecretKey("sb_publishable_abc123"));
            Assert.IsFalse(GameroomCloud.IsSecretKey(null));
        }

        [Test]
        public void StaysOffWithoutServerTest()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GameroomTests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                using (var cloud = new GameroomCloud(dir, null, null))
                {
                    Assert.IsFalse(cloud.IsConfigured);
                    Assert.IsFalse(cloud.IsSignedIn);
                    var result = cloud.SignInAsync("player@example.com", "password").Result;
                    Assert.IsFalse(result.Success, "Nothing is sent anywhere until a server is configured.");

                    cloud.Configure("https://abcd1234.supabase.co", "sb_secret_abc123");
                    Assert.IsFalse(cloud.IsConfigured, "A secret key is never used.");

                    cloud.Configure("https://abcd1234.supabase.co", "sb_publishable_abc123");
                    Assert.IsTrue(cloud.IsConfigured);
                    Assert.AreEqual("https://abcd1234.supabase.co", cloud.ServerUrl);
                }
            }
            finally
            {
                System.IO.Directory.Delete(dir, true);
            }
        }
    }
}
