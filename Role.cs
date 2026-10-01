using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class Role
    {
        public string Username { get; set; }
        public string Uuid { get; set; }
        public string Type { get; set; }

        // ★ 正版账号的令牌信息（离线账号留空即可）
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
    }
}