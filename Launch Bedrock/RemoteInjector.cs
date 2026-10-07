using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Launch_Bedrock
{
    /// <summary>
    /// 通过 CreateRemoteThread + LoadLibraryA 把 DLL 注入到目标进程。
    /// 只适用于 CREATE_SUSPENDED 出来的进程，或加载器已完成的目标。
    /// </summary>
    internal static class RemoteInjector
    {
        private const uint ProcessAll = 0x001F0FFF;
        private const uint MemCommit = 0x00001000;
        private const uint MemReserve = 0x00002000;
        private const uint MemRelease = 0x00008000;
        private const uint PageReadWrite = 0x04;
        private const uint Infinite = 0xFFFFFFFF;

        public static bool Inject(uint processId, string dllPath)
        {
            var fullPath = System.IO.Path.GetFullPath(dllPath);
            if (!System.IO.File.Exists(fullPath))
                throw new System.IO.FileNotFoundException("待注入 DLL 不存在", fullPath);

            var process = OpenProcess(ProcessAll, false, processId);
            if (process == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, "OpenProcess 失败，错误码=" + err);
            }

            try
            {
                return InjectCore(process, fullPath);
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static bool InjectCore(IntPtr process, string dllPath)
        {
            // 1) 取 LoadLibraryA 地址（各进程 kernel32 映射一致）
            var kernel32 = GetModuleHandleW("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, "GetModuleHandle(kernel32) 失败，错误码=" + err);
            }

            var loadLibraryA = GetProcAddress(kernel32, "LoadLibraryA");
            if (loadLibraryA == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, "GetProcAddress(LoadLibraryA) 失败，错误码=" + err);
            }

            // 2) 在目标进程分配路径缓冲
            var bytes = Encoding.ASCII.GetBytes(dllPath);
            var size = (uint)(bytes.Length + 1);
            var remote = VirtualAllocEx(process, IntPtr.Zero, size, MemCommit | MemReserve, PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, "VirtualAllocEx 失败，错误码=" + err);
            }

            var ok = false;
            try
            {
                // 3) 写路径
                var buffer = new byte[bytes.Length + 1];
                Buffer.BlockCopy(bytes, 0, buffer, 0, bytes.Length);
                if (!WriteProcessMemory(process, remote, buffer, (IntPtr)size, out var written) ||
                    (long)written != size)
                {
                    var err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, "WriteProcessMemory 失败，错误码=" + err);
                }

                // 4) 远程线程
                var thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibraryA, remote, 0, out _);
                if (thread == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, "CreateRemoteThread 失败，错误码=" + err);
                }

                try
                {
                    var wait = WaitForSingleObject(thread, Infinite);
                    if (wait != 0)
                    {
                        var err = Marshal.GetLastWin32Error();
                        throw new Win32Exception(err,
                            "WaitForSingleObject 失败，返回值=" + wait + "，错误码=" + err);
                    }

                    if (!GetExitCodeThread(thread, out var exitCode))
                    {
                        var err = Marshal.GetLastWin32Error();
                        throw new Win32Exception(err, "GetExitCodeThread 失败，错误码=" + err);
                    }

                    if (exitCode == 0)
                        throw new Win32Exception("LoadLibraryA 在目标进程中失败（返回 NULL）");

                    ok = true;
                }
                finally
                {
                    CloseHandle(thread);
                }
            }
            finally
            {
                VirtualFreeEx(process, remote, IntPtr.Zero, MemRelease);
            }
            return ok;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, uint size,
            uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address,
            byte[] buffer, IntPtr size, out IntPtr bytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes,
            uint stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);
    }
}