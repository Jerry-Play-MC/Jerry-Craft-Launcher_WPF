using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages
{
    public static class FontManager
    {
        public static event Action FontChanged;

        public static string Current { get; private set; } = "Default";

        public static readonly Dictionary<string, string> Supported =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Default", "系统默认" },
            { "Microsoft YaHei UI", "微软雅黑" },
            { "SimSun", "宋体" },
            { "SimHei", "黑体" },
            { "KaiTi", "楷体" },
            { "Segoe UI", "Segoe UI" },
        };

        public static void Initialize()
        {
            string saved = SettingsManager.GetFont();
            if (string.IsNullOrEmpty(saved) || !Supported.ContainsKey(saved))
                saved = "Default";

            Current = saved;
            Apply(saved, raiseEvent: false);
        }

        public static void Switch(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!Supported.ContainsKey(name)) return;
            if (string.Equals(Current, name, StringComparison.OrdinalIgnoreCase)) return;

            Current = name;
            SettingsManager.SetFont(name);
            Apply(name, raiseEvent: true);
        }

        private static void Apply(string name, bool raiseEvent)
        {
            var app = Application.Current;
            if (app == null) return;

            string family = name == "Default"
                ? "Microsoft YaHei UI, Segoe UI"
                : name;

            app.Resources["App.FontFamily"] = new FontFamily(family);

            if (raiseEvent)
            {
                var h = FontChanged;
                if (h != null) h();
            }
        }
    }
}