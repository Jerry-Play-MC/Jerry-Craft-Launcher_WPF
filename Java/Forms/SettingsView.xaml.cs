using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Updater;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class SettingsView : UserControl
    {
        private bool _suppressEvents;
        private List<JavaItem> _javaItems = new List<JavaItem>();

        public SettingsView()
        {
            InitializeComponent();
            Loaded += SettingsView_Loaded;
            LanguageManager.LanguageChanged += OnLanguageChanged;
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshIsolationCheck();
            RefreshVersionText();
            RefreshLanguageBox();
            // Java 列表延迟到切换到 Java 面板时再扫描，避免启动时无谓的注册表 IO
        }

        private void OnLanguageChanged()
        {
            Dispatcher.Invoke(() =>
            {
                RefreshVersionText();
                SetUpdateStatus("", "#4F46E5");
                if (JavaPanel != null && JavaPanel.Visibility == Visibility.Visible)
                    RefreshJavaList();
            });
        }

        private void SubNav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            if (LauncherPanel == null || JavaPanel == null
                || GamePanel == null || AboutPanel == null) return;

            string tag = rb.Tag as string;

            LauncherPanel.Visibility = Visibility.Collapsed;
            JavaPanel.Visibility = Visibility.Collapsed;
            GamePanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;

            switch (tag)
            {
                case "Launcher":
                    LauncherPanel.Visibility = Visibility.Visible;
                    RefreshVersionText();
                    RefreshLanguageBox();
                    break;
                case "Java":
                    JavaPanel.Visibility = Visibility.Visible;
                    RefreshJavaList();
                    break;
                case "Game":
                    GamePanel.Visibility = Visibility.Visible;
                    RefreshIsolationCheck();
                    break;
                case "About":
                    AboutPanel.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void RefreshIsolationCheck()
        {
            if (IsolationCheck == null) return;
            _suppressEvents = true;
            try { IsolationCheck.IsChecked = SettingsManager.GetIsolationGameData(); }
            finally { _suppressEvents = false; }
        }

        private void IsolationCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (IsolationCheck == null) return;
            bool value = IsolationCheck.IsChecked == true;
            SettingsManager.SetIsolationGameData(value);
            App.Config.Isolated = value;
        }

        private void RefreshVersionText()
        {
            if (CurrentVersionText == null) return;
            CurrentVersionText.Text = LanguageManager.Get("Settings.CurrentVersion") + App.Version;
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (CheckUpdateButton == null) return;

            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.Content = LanguageManager.Get("Settings.Checking");
            SetUpdateStatus(LanguageManager.Get("Settings.Checking"), "#4F46E5");

            try
            {
                var result = await UpdateService.CheckAsync();
                if (!result.Success)
                {
                    SetUpdateStatus(result.Message, "#E53935");
                    LanguageManager.ShowWarning("Settings.CheckFailed", result.Message);
                    return;
                }
                if (!result.HasUpdate)
                {
                    SetUpdateStatus(LanguageManager.Get("Settings.UpToDate") + " "
                        + result.CurrentVersion, "#10B981");
                    LanguageManager.ShowInfo("Settings.UpToDateWithVersion", result.CurrentVersion);
                    return;
                }

                SetUpdateStatus(LanguageManager.Get("Settings.NewVersionFound") + " "
                    + result.LatestVersion, "#4F46E5");

                var win = new UpdateWindow(result.Info) { Owner = Window.GetWindow(this) };
                win.ShowDialog();
                SetUpdateStatus("", "#4F46E5");
            }
            catch (Exception ex)
            {
                SetUpdateStatus(LanguageManager.Get("Settings.CheckFailedShort")
                    + "：" + ex.Message, "#E53935");
                LanguageManager.ShowError("Settings.CheckError", ex.Message);
            }
            finally
            {
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateButton.Content = LanguageManager.Get("Settings.CheckUpdate");
            }
        }

        private void SetUpdateStatus(string text, string colorHex)
        {
            if (UpdateStatusText == null) return;
            UpdateStatusText.Text = text;
            try
            {
                UpdateStatusText.Foreground =
                    (Brush)new BrushConverter().ConvertFromString(colorHex);
            }
            catch { }
        }

        // ---------- 语言切换 ----------

        private void RefreshLanguageBox()
        {
            if (LanguageBox == null) return;
            _suppressEvents = true;
            try
            {
                LanguageBox.Items.Clear();
                foreach (var kv in LanguageManager.Supported)
                {
                    LanguageBox.Items.Add(new ComboBoxItem
                    {
                        Content = kv.Value,
                        Tag = kv.Key
                    });
                }
                foreach (ComboBoxItem item in LanguageBox.Items)
                {
                    if (string.Equals(item.Tag as string, LanguageManager.Current,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        LanguageBox.SelectedItem = item;
                        break;
                    }
                }
            }
            finally { _suppressEvents = false; }
        }

        private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (LanguageBox == null) return;
            var item = LanguageBox.SelectedItem as ComboBoxItem;
            if (item == null) return;
            string code = item.Tag as string;
            if (string.IsNullOrEmpty(code)) return;
            LanguageManager.Switch(code);
            RefreshVersionText();
        }

        // ============================================================
        //   Java 管理
        // ============================================================

        public class JavaItem
        {
            public string Title { get; set; }      // "Java 17.0.2" / "自动选择"
            public string Path { get; set; }       // Java 根目录
            public string MetaLine { get; set; }   // "64 位 · 来源：导入"
            public string Tag { get; set; }        // "JDK" / "JRE" / "AUTO"
            public int Major { get; set; }
            public bool IsImported { get; set; }   // true = 来自 Settings.json
            public bool IsAuto { get; set; }       // true = "自动选择" 占位项
        }

        private void RefreshJavaList()
        {
            if (JavaList == null) return;

            var items = new List<JavaItem>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 导入的（Settings.json）
            foreach (var root in LoadImportedJavaPaths())
            {
                var item = TryCreateJavaItem(root, true);
                if (item != null && seenPaths.Add(item.Path))
                    items.Add(item);
            }

            // 2) 扫描的（内置 + 注册表 + 用户目录）
            List<JavaLocator.JavaCandidate> scanned;
            try { scanned = JavaLocator.GetAllCandidates(); }
            catch { scanned = new List<JavaLocator.JavaCandidate>(); }

            foreach (var c in scanned)
            {
                string root;
                try
                {
                    root = System.IO.Path.GetDirectoryName(
                        System.IO.Path.GetDirectoryName(c.Path));
                }
                catch { continue; }
                if (string.IsNullOrEmpty(root)) continue;

                var item = TryCreateJavaItem(root, false);
                if (item != null && seenPaths.Add(item.Path))
                    items.Add(item);
            }

            _javaItems = items;

            // 版本降序
            items.Sort((a, b) => b.Major.CompareTo(a.Major));

            JavaList.ItemsSource = null;
            JavaList.ItemsSource = items;

            if (JavaEmptyText != null)
                JavaEmptyText.Visibility = items.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;

            if (JavaCountText != null)
                JavaCountText.Text = string.Format(
                    LanguageManager.Get("Settings.Java.CountFormat"), items.Count);

            RefreshStartJavaBox(items);
        }

        private void RefreshStartJavaBox(List<JavaItem> items)
        {
            if (StartJavaBox == null) return;

            var autoItem = new JavaItem
            {
                IsAuto = true,
                Tag = "AUTO",
                Title = LanguageManager.Get("Settings.Java.Auto"),
                Path = "",
                MetaLine = "",
            };

            var comboItems = new List<JavaItem> { autoItem };
            comboItems.AddRange(items);

            string currentUse = "auto";
            try { currentUse = SettingsManager.GetJavaUse(); }
            catch { }

            _suppressEvents = true;
            try
            {
                StartJavaBox.ItemsSource = null;
                StartJavaBox.ItemsSource = comboItems;
                StartJavaBox.SelectedItem = autoItem;

                if (!string.IsNullOrEmpty(currentUse) &&
                    !string.Equals(currentUse, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var it in comboItems)
                    {
                        if (!it.IsAuto && string.Equals(it.Path, currentUse.TrimEnd('\\'),
                            StringComparison.OrdinalIgnoreCase))
                        {
                            StartJavaBox.SelectedItem = it;
                            break;
                        }
                    }
                }
            }
            finally { _suppressEvents = false; }
        }

        private void StartJavaBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (StartJavaBox == null) return;

            var item = StartJavaBox.SelectedItem as JavaItem;
            if (item == null) return;

            try
            {
                if (item.IsAuto)
                    SettingsManager.SetJavaUse("auto");
                else
                    SettingsManager.SetJavaUse(item.Path);
            }
            catch (Exception ex)
            {
                LanguageManager.ShowError("Settings.Java.SaveUseFailed", ex.Message);
            }
        }

        private JavaItem TryCreateJavaItem(string root, bool isImported)
        {
            if (string.IsNullOrEmpty(root)) return null;
            root = root.TrimEnd('\\');

            string javaExe = System.IO.Path.Combine(root, "bin", "java.exe");
            if (!System.IO.File.Exists(javaExe)) return null;

            bool isJdk = System.IO.File.Exists(
                System.IO.Path.Combine(root, "bin", "javac.exe"));

            string versionStr = ReadJavaVersionString(root);
            int major = GetMajorFromVersionString(versionStr);
            if (major <= 0) return null;

            bool is64 = ReadJavaIs64Bit(root);

            string title = "Java " + (string.IsNullOrEmpty(versionStr)
                ? major.ToString() : versionStr);

            string source = isImported
                ? LanguageManager.Get("Settings.Java.Source.Imported")
                : LanguageManager.Get("Settings.Java.Source.Scanned");

            string arch = is64
                ? LanguageManager.Get("Settings.Java.Arch.x64")
                : LanguageManager.Get("Settings.Java.Arch.x86");

            return new JavaItem
            {
                Title = title,
                Path = root,
                Tag = isJdk ? "JDK" : "JRE",
                MetaLine = arch + " · " + source,
                Major = major,
                IsImported = isImported,
            };
        }

        private static string ReadJavaVersionString(string root)
        {
            string releaseFile = System.IO.Path.Combine(root, "release");
            if (!System.IO.File.Exists(releaseFile)) return null;
            try
            {
                foreach (var line in System.IO.File.ReadAllLines(releaseFile))
                {
                    if (line.StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase))
                        return line.Substring("JAVA_VERSION=".Length).Trim('"', ' ');
                }
            }
            catch { }
            return null;
        }

        private static bool ReadJavaIs64Bit(string root)
        {
            string releaseFile = System.IO.Path.Combine(root, "release");
            if (System.IO.File.Exists(releaseFile))
            {
                try
                {
                    foreach (var line in System.IO.File.ReadAllLines(releaseFile))
                    {
                        if (line.StartsWith("OS_ARCH=", StringComparison.OrdinalIgnoreCase))
                        {
                            string arch = line.Substring("OS_ARCH=".Length).Trim('"', ' ');
                            if (arch.Equals("amd64", StringComparison.OrdinalIgnoreCase) ||
                                arch.Equals("x86_64", StringComparison.OrdinalIgnoreCase) ||
                                arch.Equals("aarch64", StringComparison.OrdinalIgnoreCase))
                                return true;
                            if (arch.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
                                arch.Equals("i386", StringComparison.OrdinalIgnoreCase) ||
                                arch.Equals("arm", StringComparison.OrdinalIgnoreCase))
                                return false;
                        }
                    }
                }
                catch { }
            }
            return Environment.Is64BitOperatingSystem;
        }

        private static int GetMajorFromVersionString(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(version, @"^(\d+)");
            if (!m.Success) return 0;
            int major = int.Parse(m.Groups[1].Value);
            if (major == 1 && version.StartsWith("1."))
            {
                var m2 = System.Text.RegularExpressions.Regex.Match(version, @"^1\.(\d+)");
                if (m2.Success) return int.Parse(m2.Groups[1].Value);
            }
            return major;
        }

        // ============================================================
        //   Settings.json 读写（只碰根键 Java 数组，其余键原样保留）
        // ============================================================
        private static List<string> LoadImportedJavaPaths()
        {
            try { return SettingsManager.GetJavaPaths(); }
            catch { return new List<string>(); }
        }

        private static void SaveImportedJavaPaths(List<string> paths)
        {
            SettingsManager.SetJavaPaths(paths);
        }

        // ============================================================
        //   导入 / 移除
        // ============================================================

        private void ImportJava_Click(object sender, RoutedEventArgs e)
        {
            string root;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择 Java 安装目录（包含 bin\\java.exe 和 bin\\javaw.exe）";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;
                root = dlg.SelectedPath.TrimEnd('\\');
            }

            string javaExe = System.IO.Path.Combine(root, "bin", "java.exe");
            string javawExe = System.IO.Path.Combine(root, "bin", "javaw.exe");
            if (!System.IO.File.Exists(javaExe) || !System.IO.File.Exists(javawExe))
            {
                MessageBox.Show(
                    "所选目录不是有效的 Java 根目录\n（缺少 bin\\java.exe 或 bin\\javaw.exe）。",
                    "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var paths = LoadImportedJavaPaths();
            if (paths.Any(p => string.Equals(p.TrimEnd('\\'), root,
                StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("该 Java 已导入。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            paths.Add(root);
            SaveImportedJavaPaths(paths);

            RefreshJavaList();
        }

        private void RemoveJava_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var item = btn != null ? btn.Tag as JavaItem : null;
            if (item == null || item.IsAuto) return;

            if (!item.IsImported)
            {
                MessageBox.Show(
                    "这是自动扫描到的 Java，无法从导入列表中移除。\n" +
                    "如需排除，请卸载对应 Java 或清理其注册表项。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var paths = LoadImportedJavaPaths();
            paths.RemoveAll(p => string.Equals(p.TrimEnd('\\'), item.Path,
                StringComparison.OrdinalIgnoreCase));
            SaveImportedJavaPaths(paths);
            RefreshJavaList();
        }

        // ============================================================
        //   写入注册表（三按钮决策 + 提权副本）
        // ============================================================

        private async void RegisterJava_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var item = btn != null ? btn.Tag as JavaItem : null;
            if (item == null || item.IsAuto) return;

            string javaHome = item.Path;

            // 已经是管理员 → 直接写 HKLM
            if (IsRunAsAdmin())
            {
                await WriteRegistryAsync(javaHome, forCurrentUser: false, btn: btn);
                return;
            }

            // 非管理员 → 三按钮询问
            var choice = MessageBox.Show(
                LanguageManager.Get("Settings.Java.RegisterNeedAdmin"),
                LanguageManager.Get("Settings.Java.RegisterTitle"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            switch (choice)
            {
                case MessageBoxResult.Yes:
                    TryLaunchElevatedRegistration(javaHome);
                    break;

                case MessageBoxResult.No:
                    await WriteRegistryAsync(javaHome, forCurrentUser: true, btn: btn);
                    break;

                case MessageBoxResult.Cancel:
                default:
                    return;
            }
        }

        private async System.Threading.Tasks.Task WriteRegistryAsync(
            string javaHome, bool forCurrentUser, Button btn)
        {
            string title = LanguageManager.Get("Settings.Java.RegisterTitle");
            if (btn != null) btn.IsEnabled = false;

            string regPath = null;
            Exception error = null;

            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        regPath = Java.Install_Java.JavaRegistryRegistrar.Register(
                            javaHome, forCurrentUser);
                    }
                    catch (Exception ex) { error = ex; }
                });

                if (error is UnauthorizedAccessException)
                {
                    MessageBox.Show(
                        LanguageManager.Get("Settings.Java.RegisterNeedAdmin"),
                        title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else if (error != null)
                {
                    MessageBox.Show(
                        string.Format(LanguageManager.Get("Settings.Java.RegisterFailed"),
                            error.Message),
                        title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    string key = forCurrentUser
                        ? "Settings.Java.RegisterSuccessUser"
                        : "Settings.Java.RegisterSuccess";
                    MessageBox.Show(
                        string.Format(LanguageManager.Get(key), regPath),
                        title, MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private static bool IsRunAsAdmin()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(
                        System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        private static void TryLaunchElevatedRegistration(string javaHome)
        {
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess()
                    .MainModule.FileName;

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--register-java \"" + javaHome + "\"",
                    UseShellExecute = true,
                    Verb = "runas",   // 触发 UAC
                };

                System.Diagnostics.Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 玩家在 UAC 对话框点了"否"——等同于取消
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(
                        LanguageManager.Get("Settings.Java.RegisterFailed"), ex.Message),
                    LanguageManager.Get("Settings.Java.RegisterTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}