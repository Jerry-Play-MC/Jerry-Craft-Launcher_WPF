namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class InstallStages
    {
        // ─── Vanilla ───
        public SubTaskItem Manifest;
        public SubTaskItem VersionJson;
        public SubTaskItem ClientJar;
        public SubTaskItem AssetIndex;
        public SubTaskItem LogConfig;
        public SubTaskItem Libraries;
        public SubTaskItem Assets;
        public SubTaskItem VirtualAssets;

        // ─── Forge / NeoForge ───
        public SubTaskItem ForgeInstaller;
        public SubTaskItem ForgeParse;
        public SubTaskItem ForgeLibraries;
        public SubTaskItem ForgeProcessors;

        // ─── Fabric / Quilt / LegacyFabric ───
        public SubTaskItem FabricProfile;
        public SubTaskItem FabricCore;
        public SubTaskItem FabricLibraries;

        // ─── LabyMod ───
        public SubTaskItem LabyMeta;
        public SubTaskItem LabyCore;
        public SubTaskItem LabyLibraries;
        public SubTaskItem LabyAssets;

        // ─── OptiFine ───
        public SubTaskItem OptifineInstaller;
        public SubTaskItem OptifineLibraries;
        public SubTaskItem OptifineRun;

        // ─── LiteLoader ───
        public SubTaskItem LiteLoaderInstaller;
    }
}