using System;
using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class Role
    {
        public string Username { get; set; }
        public string Uuid { get; set; }
        public string Type { get; set; }

        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public string ClientToken { get; set; }
        public string Xuid { get; set; }

        [ScriptIgnore]
        public string Initial
        {
            get
            {
                if (string.IsNullOrEmpty(Username)) return "?";
                return Username.Substring(0, 1).ToUpperInvariant();
            }
        }

        /// <summary>
        /// 是不是微软正版账号。UI 用它判断是否显示“刷新令牌”按钮。
        /// </summary>
        [ScriptIgnore]
        public bool IsMicrosoft
        {
            get { return string.Equals(Type, "Microsoft", StringComparison.OrdinalIgnoreCase); }
        }
    }
}