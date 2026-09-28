using System;
using System.IO;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class AppConfig
    {
        /// <summary>
        /// 游戏目录（.minecraft）。默认放在 exe 同级的 .minecraft 文件夹。
        /// </summary>
        public string GameDir { get; set; } =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft");

        /// <summary>
        /// Java 基准目录。可传父目录（下面有多个 jdk-xxx 子文件夹），
        /// 也可传 Java home 本身（根目录直接有 bin\java.exe）。
        /// </summary>
        public string JavaBaseDir { get; set; } = @"C:\Program Files\Java";

        /// <summary>
        /// 当前选中的版本名，对应 .minecraft\versions 下的文件夹名。
        /// </summary>
        public string CurrentVersion { get; set; }

        /// <summary>
        /// 是否开启版本隔离（仅 client 有效）。
        /// </summary>
        public bool Isolated { get; set; } = false;
    }
}