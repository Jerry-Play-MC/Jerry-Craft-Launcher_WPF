using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
using System.Windows.Threading;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class ClientInstallWindow : Window
    {
        private static readonly string[] LoaderTypes = {
            "Vanilla", "Forge", "NeoForge", "Fabric", "Quilt"
        };

        private static readonly HashSet<string> LoadersIncompatibleWithOptifine =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "NeoForge", "Fabric", "Quilt"
            };

        private const string BMCL_BASE = "https://bmclapi2.bangbang93.com";
        private const string FORGE_MAVEN = "https://maven.minecraftforge.net";
        private const string NEOFORGE_MAVEN = "https://maven.neoforged.net/releases";

        private class LoaderVersionEntry
        {
            public string Version { get; set; }
            public string Display { get; set; }
            public override string ToString() { return Display; }
        }

        private class OptiFineItem
        {
            public string McVersion { get; set; }
            public string Patch { get; set; }
            public string Type { get; set; }
            public string FileName { get; set; }
            public string Forge { get; set; }

            public bool IsForgeUniversal
            {
                get
                {
                    if (string.IsNullOrEmpty(Forge)) return false;
                    return Forge.Equals("N/A", StringComparison.OrdinalIgnoreCase);
                }
            }

            public string Display
            {
                get
                {
                    string name = string.IsNullOrEmpty(Type) ? Patch : Type + "_" + Patch;
                    if (IsForgeUniversal) return name;
                    if (string.IsNullOrEmpty(Forge)) return name;
                    return name + " (" + LanguageManager.Get("ClientInstall.WithForge")
                        + " " + ExtractForgeShort(Forge) + ")";
                }
            }

            public override string ToString() { return Display; }
        }

        private class LoaderVersionResult
        {
            public List<string> Versions = new List<string>();
            public string Error;
        }

        private readonly string _gameVersion;
        private List<OptiFineItem> _allOptifine = new List<OptiFineItem>();
        private bool _loadingOptifine;
        private bool _suppressEvents;

        private readonly Dictionary<string, LoaderVersionResult> _loaderVersionCache =
            new Dictionary<string, LoaderVersionResult>(StringComparer.OrdinalIgnoreCase);
        private bool _loadingLoaderVersions;
        private string _lastLoaderVersionError;

        private bool _installing;
        private TextWriter _originalOut;
        private TextWriter _originalErr;
        private LogWriter _uiWriter;
        private int _logLineCount;

        public ClientInstallWindow(string gameVersion)
        {
            InitializeComponent();

            _gameVersion = gameVersion ?? "";
            GameVersionBox.Text = _gameVersion;

            _suppressEvents = true;
            LoaderBox.ItemsSource = LoaderTypes;
            LoaderBox.SelectedIndex = 0;
            _suppressEvents = false;

            LoaderVersionBox.IsEnabled = false;

            Loaded += ClientInstallWindow_Loaded;
        }

        private async void ClientInstallWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadOptifineListAsync();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_installing) DragMove();
        }

        private void LoaderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (LoaderBox == null) return;

            string loader = LoaderBox.SelectedItem as string;

            bool incompatible = LoadersIncompatibleWithOptifine.Contains(loader ?? "");
            bool isVanilla = "Vanilla".Equals(loader, StringComparison.OrdinalIgnoreCase);

            _suppressEvents = true;
            try
            {
                if (incompatible) OptifineCheck.IsChecked = false;
                OptifineCheck.IsEnabled = !incompatible;
                if (OptifineCheck.IsChecked != true)
                    OptifineVersionBox.IsEnabled = false;

                LoaderVersionBox.SelectedItem = null;
                LoaderVersionBox.ItemsSource = null;
                _lastLoaderVersionError = null;

                LoaderVersionBox.IsEnabled = !isVanilla;
            }
            finally
            {
                _suppressEvents = false;
            }

            RefreshOptifineDropdown();
            UpdateHints();

            if (!isVanilla)
                _ = LoadLoaderVersionsAsync(loader);
        }

        private async Task LoadLoaderVersionsAsync(string loaderName)
        {
            if (string.IsNullOrEmpty(_gameVersion)) return;
            if ("Vanilla".Equals(loaderName, StringComparison.OrdinalIgnoreCase)) return;

            string cacheKey = loaderName + "|" + _gameVersion;

            if (_loaderVersionCache.ContainsKey(cacheKey))
            {
                var cached = _loaderVersionCache[cacheKey];
                ApplyLoaderVersions(cached.Versions);
                _lastLoaderVersionError = cached.Error;
                UpdateHints();
                return;
            }

            _loadingLoaderVersions = true;
            _lastLoaderVersionError = null;
            UpdateHints();

            LoaderVersionResult result = null;
            try
            {
                result = await Task.Run(() => FetchLoaderVersions(loaderName, _gameVersion));
            }
            catch (Exception ex)
            {
                result = new LoaderVersionResult { Error = ex.Message };
            }

            _loadingLoaderVersions = false;
            if (result == null) result = new LoaderVersionResult { Error = "Unknown" };

            _loaderVersionCache[cacheKey] = result;
            ApplyLoaderVersions(result.Versions);
            _lastLoaderVersionError = result.Error;
            UpdateHints();
        }

        private void ApplyLoaderVersions(List<string> versions)
        {
            if (LoaderVersionBox == null) return;

            var entries = new List<LoaderVersionEntry>();
            entries.Add(new LoaderVersionEntry
            {
                Version = null,
                Display = LanguageManager.Get("ClientInstall.LatestVersion")
            });
            foreach (var v in versions)
                entries.Add(new LoaderVersionEntry { Version = v, Display = v });

            _suppressEvents = true;
            try
            {
                LoaderVersionBox.ItemsSource = entries;
                LoaderVersionBox.SelectedIndex = 0;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void LoaderVersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;

            string loader = LoaderBox.SelectedItem as string;
            if ("Forge".Equals(loader, StringComparison.OrdinalIgnoreCase))
            {
                RefreshOptifineDropdown();
                UpdateHints();
            }
        }

        private static LoaderVersionResult FetchLoaderVersions(string loaderName, string mcVersion)
        {
            switch (loaderName)
            {
                case "Forge": return FetchForgeVersions(mcVersion);
                case "NeoForge": return FetchNeoForgeVersions(mcVersion);
                case "Fabric": return FetchFabricVersions(mcVersion);
                case "Quilt": return FetchQuiltVersions(mcVersion);
            }
            return new LoaderVersionResult { Error = "Unknown loader: " + loaderName };
        }

        private static LoaderVersionResult FetchForgeVersions(string mcVersion)
        {
            var result = new LoaderVersionResult();
            string[] urls = {
                BMCL_BASE + "/maven/net/minecraftforge/forge/maven-metadata.xml",
                FORGE_MAVEN + "/net/minecraftforge/forge/maven-metadata.xml",
            };

            string xml = null;
            string lastError = null;
            foreach (var url in urls)
            {
                try { xml = DownloadString(url, 30000); if (!string.IsNullOrEmpty(xml)) break; }
                catch (Exception ex) { lastError = ex.Message; }
            }

            if (string.IsNullOrEmpty(xml))
            {
                result.Error = LanguageManager.Get("ClientInstall.LoaderListFailed") + " Forge"
                    + (lastError != null ? ": " + lastError : "");
                return result;
            }

            try
            {
                string prefix = mcVersion + "-";
                var matches = Regex.Matches(xml, @"<version>([^<]+)</version>");
                foreach (Match m in matches)
                {
                    string v = m.Groups[1].Value;
                    if (!v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (v.IndexOf("-pre", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (v.IndexOf("-beta", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (v.IndexOf("-alpha", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    result.Versions.Add(v.Substring(prefix.Length));
                }
                result.Versions.Sort((a, b) => CompareVersionNumbers(b, a));
            }
            catch (Exception ex)
            {
                result.Error = LanguageManager.Get("ClientInstall.LoaderListFailed") + " Forge: "
                    + ex.Message;
            }

            return result;
        }

        private static LoaderVersionResult FetchNeoForgeVersions(string mcVersion)
        {
            var result = new LoaderVersionResult();
            string[] urls = {
                BMCL_BASE + "/maven/net/neoforged/neoforge/maven-metadata.xml",
                NEOFORGE_MAVEN + "/net/neoforged/neoforge/maven-metadata.xml",
            };

            string xml = null;
            string lastError = null;
            foreach (var url in urls)
            {
                try { xml = DownloadString(url, 30000); if (!string.IsNullOrEmpty(xml)) break; }
                catch (Exception ex) { lastError = ex.Message; }
            }

            if (string.IsNullOrEmpty(xml))
            {
                result.Error = LanguageManager.Get("ClientInstall.LoaderListFailed") + " NeoForge"
                    + (lastError != null ? ": " + lastError : "");
                return result;
            }

            try
            {
                string prefix;
                if (mcVersion.StartsWith("1."))
                    prefix = mcVersion.Substring(2) + ".";
                else
                    prefix = mcVersion + ".";

                var matches = Regex.Matches(xml, @"<version>([^<]+)</version>");
                foreach (Match m in matches)
                {
                    string v = m.Groups[1].Value;
                    if (!v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (v.IndexOf("-pre", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (v.IndexOf("-alpha", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    result.Versions.Add(v);
                }
                result.Versions.Sort((a, b) => CompareVersionNumbers(b, a));
            }
            catch (Exception ex)
            {
                result.Error = LanguageManager.Get("ClientInstall.LoaderListFailed") + " NeoForge: "
                    + ex.Message;
            }

            return result;
        }

        private static LoaderVersionResult FetchFabricVersions(string mcVersion)
        {
            var result = new LoaderVersionResult();
            try
            {
                string[] urls = {
                    "https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(mcVersion),
                    BMCL_BASE + "/fabric-meta/v2/versions/loader/" + Uri.EscapeDataString(mcVersion),
                };
                string json = null;
                foreach (var url in urls)
                {
                    json = DownloadString(url, 15000);
                    if (!string.IsNullOrEmpty(json)) break;
                }
                if (string.IsNullOrEmpty(json))
                {
                    result.Error = LanguageManager.Get("ClientInstall.LoaderListFailed") + " Fabric";
                    return result;
                }

                var arr = new JavaScriptSerializer().Deserialize<ArrayList>(json);
                if (arr == null) return result;

                foreach (var item in arr)
                {
                    var dict = item as Dictionary<string, object>;
                    if (dict == null || !dict.ContainsKey("loader")) continue;
                    var loader = dict["loader"] as Dictionary<string, object>;
                    if (loader == null || !loader.ContainsKey("version")) continue;
                    string v = Convert.ToString(loader["version"]);
                    if (!string.IsNullOrEmpty(v)) result.Versions.Add(v);
                }
            }
            catch (Exception ex)
            {
                result.Error = LanguageManager.Get("ClientInstall.LoaderListFailed") + " Fabric: "
                    + ex.Message;
            }
            return result;
        }

        private static LoaderVersionResult FetchQuiltVersions(string mcVersion)
        {
            var result = new LoaderVersionResult();
            try
            {
                string[] urls = {
                    "https://meta.quiltmc.org/v3/versions/loader/" + Uri.EscapeDataString(mcVersion),
                    BMCL_BASE + "/quilt-meta/v3/versions/loader/" + Uri.EscapeDataString(mcVersion),
                };
                string json = null;
                foreach (var url in urls)
                {
                    json = DownloadString(url, 15000);
                    if (!string.IsNullOrEmpty(json)) break;
                }
                if (string.IsNullOrEmpty(json))
                {
                    result.Error = LanguageManager.Get("ClientInstall.LoaderListFailed") + " Quilt";
                    return result;
                }

                var arr = new JavaScriptSerializer().Deserialize<ArrayList>(json);
                if (arr == null) return result;

                foreach (var item in arr)
                {
                    var dict = item as Dictionary<string, object>;
                    if (dict == null || !dict.ContainsKey("loader")) continue;
                    var loader = dict["loader"] as Dictionary<string, object>;
                    if (loader == null || !loader.ContainsKey("version")) continue;
                    string v = Convert.ToString(loader["version"]);
                    if (!string.IsNullOrEmpty(v)) result.Versions.Add(v);
                }
            }
            catch (Exception ex)
            {
                result.Error = LanguageManager.Get("ClientInstall.LoaderListFailed") + " Quilt: "
                    + ex.Message;
            }
            return result;
        }

        private static string DownloadString(string url, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static int CompareVersionNumbers(string a, string b)
        {
            try { return new Version(a).CompareTo(new Version(b)); }
            catch { return string.Compare(a, b, StringComparison.Ordinal); }
        }

        private void OptifineCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (OptifineCheck == null) return;

            bool want = OptifineCheck.IsChecked == true;
            string loader = LoaderBox.SelectedItem as string;

            _suppressEvents = true;
            try
            {
                if (want && LoadersIncompatibleWithOptifine.Contains(loader ?? ""))
                {
                    int idx = Array.IndexOf(LoaderTypes, "Forge");
                    if (idx >= 0) LoaderBox.SelectedIndex = idx;
                    loader = "Forge";
                }
                OptifineVersionBox.IsEnabled = want;
            }
            finally
            {
                _suppressEvents = false;
            }

            RefreshOptifineDropdown();
            UpdateHints();
        }

        private void OptifineVersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (OptifineVersionBox == null) return;

            var item = OptifineVersionBox.SelectedItem as OptiFineItem;
            if (item == null) return;

            string loader = LoaderBox.SelectedItem as string;

            _suppressEvents = true;
            try
            {
                if (!string.IsNullOrEmpty(item.Forge) && !item.IsForgeUniversal)
                {
                    if (!"Forge".Equals(loader, StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = Array.IndexOf(LoaderTypes, "Forge");
                        if (idx >= 0) LoaderBox.SelectedIndex = idx;
                    }
                    string forgeShort = ExtractForgeShort(item.Forge);
                    SelectLoaderVersionByValue(forgeShort);
                }
                else if (item.IsForgeUniversal)
                {
                    if (!"Forge".Equals(loader, StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = Array.IndexOf(LoaderTypes, "Forge");
                        if (idx >= 0) LoaderBox.SelectedIndex = idx;
                    }
                    SelectLoaderVersionByValue(null);
                }
                else
                {
                    if (!"Vanilla".Equals(loader, StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = Array.IndexOf(LoaderTypes, "Vanilla");
                        if (idx >= 0) LoaderBox.SelectedIndex = idx;
                    }
                    SelectLoaderVersionByValue(null);
                }
            }
            finally
            {
                _suppressEvents = false;
            }

            UpdateHints();
        }

        private void SelectLoaderVersionByValue(string version)
        {
            var entries = LoaderVersionBox.ItemsSource as List<LoaderVersionEntry>;
            if (entries == null) return;

            bool prev = _suppressEvents;
            _suppressEvents = true;
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (string.Equals(entries[i].Version, version, StringComparison.OrdinalIgnoreCase))
                    {
                        LoaderVersionBox.SelectedIndex = i;
                        return;
                    }
                }
                LoaderVersionBox.SelectedIndex = 0;
            }
            finally
            {
                _suppressEvents = prev;
            }
        }

        private string GetSelectedLoaderVersion()
        {
            var entry = LoaderVersionBox.SelectedItem as LoaderVersionEntry;
            return entry != null ? entry.Version : null;
        }

        private void UpdateHints()
        {
            string loader = LoaderBox.SelectedItem as string;

            if (_loadingLoaderVersions)
            {
                LoaderVersionHint.Text = string.Format(
                    LanguageManager.Get("ClientInstall.LoadingVersions"), loader ?? "");
            }
            else if (!string.IsNullOrEmpty(_lastLoaderVersionError))
            {
                LoaderVersionHint.Text = _lastLoaderVersionError;
            }
            else
            {
                switch (loader)
                {
                    case "Vanilla":
                        LoaderVersionHint.Text = LanguageManager.Get("ClientInstall.VanillaHint");
                        break;
                    case "Forge":
                        LoaderVersionHint.Text = LanguageManager.Get("ClientInstall.ForgeHint");
                        break;
                    case "NeoForge":
                    case "Fabric":
                    case "Quilt":
                        LoaderVersionHint.Text = LanguageManager.Get("ClientInstall.LoaderHint");
                        break;
                }

                if (!"Vanilla".Equals(loader, StringComparison.OrdinalIgnoreCase) &&
                    LoaderVersionBox.ItemsSource != null)
                {
                    var entries = LoaderVersionBox.ItemsSource as List<LoaderVersionEntry>;
                    int count = entries != null ? entries.Count - 1 : 0;
                    if (count > 0)
                        LoaderVersionHint.Text += string.Format(
                            LanguageManager.Get("ClientInstall.LoadedCount"), count);
                    else
                        LoaderVersionHint.Text += LanguageManager.Get("ClientInstall.NoVersions");
                }
            }

            if (OptifineCheck.IsChecked == true)
            {
                int count = OptifineVersionBox.ItemsSource == null
                    ? 0
                    : ((IEnumerable<object>)OptifineVersionBox.ItemsSource).Count();

                if (_loadingOptifine)
                    OptifineHint.Text = LanguageManager.Get("ClientInstall.OptifineLoading");
                else if (count == 0)
                    OptifineHint.Text = LanguageManager.Get("ClientInstall.OptifineNone");
                else
                    OptifineHint.Text = string.Format(
                        LanguageManager.Get("ClientInstall.OptifineLoaded"), count);
            }
            else
            {
                OptifineHint.Text = LanguageManager.Get("ClientInstall.OptifineHint");
            }
        }

        private async Task LoadOptifineListAsync()
        {
            if (string.IsNullOrEmpty(_gameVersion)) return;

            _loadingOptifine = true;
            UpdateHints();

            string url = BMCL_BASE + "/optifine/" + Uri.EscapeDataString(_gameVersion);

            List<OptiFineItem> list = null;
            try { list = await Task.Run(() => FetchOptifineList(url)); }
            catch { }

            _loadingOptifine = false;
            if (list == null) list = new List<OptiFineItem>();
            _allOptifine = list;

            RefreshOptifineDropdown();
            UpdateHints();
        }

        private static List<OptiFineItem> FetchOptifineList(string url)
        {
            var result = new List<OptiFineItem>();
            try
            {
                string json = DownloadString(url, 15000);
                if (string.IsNullOrEmpty(json)) return result;

                var arr = new JavaScriptSerializer().Deserialize<ArrayList>(json);
                if (arr == null) return result;

                foreach (var item in arr)
                {
                    var dict = item as Dictionary<string, object>;
                    if (dict == null) continue;

                    string patch = dict.ContainsKey("patch") ? Convert.ToString(dict["patch"]) : null;
                    if (string.IsNullOrEmpty(patch)) continue;

                    result.Add(new OptiFineItem
                    {
                        McVersion = dict.ContainsKey("mcversion") ? Convert.ToString(dict["mcversion"]) : "",
                        Patch = patch,
                        Type = dict.ContainsKey("type") ? Convert.ToString(dict["type"]) : "",
                        FileName = dict.ContainsKey("filename") ? Convert.ToString(dict["filename"]) : "",
                        Forge = dict.ContainsKey("forge") ? Convert.ToString(dict["forge"]) : "",
                    });
                }
            }
            catch { }

            result.Sort((a, b) => string.Compare(b.Patch, a.Patch, StringComparison.Ordinal));
            return result;
        }

        private void RefreshOptifineDropdown()
        {
            if (OptifineVersionBox == null) return;
            if (_allOptifine == null) return;

            string loader = LoaderBox.SelectedItem as string;

            List<OptiFineItem> filtered;

            if ("Forge".Equals(loader, StringComparison.OrdinalIgnoreCase))
            {
                string forgeShort = GetSelectedLoaderVersion();

                filtered = _allOptifine.Where(v =>
                {
                    if (string.IsNullOrEmpty(v.Forge)) return false;
                    if (v.IsForgeUniversal) return true;
                    if (string.IsNullOrEmpty(forgeShort)) return true;
                    return ExtractForgeShort(v.Forge)
                        .Equals(forgeShort, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }
            else
            {
                filtered = new List<OptiFineItem>(_allOptifine);
            }

            object prev = OptifineVersionBox.SelectedItem;

            _suppressEvents = true;
            try
            {
                OptifineVersionBox.ItemsSource = filtered;

                bool restored = false;
                if (prev != null)
                {
                    var same = filtered.FirstOrDefault(v =>
                        v.Patch == ((OptiFineItem)prev).Patch &&
                        v.Type == ((OptiFineItem)prev).Type);
                    if (same != null)
                    {
                        OptifineVersionBox.SelectedItem = same;
                        restored = true;
                    }
                }

                if (!restored && filtered.Count > 0)
                    OptifineVersionBox.SelectedIndex = 0;
                else if (filtered.Count == 0)
                    OptifineVersionBox.SelectedItem = null;
            }
            finally
            {
                _suppressEvents = false;
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

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;

            string gameVersion = (_gameVersion ?? "").Trim();
            string loaderName = LoaderBox.SelectedItem as string;
            string loaderVersion = (GetSelectedLoaderVersion() ?? "").Trim();
            bool withOptifine = OptifineCheck.IsChecked == true;
            var optifineItem = OptifineVersionBox.SelectedItem as OptiFineItem;
            string optifineVersion = optifineItem != null ? optifineItem.Patch : "";

            if (string.IsNullOrEmpty(gameVersion))
            {
                LanguageManager.ShowInfo("ClientInstall.NoVersion");
                return;
            }

            if (string.IsNullOrEmpty(loaderName))
                loaderName = "Vanilla";

            if (withOptifine && LoadersIncompatibleWithOptifine.Contains(loaderName))
            {
                LanguageManager.ShowWarning("ClientInstall.OptifineConflict", loaderName);
                return;
            }

            string loaderType = loaderName;
            if (!string.IsNullOrEmpty(loaderVersion) &&
                !"Vanilla".Equals(loaderName, StringComparison.OrdinalIgnoreCase))
            {
                loaderType += "[" + loaderVersion + "]";
            }

            if (withOptifine)
            {
                loaderType += "OptiFine";
                if (!string.IsNullOrEmpty(optifineVersion))
                    loaderType += "[" + optifineVersion + "]";
            }

            string gameDir = App.Config.GameDir;

            _installing = true;
            InstallButton.IsEnabled = false;
            InstallStatusText.Text = LanguageManager.Get("ClientInstall.Installing");
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
                AppendLogLines(new List<string> {
                    "==================================================",
                    "[Launcher] " + LanguageManager.Get("ClientInstall.BeginInstall"),
                    "[Launcher] " + LanguageManager.Get("ClientInstall.GameVersion")
                        + "   : " + gameVersion,
                    "[Launcher] " + LanguageManager.Get("ClientInstall.Loader")
                        + "     : " + loaderName,
                    "[Launcher] " + LanguageManager.Get("ClientInstall.LoaderVersion")
                        + " : " + (string.IsNullOrEmpty(loaderVersion)
                            ? LanguageManager.Get("ClientInstall.LatestVersionShort") : loaderVersion),
                    "[Launcher] " + LanguageManager.Get("ClientInstall.Optifine")
                        + "   : " + (withOptifine
                            ? (string.IsNullOrEmpty(optifineVersion)
                                ? LanguageManager.Get("ClientInstall.LatestVersionShort") : optifineVersion)
                            : LanguageManager.Get("ClientInstall.DoNotInstall")),
                    "[Launcher] " + LanguageManager.Get("ClientInstall.TargetDir")
                        + "   : " + gameDir,
                    "[Launcher] loaderType : " + loaderType,
                    "=================================================="
                });

                int rc = await Task.Run(() =>
                    Install_Minecraft_Versions.VersionInstaller.Run(
                        "client", loaderType, gameVersion, gameDir, "none"));

                if (_uiWriter != null) { try { _uiWriter.Flush(); } catch { } }
                await Dispatcher.Yield(DispatcherPriority.Background);

                if (rc == 0)
                {
                    AppendLogLines(new List<string> { "",
                        "[Launcher] " + LanguageManager.Get("ClientInstall.InstallDoneLog") });
                    InstallStatusText.Text = LanguageManager.Get("ClientInstall.InstallDone");

                    LanguageManager.ShowInfo("ClientInstall.InstallSuccess", gameVersion);

                    DialogResult = true;
                    Close();
                }
                else
                {
                    AppendLogLines(new List<string> { "",
                        "[Launcher] " + LanguageManager.Get("ClientInstall.InstallFailedLog")
                        + " rc=" + rc });
                    InstallStatusText.Text = LanguageManager.Get("ClientInstall.InstallFailed");

                    LanguageManager.ShowError("ClientInstall.InstallFailed");
                }
            }
            catch (Exception ex)
            {
                if (_uiWriter != null) { try { _uiWriter.Flush(); } catch { } }
                AppendLogLines(new List<string> { "",
                    "[Launcher] " + LanguageManager.Get("ClientInstall.InstallErrorLog")
                    + " " + ex.Message });
                InstallStatusText.Text = LanguageManager.Get("ClientInstall.InstallFailed");

                LanguageManager.ShowError("ClientInstall.InstallError", ex.Message);
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
            LoaderBox.IsEnabled = enabled;
            LoaderVersionBox.IsEnabled = enabled
                && !"Vanilla".Equals(LoaderBox.SelectedItem as string,
                                     StringComparison.OrdinalIgnoreCase);
            OptifineCheck.IsEnabled = enabled;
            OptifineVersionBox.IsEnabled = enabled && OptifineCheck.IsChecked == true;
        }

        private void AppendLogLines(List<string> lines)
        {
            if (lines == null || lines.Count == 0) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action<List<string>>(AppendLogLines),
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
                    new Action<List<string>>(AppendLogLines),
                    tail);
                return;
            }

            var sb = new StringBuilder(lines.Count * 64);
            foreach (var l in lines) { sb.Append(l); sb.Append("\r\n"); }

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