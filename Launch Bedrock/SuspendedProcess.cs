using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Launch_Bedrock
{
    /// <summary>
    /// 以 CREATE_SUSPENDED 启动一个进程；在 Resume 之前主线程不会执行任何用户代码。
    /// </summary>
    internal sealed class SuspendedProcess : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public uint Cb;
            public IntPtr Reserved;
            public IntPtr Desktop;
            public IntPtr Title;
            public uint X, Y, XSize, YSize;
            public uint XCountChars, YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNewConsole = 0x00000010;
        private const uint CreateUnicodeEnvironment = 0x00000400;

        private IntPtr _process;
        private IntPtr _thread;
        private bool _resumed;

        public uint ProcessId { get; private set; }

        private SuspendedProcess(ProcessInformation info)
        {
            _process = info.Process;
            _thread = info.Thread;
            ProcessId = info.ProcessId;
        }

        public static SuspendedProcess Start(string executable, string arguments, string workingDirectory, bool newConsole)
        {
            var fullPath = Path.GetFullPath(executable);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("找不到可执行文件：" + fullPath, fullPath);

            var fullWorkingDir = Path.GetFullPath(
                string.IsNullOrEmpty(workingDirectory) ? Path.GetDirectoryName(fullPath) : workingDirectory);

            // CreateProcessW 的 lpCommandLine 是可写的，所以要 ToCharArray + 尾 NUL
            var commandLine = ("\"" + fullPath.Replace("\"", "\\\"") + "\"" +
                               (string.IsNullOrEmpty(arguments) ? "" : " " + arguments) + "\0").ToCharArray();

            var si = new StartupInfo();
            si.Cb = (uint)Marshal.SizeOf(typeof(StartupInfo));

            var flags = CreateSuspended;
            if (newConsole) flags |= CreateNewConsole;

            var pi = new ProcessInformation();
            var ok = CreateProcessW(
                fullPath, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                IntPtr.Zero, fullWorkingDir, ref si, out pi);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW 失败");

            return new SuspendedProcess(pi);
        }

        public void Resume()
        {
            if (_resumed) return;
            if (ResumeThread(_thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread 失败");
            _resumed = true;
        }

        public void Terminate(uint exitCode)
        {
            if (_process != IntPtr.Zero)
                TerminateProcess(_process, exitCode);
        }

        public void Dispose()
        {
            var t = Interlocked.Exchange(ref _thread, IntPtr.Zero);
            if (t != IntPtr.Zero) CloseHandle(t);
            var p = Interlocked.Exchange(ref _process, IntPtr.Zero);
            if (p != IntPtr.Zero) CloseHandle(p);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(
            string applicationName,
            char[] commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}