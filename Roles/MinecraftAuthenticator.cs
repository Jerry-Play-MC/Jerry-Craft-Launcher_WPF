using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
using System.Collections;               // ★ IList / IDictionary 相关
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
        private const string DEFAULT_CLIENT_ID = "04bc9a34-3d65-4526-9201-28bca0c4bef7";
        private static readonly string[] DEFAULT_SCOPES = new[] { "XboxLive.signin", "offline_access" };

        // 网络请求超时（毫秒）
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
            Log("========== 开始认证流程 ==========");
            DeviceAuthResult azureResult = GetAzureTokenByDeviceCode();

            try
            {
                Log("[认证] 正在将 Azure Token 交换为 Minecraft Token...");
                MinecraftAuthResult mcResult = ExchangeToMinecraft(azureResult.access_token);

                string targetPath = filePath;
                if (string.IsNullOrEmpty(targetPath))
                    targetPath = GetDefaultAccountPath(mcResult.Uuid, mcResult.Username);

                SaveAccount(targetPath, mcResult, azureResult.refresh_token);
                Log("[认证] 成功！账号已保存至: " + targetPath);
                return targetPath;
            }
            catch (Exception ex) when (
                ex.Message.Contains("XSTS") ||
                ex.Message.Contains("minecraftservices") ||
                ex.Message.Contains("403") ||
                ex.Message.Contains("401"))
            {
                Log("[认证错误] 交换 Minecraft Token 失败: " + ex.Message);
                Log("[认证错误] 堆栈: " + ex.StackTrace);

                return CacheAzureRefreshTokenOnly(azureResult.refresh_token);
            }
        }

        public string CompleteLoginFromCache(string cacheFilePath)
        {
            Log("[缓存登录] 开始，缓存文件: " + cacheFilePath);
            if (!File.Exists(cacheFilePath))
                throw new FileNotFoundException(LanguageManager.Get("Auth.CacheMissing"), cacheFilePath);

            string json = SecureStorage.ReadAllTextWithMigration(cacheFilePath);
            if (string.IsNullOrEmpty(json))
                throw new Exception(LanguageManager.Get("Auth.CacheCorrupted"));
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            string refreshToken = data["refreshToken"].ToString();

            RefreshAzureToken(refreshToken, out string newAccessToken, out string newRefreshToken);

            MinecraftAuthResult mcResult = ExchangeToMinecraft(newAccessToken);

            string userPath = GetDefaultAccountPath(mcResult.Uuid, mcResult.Username);
            SaveAccount(userPath, mcResult, newRefreshToken);

            try { File.Delete(cacheFilePath); } catch { }

            Log("[缓存登录] 完成，账号路径: " + userPath);
            return userPath;
        }

        public string CacheAzureCredentialsOnly()
        {
            DeviceAuthResult azureResult = GetAzureTokenByDeviceCode();
            return SaveCache(azureResult.refresh_token);
        }

        // ========== 私有辅助 ==========

        // ★ 统一日志方法
        private static void Log(string message)
        {
            string logLine = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}", DateTime.Now, message);
            System.Diagnostics.Debug.WriteLine(logLine);
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Launcher Setting", "Logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "auth_log.txt"), logLine + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// ★ 从 JavaScriptSerializer 反序列化出来的 result 中，
        ///   提取 DisplayClaims.xui[0] 里指定字段（如 uhs、xid）的值。
        ///   关键点：JavaScriptSerializer 把 JSON 数组反序列化成 ArrayList，
        ///   它只实现 IList，不实现 object[]。所以必须用 IList / IDictionary 接口接收。
        /// </summary>
        private static string ExtractFromDisplayClaims(
            Dictionary<string, object> result, string key)
        {
            if (result == null || !result.ContainsKey("DisplayClaims"))
                return null;

            // 1. DisplayClaims → 用 IDictionary 接口接收
            var displayClaims = result["DisplayClaims"] as IDictionary<string, object>;
            if (displayClaims == null || !displayClaims.ContainsKey("xui"))
                return null;

            // 2. xui → JavaScriptSerializer 返回的是 ArrayList，用 IList 接口接收
            //    这行是修复的关键：as object[] 会返回 null，as IList 才能拿到
            var xuiList = displayClaims["xui"] as IList;
            if (xuiList == null || xuiList.Count == 0)
                return null;

            // 3. xui[0] → 用 IDictionary 接口接收
            var xui = xuiList[0] as IDictionary<string, object>;
            if (xui == null || !xui.ContainsKey(key))
                return null;

            var value = xui[key];
            return value == null ? null : value.ToString();
        }

        private string CacheAzureRefreshTokenOnly(string refreshToken)
        {
            return SaveCache(refreshToken);
        }

        /// <summary>
        /// 用现有的 Azure access_token 走一遍 Xbox Live -> XSTS -> Minecraft 交换，
        /// 返回新的 Minecraft 令牌和档案信息。供 App 层刷新过期令牌时调用。
        /// </summary>
        public MinecraftAuthResult RefreshToMinecraft(string azureAccessToken)
        {
            return ExchangeToMinecraft(azureAccessToken);
        }

        private string SaveCache(string refreshToken)
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
            Log("[缓存] 已保存待审核刷新令牌到: " + filePath);
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

        public void RefreshAzureToken(string refreshToken, out string accessToken, out string newRefreshToken)
        {
            const string tokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
            string postData = "client_id=" + Uri.EscapeDataString(_clientId) +
                              "&refresh_token=" + Uri.EscapeDataString(refreshToken) +
                              "&grant_type=refresh_token" +
                              "&scope=" + Uri.EscapeDataString(string.Join(" ", _scopes));

            Log("[刷新 Azure Token] 请求中...");
            string json = PostForm(tokenUrl, postData);
            var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            if (result.ContainsKey("error"))
            {
                throw new Exception(string.Format(LanguageManager.Get("Auth.RefreshFailed"),
                    result["error"], result["error_description"]));
            }

            accessToken = result["access_token"].ToString();
            newRefreshToken = result.ContainsKey("refresh_token")
                ? result["refresh_token"].ToString()
                : refreshToken;
            Log("[刷新 Azure Token] 成功");
        }

        private DeviceAuthResult GetAzureTokenByDeviceCode()
        {
            const string deviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
            const string tokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

            string deviceData = "client_id=" + Uri.EscapeDataString(_clientId) +
                                "&scope=" + Uri.EscapeDataString(string.Join(" ", _scopes));

            Log("[设备代码] 请求设备代码...");
            string deviceJson;
            try { deviceJson = PostForm(deviceCodeUrl, deviceData); }
            catch (Exception ex) { throw new Exception(string.Format(LanguageManager.Get("Auth.DeviceCodeFailed"), ex.Message), ex); }

            var deviceInfo = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(deviceJson);

            string deviceCode = deviceInfo["device_code"].ToString();
            string userCode = deviceInfo["user_code"].ToString();
            string verificationUri = deviceInfo["verification_uri"].ToString();
            int interval = Convert.ToInt32(deviceInfo["interval"]);
            int expiresIn = Convert.ToInt32(deviceInfo["expires_in"]);

            Log("[设备代码] 获取成功，UserCode: " + userCode);

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
                    {
                        Log("[设备代码] 用户已在浏览器完成授权，成功获取 Azure Token");
                        return result;
                    }
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
            throw new TimeoutException(LanguageManager.Get("Auth.Timeout"));
        }

        private MinecraftAuthResult ExchangeToMinecraft(string azureAccessToken)
        {
            Log("[交换] 开始 Xbox Live -> XSTS -> Minecraft 令牌交换");
            Log("[交换] Azure Token: " + azureAccessToken);

            string xblToken = GetXboxLiveToken(azureAccessToken, out string xuid);
            Log("[交换] XBL Token 获取成功, XUID: " + (xuid ?? "(无)"));

            string xstsToken = GetXSTSToken(xblToken, out string userHash);
            Log("[交换] XSTS Token 获取成功, UserHash: " + (userHash ?? "(无)"));

            string mcToken = GetMinecraftToken(xstsToken, userHash);
            Log("[交换] Minecraft Token 获取成功");

            var profile = GetMinecraftProfile(mcToken);
            profile.Xuid = xuid;
            Log("[交换] Minecraft 档案获取成功: " + profile.Username + " (" + profile.Uuid + ")");
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

            Log("[XBL] 请求 Xbox Live Token...");
            string json = PostJson("https://user.auth.xboxlive.com/user/authenticate", payload);
            var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);

            // ★ 用接口版辅助方法提取 XUID（XBL 响应里通常没有 xid，返回 null 也无妨）
            xuid = ExtractFromDisplayClaims(result, "xid");

            Log("[XBL] 响应成功, XUID: " + (xuid ?? "(无)"));
            return result["Token"].ToString();
        }

        private string GetXSTSToken(string xblToken, out string userHash)
        {
            userHash = null;
            var payload = new Dictionary<string, object>();
            var properties = new Dictionary<string, object>();
            properties.Add("SandboxId", "RETAIL");
            properties.Add("UserTokens", new string[] { xblToken });
            payload.Add("Properties", properties);
            payload.Add("RelyingParty", "rp://api.minecraftservices.com/");
            payload.Add("TokenType", "JWT");

            Log("[XSTS] 请求 XSTS Token，RelyingParty: rp://api.minecraftservices.com/");
            string json = PostJson("https://xsts.auth.xboxlive.com/xsts/authorize", payload);
            var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);

            if (result.ContainsKey("XErr"))
            {
                string err = result["XErr"].ToString();
                Log("[XSTS] 发生错误，XErr: " + err);
                if (err == "2148916233") throw new Exception(LanguageManager.Get("Auth.ChildAccount"));
                if (err == "2148916235") throw new Exception(LanguageManager.Get("Auth.RegionUnsupported"));
                throw new Exception(string.Format(LanguageManager.Get("Auth.XstsError"), err));
            }

            // ★ 用接口版辅助方法提取 UserHash（uhs）
            userHash = ExtractFromDisplayClaims(result, "uhs");

            if (string.IsNullOrEmpty(userHash))
                Log("[XSTS] 警告：未能解析到 UserHash！原始响应: " + json);

            Log("[XSTS] 响应成功，Token 已获取，UserHash: " + (userHash ?? "(无)"));
            return result["Token"].ToString();
        }

        private string GetMinecraftToken(string xstsToken, string userHash)
        {
            var payload = new Dictionary<string, object>();
            // ★ 正确格式：XBL3.0 x=<UserHash>;<XSTS_Token>
            payload.Add("identityToken", "XBL3.0 x=" + userHash + ";" + xstsToken);

            Log("[MC登录] 请求 Minecraft Token，UserHash: " + (userHash ?? "(无)"));
            string json = PostJson("https://api.minecraftservices.com/authentication/login_with_xbox", payload);
            var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);

            if (result.ContainsKey("access_token"))
            {
                Log("[MC登录] 响应成功，拿到 Minecraft AccessToken");
                return result["access_token"].ToString();
            }
            else
            {
                Log("[MC登录] 响应异常: " + json);
                throw new Exception("Minecraft 登录失败，未返回 access_token。响应: " + json);
            }
        }

        private MinecraftAuthResult GetMinecraftProfile(string mcToken)
        {
            Log("[档案] 请求 Minecraft 档案...");
            var request = (HttpWebRequest)WebRequest.Create("https://api.minecraftservices.com/minecraft/profile");
            request.Method = "GET";
            request.Headers["Authorization"] = "Bearer " + mcToken;
            request.Timeout = HTTP_TIMEOUT_MS;
            request.ReadWriteTimeout = HTTP_TIMEOUT_MS;

            Log("[HTTP GET] GET https://api.minecraftservices.com/minecraft/profile");
            Log("[HTTP GET] 请求头 Authorization: Bearer " + mcToken);

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                string json = reader.ReadToEnd();
                Log("[HTTP GET] 响应成功，状态码: " + (int)response.StatusCode);
                Log("[HTTP GET] 响应体: " + json);
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
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
                type = "Microsoft",
                userProperties = new { }
            };
            string json = new JavaScriptSerializer().Serialize(accountObj);
            SecureStorage.WriteAllTextEncrypted(filePath, json);
        }

        // ----- HTTP 辅助 -----

        // 完整打印请求体与响应体
        private string PostForm(string url, string formData)
        {
            Log("[HTTP Form] POST " + url);
            Log("[HTTP Form] 请求体: " + formData);

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
                {
                    string resp = reader.ReadToEnd();
                    Log("[HTTP Form] 响应成功，状态码: " + (int)response.StatusCode);
                    Log("[HTTP Form] 响应体: " + resp);
                    return resp;
                }
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
                Log("[HTTP Form] 请求失败! HTTP " + statusCode);
                Log("[HTTP Form] 失败响应体: " + (responseBody ?? "(无)"));
                throw new Exception(
                    string.Format(LanguageManager.Get("Auth.PostFailed"), url, statusCode) +
                    (responseBody != null ? ": " + responseBody : ""), ex);
            }
        }

        // 完整打印请求体与响应体
        private string PostJson(string url, object payload)
        {
            string jsonData = new JavaScriptSerializer().Serialize(payload);
            Log("[HTTP JSON] POST " + url);
            Log("[HTTP JSON] 请求体: " + jsonData);

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
                {
                    string resp = reader.ReadToEnd();
                    Log("[HTTP JSON] 响应成功，状态码: " + (int)response.StatusCode);
                    Log("[HTTP JSON] 响应体: " + resp);
                    return resp;
                }
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
                Log("[HTTP JSON] 请求失败! HTTP " + statusCode);
                Log("[HTTP JSON] 失败响应体: " + (responseBody ?? "(无)"));
                throw new Exception(
                    string.Format(LanguageManager.Get("Auth.PostFailed"), url, statusCode) +
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
    }

    // ★ 从嵌套 private 提升为顶层 public，便于 App 层调用
    public class MinecraftAuthResult
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public string Uuid { get; set; }
        public string Username { get; set; }
        public string Xuid { get; set; }
    }
}