namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class ServerInfo
    {
        public string Name { get; set; }              // 文件夹名（即服务器名）
        public string FolderPath { get; set; }
        public string JarPath { get; set; }
        public string PropertiesPath { get; set; }
        public string Motd { get; set; }              // 从 server.properties 读取

        public override string ToString() => Name;
    }
}