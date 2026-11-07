using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MyPreload;

/// <summary>
/// 挂钩 ntdll 文件相关 API，处理基岩版数据隔离和启动信息语言文件重定向。
/// </summary>
internal static unsafe class FileRedirectHooks
{
    private const int BufferChars = 2048;
    private static readonly nint InvalidHandle = new(-1);

    private static ConfigManager? _config;
    private static bool _detailedLog;
    private static int _launchInfoRedirectLogged;

    private static nint _createFile;
    private static nint _openFile;
    private static nint _queryAttributes;
    private static nint _queryFullAttributes;
    private static nint _setInformation;
    private static nint _deleteFile;
    private static nint _queryDirectory;
    private static nint _createSection;

    public static void Install(ConfigManager config)
    {
        _config = config;
        _detailedLog = config.GetConfigBool("isDetailedLog");

        nint ntdll = NativeMethods.GetModuleHandleW("ntdll.dll");
        if (ntdll == 0)
        {
            Logger.Error("Get ntdll pt error", "FileRedirectHooks");
            return;
        }

        _createFile = NativeMethods.GetProcAddress(ntdll, "NtCreateFile");
        _openFile = NativeMethods.GetProcAddress(ntdll, "NtOpenFile");
        _queryAttributes = NativeMethods.GetProcAddress(ntdll, "NtQueryAttributesFile");
        _queryFullAttributes = NativeMethods.GetProcAddress(ntdll, "NtQueryFullAttributesFile");
        _setInformation = NativeMethods.GetProcAddress(ntdll, "NtSetInformationFile");
        _deleteFile = NativeMethods.GetProcAddress(ntdll, "NtDeleteFile");
        _queryDirectory = NativeMethods.GetProcAddress(ntdll, "NtQueryDirectoryFile");
        _createSection = NativeMethods.GetProcAddress(ntdll, "NtCreateSection");

        LogAddresses();

        int attached = 0;
        attached += Attach(ref _createFile, (nint)(delegate* unmanaged<nint*, uint, ObjectAttributes*, IoStatusBlock*, long*, uint, uint, uint, uint, void*, uint, int>)&OnCreateFile) ? 1 : 0;
        attached += Attach(ref _openFile, (nint)(delegate* unmanaged<nint*, uint, ObjectAttributes*, IoStatusBlock*, uint, uint, int>)&OnOpenFile) ? 1 : 0;
        attached += Attach(ref _queryAttributes, (nint)(delegate* unmanaged<ObjectAttributes*, void*, int>)&OnQueryAttributes) ? 1 : 0;
        attached += Attach(ref _queryFullAttributes, (nint)(delegate* unmanaged<ObjectAttributes*, void*, int>)&OnQueryFullAttributes) ? 1 : 0;
        attached += Attach(ref _setInformation, (nint)(delegate* unmanaged<nint, IoStatusBlock*, void*, uint, FileInformationClass, int>)&OnSetInformation) ? 1 : 0;
        attached += Attach(ref _deleteFile, (nint)(delegate* unmanaged<ObjectAttributes*, int>)&OnDeleteFile) ? 1 : 0;
        attached += Attach(ref _queryDirectory, (nint)(delegate* unmanaged<nint, nint, void*, void*, IoStatusBlock*, void*, uint, FileInformationClass, byte, UnicodeString*, byte, int>)&OnQueryDirectory) ? 1 : 0;
        attached += Attach(ref _createSection, (nint)(delegate* unmanaged<nint*, uint, ObjectAttributes*, long*, uint, uint, nint, int>)&OnCreateSection) ? 1 : 0;

        Logger.Success(attached == 8
            ? "File Redirector Hooked Successfully. Attached: 8"
            : $"Detour attach incomplete. Attached: {attached}/8", "FileRedirectHooks");
    }

    private static void LogAddresses()
    {
        Logger.Info($"NtCreateFile addr: 0x{_createFile:X}", "FileRedirectHooks");
        Logger.Info($"NtOpenFile addr: 0x{_openFile:X}", "FileRedirectHooks");
        Logger.Info($"NtQueryAttributesFile addr: 0x{_queryAttributes:X}", "FileRedirectHooks");
        Logger.Info($"NtQueryFullAttributesFile addr: 0x{_queryFullAttributes:X}", "FileRedirectHooks");
        Logger.Info($"NtSetInformationFile addr: 0x{_setInformation:X}", "FileRedirectHooks");
        Logger.Info($"NtDeleteFile addr: 0x{_deleteFile:X}", "FileRedirectHooks");
        Logger.Info($"NtQueryDirectoryFile addr: 0x{_queryDirectory:X}", "FileRedirectHooks");
        Logger.Info($"NtCreateSection addr: 0x{_createSection:X}", "FileRedirectHooks");
    }

    private static bool Attach(ref nint original, nint detour)
    {
        if (!InlineHook.TryCreate(original, detour, out nint trunk))
            return false;
        original = trunk;
        return true;
    }

    /// <summary>构造重定向后的 OBJECT_ATTRIBUTES；不命中时返回 false。</summary>
    private static bool TryRedirect(
        ObjectAttributes* attributes, ObjectAttributes* patched, UnicodeString* name, char* buffer,
        string operation, out string? relative)
    {
        relative = null;
        if (attributes is null || attributes->ObjectName is null || attributes->ObjectName->Buffer is null)
            return false;

        string path = new(attributes->ObjectName->Buffer, 0, attributes->ObjectName->Length / 2);
        if (_detailedLog)
            Logger.WriteFromHook(LogLevel.Info, $"{operation}: {path}", "FileRedirectHooks");

        string redirect = PathRedirector.GetRedirectedRelativePath(path);
        if (TryRedirectLaunchInfo(path, attributes->RootDirectory, out string launchInfoPath))
        {
            if (launchInfoPath.Length >= BufferChars)
                return false;
            launchInfoPath.AsSpan().CopyTo(new Span<char>(buffer, launchInfoPath.Length));
            buffer[launchInfoPath.Length] = '\0';
            *name = new UnicodeString
            {
                Length = (ushort)(launchInfoPath.Length * 2),
                MaximumLength = (ushort)((launchInfoPath.Length + 1) * 2),
                Buffer = buffer,
            };
            *patched = *attributes;
            patched->ObjectName = name;
            patched->RootDirectory = nint.Zero;
            patched->SecurityDescriptor = nint.Zero;
            relative = launchInfoPath;
            return true;
        }
        if (redirect.Length == 0)
            return false;

        nint root = PathRedirector.GetRootHandle(_config!);
        if (root == InvalidHandle)
            return false;

        redirect.AsSpan().CopyTo(new Span<char>(buffer, redirect.Length));
        buffer[redirect.Length] = '\0';

        *name = new UnicodeString
        {
            Length = (ushort)(redirect.Length * 2),
            MaximumLength = (ushort)((redirect.Length + 1) * 2),
            Buffer = buffer,
        };

        *patched = *attributes;
        patched->Attributes = NtConstants.DontReparse;
        patched->ObjectName = name;
        patched->RootDirectory = root;
        patched->SecurityDescriptor = nint.Zero;

        relative = redirect;
        return true;
    }

    private static bool TryRedirectLaunchInfo(string path, nint rootDirectory, out string redirectedPath)
    {
        redirectedPath = string.Empty;
        if (_config?.GetConfigBool("launchInfoEnabled") != true ||
            !path.EndsWith(".lang", StringComparison.OrdinalIgnoreCase))
            return false;

        string normalizedPath = path.Replace('/', '\\');
        bool isVanillaText = normalizedPath.IndexOf(@"\data\resource_packs\vanilla\texts\",
            StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isVanillaText && rootDirectory != 0)
        {
            char* rootPath = stackalloc char[BufferChars];
            uint length = NativeMethods.GetFinalPathNameByHandleW(rootDirectory, rootPath, BufferChars, 0);
            if (length > 0 && length < BufferChars)
            {
                var root = new string(rootPath, 0, (int)length).TrimEnd('\\');
                var combined = root + "\\" + normalizedPath.TrimStart('\\');
                isVanillaText = combined.IndexOf(@"\data\resource_packs\vanilla\texts\",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        if (!isVanillaText)
            return false;

        var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(gameDirectory))
            return false;
        var shadowPath = Path.Combine(gameDirectory, "config", "JerryCraft", "launch-info", Path.GetFileName(path));
        if (!File.Exists(shadowPath))
            return false;

        redirectedPath = @"\??\" + Path.GetFullPath(shadowPath);
        if (Interlocked.Exchange(ref _launchInfoRedirectLogged, 1) == 0)
            Logger.WriteFromHook(LogLevel.Success, $"Launch info language redirected: {Path.GetFileName(path)}", "FileRedirectHooks");
        return true;
    }

    private static uint AsDirectoryFlag(uint options, string relative) =>
        PathRedirector.IsDirectory(relative) || relative.EndsWith('\\')
            ? (options & ~NtConstants.NonDirectoryFile) | NtConstants.DirectoryFile
            : options;

    [UnmanagedCallersOnly]
    private static int OnCreateFile(nint* handle, uint access, ObjectAttributes* attributes, IoStatusBlock* io,
        long* allocation, uint fileAttributes, uint share, uint disposition, uint options, void* ea, uint eaLength)
    {
        ObjectAttributes patched = default;
        UnicodeString name = default;
        char* buffer = stackalloc char[BufferChars];

        bool redirected = TryRedirect(attributes, &patched, &name, buffer, "NtCreateFile", out string? relative);
        if (redirected)
            options = AsDirectoryFlag(options, relative!);

        return ((delegate* unmanaged<nint*, uint, ObjectAttributes*, IoStatusBlock*, long*, uint, uint, uint, uint, void*, uint, int>)_createFile)(
            handle, access, redirected ? &patched : attributes, io, allocation, fileAttributes, share, disposition, options, ea, eaLength);
    }

    [UnmanagedCallersOnly]
    private static int OnOpenFile(nint* handle, uint access, ObjectAttributes* attributes, IoStatusBlock* io,
        uint share, uint options)
    {
        ObjectAttributes patched = default;
        UnicodeString name = default;
        char* buffer = stackalloc char[BufferChars];

        bool redirected = TryRedirect(attributes, &patched, &name, buffer, "NtOpenFile", out string? relative);
        if (redirected)
            options = AsDirectoryFlag(options, relative!);

        return ((delegate* unmanaged<nint*, uint, ObjectAttributes*, IoStatusBlock*, uint, uint, int>)_openFile)(
            handle, access, redirected ? &patched : attributes, io, share, options);
    }

    [UnmanagedCallersOnly]
    private static int OnQueryAttributes(ObjectAttributes* attributes, void* fileInformation)
    {
        ObjectAttributes patched = default;
        UnicodeString name = default;
        char* buffer = stackalloc char[BufferChars];

        bool redirected = TryRedirect(attributes, &patched, &name, buffer, "NtQueryAttributesFile", out _);
        return ((delegate* unmanaged<ObjectAttributes*, void*, int>)_queryAttributes)(
            redirected ? &patched : attributes, fileInformation);
    }

    [UnmanagedCallersOnly]
    private static int OnQueryFullAttributes(ObjectAttributes* attributes, void* fileInformation)
    {
        ObjectAttributes patched = default;
        UnicodeString name = default;
        char* buffer = stackalloc char[BufferChars];

        bool redirected = TryRedirect(attributes, &patched, &name, buffer, "NtQueryFullAttributesFile", out _);
        return ((delegate* unmanaged<ObjectAttributes*, void*, int>)_queryFullAttributes)(
            redirected ? &patched : attributes, fileInformation);
    }

    [UnmanagedCallersOnly]
    private static int OnSetInformation(nint handle, IoStatusBlock* io, void* information, uint length,
        FileInformationClass fileClass)
    {
        if (fileClass is FileInformationClass.FileRenameInformation or FileInformationClass.FileRenameInformationEx)
        {
            byte* info = (byte*)information;
            if (info is not null)
            {
                uint nameLength = *(uint*)(info + 16);
                if (nameLength > 0)
                {
                    string original = new((char*)(info + NtConstants.RenameFileNameOffset), 0, (int)(nameLength / 2));
                    string relative = PathRedirector.GetRedirectedRelativePath(original);
                    if (relative.Length > 0)
                    {
                        nint root = PathRedirector.GetRootHandle(_config!);
                        if (root != InvalidHandle)
                        {
                            byte* buffer = stackalloc byte[NtConstants.RenameStructSize + relative.Length * 2];
                            buffer[0] = *(byte*)(info + 0);
                            *(nint*)(buffer + 8) = root;
                            *(uint*)(buffer + 16) = (uint)(relative.Length * 2);
                            relative.AsSpan().CopyTo(new Span<char>((char*)(buffer + NtConstants.RenameFileNameOffset), relative.Length));

                            return ((delegate* unmanaged<nint, IoStatusBlock*, void*, uint, FileInformationClass, int>)_setInformation)(
                                handle, io, buffer, (uint)(NtConstants.RenameStructSize + relative.Length * 2), fileClass);
                        }
                    }
                }
            }
        }

        return ((delegate* unmanaged<nint, IoStatusBlock*, void*, uint, FileInformationClass, int>)_setInformation)(
            handle, io, information, length, fileClass);
    }

    [UnmanagedCallersOnly]
    private static int OnDeleteFile(ObjectAttributes* attributes)
    {
        ObjectAttributes patched = default;
        UnicodeString name = default;
        char* buffer = stackalloc char[BufferChars];

        bool redirected = TryRedirect(attributes, &patched, &name, buffer, "NtDeleteFile", out _);
        return ((delegate* unmanaged<ObjectAttributes*, int>)_deleteFile)(redirected ? &patched : attributes);
    }

    [UnmanagedCallersOnly]
    private static int OnQueryDirectory(nint handle, nint eventHandle, void* apcRoutine, void* apcContext,
        IoStatusBlock* io, void* fileInformation, uint length, FileInformationClass fileClass, byte singleEntry,
        UnicodeString* fileName, byte restartScan)
    {
        return ((delegate* unmanaged<nint, nint, void*, void*, IoStatusBlock*, void*, uint, FileInformationClass, byte, UnicodeString*, byte, int>)_queryDirectory)(
            handle, eventHandle, apcRoutine, apcContext, io, fileInformation, length, fileClass, singleEntry, fileName, restartScan);
    }

    [UnmanagedCallersOnly]
    private static int OnCreateSection(nint* sectionHandle, uint access, ObjectAttributes* attributes,
        long* maximumSize, uint protection, uint allocationAttributes, nint fileHandle)
    {
        if (attributes is not null && attributes->ObjectName is not null && attributes->ObjectName->Buffer is not null)
        {
            string original = new(attributes->ObjectName->Buffer, 0, attributes->ObjectName->Length / 2);
            string relative = PathRedirector.GetRedirectedRelativePath(original);
            if (relative.Length > 0)
            {
                nint root = PathRedirector.GetRootHandle(_config!);
                if (root != InvalidHandle)
                {
                    char* buffer = stackalloc char[relative.Length + 1];
                    relative.AsSpan().CopyTo(new Span<char>(buffer, relative.Length));
                    buffer[relative.Length] = '\0';

                    var name = new UnicodeString
                    {
                        Length = (ushort)(relative.Length * 2),
                        MaximumLength = (ushort)((relative.Length + 1) * 2),
                        Buffer = buffer,
                    };

                    ObjectAttributes redirected = *attributes;
                    redirected.ObjectName = &name;
                    redirected.RootDirectory = root;

                    return ((delegate* unmanaged<nint*, uint, ObjectAttributes*, long*, uint, uint, nint, int>)_createSection)(
                        sectionHandle, access, &redirected, maximumSize, protection, allocationAttributes, fileHandle);
                }
            }
        }

        return ((delegate* unmanaged<nint*, uint, ObjectAttributes*, long*, uint, uint, nint, int>)_createSection)(
            sectionHandle, access, attributes, maximumSize, protection, allocationAttributes, fileHandle);
    }
}