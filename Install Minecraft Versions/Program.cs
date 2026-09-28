using System;
using System.IO;
using System.Net;
using System.Threading;

namespace Install_Minecraft_Versions
{
    public static class VersionInstaller
    {
        private class LoaderSpec
        {
            public bool HasVanilla;
            public bool HasForge;
            public string ForgeVersion;
            public bool HasNeoForge;
            public string NeoForgeVersion;
            public bool HasFabric;
            public string FabricVersion;
            public bool HasQuilt;
            public string QuiltVersion;
            public bool HasOptiFine;
            public string OptiFineVersion;

            public bool IsEmpty()
            {
                return !HasVanilla && !HasForge && !HasNeoForge
                    && !HasFabric && !HasQuilt && !HasOptiFine;
            }
        }

        // ============================================================
        //   新的公开入口：替代原来的 Main
        // ============================================================
        /// <summary>
        /// 安装 Minecraft 版本及加载器。
        /// </summary>
        /// <param name="minecraftType">client / server</param>
        /// <param name="loaderType">加载器组合，如 "Vanilla" / "Forge[47.2.0]" / "Forge[47.2.0]OptiFine[I6]"</param>
        /// <param name="version">游戏版本，如 "1.20.1"</param>
        /// <param name="minecraftPath">.minecraft 路径</param>
        /// <param name="loaderVersionList">manifest 目录，或 "none"</param>
        /// <returns>0 = 成功，1 = 失败</returns>
        public static int Run(string minecraftType, string loaderType, string version,
                              string minecraftPath, string loaderVersionList)
        {
            try
            {
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;

                // 注意：原来的 StartExitListener 已删除，
                // 那是给 CLI 用的 Console.ReadLine 监听，GUI 里没意义。

                if (string.IsNullOrEmpty(minecraftType))
                    throw new ArgumentException("minecraftType 不能为空");
                if (string.IsNullOrEmpty(loaderType))
                    throw new ArgumentException("loaderType 不能为空");
                if (string.IsNullOrEmpty(version))
                    throw new ArgumentException("version 不能为空");
                if (string.IsNullOrEmpty(minecraftPath))
                    throw new ArgumentException("minecraftPath 不能为空");
                if (string.IsNullOrEmpty(loaderVersionList))
                    loaderVersionList = "none";

                LoaderSpec spec = ParseLoaderSpec(loaderType);

                Console.WriteLine($"[参数] 类型={minecraftType} | 加载器={loaderType} | " +
                                  $"版本={version} | 路径={minecraftPath} | " +
                                  $"manifest={loaderVersionList}");
                Console.WriteLine($"[参数] 解析结果：");
                Console.WriteLine($"[参数]   Vanilla  = {spec.HasVanilla}");
                Console.WriteLine($"[参数]   Forge    = {spec.HasForge} ({spec.ForgeVersion ?? "latest"})");
                Console.WriteLine($"[参数]   NeoForge = {spec.HasNeoForge} ({spec.NeoForgeVersion ?? "latest"})");
                Console.WriteLine($"[参数]   Fabric   = {spec.HasFabric} ({spec.FabricVersion ?? "latest"})");
                Console.WriteLine($"[参数]   Quilt    = {spec.HasQuilt} ({spec.QuiltVersion ?? "latest"})");
                Console.WriteLine($"[参数]   OptiFine = {spec.HasOptiFine} ({spec.OptiFineVersion ?? "latest"})");

                if (spec.IsEmpty())
                    throw new ArgumentException($"无法识别任何加载器：{loaderType}");

                // ============================================================
                // 第一步：装原版
                // ============================================================
                if (minecraftType == "client")
                    Vanilla.InstallClient(version, minecraftPath, loaderVersionList);
                else if (minecraftType == "server")
                    Vanilla.InstallServer(version, minecraftPath, loaderVersionList);
                else
                    throw new ArgumentException($"未知的游戏类型：{minecraftType}");

                // ============================================================
                // 第二步：装加载器
                // ============================================================
                if (minecraftType == "client")
                {
                    if (spec.HasForge)
                    {
                        Forge.InstallClient(version, minecraftPath,
                            spec.ForgeVersion ?? loaderVersionList);
                    }
                    else if (spec.HasNeoForge)
                    {
                        NeoForge.InstallClient(version, minecraftPath,
                            spec.NeoForgeVersion ?? loaderVersionList);
                    }
                    else if (spec.HasFabric)
                    {
                        Fabric.InstallClient(version, minecraftPath,
                            spec.FabricVersion ?? loaderVersionList);
                    }
                    else if (spec.HasQuilt)
                    {
                        Quilt.InstallClient(version, minecraftPath,
                            spec.QuiltVersion ?? loaderVersionList);
                    }

                    if (spec.HasOptiFine)
                    {
                        bool forForge = spec.HasForge || spec.HasNeoForge;
                        OptiFine.InstallClient(version, minecraftPath,
                            spec.OptiFineVersion, forForge);
                    }
                }
                else if (minecraftType == "server")
                {
                    if (spec.HasForge)
                        Forge.InstallServer(version, minecraftPath,
                            spec.ForgeVersion ?? loaderVersionList);
                    else if (spec.HasNeoForge)
                        NeoForge.InstallServer(version, minecraftPath,
                            spec.NeoForgeVersion ?? loaderVersionList);
                    else if (spec.HasFabric)
                        Fabric.InstallServer(version, minecraftPath,
                            spec.FabricVersion ?? loaderVersionList);
                    else if (spec.HasQuilt)
                        Quilt.InstallServer(version, minecraftPath,
                            spec.QuiltVersion ?? loaderVersionList);

                    if (spec.HasOptiFine)
                    {
                        OptiFine.InstallServer(version, minecraftPath, spec.OptiFineVersion);
                    }
                }

                Console.WriteLine("[完成] 全部安装完成");
                return 0;
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}"); }
                catch { }
                return 1;
            }
        }

        // ============================================================
        //  以下全部保持原样
        // ============================================================
        private static LoaderSpec ParseLoaderSpec(string spec)
        {
            var result = new LoaderSpec();
            if (string.IsNullOrEmpty(spec)) return result;

            string work = spec;

            work = TryExtract(work, "NeoForge", v =>
            {
                result.HasNeoForge = true;
                result.NeoForgeVersion = v;
            });

            work = TryExtract(work, "OptiFine", v =>
            {
                result.HasOptiFine = true;
                result.OptiFineVersion = v;
            });

            work = TryExtract(work, "Forge", v =>
            {
                result.HasForge = true;
                result.ForgeVersion = v;
            });

            work = TryExtract(work, "Fabric", v =>
            {
                result.HasFabric = true;
                result.FabricVersion = v;
            });

            work = TryExtract(work, "Quilt", v =>
            {
                result.HasQuilt = true;
                result.QuiltVersion = v;
            });

            work = TryExtract(work, "Vanilla", v =>
            {
                result.HasVanilla = true;
            });

            return result;
        }

        private static string TryExtract(string work, string name, Action<string> onFound)
        {
            if (string.IsNullOrEmpty(work)) return work;

            int idx = work.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return work;

            int end = idx + name.Length;
            string version = null;

            if (end < work.Length && work[end] == '[')
            {
                int close = work.IndexOf(']', end);
                if (close > end)
                {
                    string inside = work.Substring(end + 1, close - end - 1).Trim();
                    if (!string.IsNullOrEmpty(inside))
                        version = inside;
                    end = close + 1;
                }
            }

            onFound(version);
            return work.Remove(idx, end - idx);
        }
    }
}