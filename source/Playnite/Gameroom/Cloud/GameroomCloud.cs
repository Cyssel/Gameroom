using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.Gameroom.Updates;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Gameroom.Cloud
{
    public class CloudSession
    {
        /// <summary>Server the session belongs to.</summary>
        public string Server { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string UserId { get; set; }
        public string Email { get; set; }
    }

    public class CloudProfile
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("total_playtime_seconds")]
        public long TotalPlaytimeSeconds { get; set; }

        [JsonProperty("games_count")]
        public int GamesCount { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    public class CloudSave
    {
        [JsonProperty("id")]
        public Guid Id { get; set; }

        [JsonProperty("game_key")]
        public string GameKey { get; set; }

        [JsonProperty("game_name")]
        public string GameName { get; set; }

        [JsonProperty("steam_app_id")]
        public string SteamAppId { get; set; }

        [JsonProperty("object_path")]
        public string ObjectPath { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("file_count")]
        public int FileCount { get; set; }

        [JsonProperty("content_hash")]
        public string ContentHash { get; set; }

        [JsonProperty("trigger")]
        public string Trigger { get; set; }

        [JsonProperty("machine")]
        public string Machine { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    public class CloudResult
    {
        public bool Success { get; set; }
        /// <summary>Account was created but the e-mail address has to be confirmed before signing in.</summary>
        public bool NeedsConfirmation { get; set; }
        public string Error { get; set; }

        public static CloudResult Ok() => new CloudResult { Success = true };
        public static CloudResult Fail(string error) => new CloudResult { Error = error };
    }

    public class CloudException : Exception
    {
        public string Code { get; }
        public HttpStatusCode Status { get; }

        public CloudException(HttpStatusCode status, string code, string message) : base(message)
        {
            Status = status;
            Code = code;
        }
    }

    /// <summary>
    /// Gameroom accounts and cloud storage on the user's own Supabase project (see docs/SUPABASE.md).
    /// Nothing is sent anywhere until a server is configured. The session is stored encrypted for the current
    /// Windows user, so it survives restarts.
    /// </summary>
    public class GameroomCloud : IDisposable
    {
        public const string SavesBucket = "saves";
        private static readonly ILogger logger = LogManager.GetLogger();
        private HttpClient http;
        private readonly string sessionFile;
        private readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);
        private CloudSession session;

        public event EventHandler SessionChanged;

        /// <summary>A server is set up, see <see cref="Configure"/>.</summary>
        public bool IsConfigured => http != null;
        private HttpClient Client => http ?? throw new CloudException(0, "not_configured", "No cloud server is configured.");
        public string ServerUrl { get; private set; }
        public bool IsSignedIn => session != null && IsConfigured;
        public string Email => session?.Email;
        public string UserId => session?.UserId;

        static GameroomCloud()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public GameroomCloud(string dataDir, string serverUrl, string publishableKey)
        {
            sessionFile = Path.Combine(dataDir, "cloud-session.dat");
            session = LoadSession();
            Configure(serverUrl, publishableKey);
        }

        public void Dispose()
        {
            http?.Dispose();
        }

        private static HttpClient CreateClient(string serverUrl, string publishableKey)
        {
            var client = new HttpClient { BaseAddress = new Uri(serverUrl + "/"), Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.Add("apikey", publishableKey.Trim());
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Gameroom");
            return client;
        }

        /// <summary>
        /// Project address from what the user pasted: "https://abcd.supabase.co", also with a path like "/rest/v1".
        /// Null when it isn't a web address. Plain http is only accepted for a local Supabase.
        /// </summary>
        public static string NormalizeServerUrl(string url)
        {
            if (url.IsNullOrWhiteSpace() || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                return null;
            }

            var local = uri.IsLoopback;
            if (uri.Scheme != Uri.UriSchemeHttps && !(local && uri.Scheme == Uri.UriSchemeHttp))
            {
                return null;
            }

            return uri.GetLeftPart(UriPartial.Authority);
        }

        /// <summary>
        /// The secret (service role) key gives full access to the project, it must never be stored in the app.
        /// </summary>
        public static bool IsSecretKey(string key)
        {
            if (key.IsNullOrWhiteSpace())
            {
                return false;
            }

            key = key.Trim();
            if (key.StartsWith("sb_secret_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Legacy keys are JWTs whose payload names the role.
            var parts = key.Split('.');
            if (parts.Length == 3)
            {
                try
                {
                    var payload = parts[1].Replace('-', '+').Replace('_', '/');
                    payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                    return Encoding.UTF8.GetString(Convert.FromBase64String(payload)).Contains("\"service_role\"");
                }
                catch
                {
                }
            }

            return false;
        }

        /// <summary>
        /// Uses the given Supabase project. Signs out when it's another project than the one of the session.
        /// An empty address or key turns the cloud off.
        /// </summary>
        public void Configure(string serverUrl, string publishableKey)
        {
            var url = NormalizeServerUrl(serverUrl);
            var old = http;
            http = url == null || publishableKey.IsNullOrWhiteSpace() || IsSecretKey(publishableKey) ? null : CreateClient(url, publishableKey);
            ServerUrl = http == null ? null : url;
            old?.Dispose();
            if (session != null && !string.Equals(session.Server, ServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                SetSession(null);
            }
            else
            {
                SessionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Checks that a project answers with the key and has Gameroom's tables (supabase/setup.sql).
        /// </summary>
        public static async Task<CloudResult> TestServerAsync(string serverUrl, string publishableKey)
        {
            var url = NormalizeServerUrl(serverUrl);
            if (url == null)
            {
                return CloudResult.Fail(ResourceProvider.GetString("LOCGameroomCloudServerInvalidUrl"));
            }

            if (IsSecretKey(publishableKey))
            {
                return CloudResult.Fail(ResourceProvider.GetString("LOCGameroomCloudServerSecretKey"));
            }

            if (publishableKey.IsNullOrWhiteSpace())
            {
                return CloudResult.Fail(ResourceProvider.GetString("LOCGameroomCloudServerInvalidKey"));
            }

            try
            {
                using (var client = CreateClient(url, publishableKey))
                {
                    client.Timeout = TimeSpan.FromSeconds(20);
                    using (var response = await client.GetAsync("auth/v1/settings"))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            return CloudResult.Fail(ResourceProvider.GetString("LOCGameroomCloudServerInvalidKey"));
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            return CloudResult.Fail(ResourceProvider.GetString("LOCGameroomCloudServerInvalidUrl"));
                        }
                    }

                    // Without a session row level security returns nothing, but a missing table is an error.
                    using (var response = await client.GetAsync("rest/v1/cloud_saves?select=id&limit=1"))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return CloudResult.Fail(ResourceProvider.GetString("LOCGameroomCloudServerNotReady"));
                        }
                    }
                }

                return CloudResult.Ok();
            }
            catch (Exception e)
            {
                return CloudResult.Fail(DescribeError(e));
            }
        }

        #region Session

        private CloudSession LoadSession()
        {
            try
            {
                if (!File.Exists(sessionFile))
                {
                    return null;
                }

                var data = ProtectedData.Unprotect(File.ReadAllBytes(sessionFile), null, DataProtectionScope.CurrentUser);
                return JsonConvert.DeserializeObject<CloudSession>(Encoding.UTF8.GetString(data));
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to load cloud session.");
                return null;
            }
        }

        private void SetSession(CloudSession newSession)
        {
            session = newSession;
            try
            {
                if (newSession == null)
                {
                    if (File.Exists(sessionFile))
                    {
                        File.Delete(sessionFile);
                    }
                }
                else
                {
                    var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(newSession));
                    File.WriteAllBytes(sessionFile, ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to store cloud session.");
            }

            SessionChanged?.Invoke(this, EventArgs.Empty);
        }

        private CloudSession ParseSession(JObject json)
        {
            var user = json["user"];
            return new CloudSession
            {
                Server = ServerUrl,
                AccessToken = json["access_token"]?.ToString(),
                RefreshToken = json["refresh_token"]?.ToString(),
                ExpiresAt = DateTime.UtcNow.AddSeconds(json["expires_in"]?.Value<int>() ?? 3600),
                UserId = user?["id"]?.ToString(),
                Email = user?["email"]?.ToString()
            };
        }

        public async Task<CloudResult> SignUpAsync(string email, string password, string username)
        {
            try
            {
                var body = new JObject
                {
                    ["email"] = email.Trim(),
                    ["password"] = password,
                    ["data"] = new JObject { ["username"] = username.Trim() }
                };

                var json = await SendAsync(HttpMethod.Post, "auth/v1/signup", body, authorized: false);
                if (json["access_token"] != null)
                {
                    SetSession(ParseSession(json));
                    return CloudResult.Ok();
                }

                // E-mail confirmation is enabled for the project.
                return new CloudResult { Success = true, NeedsConfirmation = true };
            }
            catch (Exception e)
            {
                return CloudResult.Fail(DescribeError(e));
            }
        }

        public async Task<CloudResult> SignInAsync(string email, string password)
        {
            try
            {
                var body = new JObject { ["email"] = email.Trim(), ["password"] = password };
                var json = await SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=password", body, authorized: false);
                SetSession(ParseSession(json));
                return CloudResult.Ok();
            }
            catch (Exception e)
            {
                return CloudResult.Fail(DescribeError(e));
            }
        }

        public async Task SignOutAsync()
        {
            try
            {
                if (session != null)
                {
                    await SendAsync(HttpMethod.Post, "auth/v1/logout", null);
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to sign out on the server.");
            }

            SetSession(null);
        }

        private async Task EnsureFreshSessionAsync()
        {
            if (session == null || session.ExpiresAt > DateTime.UtcNow.AddMinutes(1))
            {
                return;
            }

            await refreshLock.WaitAsync();
            try
            {
                if (session == null || session.ExpiresAt > DateTime.UtcNow.AddMinutes(1))
                {
                    return;
                }

                var body = new JObject { ["refresh_token"] = session.RefreshToken };
                try
                {
                    var json = await SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=refresh_token", body, authorized: false);
                    SetSession(ParseSession(json));
                }
                catch (CloudException e) when (e.Status == HttpStatusCode.BadRequest || e.Status == HttpStatusCode.Unauthorized)
                {
                    // Refresh token was revoked or expired, the user has to sign in again.
                    logger.Warn($"Cloud session expired: {e.Message}");
                    SetSession(null);
                    throw;
                }
            }
            finally
            {
                refreshLock.Release();
            }
        }

        #endregion Session

        #region Requests

        private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, bool authorized)
        {
            if (!IsConfigured)
            {
                throw new CloudException(0, "not_configured", "No cloud server is configured.");
            }

            if (authorized)
            {
                await EnsureFreshSessionAsync();
                if (session == null)
                {
                    throw new CloudException(HttpStatusCode.Unauthorized, "not_signed_in", "Not signed in.");
                }
            }

            var request = new HttpRequestMessage(method, path);
            if (authorized)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            }

            return request;
        }

        private async Task<JObject> SendAsync(HttpMethod method, string path, JToken body, bool authorized = true, string prefer = null)
        {
            var text = await SendRawAsync(method, path, body, authorized, prefer);
            if (text.IsNullOrWhiteSpace())
            {
                return new JObject();
            }

            var token = JToken.Parse(text);
            return token as JObject ?? new JObject { ["items"] = token };
        }

        private async Task<string> SendRawAsync(HttpMethod method, string path, JToken body, bool authorized = true, string prefer = null)
        {
            using (var request = await CreateRequestAsync(method, path, authorized))
            {
                if (body != null)
                {
                    request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                }

                if (prefer != null)
                {
                    request.Headers.Add("Prefer", prefer);
                }

                using (var response = await Client.SendAsync(request))
                {
                    var text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw ToException(response.StatusCode, text);
                    }

                    return text;
                }
            }
        }

        private static CloudException ToException(HttpStatusCode status, string text)
        {
            string code = null;
            string message = text;
            try
            {
                var json = JObject.Parse(text);
                code = json["error_code"]?.ToString() ?? json["code"]?.ToString() ?? json["error"]?.ToString();
                message = json["msg"]?.ToString() ?? json["message"]?.ToString() ?? json["error_description"]?.ToString() ?? text;
            }
            catch
            {
            }

            return new CloudException(status, code, message);
        }

        /// <summary>
        /// User facing description of a failed request.
        /// </summary>
        public static string DescribeError(Exception e)
        {
            if (e is CloudException cloud)
            {
                switch (cloud.Code)
                {
                    case "invalid_credentials":
                        return ResourceProvider.GetString("LOCGameroomCloudErrorCredentials");
                    case "email_not_confirmed":
                        return ResourceProvider.GetString("LOCGameroomCloudErrorNotConfirmed");
                    case "user_already_exists":
                    case "email_exists":
                        return ResourceProvider.GetString("LOCGameroomCloudErrorExists");
                    case "weak_password":
                        return ResourceProvider.GetString("LOCGameroomCloudErrorWeakPassword");
                    case "email_address_invalid":
                    case "validation_failed":
                        return ResourceProvider.GetString("LOCGameroomCloudErrorInvalidEmail");
                    case "over_email_send_rate_limit":
                    case "over_request_rate_limit":
                        return ResourceProvider.GetString("LOCGameroomCloudErrorRateLimit");
                    case "email_address_not_authorized":
                        return ResourceProvider.GetString("LOCGameroomCloudErrorEmailNotAuthorized");
                    case "not_configured":
                        return ResourceProvider.GetString("LOCGameroomCloudNotConfigured");
                }

                return cloud.Message;
            }

            if (e is HttpRequestException || e is TaskCanceledException || e is WebException)
            {
                return ResourceProvider.GetString("LOCGameroomCloudErrorConnection");
            }

            return e.Message;
        }

        #endregion Requests

        #region Profile

        public async Task<CloudProfile> GetProfileAsync()
        {
            var text = await SendRawAsync(HttpMethod.Get, $"rest/v1/profiles?id=eq.{session?.UserId}&select=*", null);
            return JsonConvert.DeserializeObject<List<CloudProfile>>(text).FirstOrDefault();
        }

        public async Task UpdateProfileStatsAsync(long totalPlaytime, int gamesCount)
        {
            var body = new JObject
            {
                ["total_playtime_seconds"] = totalPlaytime,
                ["games_count"] = gamesCount,
                ["updated_at"] = DateTime.UtcNow.ToString("o")
            };

            await SendRawAsync(new HttpMethod("PATCH"), $"rest/v1/profiles?id=eq.{session?.UserId}", body);
        }

        public async Task<CloudResult> RenameAsync(string username)
        {
            try
            {
                await SendRawAsync(new HttpMethod("PATCH"), $"rest/v1/profiles?id=eq.{session?.UserId}", new JObject { ["username"] = username.Trim() });
                return CloudResult.Ok();
            }
            catch (CloudException e) when (e.Code == "23505")
            {
                return CloudResult.Fail(ResourceProvider.GetString("LOCGameroomCloudErrorUsernameTaken"));
            }
            catch (Exception e)
            {
                return CloudResult.Fail(DescribeError(e));
            }
        }

        #endregion Profile

        #region Saves

        public async Task<List<CloudSave>> GetSavesAsync()
        {
            var text = await SendRawAsync(HttpMethod.Get, "rest/v1/cloud_saves?select=*&order=created_at.desc", null);
            return JsonConvert.DeserializeObject<List<CloudSave>>(text);
        }

        public async Task<CloudSave> UploadSaveAsync(string archivePath, CloudSave save)
        {
            save.ObjectPath = $"{session?.UserId}/{save.ObjectPath}";
            using (var request = await CreateRequestAsync(HttpMethod.Post, $"storage/v1/object/{SavesBucket}/{EscapePath(save.ObjectPath)}", true))
            using (var stream = File.OpenRead(archivePath))
            {
                request.Headers.Add("x-upsert", "true");
                request.Content = new StreamContent(stream);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                using (var response = await Client.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw ToException(response.StatusCode, await response.Content.ReadAsStringAsync());
                    }
                }
            }

            var row = JObject.FromObject(save);
            row.Remove("id");
            var text = await SendRawAsync(HttpMethod.Post, "rest/v1/cloud_saves?on_conflict=user_id,object_path",
                new JArray(row), prefer: "resolution=merge-duplicates,return=representation");
            return JsonConvert.DeserializeObject<List<CloudSave>>(text).FirstOrDefault() ?? save;
        }

        public async Task DownloadSaveAsync(CloudSave save, string targetPath)
        {
            using (var request = await CreateRequestAsync(HttpMethod.Get, $"storage/v1/object/authenticated/{SavesBucket}/{EscapePath(save.ObjectPath)}", true))
            using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw ToException(response.StatusCode, await response.Content.ReadAsStringAsync());
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                var tempPath = targetPath + ".part";
                using (var target = File.Create(tempPath))
                {
                    await response.Content.CopyToAsync(target);
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(tempPath, targetPath);
            }
        }

        public async Task DeleteSaveAsync(CloudSave save)
        {
            var body = new JObject { ["prefixes"] = new JArray(save.ObjectPath) };
            await SendRawAsync(HttpMethod.Delete, $"storage/v1/object/{SavesBucket}", body);
            await SendRawAsync(HttpMethod.Delete, $"rest/v1/cloud_saves?id=eq.{save.Id}", null);
        }

        private static string EscapePath(string path)
        {
            return string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        }

        #endregion Saves
    }
}
