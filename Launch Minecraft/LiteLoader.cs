using System;
using System.Collections;
using System.Collections.Generic;

namespace Launch_Minecraft
{
    internal class LiteLoader : BaseLoaderLauncher
    {
        public override string LoaderName { get { return "LiteLoader"; } }

        public static void LaunchClient(string minecraftDir, string versionName,
                                        bool isolated = false, string javaBaseDir = null,
                                        string username = null, string uuid = null,
                                        string accessToken = null, string userType = null,
                                        Action<LaunchProgress> onProgress = null)
        {
            new LiteLoader().Launch(
                CreateClientContext(minecraftDir, versionName, isolated, javaBaseDir,
                                    username, uuid, accessToken, userType, onProgress));
        }

        public static void LaunchServer(string serverDir, string versionName,
                                        string javaBaseDir = null)
        {
            throw new NotSupportedException(
                "LiteLoader 是纯客户端模组加载器，没有独立的服务端。\n" +
                "如需服务端请改用 Forge / NeoForge / Fabric / Quilt。");
        }

        /// <summary>
        /// LiteLoader 强制走 LaunchWrapper 模式：无论是否与 Forge 组合，
        /// 都由我们手工拼装 tweak 参数，避免依赖 json 里的 minecraftArguments。
        /// </summary>
        protected override bool UseLaunchWrapperArgs(Dictionary<string, object> root)
        {
            return true;
        }

        protected override void AppendLaunchWrapperArgs(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        {
            base.AppendLaunchWrapperArgs(cmd, context, root);

            cmd.Add("--versionType");
            cmd.Add("LiteLoader");

            if (HasForgeLibraries(root))
            {
                // Forge + LiteLoader 组合：
                // FMLTweaker 作主 tweak，LiteLoaderTweaker 作级联
                cmd.Add("--tweakClass");
                cmd.Add("net.minecraftforge.fml.common.launcher.FMLTweaker");
                cmd.Add("--cascadedTweaks");
                cmd.Add("com.mumfrey.liteloader.launch.LiteLoaderTweaker");
            }
            else
            {
                cmd.Add("--tweakClass");
                cmd.Add("com.mumfrey.liteloader.launch.LiteLoaderTweaker");
            }

            cmd.Add("--width"); cmd.Add(context.Width.ToString());
            cmd.Add("--height"); cmd.Add(context.Height.ToString());
        }

        private bool HasForgeLibraries(Dictionary<string, object> root)
        {
            if (!root.ContainsKey("libraries")) return false;
            var libs = root["libraries"] as ArrayList;
            if (libs == null) return false;

            foreach (var libObj in libs)
            {
                var lib = libObj as Dictionary<string, object>;
                if (lib == null) continue;
                string name = lib.ContainsKey("name") ? Convert.ToString(lib["name"]) : null;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith("net.minecraftforge:", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}