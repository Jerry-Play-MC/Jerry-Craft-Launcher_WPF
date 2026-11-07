using System.Runtime.InteropServices;

namespace MyPreload;

internal static unsafe class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "Load")]
    internal static void Load() => ModuleEntry.Run();

    [UnmanagedCallersOnly(EntryPoint = "GetDllVersion")]
    internal static byte* GetDllVersion() => VersionInfo.DllVersion;

    [UnmanagedCallersOnly(EntryPoint = "GetCommitHash")]
    internal static byte* GetCommitHash() => VersionInfo.CommitHash;
}