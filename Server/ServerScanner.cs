using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class ServerScanner
    {
        private readonly string _serverRoot;
        private readonly DispatcherTimer _timer;
        private List<ServerInfo> _current = new List<ServerInfo>();

        public event Action<List<ServerInfo>> ServersChanged;

        /// <summary>当前扫描到的服务器列表（含运行状态）</summary>
        public List<ServerInfo> Current
        {
            get { return _current; }
        }

        public ServerScanner()
        {
            _serverRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Server");
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, e) => Scan();
        }

        public void Start() { Scan(); _timer.Start(); }
        public void Stop() { _timer.Stop(); }

        private void Scan()
        {
            // 旧对象按 FolderPath 索引，便于复用（保留 Status / IsRunning）
            var oldMap = new Dictionary<string, ServerInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _current)
            {
                if (!string.IsNullOrEmpty(s.FolderPath))
                    oldMap[s.FolderPath] = s;
            }

            var newList = new List<ServerInfo>();

            try
            {
                if (Directory.Exists(_serverRoot))
                {
                    foreach (var dir in Directory.GetDirectories(_serverRoot))
                    {
                        string jar = Path.Combine(dir, "server.jar");
                        string bat = Path.Combine(dir, "run.bat");
                        string fabricJar = Path.Combine(dir, "fabric-server-launch.jar");
                        string quiltJar = Path.Combine(dir, "quilt-server-launch.jar");

                        bool hasEntry =
                            File.Exists(jar) ||
                            File.Exists(bat) ||
                            File.Exists(fabricJar) ||
                            File.Exists(quiltJar);
                        if (!hasEntry) continue;

                        ServerInfo info;
                        if (!oldMap.TryGetValue(dir, out info))
                        {
                            info = new ServerInfo
                            {
                                Name = Path.GetFileName(dir),
                                FolderPath = dir,
                                JarPath = File.Exists(jar) ? jar : null,
                                PropertiesPath = Path.Combine(dir, "server.properties"),
                            };
                        }
                        else
                        {
                            info.Name = Path.GetFileName(dir);
                            info.JarPath = File.Exists(jar) ? jar : null;
                            info.PropertiesPath = Path.Combine(dir, "server.properties");
                        }

                        try
                        {
                            info.Motd = ServerProperties.ReadProperty(
                                info.PropertiesPath, "motd");
                        }
                        catch { }

                        newList.Add(info);
                    }
                }
            }
            catch { }

            newList = newList.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

            if (SameList(newList, _current)) return;
            _current = newList;
            var h = ServersChanged;
            if (h != null) h(newList);
        }

        private bool SameList(List<ServerInfo> a, List<ServerInfo> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Name != b[i].Name) return false;
                if (a[i].Motd != b[i].Motd) return false;
            }
            return true;
        }
    }
}