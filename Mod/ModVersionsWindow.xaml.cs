using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class ModVersionsWindow : Window
    {
        private readonly string _modId;
        private readonly ModProjectType _projectType;
        private readonly string _minecraftDir;
        private readonly string _currentVersion;
        private readonly bool _isVersionIsolated;

        private List<ModVersion> _allVersions = new List<ModVersion>();
        private List<ModVersion> _filtered = new List<ModVersion>();

        private bool _loading;
        private bool _filtering;

        public ModVersionsWindow(string modId, ModProjectType type, string title)
        {
            InitializeComponent();

            _modId = modId;
            _projectType = type;
            _minecraftDir = MinecraftCore.GetMinecraftDir();
            _currentVersion = App.Config.CurrentVersion;
            _isVersionIsolated = App.Config.Isolated;

            TitleText.Text = "版本列表：" + (string.IsNullOrEmpty(title) ? modId : title);

            SortBox.SelectedIndex = 0;
            SortDirBox.SelectedIndex = 0;

            Loaded += (s, e) => _ = LoadVersionsAsync();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private async Task LoadVersionsAsync()
        {
            if (_loading) return;
            _loading = true;

            StatusText.Text = "正在加载版本...";
            VersionList.ItemsSource = new List<string> { "加载中..." };
            SetFilterEnabled(false);

            try
            {
                var list = await Task.Run(() => ModApiService.GetModVersions(_modId));
                _allVersions = list ?? new List<ModVersion>();

                if (_allVersions.Count == 0)
                {
                    VersionList.ItemsSource = new List<string> { "没有找到版本" };
                    StatusText.Text = "共 0 个版本";
                    return;
                }

                FillFilterBoxes();
                ApplyFilter();
            }
            catch (Exception ex)
            {
                VersionList.ItemsSource = new List<string> { "加载失败：" + ex.Message };
                StatusText.Text = "加载失败";
            }
            finally
            {
                _loading = false;
                SetFilterEnabled(true);
            }
        }

        private void SetFilterEnabled(bool enabled)
        {
            SearchBox.IsEnabled = enabled;
            SortBox.IsEnabled = enabled;
            SortDirBox.IsEnabled = enabled;
            GameVersionBox.IsEnabled = enabled;
            LoaderBox.IsEnabled = enabled;
        }

        private void FillFilterBoxes()
        {
            var gameVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var loaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var v in _allVersions)
            {
                if (v.GameVersions != null)
                    foreach (var g in v.GameVersions) gameVersions.Add(g);
                if (v.Loaders != null)
                    foreach (var l in v.Loaders) loaders.Add(l);
            }

            var sortedVersions = gameVersions.ToList();
            sortedVersions.Sort((a, b) => CompareVersions(b, a));

            GameVersionBox.Items.Clear();
            GameVersionBox.Items.Add("全部");
            foreach (var v in sortedVersions) GameVersionBox.Items.Add(v);
            GameVersionBox.SelectedIndex = 0;

            LoaderBox.Items.Clear();
            LoaderBox.Items.Add("全部");
            foreach (var l in loaders.OrderBy(x => x)) LoaderBox.Items.Add(l);
            LoaderBox.SelectedIndex = 0;
        }

        private static int CompareVersions(string a, string b)
        {
            try
            {
                var pa = a.Split('.').Select(int.Parse).ToArray();
                var pb = b.Split('.').Select(int.Parse).ToArray();
                int len = Math.Max(pa.Length, pb.Length);
                for (int i = 0; i < len; i++)
                {
                    int va = i < pa.Length ? pa[i] : 0;
                    int vb = i < pb.Length ? pb[i] : 0;
                    if (va != vb) return va.CompareTo(vb);
                }
                return 0;
            }
            catch
            {
                return string.Compare(a, b, StringComparison.Ordinal);
            }
        }

        private void Filter_Changed(object sender, EventArgs e)
        {
            if (_loading || _filtering) return;
            if (GameVersionBox == null || LoaderBox == null) return;
            if (GameVersionBox.Items.Count == 0) return;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            _filtering = true;
            try
            {
                string search = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
                string game = GameVersionBox.SelectedItem as string ?? "全部";
                string loader = LoaderBox.SelectedItem as string ?? "全部";
                int sortIdx = Math.Max(0, SortBox.SelectedIndex);
                bool desc = SortDirBox.SelectedIndex == 0;

                var q = _allVersions.AsEnumerable();

                if (!string.IsNullOrEmpty(search))
                    q = q.Where(v => v.VersionNumber != null &&
                                     v.VersionNumber.ToLowerInvariant().Contains(search));

                if (game != "全部")
                    q = q.Where(v => v.GameVersions != null && v.GameVersions.Contains(game));

                if (loader != "全部")
                    q = q.Where(v => v.Loaders != null && v.Loaders.Contains(loader));

                switch (sortIdx)
                {
                    case 1:
                        q = desc ? q.OrderByDescending(v => v.Downloads)
                                 : q.OrderBy(v => v.Downloads);
                        break;
                    case 2:
                        q = desc ? q.OrderByDescending(v => v.VersionNumber)
                                 : q.OrderBy(v => v.VersionNumber);
                        break;
                    default:
                        q = desc ? q.OrderByDescending(v => v.DatePublished)
                                 : q.OrderBy(v => v.DatePublished);
                        break;
                }

                _filtered = q.ToList();
                VersionList.ItemsSource = _filtered;
                StatusText.Text = "共 " + _allVersions.Count
                    + " 个版本，显示 " + _filtered.Count + " 个";
            }
            finally
            {
                _filtering = false;
            }
        }

        private void Download_Click(object sender, RoutedEventArgs e)
        {
            var version = VersionList.SelectedItem as ModVersion;
            if (version == null)
            {
                MessageBox.Show("请先选择一个版本。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string url = version.Files != null && version.Files.Count > 0
                ? version.Files[0].Url : null;
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("该版本没有可下载的文件。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string defaultFileName = version.Files[0].Filename
                ?? (_modId + "-" + version.VersionNumber + ".jar");

            switch (_projectType)
            {
                case ModProjectType.Mod:
                    HandleModDownload(version, url, defaultFileName);
                    break;
                case ModProjectType.ResourcePack:
                    HandleSimpleDownload(version, url, defaultFileName, GetResourcePackTargetDirectory());
                    break;
                case ModProjectType.Shader:
                    HandleSimpleDownload(version, url, defaultFileName, GetShaderTargetDirectory());
                    break;
                case ModProjectType.DataPack:
                    HandleDataPackDownload(version, url, defaultFileName);
                    break;
                case ModProjectType.Modpack:
                    HandleModpackDownload(version, url, defaultFileName);
                    break;
                default:
                    HandleSimpleDownload(version, url, defaultFileName,
                        Path.Combine(_minecraftDir, "mods"));
                    break;
            }
        }

        /// <summary>
        /// Mod 下载入口：
        ///   · 未开启版本隔离 → 直接下载到 .minecraft\mods
        ///   · 开启版本隔离   → 扫描本地版本，列出所有符合该 Mod 要求的实例，让用户选择
        /// </summary>
        private void HandleModDownload(ModVersion version, string url, string fileName)
        {
            // 实时读设置，避免用户中途改设置导致判断过时
            bool isolated = SettingsManager.GetIsolationGameData();

            // ---------- 未隔离：直接下到 .minecraft\mods ----------
            if (!isolated)
            {
                string targetDir = Path.Combine(_minecraftDir, "mods");
                HandleSimpleDownload(version, url, fileName, targetDir);
                return;
            }

            // ---------- 已隔离：找出所有符合要求的版本实例 ----------
            var all = LocalVersionScanner.Scan();
            var matched = new List<LocalVersionInfo>();

            foreach (var lv in all)
            {
                if (MatchModRequirements(lv, version))
                    matched.Add(lv);
            }

            // 没有任何匹配 → 询问是否直接下到 .minecraft\mods
            if (matched.Count == 0)
            {
                string gameReq = version.GameVersions != null && version.GameVersions.Count > 0
                    ? string.Join(" / ", version.GameVersions.ToArray()) : "不限";
                string loaderReq = version.Loaders != null && version.Loaders.Count > 0
                    ? string.Join(" / ", version.Loaders.ToArray()) : "不限";

                var r = MessageBox.Show(
                    "没有找到符合此 Mod 要求的版本实例。\n\n" +
                    "Mod 要求游戏版本：" + gameReq + "\n" +
                    "Mod 要求加载器：" + loaderReq + "\n\n" +
                    "是否改为直接下载到 .minecraft\\mods 目录？\n" +
                    "（注意：开启版本隔离时，该目录下的 Mod 默认不会被加载）",
                    "未找到匹配版本",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (r != MessageBoxResult.Yes) return;

                HandleSimpleDownload(version, url, fileName,
                    Path.Combine(_minecraftDir, "mods"));
                return;
            }

            // ---------- 弹出选择窗口 ----------
            var dlg = new ModInstallTargetWindow(matched, version)
            {
                Owner = this
            };

            if (dlg.ShowDialog() != true) return;
            var sel = dlg.SelectedVersion;
            if (sel == null) return;

            string target = Path.Combine(sel.FolderPath, "mods");
            HandleSimpleDownload(version, url, fileName, target);
        }

        /// <summary>
        /// 判断本地版本是否满足 Mod 的 GameVersions / Loaders 要求。
        /// · GameVersions 为空 → 视为不限，匹配通过
        /// · Loaders 为空     → 视为不限，匹配通过
        /// </summary>
        private static bool MatchModRequirements(LocalVersionInfo local, ModVersion mod)
        {
            if (local == null || mod == null) return false;

            // 游戏版本
            if (mod.GameVersions != null && mod.GameVersions.Count > 0)
            {
                bool hit = false;
                foreach (var g in mod.GameVersions)
                {
                    if (string.Equals(g, local.MinecraftVersion,
                            StringComparison.OrdinalIgnoreCase))
                    { hit = true; break; }
                }
                if (!hit) return false;
            }

            // 加载器
            if (mod.Loaders != null && mod.Loaders.Count > 0)
            {
                string loaderLower = (local.LoaderType ?? "vanilla").ToLowerInvariant();
                bool hit = false;
                foreach (var l in mod.Loaders)
                {
                    if (string.Equals(l, loaderLower, StringComparison.OrdinalIgnoreCase))
                    { hit = true; break; }
                }
                if (!hit) return false;
            }

            return true;
        }

        private void HandleSimpleDownload(ModVersion version, string url,
            string fileName, string targetDir)
        {
            if (string.IsNullOrEmpty(targetDir)) return;
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            string savePath = Path.Combine(targetDir, fileName);
            _ = DownloadFileAsync(url, savePath, version.VersionNumber);
        }

        private void HandleDataPackDownload(ModVersion version, string url, string fileName)
        {
            string savesDir = Path.Combine(_minecraftDir, "saves");
            if (!Directory.Exists(savesDir))
            {
                MessageBox.Show("未找到 saves 文件夹，请先创建世界。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var worlds = Directory.GetDirectories(savesDir);
            if (worlds.Length == 0)
            {
                MessageBox.Show("未找到任何存档，请先创建世界。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string worldPath = worlds[0];
            string datapackDir = Path.Combine(worldPath, "datapacks");
            if (!Directory.Exists(datapackDir)) Directory.CreateDirectory(datapackDir);

            string savePath = Path.Combine(datapackDir, fileName);
            var r = MessageBox.Show("即将下载数据包到：\n" + savePath + "\n是否继续？",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
                _ = DownloadFileAsync(url, savePath, version.VersionNumber);
        }

        private void HandleModpackDownload(ModVersion version, string url, string fileName)
        {
            string cacheDir = Path.Combine(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Launcher Setting"),
                "ModPack");
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
            string cachedFilePath = Path.Combine(cacheDir, fileName);

            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            dlg.Description = "选择整合包解压目标目录（建议选 versions）";
            string defaultPath = Path.Combine(_minecraftDir, "versions");
            dlg.SelectedPath = Directory.Exists(defaultPath) ? defaultPath : _minecraftDir;

            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string extractTarget = dlg.SelectedPath;

            if (File.Exists(cachedFilePath))
            {
                StatusText.Text = "使用缓存整合包：" + fileName;
                _ = ProcessModpackAsync(cachedFilePath, version, extractTarget);
                return;
            }

            _ = DownloadModpackAsync(url, cachedFilePath, version, extractTarget);
        }

        private async Task DownloadModpackAsync(string url, string savePath,
            ModVersion version, string extractTarget)
        {
            StatusText.Text = "下载整合包中...";
            try
            {
                await Task.Run(() => DownloadFileWithRetry(url, savePath, null, 3));
                await ProcessModpackAsync(savePath, version, extractTarget);
            }
            catch (Exception ex)
            {
                MessageBox.Show("整合包下载失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DownloadFileAsync(string url, string savePath, string versionNumber)
        {
            StatusText.Text = "正在准备下载 " + versionNumber + "...";

            try
            {
                // 先镜像，失败回退原 URL
                bool ok = await DownloadWithProgressAsync(
                    ModApiService.GetMirrorUrl(url), savePath, versionNumber);

                if (!ok)
                {
                    ok = await DownloadWithProgressAsync(url, savePath, versionNumber);
                    if (!ok) throw new Exception("所有下载源均不可用");
                }

                StatusText.Text = "下载完成：" + Path.GetFileName(savePath);
                MessageBox.Show("下载完成：\n" + savePath, "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText.Text = "下载失败";
                MessageBox.Show("下载失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 带进度回调的下载：状态栏显示"已下载 / 总大小"。
        /// 返回 true 表示成功，false 表示失败（会清理半截文件）。
        /// </summary>
        private async Task<bool> DownloadWithProgressAsync(
            string url, string savePath, string versionNumber)
        {
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "JerryStudioLauncher/1.0");

                    wc.DownloadProgressChanged += (s, e) =>
                    {
                        long total = e.TotalBytesToReceive;
                        long received = e.BytesReceived;
                        int pct = total > 0 ? (int)(received * 100 / total) : 0;

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (total > 0)
                            {
                                StatusText.Text = "正在下载 " + versionNumber + "：" +
                                    pct + "%  (" + FormatSize(received) +
                                    " / " + FormatSize(total) + ")";
                            }
                            else
                            {
                                StatusText.Text = "正在下载 " + versionNumber + "：" +
                                    FormatSize(received);
                            }
                        }));
                    };

                    await wc.DownloadFileTaskAsync(url, savePath);
                    return true;
                }
            }
            catch
            {
                try { if (File.Exists(savePath)) File.Delete(savePath); } catch { }
                return false;
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024)
                return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }

        // ============================================================
        //   整合包处理
        // ============================================================
        private async Task ProcessModpackAsync(string filePath, ModVersion version,
            string extractTarget)
        {
            await Task.Run(() =>
            {
                try
                {
                    string extractTemp = Path.Combine(Path.GetTempPath(),
                        "modpack_extract_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(extractTemp);

                    string overridesDir = Path.Combine(extractTemp, "overrides");

                    ExtractZipSafely(filePath, extractTemp);

                    string indexJsonPath = Path.Combine(extractTemp, "modrinth.index.json");
                    if (File.Exists(indexJsonPath))
                    {
                        string json = File.ReadAllText(indexJsonPath);
                        var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                        var index = ser.Deserialize<Dictionary<string, object>>(json);
                        if (index == null) throw new Exception("modrinth.index.json 解析失败");

                        var deps = JsonHelper.GetObject(index, "dependencies");
                        string mcVersion = JsonHelper.GetString(deps, "minecraft");

                        string loaderType = "vanilla";
                        string loaderVersion = null;

                        string neo = JsonHelper.GetString(deps, "neoforge");
                        string forge = JsonHelper.GetString(deps, "forge");
                        string fabric = JsonHelper.GetString(deps, "fabric-loader");
                        string quilt = JsonHelper.GetString(deps, "quilt-loader");

                        if (!string.IsNullOrEmpty(neo))
                        {
                            loaderType = "neoforge"; loaderVersion = neo;
                        }
                        else if (!string.IsNullOrEmpty(forge))
                        {
                            loaderType = "forge"; loaderVersion = forge;
                            if (!loaderVersion.Contains("-"))
                                loaderVersion = mcVersion + "-" + loaderVersion;
                        }
                        else if (!string.IsNullOrEmpty(fabric))
                        {
                            loaderType = "fabric"; loaderVersion = fabric;
                        }
                        else if (!string.IsNullOrEmpty(quilt))
                        {
                            loaderType = "quilt"; loaderVersion = quilt;
                        }

                        if (loaderType == "vanilla" && version.Loaders != null &&
                            version.Loaders.Count > 0)
                        {
                            string apiLoader = version.Loaders.FirstOrDefault(l =>
                                l.Equals("fabric", StringComparison.OrdinalIgnoreCase) ||
                                l.Equals("forge", StringComparison.OrdinalIgnoreCase) ||
                                l.Equals("quilt", StringComparison.OrdinalIgnoreCase) ||
                                l.Equals("neoforge", StringComparison.OrdinalIgnoreCase));
                            if (!string.IsNullOrEmpty(apiLoader))
                            {
                                loaderType = apiLoader.ToLowerInvariant();
                                loaderVersion = "latest";
                            }
                        }

                        string modsDirForDetection = Path.Combine(overridesDir, "mods");
                        if (loaderType == "vanilla" && Directory.Exists(modsDirForDetection))
                        {
                            foreach (var jar in Directory.GetFiles(modsDirForDetection, "*.jar"))
                            {
                                string fn = Path.GetFileName(jar).ToLowerInvariant();
                                if (fn.Contains("fabric") || fn.Contains("fabric-api") ||
                                    fn.Contains("sodium") || fn.Contains("iris"))
                                { loaderType = "fabric"; loaderVersion = "latest"; break; }
                                if (fn.Contains("quilt"))
                                { loaderType = "quilt"; loaderVersion = "latest"; break; }
                                if (fn.Contains("neoforge"))
                                { loaderType = "neoforge"; loaderVersion = "latest"; break; }
                                if (fn.Contains("forge"))
                                { loaderType = "forge"; loaderVersion = "latest"; break; }
                            }
                        }

                        if (string.IsNullOrEmpty(mcVersion))
                            throw new Exception("无法从 modrinth.index.json 中解析 Minecraft 版本号");

                        string versionId = null;
                        string originalLoaderType = loaderType;

                        if (loaderVersion == "latest")
                        {
                            string latest = GetLatestLoaderVersion(loaderType, mcVersion);
                            if (!string.IsNullOrEmpty(latest))
                            {
                                loaderVersion = latest;
                            }
                            else
                            {
                                versionId = mcVersion;
                                Install_Minecraft_Versions.VersionInstaller.Run(
                                    "client", "Vanilla", mcVersion, _minecraftDir, "none");
                            }
                        }

                        if (loaderVersion != "latest" && !string.IsNullOrEmpty(loaderVersion) &&
                            loaderType != "vanilla")
                        {
                            string loaderArg;
                            switch (loaderType)
                            {
                                case "forge":
                                    loaderArg = "Forge[" + ExtractForgeShort(loaderVersion) + "]";
                                    break;
                                case "fabric":
                                    loaderArg = "Fabric[" + loaderVersion + "]";
                                    break;
                                case "quilt":
                                    loaderArg = "Quilt[" + loaderVersion + "]";
                                    break;
                                case "neoforge":
                                    loaderArg = "NeoForge[" + loaderVersion + "]";
                                    break;
                                default:
                                    loaderArg = "Vanilla";
                                    break;
                            }

                            Install_Minecraft_Versions.VersionInstaller.Run(
                                "client", loaderArg, mcVersion, _minecraftDir, "none");

                            versionId = GuessVersionId(loaderType, loaderVersion, mcVersion);
                        }
                        else if (string.IsNullOrEmpty(versionId) && loaderType != "vanilla")
                        {
                            versionId = mcVersion;
                            Install_Minecraft_Versions.VersionInstaller.Run(
                                "client", "Vanilla", mcVersion, _minecraftDir, "none");
                        }

                        string packName = JsonHelper.GetString(index, "name") ?? "Modpack";
                        foreach (char c in Path.GetInvalidFileNameChars())
                            packName = packName.Replace(c, '_');
                        if (string.IsNullOrEmpty(packName) || packName.Trim().Length == 0)
                            packName = "Modpack_" + Guid.NewGuid().ToString("N").Substring(0, 8);

                        string packDir = Path.Combine(extractTarget, packName);
                        Directory.CreateDirectory(packDir);

                        if (versionId != null)
                        {
                            string loaderDir = Path.Combine(
                                Path.Combine(_minecraftDir, "versions"), versionId);
                            string loaderJsonPath = Path.Combine(loaderDir, versionId + ".json");
                            if (File.Exists(loaderJsonPath))
                            {
                                string loaderJson = File.ReadAllText(loaderJsonPath);
                                var ser2 = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                                var jsonObj = ser2.Deserialize<Dictionary<string, object>>(loaderJson);
                                if (jsonObj != null)
                                {
                                    jsonObj["id"] = packName;
                                    if (!jsonObj.ContainsKey("inheritsFrom"))
                                        jsonObj["inheritsFrom"] = mcVersion;
                                    File.WriteAllText(Path.Combine(packDir, packName + ".json"),
                                        ser2.Serialize(jsonObj), Encoding.UTF8);
                                }
                            }

                            string loaderJar = Path.Combine(loaderDir, versionId + ".jar");
                            string targetJar = Path.Combine(packDir, packName + ".jar");
                            if (File.Exists(loaderJar) && !File.Exists(targetJar))
                                File.Copy(loaderJar, targetJar, true);
                        }

                        if (Directory.Exists(overridesDir))
                            CopyDirectory(overridesDir, packDir, true);
                        else
                        {
                            foreach (var dir in Directory.GetDirectories(extractTemp))
                            {
                                string dn = Path.GetFileName(dir);
                                if (dn != "overrides" && dn != "META-INF")
                                    CopyDirectory(dir, packDir, true);
                            }
                            foreach (var file in Directory.GetFiles(extractTemp))
                            {
                                string fn = Path.GetFileName(file);
                                if (fn != "modrinth.index.json")
                                    File.Copy(file, Path.Combine(packDir, fn), true);
                            }
                        }

                        string modsDir = Path.Combine(packDir, "mods");
                        CleanCorruptedFiles(modsDir);

                        var tasks = new List<DownloadTaskWithSha>();
                        var filesArr = JsonHelper.GetArray(index, "files");
                        if (filesArr != null)
                        {
                            foreach (var fileEntry in filesArr)
                            {
                                string path = JsonHelper.GetString(fileEntry, "path");
                                var downloads = JsonHelper.GetArray(fileEntry, "downloads");
                                string downloadUrl = downloads != null && downloads.Count > 0
                                    ? Convert.ToString(downloads[0]) : null;
                                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(downloadUrl))
                                    continue;

                                var hashes = JsonHelper.GetObject(fileEntry, "hashes");
                                string sha1 = JsonHelper.GetString(hashes, "sha1");
                                if (string.IsNullOrEmpty(sha1))
                                    sha1 = JsonHelper.GetString(fileEntry, "sha1");

                                string targetPath = Path.Combine(packDir,
                                    path.Replace('/', Path.DirectorySeparatorChar));
                                string targetDir = Path.GetDirectoryName(targetPath);
                                if (!Directory.Exists(targetDir))
                                    Directory.CreateDirectory(targetDir);

                                if (File.Exists(targetPath) && IsValidJarFile(targetPath, sha1))
                                    continue;

                                tasks.Add(new DownloadTaskWithSha
                                {
                                    Url = downloadUrl,
                                    SavePath = targetPath,
                                    FileName = Path.GetFileName(path),
                                    ExpectedSha1 = sha1
                                });
                            }
                        }

                        var failedFiles = new List<string>();
                        object lockFailed = new object();
                        int total = tasks.Count;
                        int completed = 0;
                        int failedCount = 0;

                        if (total > 0)
                        {
                            this.Dispatcher.Invoke(() =>
                                StatusText.Text = "正在下载文件: 0/" + total);

                            var handles = new List<ManualResetEvent>();
                            int maxC = Math.Min(10, total);

                            using (var sem = new Semaphore(maxC, maxC))
                            {
                                foreach (var task in tasks)
                                {
                                    var done = new ManualResetEvent(false);
                                    handles.Add(done);

                                    ThreadPool.QueueUserWorkItem(_ =>
                                    {
                                        try
                                        {
                                            sem.WaitOne();
                                            bool ok = DownloadFileWithRetry(
                                                task.Url, task.SavePath, task.ExpectedSha1, 3);
                                            if (ok)
                                            {
                                                Interlocked.Increment(ref completed);
                                                int c = completed;
                                                this.Dispatcher.Invoke(() =>
                                                    StatusText.Text = "正在下载文件["
                                                        + c + "/" + total + "]: " + task.FileName);
                                            }
                                            else
                                            {
                                                lock (lockFailed) failedFiles.Add(task.FileName);
                                                Interlocked.Increment(ref failedCount);
                                            }
                                        }
                                        catch
                                        {
                                            lock (lockFailed) failedFiles.Add(task.FileName);
                                            Interlocked.Increment(ref failedCount);
                                        }
                                        finally
                                        {
                                            sem.Release();
                                            done.Set();
                                        }
                                    });
                                }

                                foreach (var h in handles) { h.WaitOne(); h.Close(); }
                            }

                            this.Dispatcher.Invoke(() =>
                                StatusText.Text = "下载完成，成功 " + completed
                                    + " 个，失败 " + failedCount + " 个");
                        }

                        if (failedFiles.Count > 0)
                        {
                            var finalFailed = new List<string>();
                            var retryTasks = tasks.Where(t => failedFiles.Contains(t.FileName)).ToList();
                            foreach (var t in retryTasks)
                            {
                                if (!DownloadFileWithRetry(t.Url, t.SavePath, t.ExpectedSha1, 2))
                                    finalFailed.Add(t.FileName);
                            }

                            if (finalFailed.Count > 0)
                            {
                                this.Dispatcher.Invoke(() =>
                                    MessageBox.Show(
                                        "以下文件下载失败，请手动下载：\n"
                                        + string.Join("\n", finalFailed.ToArray()),
                                        "整合包下载不完整",
                                        MessageBoxButton.OK, MessageBoxImage.Warning));
                            }
                        }

                        CleanCorruptedFiles(modsDir);
                        DownloadMissingDependencies(packDir, mcVersion, originalLoaderType);

                        try { Directory.Delete(extractTemp, true); } catch { }

                        this.Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = "整合包安装完成：" + packDir;
                            MessageBox.Show(
                                "整合包已安装到：\n" + packDir
                                + "\n\n现在可以在启动器中选中 '" + packName + "' 启动。",
                                "安装成功", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
                    }
                    else
                    {
                        foreach (var dir in Directory.GetDirectories(extractTemp))
                        {
                            if (Path.GetFileName(dir) != "META-INF")
                                CopyDirectory(dir, extractTarget, true);
                        }
                        foreach (var file in Directory.GetFiles(extractTemp))
                        {
                            if (Path.GetFileName(file) != "modrinth.index.json")
                                File.Copy(file,
                                    Path.Combine(extractTarget, Path.GetFileName(file)), true);
                        }
                        try { Directory.Delete(extractTemp, true); } catch { }

                        this.Dispatcher.Invoke(() =>
                            MessageBox.Show("整合包已解压（缺少 modrinth.index.json）。",
                                "提示", MessageBoxButton.OK, MessageBoxImage.Warning));
                    }
                }
                catch (Exception ex)
                {
                    this.Dispatcher.Invoke(() =>
                        MessageBox.Show("处理整合包失败：" + ex.Message, "错误",
                            MessageBoxButton.OK, MessageBoxImage.Error));
                }
            });
        }

        private void DownloadMissingDependencies(string packDir, string mcVersion, string loaderType)
        {
            string modsDir = Path.Combine(packDir, "mods");
            if (!Directory.Exists(modsDir)) return;

            CleanCorruptedFiles(modsDir);

            var installed = new HashSet<string>();
            foreach (var file in Directory.GetFiles(modsDir, "*.jar"))
            {
                string id = ExtractModIdFromJar(file);
                if (!string.IsNullOrEmpty(id)) installed.Add(id);
            }

            var missing = new HashSet<string>();
            foreach (var file in Directory.GetFiles(modsDir, "*.jar"))
            {
                var deps = ExtractDependenciesFromJar(file);
                foreach (var dep in deps)
                {
                    if (dep == "minecraft" || dep == "java"
                        || dep == "fabricloader" || dep == "forge") continue;
                    if (!installed.Contains(dep)) missing.Add(dep);
                }
            }

            if (loaderType.Equals("fabric", StringComparison.OrdinalIgnoreCase))
            {
                bool hasFabricApiModules = missing.Any(d =>
                    d.StartsWith("fabric-") &&
                    !d.Equals("fabricloader", StringComparison.OrdinalIgnoreCase) &&
                    !d.Equals("fabric-carpet", StringComparison.OrdinalIgnoreCase) &&
                    !d.Equals("fabric-language-kotlin", StringComparison.OrdinalIgnoreCase));

                if (hasFabricApiModules && !installed.Contains("fabric-api"))
                {
                    string url = SearchAndGetDownloadUrl("fabric-api", mcVersion, loaderType);
                    if (!string.IsNullOrEmpty(url))
                    {
                        string savePath = Path.Combine(modsDir, "fabric-api-latest.jar");
                        if (DownloadFileWithRetry(url, savePath, null, 3))
                        {
                            installed.Add("fabric-api");
                            missing.RemoveWhere(d =>
                                d.StartsWith("fabric-") &&
                                !d.Equals("fabricloader", StringComparison.OrdinalIgnoreCase) &&
                                !d.Equals("fabric-carpet", StringComparison.OrdinalIgnoreCase) &&
                                !d.Equals("fabric-language-kotlin", StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }
            }

            foreach (var modId in missing)
            {
                try
                {
                    string url = SearchAndGetDownloadUrl(modId, mcVersion, loaderType);
                    if (string.IsNullOrEmpty(url)) continue;

                    string savePath = Path.Combine(modsDir, modId + "-" + mcVersion + ".jar");
                    DownloadFileWithRetry(url, savePath, null, 3);
                }
                catch { }
            }

            CleanCorruptedFiles(modsDir);
        }

        private string GetLatestLoaderVersion(string loaderType, string mcVersion)
        {
            try
            {
                string url;
                switch (loaderType.ToLowerInvariant())
                {
                    case "forge":
                        url = "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/maven-metadata.xml";
                        break;
                    case "fabric":
                        url = "https://meta.fabricmc.net/v2/versions/loader/" + mcVersion;
                        break;
                    case "quilt":
                        url = "https://meta.quiltmc.org/v3/versions/loader/" + mcVersion;
                        break;
                    case "neoforge":
                        url = "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml";
                        break;
                    default: return null;
                }

                using (var wc = new WebClient())
                {
                    string data = wc.DownloadString(url);

                    if (loaderType == "forge" || loaderType == "neoforge")
                    {
                        string prefix = loaderType == "forge"
                            ? mcVersion + "-"
                            : (mcVersion.StartsWith("1.")
                                ? mcVersion.Substring(2) + "."
                                : mcVersion + ".");

                        var matches = Regex.Matches(data, @"<version>([^<]+)</version>");
                        string best = null;
                        foreach (Match m in matches)
                        {
                            string v = m.Groups[1].Value;
                            if (!v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                            if (v.Contains("-pre") || v.Contains("-beta") || v.Contains("-alpha"))
                                continue;
                            if (best == null || string.Compare(v, best) > 0) best = v;
                        }
                        if (best != null && loaderType == "forge")
                            return best.Substring(prefix.Length);
                        return best;
                    }
                    else
                    {
                        var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                        var arr = ser.Deserialize<System.Collections.ArrayList>(data);
                        if (arr != null && arr.Count > 0)
                        {
                            var loader = JsonHelper.GetObject(arr[0], "loader");
                            return JsonHelper.GetString(loader, "version");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[整合包] 获取 " + loaderType + " 最新版本失败：" + ex.Message);
            }
            return null;
        }

        private static string GuessVersionId(string loaderType, string loaderVersion, string mcVersion)
        {
            switch (loaderType.ToLowerInvariant())
            {
                case "forge": return mcVersion + "-forge-" + ExtractForgeShort(loaderVersion);
                case "neoforge": return loaderVersion;
                case "fabric": return "fabric-loader-" + loaderVersion + "-" + mcVersion;
                case "quilt": return "quilt-loader-" + loaderVersion + "-" + mcVersion;
                default: return mcVersion;
            }
        }

        private static string ExtractForgeShort(string forge)
        {
            if (string.IsNullOrEmpty(forge)) return "";
            int dash = forge.IndexOf('-');
            if (dash > 0 && dash < forge.Length - 1)
                return forge.Substring(dash + 1);
            return forge;
        }

        private string SearchAndGetDownloadUrl(string modId, string mcVersion, string loaderType)
        {
            try
            {
                string searchUrl = "https://api.modrinth.com/v2/search?query="
                    + Uri.EscapeDataString(modId) + "&limit=1";

                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

                using (var client = new WebClient())
                {
                    string json = client.DownloadString(searchUrl);
                    var root = ser.Deserialize<Dictionary<string, object>>(json);
                    var hits = JsonHelper.GetArray(root, "hits");
                    if (hits == null || hits.Count == 0) return null;

                    string projectId = JsonHelper.GetString(hits[0], "project_id");
                    if (string.IsNullOrEmpty(projectId)) return null;

                    string versionsUrl = "https://api.modrinth.com/v2/project/"
                        + projectId + "/version";
                    string versionsJson = client.DownloadString(versionsUrl);
                    var versions = ser.Deserialize<ArrayList>(versionsJson);
                    if (versions == null) return null;

                    foreach (var version in versions)
                    {
                        var gameVersions = JsonHelper.GetArray(version, "game_versions");
                        var loaders = JsonHelper.GetArray(version, "loaders");
                        if (gameVersions == null || loaders == null) continue;

                        bool hitVersion = false;
                        foreach (var g in gameVersions)
                            if (Convert.ToString(g) == mcVersion) { hitVersion = true; break; }
                        if (!hitVersion) continue;

                        bool hitLoader = false;
                        foreach (var l in loaders)
                            if (Convert.ToString(l).Equals(loaderType,
                                StringComparison.OrdinalIgnoreCase)) { hitLoader = true; break; }
                        if (!hitLoader) continue;

                        var files = JsonHelper.GetArray(version, "files");
                        if (files == null || files.Count == 0) continue;

                        foreach (var f in files)
                            if (JsonHelper.GetBool(f, "primary"))
                                return JsonHelper.GetString(f, "url");

                        return JsonHelper.GetString(files[0], "url");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[搜索] " + modId + " 失败：" + ex.Message);
            }
            return null;
        }

        private string ExtractModIdFromJar(string jarPath)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(jarPath))
                {
                    var fabricEntry = zip.GetEntry("fabric.mod.json");
                    if (fabricEntry != null)
                    {
                        using (var stream = fabricEntry.Open())
                        using (var reader = new StreamReader(stream))
                        {
                            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                            var obj = ser.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                            return JsonHelper.GetString(obj, "id");
                        }
                    }

                    var forgeEntry = zip.GetEntry("META-INF/mods.toml");
                    if (forgeEntry != null)
                    {
                        using (var stream = forgeEntry.Open())
                        using (var reader = new StreamReader(stream))
                        {
                            string toml = reader.ReadToEnd();
                            var m = Regex.Match(toml, @"modId\s*=\s*""([^""]+)""");
                            if (m.Success) return m.Groups[1].Value;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private List<string> ExtractDependenciesFromJar(string jarPath)
        {
            var deps = new List<string>();
            try
            {
                using (var zip = ZipFile.OpenRead(jarPath))
                {
                    var forgeEntry = zip.GetEntry("META-INF/mods.toml");
                    if (forgeEntry != null)
                    {
                        using (var stream = forgeEntry.Open())
                        using (var reader = new StreamReader(stream))
                        {
                            string toml = reader.ReadToEnd();
                            var ms = Regex.Matches(toml,
                                @"^\[+dependencies\.([\w.]+)\]+\s*$", RegexOptions.Multiline);
                            foreach (Match m in ms)
                                if (m.Groups.Count > 1) deps.Add(m.Groups[1].Value);
                        }
                    }

                    var fabricEntry = zip.GetEntry("fabric.mod.json");
                    if (fabricEntry != null)
                    {
                        using (var stream = fabricEntry.Open())
                        using (var reader = new StreamReader(stream))
                        {
                            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                            var obj = ser.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                            var depends = JsonHelper.GetObject(obj, "depends");
                            if (depends != null)
                            {
                                foreach (var kv in depends)
                                {
                                    if (kv.Key != "minecraft" && kv.Key != "java"
                                        && kv.Key != "fabricloader")
                                        deps.Add(kv.Key);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return deps;
        }

        private bool IsValidJarFile(string filePath, string expectedSha1 = null)
        {
            try
            {
                if (!File.Exists(filePath)) return false;

                if (!string.IsNullOrEmpty(expectedSha1))
                {
                    using (var fs = File.OpenRead(filePath))
                    using (var sha1 = System.Security.Cryptography.SHA1.Create())
                    {
                        byte[] hash = sha1.ComputeHash(fs);
                        string actual = BitConverter.ToString(hash).Replace("-", "").ToLower();
                        if (!actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                }

                using (var zip = ZipFile.OpenRead(filePath))
                {
                    return true;
                }
            }
            catch { return false; }
        }

        private void CleanCorruptedFiles(string directory)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.GetFiles(directory, "*.jar"))
            {
                if (!IsValidJarFile(file))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }

        private bool DownloadFileWithRetry(string url, string savePath,
            string expectedSha1 = null, int maxRetries = 3)
        {
            string dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(savePath) && IsValidJarFile(savePath, expectedSha1))
                return true;

            if (File.Exists(savePath))
            {
                try { File.Delete(savePath); } catch { }
            }

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    string u = (attempt == 0) ? ModApiService.GetMirrorUrl(url) : url;
                    using (var client = new WebClient())
                    {
                        client.Headers.Add("User-Agent", "JerryStudioLauncher/1.0");
                        client.DownloadFile(u, savePath);
                    }

                    if (IsValidJarFile(savePath, expectedSha1)) return true;
                    try { File.Delete(savePath); } catch { }
                }
                catch
                {
                    try { if (File.Exists(savePath)) File.Delete(savePath); } catch { }
                }

                if (attempt < maxRetries - 1)
                    Thread.Sleep(2000 * (attempt + 1));
            }

            return false;
        }

        private static void ExtractZipSafely(string zipPath, string targetDir)
        {
            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string dest = Path.Combine(targetDir,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    string destDir = Path.GetDirectoryName(dest);
                    if (!Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    try { entry.ExtractToFile(dest, true); }
                    catch { }
                }
            }
        }

        private static void CopyDirectory(string sourceDir, string targetDir, bool overwrite)
        {
            if (!Directory.Exists(sourceDir)) return;
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite);
            foreach (var dir in Directory.GetDirectories(sourceDir))
                CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)), overwrite);
        }

        private string GetModTargetDirectory()
        {
            if (!_isVersionIsolated)
                return Path.Combine(_minecraftDir, "mods");

            if (string.IsNullOrEmpty(_currentVersion))
            {
                MessageBox.Show("没有选择版本，无法定位 mods 目录。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return Path.Combine(
                Path.Combine(Path.Combine(_minecraftDir, "versions"), _currentVersion), "mods");
        }

        private string GetResourcePackTargetDirectory()
        {
            if (!_isVersionIsolated) return Path.Combine(_minecraftDir, "resourcepacks");
            return Path.Combine(
                Path.Combine(Path.Combine(_minecraftDir, "versions"), _currentVersion),
                "resourcepacks");
        }

        private string GetShaderTargetDirectory()
        {
            if (!_isVersionIsolated) return Path.Combine(_minecraftDir, "shaderpacks");
            return Path.Combine(
                Path.Combine(Path.Combine(_minecraftDir, "versions"), _currentVersion),
                "shaderpacks");
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private class DownloadTaskWithSha
        {
            public string Url { get; set; }
            public string SavePath { get; set; }
            public string FileName { get; set; }
            public string ExpectedSha1 { get; set; }
        }
    }
}