using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>
    /// 扫描 程序目录\Server\*\server.jar
    /// 每 2 秒比对一次，只有真正变化才触发 ServersChanged。
    /// </summary>
    public class ServerScanner
    {
        private readonly string _serverRoot;
        private readonly DispatcherTimer _timer;
        private List<ServerInfo> _current = new List<ServerInfo>();

        public event Action<List<ServerInfo>> ServersChanged;

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
            var list = new List<ServerInfo>();
            try
            {
                if (Directory.Exists(_serverRoot))
                {
                    foreach (var dir in Directory.GetDirectories(_serverRoot))
                    {
                        string jar = Path.Combine(dir, "server.jar");
                        if (!File.Exists(jar)) continue;

                        var info = new ServerInfo
                        {
                            Name = Path.GetFileName(dir),
                            FolderPath = dir,
                            JarPath = jar,
                            PropertiesPath = Path.Combine(dir, "server.properties"),
                        };
                        try
                        {
                            info.Motd = ServerProperties.ReadProperty(info.PropertiesPath, "motd");
                        }
                        catch { }

                        list.Add(info);
                    }
                }
            }
            catch { }

            list = list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

            if (SameList(list, _current)) return;
            _current = list;
            ServersChanged?.Invoke(list);
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