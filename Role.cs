using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class Role
    {
        public string Username { get; set; }
        public string Uuid { get; set; }
        public string Type { get; set; }

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