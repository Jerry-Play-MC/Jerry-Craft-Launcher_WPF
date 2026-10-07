using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class VersionInfo
    {
        public string Name { get; set; }
        public string FolderPath { get; set; }
        public string JarPath { get; set; }
        public string JsonPath { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }

    public class VersionScanner
    {
        private readonly string _versionsDir;
        private readonly DispatcherTimer _timer;
        private List<VersionInfo> _current = new List<VersionInfo>();

        /// <summary>
        /// 版本列表发生变化时触发，参数是新的完整列表。
        /// 只在列表内容真的变了才触发，不会每秒都触发。
        /// </summary>
        public event Action<List<VersionInfo>> VersionsChanged;

        public VersionScanner()
        {
            // 从 App.Config 拿游戏目录，再拼 versions
            _versionsDir = Path.Combine(App.Config.GameDir, "versions");

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (s, e) => Scan();
        }

        public void Start()
        {
            Scan();          // 立即扫一次，不用等第一秒
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        private void Scan()
        {
            var list = new List<VersionInfo>();

            try
            {
                if (Directory.Exists(_versionsDir))
                {
                    foreach (var dir in Directory.GetDirectories(_versionsDir))
                    {
                        string name = Path.GetFileName(dir);
                        string jar = Path.Combine(dir, name + ".jar");
                        string json = Path.Combine(dir, name + ".json");

                        // 同时存在 xxx.jar 和 xxx.json 才算一个有效版本
                        if (File.Exists(jar) && File.Exists(json))
                        {
                            list.Add(new VersionInfo
                            {
                                Name = name,
                                FolderPath = dir,
                                JarPath = jar,
                                JsonPath = json
                            });
                        }
                    }
                }
            }
            catch
            {
                // 目录被占用 / 权限问题 → 当空列表处理
            }

            // 版本名倒序，新的在上面
            list = list.OrderByDescending(v => v.Name).ToList();

            if (SameList(list, _current)) return;

            _current = list;
            VersionsChanged?.Invoke(list);
        }

        private bool SameList(List<VersionInfo> a, List<VersionInfo> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Name != b[i].Name) return false;
            }
            return true;
        }
    }
}