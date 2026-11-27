using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
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
            public bool HasLegacyFabric;
            public string LegacyFabricVersion;
            public bool HasQuilt;
            public string QuiltVersion;
            public bool HasOptiFine;
            public string OptiFineVersion;
            public bool HasLiteLoader;
            public string LiteLoaderVersion;
            public bool HasLabyMod;

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
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Program] 设置 Console.OutputEncoding 失败: " + ex.Message);
                }

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

                recordLog(LogLevel.Info, $"[参数] 类型={minecraftType} | 加载器={loaderType} | " +
                                  $"版本={version} | 路径={minecraftPath} | " +
                                  $"manifest={loaderVersionList}");
                recordLog(LogLevel.Info, $"[参数] 解析结果：");
                recordLog(LogLevel.Info, $"[参数]   Vanilla      = {spec.HasVanilla}");
                recordLog(LogLevel.Info, $"[参数]   Forge        = {spec.HasForge} ({spec.ForgeVersion ?? "latest"})");
                recordLog(LogLevel.Info, $"[参数]   NeoForge     = {spec.HasNeoForge} ({spec.NeoForgeVersion ?? "latest"})");
                recordLog(LogLevel.Info, $"[参数]   Fabric       = {spec.HasFabric} ({spec.FabricVersion ?? "latest"})");
                recordLog(LogLevel.Info, $"[参数]   LegacyFabric = {spec.HasLegacyFabric} ({spec.LegacyFabricVersion ?? "latest"})");
                recordLog(LogLevel.Info, $"[参数]   Quilt        = {spec.HasQuilt} ({spec.QuiltVersion ?? "latest"})");
                recordLog(LogLevel.Info, $"[参数]   LiteLoader   = {spec.HasLiteLoader} ({spec.LiteLoaderVersion ?? "latest"})");
                recordLog(LogLevel.Info, $"[参数]   OptiFine     = {spec.HasOptiFine} ({spec.OptiFineVersion ?? "latest"})");
                recordLog(LogLevel.Info, $"[参数]   LabyMod      = {spec.HasLabyMod}");

                if (spec.IsEmpty())
                    throw new ArgumentException($"无法识别任何加载器：{loaderType}");

                if (minecraftType == "client")
                    InstallVanilla.InstallClient(version, minecraftPath, loaderVersionList);
                else if (minecraftType == "server")
                    InstallVanilla.InstallServer(version, minecraftPath, loaderVersionList);
                else
                    throw new ArgumentException($"未知的游戏类型：{minecraftType}");

                if (minecraftType == "client")
                {
                    string forgeVersionId = null;

                    if (spec.HasForge)
                        forgeVersionId = InstallForge.InstallClient(version, minecraftPath,
                            spec.ForgeVersion ?? loaderVersionList);
                    else if (spec.HasNeoForge)
                        InstallNeoForge.InstallClient(version, minecraftPath,
                            spec.NeoForgeVersion ?? loaderVersionList);
                    else if (spec.HasFabric)
                        InstallFabric.InstallClient(version, minecraftPath,
                            spec.FabricVersion ?? loaderVersionList);
                    else if (spec.HasLegacyFabric)
                        InstallLegacyFabric.InstallClient(version, minecraftPath,
                            spec.LegacyFabricVersion ?? loaderVersionList);
                    else if (spec.HasQuilt)
                        InstallQuilt.InstallClient(version, minecraftPath,
                            spec.QuiltVersion ?? loaderVersionList);
                    else if (spec.HasLabyMod)
                        InstallLabyMod.InstallClient(version, minecraftPath, loaderVersionList);

                    if (spec.HasLiteLoader)
                    {
                        if (!string.IsNullOrEmpty(forgeVersionId))
                        {
                            Console.WriteLine($"[LiteLoader] 与 Forge 组合，继承版本 {forgeVersionId}");
                            InstallLiteLoader.InstallClientForForge(version, minecraftPath,
                                spec.LiteLoaderVersion ?? loaderVersionList, forgeVersionId);
                        }
                        else
                        {
                            InstallLiteLoader.InstallClient(version, minecraftPath,
                                spec.LiteLoaderVersion ?? loaderVersionList);
                        }
                    }

                    if (spec.HasOptiFine)
                    {
                        bool forForge = spec.HasForge || spec.HasNeoForge;
                        InstallOptiFine.InstallClient(version, minecraftPath,
                            spec.OptiFineVersion, forForge);
                    }
                }
                else if (minecraftType == "server")
                {
                    if (spec.HasForge)
                        InstallForge.InstallServer(version, minecraftPath,
                            spec.ForgeVersion ?? loaderVersionList);
                    else if (spec.HasNeoForge)
                        InstallNeoForge.InstallServer(version, minecraftPath,
                            spec.NeoForgeVersion ?? loaderVersionList);
                    else if (spec.HasFabric)
                        InstallFabric.InstallServer(version, minecraftPath,
                            spec.FabricVersion ?? loaderVersionList);
                    else if (spec.HasQuilt)
                        InstallQuilt.InstallServer(version, minecraftPath,
                            spec.QuiltVersion ?? loaderVersionList);

                    if (spec.HasOptiFine)
                        InstallOptiFine.InstallServer(version, minecraftPath, spec.OptiFineVersion);
                }

                recordLog(LogLevel.Info, "[完成] 全部安装完成");
                return 0;
            }
            catch (Exception ex)
            {
                recordLog(LogLevel.Error, "[致命] 安装流程异常终止: " + ex.ToString());
                try { Console.Error.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}"); }
                catch (Exception cerrEx)
                {
                    System.Diagnostics.Debug.WriteLine("[Program] 写 stderr 失败: " + cerrEx.Message);
                }
                return 1;
            }
        }

        // ═══════════════════════════════════════════════════════
        //  新异步入口 —— 流程同 Run，但每步上报到 TaskGroup
        //  ClientInstallView 调用它（能显示阶段进度 / 支持多任务并发）
        // ═══════════════════════════════════════════════════════
        public static async Task<int> RunAsync(
    string type,
    string loaderType,
    string mcVersion,
    string gameDir,
    string javaPath,
    Action<string> log = null,
    TaskGroup group = null,
    CancellationTokenSource cts = null)
        {
            void L(string s) { log?.Invoke(s); }
            cts = cts ?? new CancellationTokenSource();
            var ct = cts.Token;

            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;

                var spec = ParseLoaderSpec(loaderType);
                if (spec.IsEmpty())
                    throw new ArgumentException("无法识别任何加载器：" + loaderType);

                L($"[Installer] 目标: {mcVersion} + {loaderType}");

                // ═══════════════════════════════════════════════════
                // ★ 第一步：一次性声明所有阶段（全部 Pending）
                // ═══════════════════════════════════════════════════
                var stages = new InstallStages();
                if (group != null)
                {
                    void AddStage(ref SubTaskItem field, string name, double weight)
                    {
                        field = group.AddStage(name, weight);
                    }

                    // Vanilla
                    AddStage(ref stages.Manifest, "Fetch version manifest", 1);
                    AddStage(ref stages.VersionJson, "Download version JSON", 2);
                    AddStage(ref stages.ClientJar, "Download client.jar", 20);
                    AddStage(ref stages.AssetIndex, "Download asset index", 3);
                    AddStage(ref stages.LogConfig, "Download log config", 1);
                    AddStage(ref stages.Libraries, "Download libraries", 50);
                    AddStage(ref stages.Assets, "Download assets", 100);
                    AddStage(ref stages.VirtualAssets, "Build virtual assets", 5);

                    // Forge / NeoForge
                    if (spec.HasForge || spec.HasNeoForge)
                    {
                        AddStage(ref stages.ForgeInstaller, "Download Forge installer", 10);
                        AddStage(ref stages.ForgeParse, "Parse Forge installer", 2);
                        AddStage(ref stages.ForgeLibraries, "Download Forge libraries", 20);
                        AddStage(ref stages.ForgeProcessors, "Run Forge processors", 15);
                    }

                    // Fabric / Quilt / LegacyFabric
                    if (spec.HasFabric || spec.HasLegacyFabric || spec.HasQuilt)
                    {
                        AddStage(ref stages.FabricProfile, "Fetch loader profile", 2);
                        AddStage(ref stages.FabricLibraries, "Download loader libraries", 15);
                        AddStage(ref stages.FabricCore, "Copy client.jar", 3);
                    }

                    // LabyMod
                    if (spec.HasLabyMod)
                    {
                        AddStage(ref stages.LabyMeta, "Fetch LabyMod manifest", 5);
                        AddStage(ref stages.LabyCore, "Download LabyMod core", 30);
                        AddStage(ref stages.LabyLibraries, "Download LabyMod libraries", 40);
                        AddStage(ref stages.LabyAssets, "Download LabyMod assets", 20);
                    }

                    // OptiFine
                    if (spec.HasOptiFine)
                    {
                        AddStage(ref stages.OptifineInstaller, "Download OptiFine installer", 5);
                        AddStage(ref stages.OptifineLibraries, "Download OptiFine libraries", 8);
                        AddStage(ref stages.OptifineRun, "Run OptiFine installer", 10);
                    }

                    // LiteLoader
                    if (spec.HasLiteLoader)
                    {
                        AddStage(ref stages.LiteLoaderInstaller, "Install LiteLoader", 5);
                    }
                }

                // ═══════════════════════════════════════════════════
                // ★ 第二步：构建 DAG，节点内部负责推进自己的阶段
                // ═══════════════════════════════════════════════════
                var graph = new TaskGraph(group, L, cts);

                // 节点 1：Vanilla
                graph.Add("Install vanilla", 100,
                    work: c => Task.Run(() =>
                        InstallVanilla.InstallClient(
                            mcVersion, gameDir, "none", group, c, stages), c));

                // 节点 2：Forge / NeoForge
                if (spec.HasForge || spec.HasNeoForge)
                {
                    graph.Add("Install Forge", 30,
                        work: c => Task.Run(() =>
                        {
                            if (spec.HasForge)
                                InstallForge.InstallClient(mcVersion, gameDir,
                                    spec.ForgeVersion ?? "none", group, c, stages);
                            else
                                InstallNeoForge.InstallClient(mcVersion, gameDir,
                                    spec.NeoForgeVersion ?? "none", group, c, stages);
                        }, c),
                        dependsOn: new[] { "Install vanilla" });
                }

                // 节点 3：Fabric
                if (spec.HasFabric)
                {
                    graph.Add("Install Fabric", 20,
                        work: c => Task.Run(() =>
                            InstallFabric.InstallClient(mcVersion, gameDir, spec.FabricVersion ?? "none", group, c, stages), c),
                        dependsOn: new[] { "Install vanilla" });
                }

                // 节点 4：Quilt (★ 就是这里之前缺失了)
                if (spec.HasQuilt)
                {
                    graph.Add("Install Quilt", 20,
                        work: c => Task.Run(() =>
                            InstallQuilt.InstallClient(mcVersion, gameDir, spec.QuiltVersion ?? "none", group, c, stages), c),
                        dependsOn: new[] { "Install vanilla" });
                }

                // 节点 5：LegacyFabric
                if (spec.HasLegacyFabric)
                {
                    graph.Add("Install LegacyFabric", 20,
                        work: c => Task.Run(() =>
                            InstallLegacyFabric.InstallClient(mcVersion, gameDir, spec.LegacyFabricVersion ?? "none", group, c, stages), c),
                        dependsOn: new[] { "Install vanilla" });
                }

                // 节点 6：LabyMod
                if (spec.HasLabyMod)
                {
                    graph.Add("Install LabyMod", 100,
                        work: c => Task.Run(() =>
                            InstallLabyMod.InstallClient(mcVersion, gameDir, "none", "production", group, c, stages), c),
                        dependsOn: new[] { "Install vanilla" });
                }

                // 节点 7：OptiFine
                if (spec.HasOptiFine)
                {
                    var deps = new List<string> { "Install vanilla" };
                    if (spec.HasForge || spec.HasNeoForge) deps.Add("Install Forge");
                    graph.Add("Install OptiFine", 15,
                        work: c => Task.Run(() =>
                            InstallOptiFine.InstallClient(
                                mcVersion, gameDir, spec.OptiFineVersion,
                                spec.HasForge || spec.HasNeoForge, group, c, stages), c),
                        dependsOn: deps.ToArray());
                }

                // 节点 8：LiteLoader
                if (spec.HasLiteLoader)
                {
                    var deps = new List<string> { "Install vanilla" };
                    if (spec.HasForge) deps.Add("Install Forge");
                    graph.Add("Install LiteLoader", 10,
                        work: c => Task.Run(() =>
                            InstallLiteLoader.InstallClient(
                                mcVersion, gameDir, spec.LiteLoaderVersion ?? "none", group, c, stages), c),
                        dependsOn: deps.ToArray());
                }

                await graph.RunAsync();
                L("[Installer] 全部阶段完成");
                return 0;
            }
            catch (OperationCanceledException) { L("[Installer] 已取消"); return -1; }
            catch (Exception ex) { L("[Installer] 失败: " + ex.Message); return -1; }
        }

        // 把 InstallXXX 内部的 Console.WriteLine 转发给 group 日志
        private class LogForwarder : System.IO.TextWriter
        {
            private readonly System.IO.TextWriter _inner;
            private readonly Action<string> _log;
            public LogForwarder(System.IO.TextWriter inner, Action<string> log)
            { _inner = inner; _log = log; }
            public override System.Text.Encoding Encoding => _inner?.Encoding ?? System.Text.Encoding.UTF8;
            public override void WriteLine(string value)
            {
                _log?.Invoke(value);
                _inner?.WriteLine(value);
            }
            public override void Write(string value) { _inner?.Write(value); }
            public override void WriteLine() { _inner?.WriteLine(); }
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

            work = TryExtract(work, "LabyMod", v =>
            {
                result.HasLabyMod = true;
            });

            work = TryExtract(work, "OptiFine", v =>
            {
                result.HasOptiFine = true;
                result.OptiFineVersion = v;
            });

            // 必须在 Fabric 之前提取，否则 "Fabric" 会先吃掉 "LegacyFabric"
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