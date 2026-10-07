using System;
using System.Runtime.InteropServices;

namespace MyPreload;

/// <summary>
/// 挂钩 kernelbase 的包标识 API，为无包标识的落盘进程伪造虚拟包信息。
/// 返回形如 "Microsoft.MinecraftUWP_1.26.52.0_x64__8wekyb3d8bbwe" 的完整包名。
/// </summary>
internal static unsafe class PackageIdentityHooks
{
    // 要和商店版一致，让游戏内部所有"按包名拼路径"的分支都走正常逻辑
    private const string FakePackageName = "Microsoft.MinecraftUWP";
    private const string FakeFamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
    private const string FakePackageFullName = "Microsoft.MinecraftUWP_1.26.52.0_x64__8wekyb3d8bbwe";
    private const string FakePackagePath = @"D:\MC\26.52";
    private const string FakeAumid = "Microsoft.MinecraftUWP_8wekyb3d8bbwe!App";

    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    private static nint _getFullName;
    private static nint _getFamilyName;
    private static nint _getPath;
    private static nint _getAumid;
    private static nint _getId;

    public static void Install()
    {
        nint kbase = NativeMethods.GetModuleHandleW("kernelbase.dll");
        if (kbase == 0)
        {
            Logger.Error("kernelbase.dll not loaded", "PackageIdentityHooks");
            return;
        }

        _getFullName = NativeMethods.GetProcAddress(kbase, "GetCurrentPackageFullName");
        _getFamilyName = NativeMethods.GetProcAddress(kbase, "GetCurrentPackageFamilyName");
        _getPath = NativeMethods.GetProcAddress(kbase, "GetCurrentPackagePath");
        _getAumid = NativeMethods.GetProcAddress(kbase, "GetCurrentApplicationUserModelId");
        _getId = NativeMethods.GetProcAddress(kbase, "GetCurrentPackageId");

        Logger.Info($"GetCurrentPackageFullName         = 0x{_getFullName:X}", "PackageIdentityHooks");
        Logger.Info($"GetCurrentPackageFamilyName       = 0x{_getFamilyName:X}", "PackageIdentityHooks");
        Logger.Info($"GetCurrentPackagePath             = 0x{_getPath:X}", "PackageIdentityHooks");
        Logger.Info($"GetCurrentApplicationUserModelId  = 0x{_getAumid:X}", "PackageIdentityHooks");
        Logger.Info($"GetCurrentPackageId               = 0x{_getId:X}", "PackageIdentityHooks");

        int attached = 0;
        if (Attach(ref _getFullName, (nint)(delegate* unmanaged<uint*, char*, int>)&OnGetFullName))
        { Logger.Info("Hooked GetCurrentPackageFullName", "PackageIdentityHooks"); attached++; }
        if (Attach(ref _getFamilyName, (nint)(delegate* unmanaged<uint*, char*, int>)&OnGetFamilyName))
        { Logger.Info("Hooked GetCurrentPackageFamilyName", "PackageIdentityHooks"); attached++; }
        if (Attach(ref _getPath, (nint)(delegate* unmanaged<uint*, char*, int>)&OnGetPath))
        { Logger.Info("Hooked GetCurrentPackagePath", "PackageIdentityHooks"); attached++; }
        if (Attach(ref _getAumid, (nint)(delegate* unmanaged<uint*, char*, int>)&OnGetAumid))
        { Logger.Info("Hooked GetCurrentApplicationUserModelId", "PackageIdentityHooks"); attached++; }
        if (Attach(ref _getId, (nint)(delegate* unmanaged<uint*, byte*, int>)&OnGetId))
        { Logger.Info("Hooked GetCurrentPackageId", "PackageIdentityHooks"); attached++; }

        Logger.Success($"Package Identity Hooks attached: {attached}/5", "PackageIdentityHooks");
    }

    private static bool Attach(ref nint original, nint detour)
    {
        if (original == 0)
        {
            Logger.Warning("Attach: original is null", "PackageIdentityHooks");
            return false;
        }
        if (!InlineHook.TryCreate(original, detour, out nint trunk))
        {
            // 打印前 16 字节，看是什么指令
            byte* p = (byte*)original;
            Logger.Warning($"TryCreate failed at 0x{original:X}: " +
                           $"{p[0]:X2} {p[1]:X2} {p[2]:X2} {p[3]:X2} " +
                           $"{p[4]:X2} {p[5]:X2} {p[6]:X2} {p[7]:X2} " +
                           $"{p[8]:X2} {p[9]:X2} {p[10]:X2} {p[11]:X2} " +
                           $"{p[12]:X2} {p[13]:X2} {p[14]:X2} {p[15]:X2}",
                           "PackageIdentityHooks");
            return false;
        }
        original = trunk;
        return true;
    }

    private static int WriteString(string value, uint* length, char* buffer)
    {
        uint needed = (uint)((value.Length + 1) * 2);
        if (length == null)
            return ErrorInsufficientBuffer;
        if (buffer == null || *length < needed)
        {
            *length = needed;
            return ErrorInsufficientBuffer;
        }

        fixed (char* src = value)
        {
            Buffer.MemoryCopy(src, buffer, *length, needed);
        }
        *length = needed;
        return ErrorSuccess;
    }

    [UnmanagedCallersOnly]
    private static int OnGetFullName(uint* length, char* buffer)
        => WriteString(FakePackageFullName, length, buffer);

    [UnmanagedCallersOnly]
    private static int OnGetFamilyName(uint* length, char* buffer)
        => WriteString(FakeFamilyName, length, buffer);

    [UnmanagedCallersOnly]
    private static int OnGetPath(uint* length, char* buffer)
        => WriteString(FakePackagePath, length, buffer);

    [UnmanagedCallersOnly]
    private static int OnGetAumid(uint* length, char* buffer)
        => WriteString(FakeAumid, length, buffer);

    [UnmanagedCallersOnly]
    private static int OnGetId(uint* length, byte* buffer)
    {
        // PACKAGE_ID 结构：name, publisher, version(4×u16), arch, resourceId, ...
        // 简单返回和完整包名一致的信息就够
        // 真实结构较长，这里用一个紧凑的构造
        string name = FakePackageName;
        uint nameBytes = (uint)((name.Length + 1) * 2);
        // 简化：只返回 name 字段就够游戏用（大部分实现只看前 8 字节的 nameLength）
        uint needed = 8 + nameBytes + 128;
        if (length == null)
            return ErrorInsufficientBuffer;
        if (buffer == null || *length < needed)
        {
            *length = needed;
            return ErrorInsufficientBuffer;
        }
        *(uint*)buffer = nameBytes;
        var span = new Span<char>(buffer + 8, name.Length + 1);
        name.AsSpan().CopyTo(span);
        span[name.Length] = '\0';
        *length = needed;
        return ErrorSuccess;
    }
}