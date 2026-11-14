using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class ModSearchView : UserControl
    {
        private const int PageSize = 20;

        private int _currentPage = 1;
        private int _totalHits = 0;
        private string _currentKeyword = "";
        private ModProjectType _currentType = ModProjectType.Mod;
        private bool _loading;

        public ModSearchView()
        {
            InitializeComponent();
            TypeBox.SelectedIndex = 0;

            Loaded += (s, e) =>
            {
                if (ModList.Items.Count == 0)
                    _ = SearchAsync();
            };
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _currentKeyword = (SearchBox.Text ?? "").Trim();
                _currentPage = 1;
                _ = SearchAsync();
            }
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            _currentKeyword = (SearchBox.Text ?? "").Trim();
            _currentPage = 1;
            _ = SearchAsync();
        }

        private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TypeBox == null) return;
            _currentType = (ModProjectType)Math.Max(0, TypeBox.SelectedIndex);
            _currentPage = 1;
            if (IsLoaded) _ = SearchAsync();
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1) { _currentPage--; _ = SearchAsync(); }
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            int totalPages = _totalHits == 0
                ? 1 : (int)Math.Ceiling((double)_totalHits / PageSize);
            if (_currentPage < totalPages) { _currentPage++; _ = SearchAsync(); }
        }

        private void ModList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var mod = ModList.SelectedItem as ModrinthMod;
            if (mod == null) return;

            var win = new ModVersionsWindow(mod.Id, _currentType, mod.Title)
            {
                Owner = Window.GetWindow(this)
            };
            win.ShowDialog();
        }

        private async Task SearchAsync()
        {
            if (_loading) return;
            _loading = true;

            SearchButton.IsEnabled = false;
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;

            // 修复：用 List<ModrinthMod> 占位，防止模板数据类型不匹配导致 UI 空白
            ModList.ItemsSource = new List<ModrinthMod> {
                new ModrinthMod { Title = LanguageManager.Get("ModSearch.Loading") }
            };

            try
            {
                var result = await Task.Run(() =>
                {
                    int total;
                    // 智能搜索：英文直搜，中文走译名表翻译 + 多候选合并
                    var list = ModApiService.SearchModsSmart(
                        _currentKeyword, _currentPage, PageSize, _currentType, out total);
                    return Tuple.Create(list, total);
                });

                _totalHits = result.Item2;
                var mods = result.Item1;

                if (mods == null || mods.Count == 0)
                {
                    // 修复：同样用 ModrinthMod 占位
                    ModList.ItemsSource = new List<ModrinthMod> {
                        new ModrinthMod { Title = LanguageManager.Get("ModSearch.NoResult") }
                    };
                    PageInfoText.Text = string.Format(
                        LanguageManager.Get("ModSearch.PageInfo"), 1, 1, 0);
                    return;
                }

                ModList.ItemsSource = mods;

                foreach (var mod in mods)
                    if (!string.IsNullOrEmpty(mod.IconUrl))
                        _ = LoadIconAsync(mod);

                int totalPages = Math.Max(1,
                    (int)Math.Ceiling((double)_totalHits / PageSize));
                PageInfoText.Text = string.Format(
                    LanguageManager.Get("ModSearch.PageInfo"),
                    _currentPage, totalPages, _totalHits);
            }
            catch (Exception ex)
            {
                ModList.ItemsSource = new List<ModrinthMod> {
                    new ModrinthMod { Title = LanguageManager.Get("Download.Failure") + "：" + ex.Message }
                };
                PageInfoText.Text = string.Format(
                    LanguageManager.Get("ModSearch.PageInfo"), 1, 1, 0);
            }
            finally
            {
                _loading = false;
                SearchButton.IsEnabled = true;

                int totalPages = Math.Max(1,
                    (int)Math.Ceiling((double)_totalHits / PageSize));
                PrevButton.IsEnabled = _currentPage > 1;
                NextButton.IsEnabled = _currentPage < totalPages;
            }
        }

        private static async Task LoadIconAsync(ModrinthMod mod)
        {
            try
            {
                string url = mod.IconUrl;
                if (string.IsNullOrEmpty(url)) return;

                byte[] data = await Task.Run(() =>
                {
                    using (var wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "JerryStudioLauncher/1.0");
                        return wc.DownloadData(url);
                    }
                });

                if (data == null || data.Length == 0) return;

                var bmp = WebPHelper.DecodeToBitmapSource(data, null);
                if (bmp != null) mod.Icon = bmp;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModSearch] 图标失败：" + ex.Message);
            }
        }
    }
}