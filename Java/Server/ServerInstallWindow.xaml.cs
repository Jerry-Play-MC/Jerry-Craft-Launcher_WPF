using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class ServerInstallWindow : Window
    {
        private static readonly string[] LoaderTypes = {
            "Vanilla",
            "Forge",
            "NeoForge",
            "Fabric",
            "Quilt"
        };

        private bool _installing;
        private TextWriter _originalOut;
        private TextWriter _originalErr;
        private LogWriter _uiWriter;

        private int _logLineCount;

        public ServerInstallWindow()
        {
            InitializeComponent();

            LoaderBox.ItemsSource = LoaderTypes;
            LoaderBox.SelectedIndex = 1;

            Loaded += (s, e) => RefreshVersions();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_installing) DragMove();
        }

        private void RefreshVersions_Click(object sender, RoutedEventArgs e)
        {
            if (!_installing) RefreshVersions();
        }

        private async void RefreshVersions()
        {
            VersionStatusText.Text = LanguageManager.Get("ServerInstall.LoadingManifest");
            GameVersionBox.IsEnabled = false;

            try
            {
                var list = await Task.Run(() => VersionManifestScanner.Fetch());

                var releases = list
                    .Where(v => v.Category == VersionCategory.Release)
                    .Select(v => v.Id)
                    .ToList();

                GameVersionBox.ItemsSource = releases;
                if (releases.Count > 0 && GameVersionBox.SelectedIndex < 0)
                    GameVersionBox.SelectedIndex = 0;

                VersionStatusText.Text = string.Format(
                    LanguageManager.Get("ServerInstall.ReleaseCount"), releases.Count);
            }
            catch (Exception ex)
            {
                VersionStatusText.Text = LanguageManager.Get("ServerInstall.LoadFailed")
                    + "：" + ex.Message;
            }
            finally
            {
                GameVersionBox.IsEnabled = true;
            }
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;

            string serverName = (ServerNameBox.Text ?? "").Trim();
            string gameVersion = (GameVersionBox.Text ?? "").Trim();
            string loaderName = LoaderBox.SelectedItem as string;
            string loaderVersion = (LoaderVersionBox.Text ?? "").Trim();

            if (string.IsNullOrEmpty(serverName))
            {
                LanguageManager.ShowInfo("ServerInstall.NameRequired");
                return;
            }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (serverName.IndexOf(c) >= 0)
                {
                    LanguageManager.ShowWarning("ServerInstall.NameInvalid", c);
                    return;
                }
            }

            if (string.IsNullOrEmpty(gameVersion))
            {
                LanguageManager.ShowInfo("ServerInstall.VersionRequired");
                return;
            }

            if (string.IsNullOrEmpty(loaderName))
                loaderName = "Vanilla";

            string baseServerDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Server");
            string serverDir = Path.Combine(baseServerDir, serverName);

            if (Directory.Exists(serverDir) &&
                Directory.GetFileSystemEntries(serverDir).Length > 0)
            {
                if (!LanguageManager.Confirm("ServerInstall.FolderExists", serverDir))
                    return;
            }

            Directory.CreateDirectory(serverDir);

            string loaderType = loaderName;
            if (!string.IsNullOrEmpty(loaderVersion))
                loaderType += "[" + loaderVersion + "]";

            _installing = true;
            InstallButton.IsEnabled = false;
            InstallStatusText.Text = LanguageManager.Get("ServerInstall.Installing");
            SetFormEnabled(false);

            LogBox.Clear();
            _logLineCount = 0;
            LogPlaceholder.Visibility = Visibility.Visible;

            _originalOut = Console.Out;
            _originalErr = Console.Error;
            _uiWriter = new LogWriter(AppendLogLines);
            Console.SetOut(_uiWriter);
            Console.SetError(_uiWriter);

            try
            {
                AppendLogLines(new System.Collections.Generic.List<string> {
                    "==================================================",
                    "[Launcher] " + LanguageManager.Get("ServerInstall.BeginInstall"),
                    "[Launcher] " + LanguageManager.Get("ServerInstall.ServerName")
                        + "   : " + serverName,
                    "[Launcher] " + LanguageManager.Get("ServerInstall.GameVersion")
                        + "   : " + gameVersion,
                    "[Launcher] " + LanguageManager.Get("ServerInstall.Loader")
                        + "     : " + loaderName,
                    "[Launcher] " + LanguageManager.Get("ServerInstall.LoaderVersion")
                        + " : " + (string.IsNullOrEmpty(loaderVersion)
                            ? LanguageManager.Get("ClientInstall.LatestVersionShort") : loaderVersion),
                    "[Launcher] " + LanguageManager.Get("ClientInstall.TargetDir")
                        + "   : " + serverDir,
                    "=================================================="
                });

                int rc = await Task.Run(() =>
                    Install_Minecraft_Versions.VersionInstaller.Run(
                        "server", loaderType, gameVersion, serverDir, "none"));

                if (_uiWriter != null) { try { _uiWriter.Flush(); } catch { } }
                await Dispatcher.Yield(DispatcherPriority.Background);

                if (rc == 0)
                {
                    AppendLogLines(new System.Collections.Generic.List<string> {
                        "",
                        "[Launcher] " + LanguageManager.Get("ClientInstall.InstallDoneLog")
                    });
                    InstallStatusText.Text = LanguageManager.Get("ClientInstall.InstallDone");

                    LanguageManager.ShowInfo("ServerInstall.InstallSuccess", serverName);

                    DialogResult = true;
                    Close();
                }
                else
                {
                    AppendLogLines(new System.Collections.Generic.List<string> {
                        "",
                        "[Launcher] " + LanguageManager.Get("ClientInstall.InstallFailedLog")
                        + " rc=" + rc
                    });
                    InstallStatusText.Text = LanguageManager.Get("ClientInstall.InstallFailed");

                    LanguageManager.ShowError("ServerInstall.InstallFailed");
                }
            }
            catch (Exception ex)
            {
                if (_uiWriter != null) { try { _uiWriter.Flush(); } catch { } }

                AppendLogLines(new System.Collections.Generic.List<string> {
                    "",
                    "[Launcher] " + LanguageManager.Get("ClientInstall.InstallErrorLog")
                    + " " + ex.Message
                });
                InstallStatusText.Text = LanguageManager.Get("ClientInstall.InstallFailed");

                LanguageManager.ShowError("ServerInstall.InstallError", ex.Message);
            }
            finally
            {
                if (_uiWriter != null) { try { _uiWriter.Flush(); } catch { } }
                if (_originalOut != null) { try { Console.SetOut(_originalOut); } catch { } }
                if (_originalErr != null) { try { Console.SetError(_originalErr); } catch { } }
                _uiWriter = null;

                _installing = false;
                InstallButton.IsEnabled = true;
                SetFormEnabled(true);
            }
        }

        private void SetFormEnabled(bool enabled)
        {
            ServerNameBox.IsEnabled = enabled;
            GameVersionBox.IsEnabled = enabled;
            LoaderBox.IsEnabled = enabled;
            LoaderVersionBox.IsEnabled = enabled;
        }

        private void AppendLogLines(System.Collections.Generic.List<string> lines)
        {
            if (lines == null || lines.Count == 0) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action<System.Collections.Generic.List<string>>(AppendLogLines),
                    lines);
                return;
            }

            LogPlaceholder.Visibility = Visibility.Collapsed;

            const int MaxBatch = 200;
            if (lines.Count > MaxBatch)
            {
                var head = lines.GetRange(0, MaxBatch);
                var tail = lines.GetRange(MaxBatch, lines.Count - MaxBatch);

                AppendLogLines(head);
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action<System.Collections.Generic.List<string>>(AppendLogLines),
                    tail);
                return;
            }

            var sb = new StringBuilder(lines.Count * 64);
            foreach (var l in lines)
            {
                sb.Append(l);
                sb.Append("\r\n");
            }

            const int MaxLines = 3000;
            if (_logLineCount + lines.Count > MaxLines)
            {
                string txt = LogBox.Text;
                int totalLines = _logLineCount + lines.Count;
                int toDrop = totalLines - MaxLines / 2;

                if (toDrop > 0)
                {
                    int dropEnd = 0;
                    int nl = 0;
                    for (int i = 0; i < txt.Length && nl < toDrop; i++)
                    {
                        if (txt[i] == '\n') { nl++; dropEnd = i + 1; }
                    }
                    if (dropEnd > 0)
                    {
                        LogBox.Text = txt.Substring(dropEnd);
                        _logLineCount -= nl;
                    }
                }
            }

            LogBox.AppendText(sb.ToString());
            _logLineCount += lines.Count;
            LogBox.ScrollToEnd();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;
            DialogResult = false;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;
            DialogResult = false;
            Close();
        }
    }
}