using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages
{
    public static class LanguageManager
    {
        /// <summary>语言切换后触发。代码赋值的文本可以订阅这个事件刷新。</summary>
        public static event Action LanguageChanged;

        /// <summary>当前生效的语言代码</summary>
        public static string Current { get; private set; } = "zh-CN";

        /// <summary>默认语言代码（不支持的项回退到这个）</summary>
        public const string DefaultLanguage = "zh-CN";

        /// <summary>回退语言代码（系统语言不在列表时使用）</summary>
        public const string FallbackLanguage = "en-US";

        /// <summary>支持的语言：Code -> 显示名（显示名用母语写，不用翻译）</summary>
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

        // ============================================================
        //  初始化
        // ============================================================

        public static void Initialize()
        {
            // 1. 用户手动选择过 → 优先使用
            string saved = SettingsManager.GetLanguage();
            if (!string.IsNullOrEmpty(saved) && Supported.ContainsKey(saved))
            {
                ApplyLanguage(saved, persist: false, raiseEvent: false);
                return;
            }

            // 2. 未设置过 → 自动检测系统语言
            string detected = DetectSystemLanguage();

            ApplyLanguage(detected, persist: true, raiseEvent: false);
        }

        // ============================================================
        //  切换语言（供设置页调用）
        // ============================================================

        public static void Switch(string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            if (!Supported.ContainsKey(code)) return;
            if (string.Equals(Current, code, StringComparison.OrdinalIgnoreCase)) return;

            ApplyLanguage(code, persist: true, raiseEvent: true);
        }

        // ============================================================
        //  内部：应用语言
        // ============================================================

        private static void ApplyLanguage(string code, bool persist, bool raiseEvent)
        {
            if (!Supported.ContainsKey(code))
                code = FallbackLanguage;

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
                if (app == null) return;

                // 移除旧的 Strings.xx-XX.xaml
                var old = app.Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null &&
                        d.Source.OriginalString.IndexOf("/Languages/Strings.",
                            StringComparison.OrdinalIgnoreCase) >= 0);

                if (old != null)
                    app.Resources.MergedDictionaries.Remove(old);

                app.Resources.MergedDictionaries.Add(dict);

                // 阿拉伯语等 RTL 语言设置 FlowDirection
                var flow = IsRtl(code)
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
                app.Resources["App.FlowDirection"] = flow;

                foreach (Window w in app.Windows)
                    w.FlowDirection = flow;

                if (raiseEvent)
                {
                    var h = LanguageChanged;
                    if (h != null) h();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[LanguageManager] 加载语言包失败：" + ex.Message);
            }
        }

        // ============================================================
        //  内部：系统语言检测
        // ============================================================

        /// <summary>
        /// 检测系统 UI 语言，返回支持列表中的代码。
        /// 匹配顺序：
        ///   1. 精确匹配（如 zh-CN、en-US）
        ///   2. 语言前缀 + 特殊区分（zh-Hans → zh-CN，zh-Hant → zh-TW）
        ///   3. 前缀匹配（en-GB → en-US）
        ///   4. 都不匹配 → en-US
        /// </summary>
        private static string DetectSystemLanguage()
        {
            try
            {
                var culture = CultureInfo.CurrentUICulture;
                string name = culture.Name; // 例如 zh-CN、zh-Hans-CN、en-GB

                // 1. 精确匹配
                if (!string.IsNullOrEmpty(name) && Supported.ContainsKey(name))
                    return name;

                // 2. 中文特殊情况：区分简繁
                if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    // 繁体：Hant、TW、HK、MO
                    if (name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("TW", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("HK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("MO", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "zh-TW";

                    // 其余中文变体统一到简体
                    return "zh-CN";
                }

                // 3. 其余语言按两字母前缀匹配
                string prefix = culture.TwoLetterISOLanguageName; // en、es、fr…
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
            catch
            {
                // 忽略：下面兜底
            }

            // 4. 不支持 → 美式英语
            return FallbackLanguage;
        }

        private static bool IsRtl(string code)
        {
            return !string.IsNullOrEmpty(code) &&
                   code.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        //  MessageBox 封装
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
                    System.Diagnostics.Debug.WriteLine(
                        "[LanguageManager] Format 失败 key=" + messageKey +
                        " 模板=" + msg + " 原因=" + ex.Message);
                }
            }

            // ★ 把 XAML 里写的字面量 \n \r \t \\ 还原成真实字符
            return Unescape(msg);
        }

        /// <summary>
        /// 把资源文件里的字面量转义序列还原成真实字符。
        /// XAML 不处理 C# 转义，所以 \n 得在运行时自己转。
        /// 如果需要显示字面反斜杠，资源里写 \\。
        /// </summary>
        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('\\') < 0) return s;   // 没有反斜杠，快速返回

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
                    default: sb.Append(c); break;   // 未知转义，保留反斜杠
                }
            }
            return sb.ToString();
        }

        // ============================================================
        //  工具
        // ============================================================

        /// <summary>按 key 取当前语言的字符串，取不到返回 key 本身</summary>
        public static string Get(string key)
        {
            var app = Application.Current;
            if (app == null) return key;

            var v = app.TryFindResource(key);
            return v != null ? v.ToString() : key;
        }

        /// <summary>获取当前语言的显示名（用于设置页下拉框）</summary>
        public static string GetDisplayName(string code)
        {
            string display;
            if (Supported.TryGetValue(code, out display))
                return display;
            return code;
        }
    }
}