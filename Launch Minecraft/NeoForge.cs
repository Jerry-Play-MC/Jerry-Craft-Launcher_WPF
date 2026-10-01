using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Launch_Minecraft
{
    internal class NeoForge : BaseLoaderLauncher
    {
        public override string LoaderName { get { return "NeoForge"; } }

        public static void LaunchClient(string minecraftDir, string versionName,
                                        bool isolated = false, string javaBaseDir = null,
                                        string username = null, string uuid = null,
                                        string accessToken = null, string userType = null,
                                        Action<LaunchProgress> onProgress = null)
        {
            new NeoForge().Launch(
                CreateClientContext(minecraftDir, versionName, isolated, javaBaseDir,
                                    username, uuid, accessToken, userType, onProgress));
        }

        public static void LaunchServer(string serverDir, string versionName,
                                        string javaBaseDir = null)
        {
            string batPath = Path.Combine(serverDir, "run.bat");
            if (IsForgeOrNeoForgeRunBat(batPath))
            {
                RunBatHidden(serverDir, batPath, javaBaseDir);
                return;
            }

            new NeoForge().Launch(
                CreateServerContext(serverDir, versionName, javaBaseDir));
        }

        protected override List<Dictionary<string, object>> FilterLibraries(
            List<Dictionary<string, object>> libs, LaunchContext context)
        {
            libs = DeduplicateLibrariesByGA(libs);
            return libs;
        }

        protected override void AppendLoaderJvmArgs(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        {
            cmd.Add("-Dfml.environment=client");
            cmd.Add($"-DlibraryDirectory={Path.Combine(context.MinecraftDir, "libraries")}");
            cmd.Add("-Dneoforge.logging.mojang.level=OFF");
            cmd.Add("-Dfml.ignorePatchDiscrepancies=true");
            cmd.Add("-Dfml.ignoreInvalidMinecraftCertificates=true");
            cmd.Add("-Djava.net.preferIPv6Addresses=system");
            cmd.Add($"-DignoreList=client-extra,{context.VersionName}.jar");
        }

        protected override List<string> ReorderClasspath(
            List<string> entries, LaunchContext context,
            Dictionary<string, object> root, string mainClass)
        {
            string coreJar = Path.Combine(
                Path.Combine(Path.Combine(context.MinecraftDir, "versions"), context.VersionName),
                context.VersionName + ".jar");

            if (File.Exists(coreJar))
            {
                entries.RemoveAll(e => string.Equals(e.Trim(), coreJar, StringComparison.OrdinalIgnoreCase));
                entries.Insert(0, coreJar);
            }

            string mcVersion = null;
            if (root.ContainsKey("minecraftVersion"))
                mcVersion = root["minecraftVersion"].ToString();
            else if (root.ContainsKey("inheritsFrom"))
                mcVersion = root["inheritsFrom"].ToString();

            if (!string.IsNullOrEmpty(mcVersion))
            {
                string originalJar = Path.Combine(
                    Path.Combine(Path.Combine(context.MinecraftDir, "versions"), mcVersion),
                    mcVersion + ".jar");
                entries.RemoveAll(e => string.Equals(e.Trim(), originalJar, StringComparison.OrdinalIgnoreCase));
            }

            return entries;
        }
    }
}