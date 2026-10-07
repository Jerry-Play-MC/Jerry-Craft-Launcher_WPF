using System;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public static class MinecraftCore
    {
        public static string GetMinecraftDir()
        {
            return App.Config.GameDir;
        }

        public static string GetLoaderType(string versionName)
        {
            if (string.IsNullOrEmpty(versionName)) return "Vanilla";
            string v = versionName.ToLowerInvariant();

            if (v.Contains("neoforge")) return "NeoForge";
            if (v.Contains("forge")) return "Forge";
            if (v.Contains("fabric")) return "Fabric";
            if (v.Contains("quilt")) return "Quilt";
            return "Vanilla";
        }
    }
}