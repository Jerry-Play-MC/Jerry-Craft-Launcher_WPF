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
            VersionStatusText.Text = "加载版本清单中...";
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

                VersionStatusText.Text = "共 " + releases.Count + " 个正式版";
            }
            catch (Exception ex)
            {
                VersionStatusText.Text = "加载失败：" + ex.Message;
            }
            finally
            {
                GameVersionBox.IsEnabled = true;
            }
        }

        // ---------- 安装 ----------

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;

            string serverName = (ServerNameBox.Text ?? "").Trim();
            string gameVersion = (GameVersionBox.Text ?? "").Trim();
            string loaderName = LoaderBox.SelectedItem as string;
            string loaderVersion = (LoaderVersionBox.Text ?? "").Trim();

            if (string.IsNullOrEmpty(serverName))
            {
                MessageBox.Show("请填写服务器名。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (serverName.IndexOf(c) >= 0)
                {
                    MessageBox.Show("服务器名包含非法字符：" + c, "提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (string.IsNullOrEmpty(gameVersion))
            {
                MessageBox.Show("请选择或输入游戏版本。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
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
                var r = MessageBox.Show(
                    "目标文件夹已存在且不为空：\n" + serverDir + "\n\n" +
                    "继续安装会覆盖部分文件，是否继续？",
                    "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }

            Directory.CreateDirectory(serverDir);

            string loaderType = loaderName;
            if (!string.IsNullOrEmpty(loaderVersion))
                loaderType += "[" + loaderVersion + "]";

            _installing = true;
            InstallButton.IsEnabled = false;
            InstallStatusText.Text = "安装中...";
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
                    "[Launcher] 开始安装服务端",
                    "[Launcher] 服务器名   : " + serverName,
                    "[Launcher] 游戏版本   : " + gameVersion,
                    "[Launcher] 加载器     : " + loaderName,
                    "[Launcher] 加载器版本 : " + (string.IsNullOrEmpty(loaderVersion) ? "（最新）" : loaderVersion),
                    "[Launcher] 目标目录   : " + serverDir,
                    "=================================================="
                });

                int rc = await Task.Run(() =>
                    Install_Minecraft_Versions.VersionInstaller.Run(
                        "server", loaderType, gameVersion, serverDir, "none"));

                // 保证所有缓冲日志都提交完
                if (_uiWriter != null) { try { _uiWriter.Flush(); } catch { } }
                await Dispatcher.Yield(DispatcherPriority.Background);

                if (rc == 0)
                {
                    AppendLogLines(new System.Collections.Generic.List<string> {
                        "",
                        "[Launcher] ✅ 安装完成"
                    });
                    InstallStatusText.Text = "安装完成";

                    MessageBox.Show(
                        "服务器【" + serverName + "】安装完成。",
                        "成功", MessageBoxButton.OK, MessageBoxImage.Information);

                    DialogResult = true;
                    Close();
                }
                else
                {
                    AppendLogLines(new System.Collections.Generic.List<string> {
                        "",
                        "[Launcher] ❌ 安装失败，退出码 " + rc
                    });
                    InstallStatusText.Text = "安装失败";

                    MessageBox.Show(
                        "安装失败，请查看下方日志中的错误信息。",
                        "失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                if (_uiWriter != null) { try { _uiWriter.Flush(); } catch { } }

                AppendLogLines(new System.Collections.Generic.List<string> {
                    "",
                    "[Launcher] ❌ 安装出错：" + ex.Message
                });
                InstallStatusText.Text = "安装失败";

                MessageBox.Show("安装出错：" + ex.Message,
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_uiWriter != null)
                {
                    try { _uiWriter.Flush(); } catch { }
                }
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

        // ============================================================
        //   批量日志：一次 AppendText 打一批
        // ============================================================

        /// <summary>
        /// 一次性提交多行日志。
        /// 关键点：
        ///   1) 跨线程调度一次就带整批，避免一行一次 BeginInvoke
        ///   2) 单批最多 200 行，超过就分批，让 UI 有机会处理其他消息
        ///   3) 直接 AppendText + ScrollToEnd，不做任何内容判断
        /// </summary>
        private void AppendLogLines(System.Collections.Generic.List<string> lines)
        {
            if (lines == null || lines.Count == 0) return;

            // 从任意线程切到 UI 线程（只切一次，带整批过去）
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action<System.Collections.Generic.List<string>>(AppendLogLines),
                    lines);
                return;
            }

            LogPlaceholder.Visibility = Visibility.Collapsed;

            // 单次 UI 批次过大也会卡，拆成 ≤200 行
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

            // 拼成一段文本，一次 AppendText
            var sb = new StringBuilder(lines.Count * 64);
            foreach (var l in lines)
            {
                sb.Append(l);
                sb.Append("\r\n");
            }

            // 超限裁剪：超过 3000 行时丢掉最旧的一半
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

            // 每个 UI 批次末尾滚一次即可
            LogBox.ScrollToEnd();
        }

        // ---------- 关闭 ----------

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

    // ================================================================
    //   批量日志 Writer：攒行 + 100ms 一次性提交，避免逐行刷 UI
    // ================================================================

    /// <summary>
    /// 把 Console.Write / WriteLine 收集成整行，缓存到队列，
    /// 每 100ms 一次性把队列交给回调。\r 视为"回到行首"，
    /// 下一次写字符时清空当前缓冲（处理进度行覆盖打印）。
    /// </summary>
    internal class LogWriter : TextWriter
    {
        private readonly Action<System.Collections.Generic.List<string>> _onLines;
        private readonly object _lock = new object();
        private readonly StringBuilder _currentLine = new StringBuilder();
        private readonly System.Collections.Generic.List<string> _pendingLines =
            new System.Collections.Generic.List<string>();
        private readonly Timer _flushTimer;

        private bool _atLineStart = true;
        private bool _flushScheduled;
        private bool _disposed;

        // 100ms 合并一次；数值越小越"实时"，但 UI 负担越大
        private const int FlushIntervalMs = 100;

        // 队列上限：超过丢最旧的一半，防止内存膨胀
        private const int MaxPendingLines = 20000;

        public LogWriter(Action<System.Collections.Generic.List<string>> onLines)
        {
            _onLines = onLines;
            _flushTimer = new Timer(_ => FlushBatch(), null,
                Timeout.Infinite, Timeout.Infinite);
        }

        public override Encoding Encoding { get { return Encoding.UTF8; } }

        public override void Write(char c)
        {
            bool needSchedule = false;

            lock (_lock)
            {
                if (c == '\r')
                {
                    // 回车 → 下一字符从行首覆盖
                    _atLineStart = true;
                    return;
                }

                if (c == '\n')
                {
                    _pendingLines.Add(_currentLine.ToString());
                    _currentLine.Clear();
                    _atLineStart = true;

                    // 首次有待处理行 → 启动 100ms 定时器
                    if (!_flushScheduled && !_disposed)
                    {
                        _flushScheduled = true;
                        needSchedule = true;
                    }

                    // 超量裁剪
                    if (_pendingLines.Count > MaxPendingLines)
                    {
                        int drop = _pendingLines.Count - MaxPendingLines / 2;
                        _pendingLines.RemoveRange(0, drop);
                        _pendingLines.Insert(0,
                            "...[丢弃 " + drop + " 行超量日志]...");
                    }
                }
                else
                {
                    if (_atLineStart)
                    {
                        _currentLine.Clear();
                        _atLineStart = false;
                    }
                    _currentLine.Append(c);
                }
            }

            if (needSchedule)
            {
                try { _flushTimer.Change(FlushIntervalMs, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }

        public override void Write(string value)
        {
            if (value == null) return;
            // 直接逐字符走自己的路径，避免基类默认实现里额外的开销
            for (int i = 0; i < value.Length; i++)
                Write(value[i]);
        }

        public override void WriteLine(string value)
        {
            Write(value);
            Write('\r');
            Write('\n');
        }

        public override void WriteLine()
        {
            Write('\r');
            Write('\n');
        }

        public override void Flush()
        {
            FlushBatch();
        }

        private void FlushBatch()
        {
            System.Collections.Generic.List<string> batch = null;

            lock (_lock)
            {
                _flushScheduled = false;

                // 未换行的半行（进度行）也带出去
                if (_currentLine.Length > 0 && !_atLineStart)
                {
                    string partial = _currentLine.ToString().TrimEnd();
                    if (partial.Length > 0)
                        _pendingLines.Add(partial);
                    _currentLine.Clear();
                    _atLineStart = true;
                }

                if (_pendingLines.Count > 0)
                {
                    batch = new System.Collections.Generic.List<string>(_pendingLines);
                    _pendingLines.Clear();
                }
            }

            if (batch != null && _onLines != null)
                _onLines(batch);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                try { _flushTimer.Dispose(); } catch { }
                FlushBatch();
            }
            base.Dispose(disposing);
        }
    }
}