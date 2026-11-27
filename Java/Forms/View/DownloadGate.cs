using System.Threading;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public static class DownloadGate
    {
        /// <summary>全进程共享的下载并发上限。</summary>
        public static readonly SemaphoreSlim Global = new SemaphoreSlim(32, 32);
    }
}