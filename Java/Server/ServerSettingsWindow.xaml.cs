using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class ServerSettingsWindow : Window
    {
        private readonly ServerInfo _info;

        public ServerSettingsWindow(ServerInfo info)
        {
            InitializeComponent();
            _info = info;
            Loaded += (s, e) => LoadSettings();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void LoadSettings()
        {
            NameBox.Text = _info.Name;

            string p = _info.PropertiesPath;

            MotdBox.Text = Or(ServerProperties.ReadProperty(p, "motd"), "A Minecraft Server");

            PortBox.Text = Or(ServerProperties.ReadProperty(p, "server-port"), "25565");
            MaxPlayersBox.Text = Or(ServerProperties.ReadProperty(p, "max-players"), "20");

            OnlineModeCheck.IsChecked = ReadBool(p, "online-mode", true);
            WhitelistCheck.IsChecked = ReadBool(p, "white-list", false);
            EnforceWhitelistCheck.IsChecked = ReadBool(p, "enforce-whitelist", false);
            PvpCheck.IsChecked = ReadBool(p, "pvp", true);
            AllowFlightCheck.IsChecked = ReadBool(p, "allow-flight", false);
            CheatCheck.IsChecked = ReadBool(p, "enable-command-block", false);

            GamemodeBox.Text = Or(ServerProperties.ReadProperty(p, "gamemode"), "survival");
            DifficultyBox.Text = Or(ServerProperties.ReadProperty(p, "difficulty"), "easy");
            ViewDistanceBox.Text = Or(ServerProperties.ReadProperty(p, "view-distance"), "10");
            SimDistanceBox.Text = Or(ServerProperties.ReadProperty(p, "simulation-distance"), "10");
        }

        private static string Or(string v, string fallback)
        {
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private bool ReadBool(string path, string key, bool def)
        {
            string v = ServerProperties.ReadProperty(path, key);
            if (string.IsNullOrEmpty(v)) return def;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string newName = (NameBox.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(newName) && newName != _info.Name)
                {
                    string parent = Path.GetDirectoryName(_info.FolderPath);
                    string newPath = Path.Combine(parent, newName);

                    if (Directory.Exists(newPath))
                    {
                        LanguageManager.ShowWarning("ServerSettings.FolderExists");
                        return;
                    }

                    Directory.Move(_info.FolderPath, newPath);

                    _info.Name = newName;
                    _info.FolderPath = newPath;
                    _info.JarPath = Path.Combine(newPath, "server.jar");
                    _info.PropertiesPath = Path.Combine(newPath, "server.properties");
                }

                string p = _info.PropertiesPath;
                if (!File.Exists(p))
                    File.WriteAllText(p, "", new UTF8Encoding(false));

                ServerProperties.WriteProperty(p, "motd", MotdBox.Text ?? "");
                ServerProperties.WriteProperty(p, "server-port", PortBox.Text ?? "25565");
                ServerProperties.WriteProperty(p, "max-players", MaxPlayersBox.Text ?? "20");

                ServerProperties.WriteProperty(p, "online-mode",
                    OnlineModeCheck.IsChecked == true ? "true" : "false");
                ServerProperties.WriteProperty(p, "white-list",
                    WhitelistCheck.IsChecked == true ? "true" : "false");
                ServerProperties.WriteProperty(p, "enforce-whitelist",
                    EnforceWhitelistCheck.IsChecked == true ? "true" : "false");
                ServerProperties.WriteProperty(p, "pvp",
                    PvpCheck.IsChecked == true ? "true" : "false");
                ServerProperties.WriteProperty(p, "allow-flight",
                    AllowFlightCheck.IsChecked == true ? "true" : "false");
                ServerProperties.WriteProperty(p, "enable-command-block",
                    CheatCheck.IsChecked == true ? "true" : "false");

                ServerProperties.WriteProperty(p, "gamemode", GamemodeBox.Text ?? "survival");
                ServerProperties.WriteProperty(p, "difficulty", DifficultyBox.Text ?? "easy");
                ServerProperties.WriteProperty(p, "view-distance",
                    ViewDistanceBox.Text ?? "10");
                ServerProperties.WriteProperty(p, "simulation-distance",
                    SimDistanceBox.Text ?? "10");

                _info.Motd = MotdBox.Text ?? "";

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                LanguageManager.ShowError("ServerSettings.SaveFailed", ex.Message);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}