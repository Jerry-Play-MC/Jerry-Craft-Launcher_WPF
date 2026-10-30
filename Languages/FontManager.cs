using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

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
            recordLog(LogLevel.Info, "初始化字体系统，支持 " + Supported.Count + " 种字体");

            string saved = SettingsManager.GetFont();
            if (string.IsNullOrEmpty(saved) || !Supported.ContainsKey(saved))
            {
                recordLog(LogLevel.Info, "未找到有效字体设置，使用默认: Default（原值: " + (saved ?? "(空)") + "）");
                saved = "Default";
            }
            else
            {
                recordLog(LogLevel.Info, "使用已保存的字体: " + saved);
            }

            Current = saved;
            Apply(saved, raiseEvent: false);
        }

        public static void Switch(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                recordLog(LogLevel.Warn, "切换字体失败：字体名为空");
                return;
            }
            if (!Supported.ContainsKey(name))
            {
                recordLog(LogLevel.Warn, "切换字体失败：不支持的字体 " + name);
                return;
            }
            if (string.Equals(Current, name, StringComparison.OrdinalIgnoreCase))
                return;

            recordLog(LogLevel.Info, "切换字体: " + Current + " -> " + name);

            Current = name;
            SettingsManager.SetFont(name);
            Apply(name, raiseEvent: true);
        }

        private static void Apply(string name, bool raiseEvent)
        {
            var app = Application.Current;
            if (app == null)
            {
                recordLog(LogLevel.Warn, "应用字体失败：Application.Current 为 null，跳过字体应用");
                return;
            }

            try
            {
                string family = name == "Default" ? "Microsoft YaHei UI, Segoe UI" : name;

                app.Resources["App.FontFamily"] = new FontFamily(family);

                recordLog(LogLevel.Info, "字体应用成功: " + name + "（family: " + family + "）");

                if (raiseEvent)
                {
                    var h = FontChanged;
                    if (h != null)
                    {
                        try { h(); }
                        catch (Exception ex)
                        {
                            recordLog(LogLevel.Error, "FontChanged 订阅者异常: " + ex.Message);
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                recordLog(LogLevel.Error, "应用字体失败: " + name + "，原因: " + ex.Message);
            }
        }
    }
}