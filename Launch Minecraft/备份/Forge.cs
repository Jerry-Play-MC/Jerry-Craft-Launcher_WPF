using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Launch_Minecraft
{
    internal class Forge : BaseLoaderLauncher
    {
        public override string LoaderName { get { return "Forge"; } }

        public static void LaunchClient(string minecraftDir, string versionName,
                                        bool isolated = false, string javaBaseDir = null)
        {
            new Forge().Launch(
                CreateClientContext(minecraftDir, versionName, isolated, javaBaseDir));
        }

        public static void LaunchServer(string serverDir, string versionName,
                                        string javaBaseDir = null)
        {
            new Forge().Launch(
                CreateServerContext(serverDir, versionName, javaBaseDir));
        }

        /// <summary>剔除 NeoForge / Fabric 库 + 按 GA 去重</summary>
        protected override List<Dictionary<string, object>> FilterLibraries(
            List<Dictionary<string, object>> libs, LaunchContext context)
        {
            // 1. 剔除 NeoForge / Fabric 的库，避免继承链混入
            libs.RemoveAll(l =>
                l.ContainsKey("name") &&
                (l["name"].ToString().Contains("neoforged") ||
                 l["name"].ToString().Contains("fabricmc")));

            // 2. ★ 按 groupId:artifactId 去重，保留最高版本
            //    解决 Log4j 2.8.1 vs 2.15.0 冲突
            libs = DeduplicateLibrariesByGA(libs);

            return libs;
        }

        /// <summary>Forge 专属 JVM 参数</summary>
        protected override void AppendLoaderJvmArgs(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        {
            cmd.Add("-Dfml.environment=client");
            cmd.Add("-Dforge.logging.mojang.level=OFF");
            cmd.Add("-Dstdout.encoding=GBK");
            cmd.Add("-Dstderr.encoding=GBK");

            string gameVersion = root.ContainsKey("inheritsFrom")
                ? root["inheritsFrom"].ToString()
                : context.VersionName;

            if (!string.IsNullOrEmpty(gameVersion))
            {
                var parts = gameVersion.Split('.');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], out int major) &&
                    int.TryParse(parts[1], out int minor) &&
                    major == 1 && minor <= 12)
                {
                    cmd.Add("-Dfml.ignoreInvalidMinecraftCertificates=true");
                    cmd.Add("-Dfml.ignorePatchDiscrepancies=true");
                }
            }
        }

        /// <summary>核心 jar 提前 + LaunchWrapper 时加父版本 client.jar</summary>
        protected override List<string> ReorderClasspath(
            List<string> entries, LaunchContext context,
            Dictionary<string, object> root, string mainClass)
        {
            string coreJar = Path.Combine(
                Path.Combine(Path.Combine(context.MinecraftDir, "versions"), context.VersionName),
                context.VersionName + ".jar");

            if (File.Exists(coreJar))
            {
                entries.RemoveAll(e => string.Equals(e, coreJar, StringComparison.OrdinalIgnoreCase));
                entries.Insert(0, coreJar);
            }

            if (mainClass == "net.minecraft.launchwrapper.Launch")
            {
                string parentVersion = null;
                if (root.ContainsKey("minecraftVersion"))
                    parentVersion = root["minecraftVersion"].ToString();
                else if (root.ContainsKey("inheritsFrom"))
                    parentVersion = root["inheritsFrom"].ToString();

                if (!string.IsNullOrEmpty(parentVersion))
                {
                    string parentJar = Path.Combine(
                        Path.Combine(Path.Combine(context.MinecraftDir, "versions"), parentVersion),
                        parentVersion + ".jar");

                    if (File.Exists(parentJar) &&
                        !entries.Any(e => string.Equals(e, parentJar, StringComparison.OrdinalIgnoreCase)))
                    {
                        int idx = entries.IndexOf(coreJar) + 1;
                        if (idx <= 0) idx = 0;
                        entries.Insert(idx, parentJar);
                    }
                }
            }

            return entries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        protected override bool UseLaunchWrapperArgs(Dictionary<string, object> root)
        {
            string mc = root.ContainsKey("mainClass")
                ? root["mainClass"].ToString() : "";
            return mc == "net.minecraft.launchwrapper.Launch";
        }

        protected override void AppendLaunchWrapperArgs(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        {
            base.AppendLaunchWrapperArgs(cmd, context, root);
            cmd.Add("--tweakClass");
            cmd.Add("net.minecraftforge.fml.common.launcher.FMLTweaker");
            cmd.Add("--versionType");
            cmd.Add("Forge");
            cmd.Add("--width"); cmd.Add(context.Width.ToString());
            cmd.Add("--height"); cmd.Add(context.Height.ToString());
        }

        /// <summary>加 -DignoreList / 移除 --demo</summary>
        protected override void PostProcessCommand(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        {
            string mc = root.ContainsKey("mainClass")
                ? root["mainClass"].ToString() : "";

            if (mc != "net.minecraft.launchwrapper.Launch")
            {
                string ignoreList =
                    "bootstraplauncher,securejarhandler,asm-commons,asm-util,asm-analysis," +
                    "asm-tree,asm,JarJarFileSystems,client-extra,fmlcore,javafmllanguage," +
                    "lowcodelanguage,mclanguage,forge-," + context.VersionName + ".jar";

                bool has = false;
                for (int i = 0; i < cmd.Count; i++)
                {
                    if (cmd[i].StartsWith("-DignoreList="))
                    {
                        cmd[i] = "-DignoreList=" + ignoreList;
                        has = true;
                        break;
                    }
                }
                if (!has)
                {
                    // ★ 关键：必须插入到 -cp 之前（JVM 参数区）
                    int cpIdx = cmd.IndexOf("-cp");
                    if (cpIdx >= 0)
                        cmd.Insert(cpIdx, "-DignoreList=" + ignoreList);
                    else
                        cmd.Add("-DignoreList=" + ignoreList);
                }
            }

            cmd.Remove("--demo");
        }
    }
}