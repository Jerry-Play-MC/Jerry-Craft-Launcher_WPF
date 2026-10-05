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
            public bool HasLegacyFabric;          // ★
            public string LegacyFabricVersion;    // ★
            public bool HasQuilt;
            public string QuiltVersion;
            public bool HasOptiFine;
            public string OptiFineVersion;
            public bool HasLiteLoader;
            public string LiteLoaderVersion;
            public bool HasLabyMod;               // ★

            public bool IsEmpty()
            {
                return !HasVanilla && !HasForge && !HasNeoForge
                    && !HasFabric && !HasLegacyFabric && !HasQuilt
                    && !HasOptiFine && !HasLiteLoader && !HasLabyMod;
            }
        }

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
                Console.WriteLine($"[参数]   Vanilla      = {spec.HasVanilla}");
                Console.WriteLine($"[参数]   Forge        = {spec.HasForge} ({spec.ForgeVersion ?? "latest"})");
                Console.WriteLine($"[参数]   NeoForge     = {spec.HasNeoForge} ({spec.NeoForgeVersion ?? "latest"})");
                Console.WriteLine($"[参数]   Fabric       = {spec.HasFabric} ({spec.FabricVersion ?? "latest"})");
                Console.WriteLine($"[参数]   LegacyFabric = {spec.HasLegacyFabric} ({spec.LegacyFabricVersion ?? "latest"})");
                Console.WriteLine($"[参数]   Quilt        = {spec.HasQuilt} ({spec.QuiltVersion ?? "latest"})");
                Console.WriteLine($"[参数]   LiteLoader   = {spec.HasLiteLoader} ({spec.LiteLoaderVersion ?? "latest"})");
                Console.WriteLine($"[参数]   OptiFine     = {spec.HasOptiFine} ({spec.OptiFineVersion ?? "latest"})");
                Console.WriteLine($"[参数]   LabyMod      = {spec.HasLabyMod}");

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
                    string forgeVersionId = null;

                    if (spec.HasForge)
                    {
                        forgeVersionId = Forge.InstallClient(version, minecraftPath,
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
                    else if (spec.HasLegacyFabric)
                    {
                        LegacyFabric.InstallClient(version, minecraftPath,
                            spec.LegacyFabricVersion ?? loaderVersionList);
                    }
                    else if (spec.HasQuilt)
                    {
                        Quilt.InstallClient(version, minecraftPath,
                            spec.QuiltVersion ?? loaderVersionList);
                    }
                    else if (spec.HasLabyMod)   // ★
                    {
                        // LabyMod 是独立的完整客户端，与其它加载器互斥
                        LabyMod.InstallClient(version, minecraftPath, loaderVersionList);
                    }

                    // ★ LiteLoader 独立处理
                    if (spec.HasLiteLoader)
                    {
                        if (!string.IsNullOrEmpty(forgeVersionId))
                        {
                            Console.WriteLine($"[LiteLoader] 与 Forge 组合，继承版本 {forgeVersionId}");
                            LiteLoader.InstallClientForForge(version, minecraftPath,
                                spec.LiteLoaderVersion ?? loaderVersionList, forgeVersionId);
                        }
                        else
                        {
                            LiteLoader.InstallClient(version, minecraftPath,
                                spec.LiteLoaderVersion ?? loaderVersionList);
                        }
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

            work = TryExtract(work, "LiteLoader", v =>
            {
                result.HasLiteLoader = true;
                result.LiteLoaderVersion = v;
            });

            work = TryExtract(work, "LabyMod", v =>   // ★
            {
                result.HasLabyMod = true;
            });

            work = TryExtract(work, "OptiFine", v =>
            {
                result.HasOptiFine = true;
                result.OptiFineVersion = v;
            });

            // ★ 必须在 Fabric 之前提取，否则 "Fabric" 会先吃掉 "LegacyFabric"
            work = TryExtract(work, "LegacyFabric", v =>
            {
                result.HasLegacyFabric = true;
                result.LegacyFabricVersion = v;
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