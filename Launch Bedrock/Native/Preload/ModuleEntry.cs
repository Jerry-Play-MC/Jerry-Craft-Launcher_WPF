using System;
using System.IO;

namespace MyPreload;

internal static unsafe class ModuleEntry
{
    private static readonly nint InvalidHandle = new(-1);
    private static bool _initialized;

    internal static void Run()
    {
        if (_initialized)
            return;
        _initialized = true;

        WriteBootMarker("module-init-start");
        try
        {
            UseExeDirectoryAsWorkingDirectory();

            var config = new ConfigManager();

            if (config.GetConfigBool("isConsole"))
                OpenConsole();

            Logger.Initialize(config.GetConfigBool("isConsole"), config.GetConfig("nativeLogFile"));
            Logger.Info($"MyPreload starting. Exe: {Environment.ProcessPath}", "ModuleEntry");
            Logger.Info($"CWD: {Environment.CurrentDirectory}", "ModuleEntry");
            Logger.Info($"PID: {Environment.ProcessId}", "ModuleEntry");
            VersionInfo.Print();

            if (config.GetConfigBool("isVersionIsolated") || config.GetConfigBool("launchInfoEnabled"))
            {
                Logger.Info("Installing Package Identity Hooks.", "ModuleEntry");
                PackageIdentityHooks.Install();

                Logger.Info("Initializing File Hook.", "ModuleEntry");
                FileRedirectHooks.Install(config);
            }
            else
            {
                Logger.Warning("Neither isVersionIsolated nor launchInfoEnabled; skipping file hook.", "ModuleEntry");
            }

            Logger.Success("Module initialization complete.", "ModuleEntry");
        }
        catch (Exception ex)
        {
            Logger.Error($"Module initialization failed: {ex}", "ModuleEntry");
        }
        WriteBootMarker("module-init-done");
    }

    private static void WriteBootMarker(string state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(directory))
                return;

            string logDir = Path.Combine(directory, "config", "JerryCraft", "logs");
            Directory.CreateDirectory(logDir);

            string path = Path.Combine(logDir, "boot.log");
            byte[] line = System.Text.Encoding.UTF8.GetBytes($"{DateTime.Now:O} {state}\n");
            nint handle = NativeMethods.CreateFileW(path, 0x40000000 /* GENERIC_WRITE */,
                0x7 /* SHARE_READ|WRITE|DELETE */, nint.Zero,
                4 /* OPEN_ALWAYS, 追加 */, 0x80 /* FILE_ATTRIBUTE_NORMAL */, nint.Zero);
            if (handle == InvalidHandle)
                return;

            fixed (byte* buffer = line)
            {
                NativeMethods.WriteFile(handle, buffer, (uint)line.Length, out _, nint.Zero);
            }
            NativeMethods.CloseHandle(handle);
        }
        catch
        {
        }
    }

    private static void UseExeDirectoryAsWorkingDirectory()
    {
        string? directory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(directory))
            NativeMethods.SetCurrentDirectoryW(directory);
    }

    private static void OpenConsole()
    {
        try
        {
            NativeMethods.AllocConsole();
            NativeMethods.SetConsoleTitleW("Minecraft Bedrock Console");
        }
        catch
        {
        }
    }
}