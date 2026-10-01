using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class MinecraftAuthenticator
    {
        static MinecraftAuthenticator()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        }

        // ========== 硬编码配置 ==========
        private const string DEFAULT_CLIENT_ID = "04bc9a34-3d65-4526-9201-28bca0c4bef7";// 此启动器申请的Client_Id:{04bc9a34-3d65-4526-9201-28bca0c4bef7}
        private static readonly string[] DEFAULT_SCOPES = new[] { "XboxLive.signin", "offline_access" };

        // 网络请求超时（毫秒）。微软/Xbox/Minecraft 服务偶尔抽风，必须有超时。
        private const int HTTP_TIMEOUT_MS = 15000;

        private readonly string _clientId;
        private readonly string[] _scopes;
        private readonly string _clientToken;

        public Action<string, string> OnUserCodeReceived { get; set; }

        public MinecraftAuthenticator() : this(DEFAULT_CLIENT_ID, DEFAULT_SCOPES) { }

        public MinecraftAuthenticator(string clientId, string[] scopes = null)
        {
            if (string.IsNullOrEmpty(clientId))
                throw new ArgumentNullException(nameof(clientId));

            _clientId = clientId;
            _scopes = scopes ?? DEFAULT_SCOPES;
            _clientToken = Guid.NewGuid().ToString("N");
        }

        // ========== 公开方法 ==========

        public string AuthenticateAndSave()
        {
            return AuthenticateAndSave(null);
        }

        public string AuthenticateAndSave(string filePath = null)
        {
            DeviceAuthResult azureResult = GetAzureTokenByDeviceCode();

            try
            {
                MinecraftAuthResult mcResult = ExchangeToMinecraft(azureResult.access_token);

                string targetPath = filePath;
                if (string.IsNullOrEmpty(targetPath))
                    targetPath = GetDefaultAccountPath(mcResult.Uuid, mcResult.Username);

                SaveAccount(targetPath, mcResult, azureResult.refresh_token);
                return targetPath;
            }
            catch (Exception ex) when (
                ex.Message.Contains("XSTS") ||
                ex.Message.Contains("minecraftservices") ||
                ex.Message.Contains("403") ||
                ex.Message.Contains("401"))
            {
                return CacheAzureRefreshTokenOnly(azureResult.refresh_token);
            }
        }

        public string CompleteLoginFromCache(string cacheFilePath)
        {
            if (!File.Exists(cacheFilePath))
                throw new FileNotFoundException("缓存文件不存在", cacheFilePath);

            string json = SecureStorage.ReadAllTextWithMigration(cacheFilePath);
            if (string.IsNullOrEmpty(json))
                throw new Exception("缓存文件无法解密或已损坏，请重新登录。");
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            string refreshToken = data["refreshToken"].ToString();

            RefreshAzureToken(refreshToken, out string newAccessToken, out string newRefreshToken);

            MinecraftAuthResult mcResult = ExchangeToMinecraft(newAccessToken);

            string userPath = GetDefaultAccountPath(mcResult.Uuid, mcResult.Username);
            SaveAccount(userPath, mcResult, newRefreshToken);

            try { File.Delete(cacheFilePath); } catch { }

            return userPath;
        }

        public string CacheAzureCredentialsOnly()
        {
            DeviceAuthResult azureResult = GetAzureTokenByDeviceCode();

            string cacheDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Launcher Setting", "AzureCache");
            Directory.CreateDirectory(cacheDir);
            string filePath = Path.Combine(cacheDir, "pending_refresh_token.cache");

            var cacheObj = new
            {
                refreshToken = azureResult.refresh_token,
                createdUtc = DateTime.UtcNow.ToString("o"),
                status = "pending_mojang_approval"
            };

            string json = new JavaScriptSerializer().Serialize(cacheObj);
            SecureStorage.WriteAllTextEncrypted(filePath, json);
            return filePath;
        }

        // ========== 私有辅助 ==========

        private string CacheAzureRefreshTokenOnly(string refreshToken)
        {
            string cacheDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Launcher Setting", "AzureCache");
            Directory.CreateDirectory(cacheDir);
            string filePath = Path.Combine(cacheDir, "pending_refresh_token.cache");

            var cacheObj = new
            {
                refreshToken = refreshToken,
                createdUtc = DateTime.UtcNow.ToString("o"),
                status = "pending_mojang_approval"
            };

            string json = new JavaScriptSerializer().Serialize(cacheObj);
            SecureStorage.WriteAllTextEncrypted(filePath, json);
            return filePath;
        }

        private string GetDefaultAccountPath(string uuid, string username)
        {
            string folder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Launcher Setting", "Roles", "Microsoft");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, uuid + ".json");
        }

        public void RefreshAzureToken(string refreshToken,
                                      out string accessToken,
                                      out string newRefreshToken)
        {
            const string tokenUrl =
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
            string postData = "client_id=" + Uri.EscapeDataString(_clientId) +
                              "&refresh_token=" + Uri.EscapeDataString(refreshToken) +
                              "&grant_type=refresh_token" +
                              "&scope=" + Uri.EscapeDataString(string.Join(" ", _scopes));

            string json = PostForm(tokenUrl, postData);
            var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            if (result.ContainsKey("error"))
                throw new Exception("刷新失败: " + result["error"] + " - " + result["error_description"]);

            accessToken = result["access_token"].ToString();

            // ★ 微软 OAuth 有时不返回新的 refresh_token（scope 未变时会复用旧的），
            //   必须容错，否则 result["refresh_token"] 会抛 KeyNotFoundException。
            newRefreshToken = result.ContainsKey("refresh_token")
                ? result["refresh_token"].ToString()
                : refreshToken;
        }

        private DeviceAuthResult GetAzureTokenByDeviceCode()
        {
            const string deviceCodeUrl =
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
            const string tokenUrl =
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

            string deviceData = "client_id=" + Uri.EscapeDataString(_clientId) +
                                "&scope=" + Uri.EscapeDataString(string.Join(" ", _scopes));

            string deviceJson;
            try { deviceJson = PostForm(deviceCodeUrl, deviceData); }
            catch (Exception ex) { throw new Exception($"设备码请求失败: {ex.Message}", ex); }

            var deviceInfo = new JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(deviceJson);

            string deviceCode = deviceInfo["device_code"].ToString();
            string userCode = deviceInfo["user_code"].ToString();
            string verificationUri = deviceInfo["verification_uri"].ToString();
            int interval = Convert.ToInt32(deviceInfo["interval"]);
            int expiresIn = Convert.ToInt32(deviceInfo["expires_in"]);

            try { System.Diagnostics.Process.Start(verificationUri); } catch { }

            if (OnUserCodeReceived != null)
                OnUserCodeReceived(userCode, verificationUri);

            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalSeconds < expiresIn)
            {
                string tokenData = "client_id=" + Uri.EscapeDataString(_clientId) +
                                   "&device_code=" + Uri.EscapeDataString(deviceCode) +
                                   "&grant_type=urn:ietf:params:oauth:grant-type:device_code";

                try
                {
                    string tokenJson = PostForm(tokenUrl, tokenData);
                    var result = new JavaScriptSerializer().Deserialize<DeviceAuthResult>(tokenJson);
                    if (!string.IsNullOrEmpty(result.access_token))
                        return result;
                }
                catch (Exception ex)
                {
                    int statusCode = 0;
                    var webEx = ex.InnerException as WebException;
                    if (webEx?.Response is HttpWebResponse response)
                        statusCode = (int)response.StatusCode;
                    else
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(
                            ex.Message, @"HTTP\s+(\d+)");
                        if (match.Success) statusCode = int.Parse(match.Groups[1].Value);
                    }

                    if (statusCode == 400 && ex.Message.Contains("authorization_pending"))
                    {
                        System.Threading.Thread.Sleep(interval * 1000);
                        continue;
                    }
                    else if (statusCode == 429)
                    {
                        interval += 5;
                        System.Threading.Thread.Sleep(interval * 1000);
                        continue;
                    }
                    else throw;
                }
            }
            throw new TimeoutException("用户登录超时。");
        }

        private MinecraftAuthResult ExchangeToMinecraft(string azureAccessToken)
        {
            string xblToken = GetXboxLiveToken(azureAccessToken, out string xuid);
            string xstsToken = GetXSTSToken(xblToken);
            string mcToken = GetMinecraftToken(xstsToken);
            var profile = GetMinecraftProfile(mcToken);
            profile.Xuid = xuid;
            return profile;
        }

        private string GetXboxLiveToken(string azureToken, out string xuid)
        {
            xuid = null;
            var payload = new Dictionary<string, object>();
            var properties = new Dictionary<string, object>();
            properties.Add("AuthMethod", "RPS");
            properties.Add("SiteName", "user.auth.xboxlive.com");
            properties.Add("RpsTicket", "d=" + azureToken);
            payload.Add("Properties", properties);
            payload.Add("RelyingParty", "http://auth.xboxlive.com");
            payload.Add("TokenType", "JWT");

            string json = PostJson("https://user.auth.xboxlive.com/user/authenticate", payload);
            var result = new JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(json);

            if (result.ContainsKey("DisplayClaims"))
            {
                var displayClaims = result["DisplayClaims"] as Dictionary<string, object>;
                if (displayClaims != null && displayClaims.ContainsKey("xui"))
                {
                    var xuiArray = displayClaims["xui"] as object[];
                    if (xuiArray != null && xuiArray.Length > 0)
                    {
                        var xui = xuiArray[0] as Dictionary<string, object>;
                        if (xui != null && xui.ContainsKey("xid"))
                            xuid = xui["xid"].ToString();
                    }
                }
            }
            return result["Token"].ToString();
        }

        private string GetXSTSToken(string xblToken)
        {
            var payload = new Dictionary<string, object>();
            var properties = new Dictionary<string, object>();
            properties.Add("SandboxId", "RETAIL");
            properties.Add("UserTokens", new string[] { xblToken });
            payload.Add("Properties", properties);
            payload.Add("RelyingParty", "rp://api.minecraftservices.com/");
            payload.Add("TokenType", "JWT");

            string json = PostJson("https://xsts.auth.xboxlive.com/xsts/authorize", payload);
            var result = new JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(json);

            if (result.ContainsKey("XErr"))
            {
                string err = result["XErr"].ToString();
                if (err == "2148916233") throw new Exception("儿童账户需家长授权。");
                if (err == "2148916235") throw new Exception("该地区不被支持。");
                throw new Exception("XSTS错误: " + err);
            }
            return result["Token"].ToString();
        }

        private string GetMinecraftToken(string xstsToken)
        {
            var payload = new Dictionary<string, object>();
            payload.Add("identityToken", "XBL3.0 x=" + xstsToken);

            string json = PostJson(
                "https://api.minecraftservices.com/authentication/login_with_xbox", payload);
            var result = new JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(json);
            return result["access_token"].ToString();
        }

        private MinecraftAuthResult GetMinecraftProfile(string mcToken)
        {
            var request = (HttpWebRequest)WebRequest.Create(
                "https://api.minecraftservices.com/minecraft/profile");
            request.Method = "GET";
            request.Headers["Authorization"] = "Bearer " + mcToken;
            request.Timeout = HTTP_TIMEOUT_MS;
            request.ReadWriteTimeout = HTTP_TIMEOUT_MS;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                string json = reader.ReadToEnd();
                var data = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(json);
                return new MinecraftAuthResult
                {
                    AccessToken = mcToken,
                    Uuid = data["id"].ToString(),
                    Username = data["name"].ToString()
                };
            }
        }

        private void SaveAccount(string filePath, MinecraftAuthResult mcResult, string refreshToken)
        {
            var accountObj = new
            {
                accessToken = mcResult.AccessToken,
                refreshToken = refreshToken,
                clientToken = _clientToken,
                uuid = mcResult.Uuid,
                username = mcResult.Username,
                xuid = mcResult.Xuid,
                type = "Microsoft",              // ★ 让 RoleManager 能识别
                userProperties = new { }
            };
            string json = new JavaScriptSerializer().Serialize(accountObj);
            SecureStorage.WriteAllTextEncrypted(filePath, json);
        }

        // ----- HTTP 辅助 -----
        private string PostForm(string url, string formData)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.UserAgent = "MinecraftLauncher/1.0";
            request.Timeout = HTTP_TIMEOUT_MS;
            request.ReadWriteTimeout = HTTP_TIMEOUT_MS;
            byte[] data = Encoding.UTF8.GetBytes(formData);
            request.ContentLength = data.Length;
            try
            {
                using (var stream = request.GetRequestStream())
                    stream.Write(data, 0, data.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                int statusCode = 0;
                string responseBody = null;
                if (ex.Response != null)
                {
                    statusCode = (int)((HttpWebResponse)ex.Response).StatusCode;
                    try
                    {
                        using (var stream = ex.Response.GetResponseStream())
                            if (stream != null && stream.CanRead)
                                using (var reader = new StreamReader(stream))
                                    responseBody = reader.ReadToEnd();
                    }
                    catch { }
                }
                throw new Exception($"POST请求失败 (URL: {url}, HTTP {statusCode})" +
                                    (responseBody != null ? ": " + responseBody : ""), ex);
            }
        }

        private string PostJson(string url, object payload)
        {
            string jsonData = new JavaScriptSerializer().Serialize(payload);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.UserAgent = "MinecraftLauncher/1.0";
            request.Timeout = HTTP_TIMEOUT_MS;
            request.ReadWriteTimeout = HTTP_TIMEOUT_MS;
            byte[] data = Encoding.UTF8.GetBytes(jsonData);
            request.ContentLength = data.Length;
            try
            {
                using (var stream = request.GetRequestStream())
                    stream.Write(data, 0, data.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                int statusCode = 0;
                string responseBody = null;
                if (ex.Response != null)
                {
                    statusCode = (int)((HttpWebResponse)ex.Response).StatusCode;
                    try
                    {
                        using (var stream = ex.Response.GetResponseStream())
                            if (stream != null && stream.CanRead)
                                using (var reader = new StreamReader(stream))
                                    responseBody = reader.ReadToEnd();
                    }
                    catch { }
                }
                throw new Exception($"POST请求失败 (URL: {url}, HTTP {statusCode})" +
                                    (responseBody != null ? ": " + responseBody : ""), ex);
            }
        }

        // ----- 内部类 -----
        private class DeviceAuthResult
        {
            public string access_token { get; set; }
            public string refresh_token { get; set; }
            public string id_token { get; set; }
            public int expires_in { get; set; }
            public string scope { get; set; }
            public string token_type { get; set; }
        }

        private class MinecraftAuthResult
        {
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
            public string Uuid { get; set; }
            public string Username { get; set; }
            public string Xuid { get; set; }
        }
    }
}