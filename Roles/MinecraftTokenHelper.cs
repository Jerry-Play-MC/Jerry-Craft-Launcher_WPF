using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>
    /// 从 Minecraft access_token（JWT）里解析过期时间。
    /// 不需要额外存字段，任何已有的 token 都能直接算。
    /// </summary>
    public static class MinecraftTokenHelper
    {
        /// <summary>
        /// 判断 token 是否已过期（默认提前 60 秒视为过期，留出边界余量）。
        /// 解析失败按“已过期”处理，让调用方走刷新流程。
        /// </summary>
        public static bool IsExpired(string accessToken, int safetySeconds = 60)
        {
            var exp = GetExpiryUtc(accessToken);
            if (exp == DateTime.MinValue) return true;
            return DateTime.UtcNow.AddSeconds(safetySeconds) >= exp;
        }

        /// <summary>从 JWT 里取 exp 转成 UTC 时间。失败返回 DateTime.MinValue。</summary>
        public static DateTime GetExpiryUtc(string accessToken)
        {
            if (string.IsNullOrEmpty(accessToken)) return DateTime.MinValue;
            try
            {
                var parts = accessToken.Split('.');
                if (parts.Length < 2) return DateTime.MinValue;

                string payload = Base64UrlDecode(parts[1]);
                var json = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(payload);
                if (json == null || !json.ContainsKey("exp"))
                    return DateTime.MinValue;

                long exp = Convert.ToInt64(json["exp"]);
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(exp);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        // JWT 用的是 Base64URL：'-' → '+'，'_' → '/'，长度不是 4 倍数要补 '='
        private static string Base64UrlDecode(string input)
        {
            string s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException("无效的 Base64URL 字符串");
            }
            byte[] bytes = Convert.FromBase64String(s);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}