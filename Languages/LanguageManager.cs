using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages
{
    public static class LanguageManager
    {
        public static event Action LanguageChanged;

        public static string Current { get; private set; } = "zh-CN";

        public const string DefaultLanguage = "zh-CN";
        public const string FallbackLanguage = "en-US";

        public static readonly Dictionary<string, string> Supported =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "zh-CN", "简体中文" },
            { "zh-TW", "繁體中文" },
            { "en-US", "English" },
            { "es-ES", "Español" },
            { "fr-FR", "Français" },
            { "pt-BR", "Português (Brasil)" },
            { "ru-RU", "Русский" },
            { "id-ID", "Bahasa Indonesia" },
            { "hi-IN", "हिन्दी" },
            { "bn-BD", "বাংলা" },
            { "ar-SA", "العربية" },
        };

        public static void Initialize()
        {
            recordLog(LogLevel.Info, "初始化语言系统，支持 " + Supported.Count + " 种语言");

            // 1. 用户手动选择过 → 优先使用
            string saved = SettingsManager.GetLanguage();
            if (!string.IsNullOrEmpty(saved) && Supported.ContainsKey(saved))
            {
                recordLog(LogLevel.Info, "使用用户已保存的语言: " + saved);
                ApplyLanguage(saved, persist: false, raiseEvent: false);
                return;
            }

            // 2. 未设置过 → 自动检测系统语言
            string detected = DetectSystemLanguage();
            recordLog(LogLevel.Info, "未找到用户语言设置，检测到系统语言: " + detected);

            ApplyLanguage(detected, persist: true, raiseEvent: false);
        }

        public static void Switch(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                recordLog(LogLevel.Warn, "切换语言失败：语言代码为空");
                return;
            }
            if (!Supported.ContainsKey(code))
            {
                recordLog(LogLevel.Warn, "切换语言失败：不支持的语言代码 " + code);
                return;
            }
            if (string.Equals(Current, code, StringComparison.OrdinalIgnoreCase))
                return;

            recordLog(LogLevel.Info, "切换语言: " + Current + " -> " + code);
            ApplyLanguage(code, persist: true, raiseEvent: true);
        }

        private static void ApplyLanguage(string code, bool persist, bool raiseEvent)
        {
            if (!Supported.ContainsKey(code))
            {
                recordLog(LogLevel.Warn, "目标语言不在支持列表，回退到: " + FallbackLanguage + "（原: " + code + "）");
                code = FallbackLanguage;
            }

            Current = code;

            if (persist)
                SettingsManager.SetLanguage(code);

            try
            {
                var dict = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/;component/Languages/Strings." + code + ".xaml", UriKind.Absolute)
                };

                var app = Application.Current;
                if (app == null)
                {
                    recordLog(LogLevel.Warn, "应用语言失败：Application.Current 为 null，跳过语言包加载");
                    return;
                }

                var old = app.Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null &&
                        d.Source.OriginalString.IndexOf("/Languages/Strings.",
                            StringComparison.OrdinalIgnoreCase) >= 0);

                if (old != null)
                    app.Resources.MergedDictionaries.Remove(old);

                app.Resources.MergedDictionaries.Add(dict);

                var flow = IsRtl(code)
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
                app.Resources["App.FlowDirection"] = flow;

                foreach (Window w in app.Windows)
                    w.FlowDirection = flow;

                recordLog(LogLevel.Info, "语言应用成功: " + code + (IsRtl(code) ? "（RTL）" : "（LTR）"));

                if (raiseEvent)
                {
                    var h = LanguageChanged;
                    if (h != null)
                    {
                        try { h(); }
                        catch (Exception ex)
                        {
                            recordLog(LogLevel.Error, "LanguageChanged 订阅者异常: " + ex.Message);
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                recordLog(LogLevel.Error, "加载语言包失败: " + code + "，原因: " + ex.Message);
            }
        }

        private static string DetectSystemLanguage()
        {
            try
            {
                var culture = CultureInfo.CurrentUICulture;
                string name = culture.Name;

                if (!string.IsNullOrEmpty(name) && Supported.ContainsKey(name))
                    return name;

                if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("TW", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("HK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("MO", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "zh-TW";

                    return "zh-CN";
                }

                string prefix = culture.TwoLetterISOLanguageName;
                if (!string.IsNullOrEmpty(prefix))
                {
                    foreach (var kv in Supported)
                    {
                        if (kv.Key.StartsWith(prefix + "-",
                                StringComparison.OrdinalIgnoreCase))
                            return kv.Key;
                    }
                }
            }
            catch (Exception ex)
            {
                recordLog(LogLevel.Warn, "检测系统语言失败，将使用回退语言: " + ex.Message);
            }

            return FallbackLanguage;
        }

        private static bool IsRtl(string code)
        {
            return !string.IsNullOrEmpty(code) &&
                   code.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        //  MessageBox 封装（保持不变）
        // ============================================================

        public static void ShowInfo(string messageKey, params object[] args)
        {
            ShowDialog(messageKey, "Dialog.Info", MessageBoxImage.Information, args);
        }

        public static void ShowError(string messageKey, params object[] args)
        {
            ShowDialog(messageKey, "Dialog.Error", MessageBoxImage.Error, args);
        }

        public static void ShowWarning(string messageKey, params object[] args)
        {
            ShowDialog(messageKey, "Dialog.Warning", MessageBoxImage.Warning, args);
        }

        public static bool Confirm(string messageKey, params object[] args)
        {
            string msg = Format(messageKey, args);
            return MessageBox.Show(msg, Get("Dialog.Confirm"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        public static bool ConfirmWarning(string messageKey, params object[] args)
        {
            string msg = Format(messageKey, args);
            return MessageBox.Show(msg, Get("Dialog.Warning"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private static void ShowDialog(string messageKey, string titleKey,
            MessageBoxImage icon, params object[] args)
        {
            string msg = Format(messageKey, args);
            MessageBox.Show(msg, Get(titleKey), MessageBoxButton.OK, icon);
        }

        private static string Format(string messageKey, object[] args)
        {
            string msg = Get(messageKey);

            if (args != null && args.Length > 0)
            {
                try { msg = string.Format(msg, args); }
                catch (Exception ex)
                {
                    recordLog(LogLevel.Warn, "字符串格式化失败 key=" + messageKey + " 模板=" + msg + " 原因=" + ex.Message);
                }
            }

            return Unescape(msg);
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('\\') < 0) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char n = s[i + 1];
                switch (n)
                {
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static string Get(string key)
        {
            var app = Application.Current;
            if (app == null) return key;

            var v = app.TryFindResource(key);
            return v != null ? v.ToString() : key;
        }

        public static string GetDisplayName(string code)
        {
            string display;
            if (Supported.TryGetValue(code, out display))
                return display;
            return code;
        }
    }
}