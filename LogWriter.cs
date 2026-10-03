using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>
    /// 把 Console.Write / WriteLine 收集成整行，缓存到队列，
    /// 每 100ms 一次性把队列交给回调。\r 视为"回到行首"，
    /// 下一次写字符时清空当前缓冲（处理进度行覆盖打印）。
    /// </summary>
    internal class LogWriter : TextWriter
    {
        private readonly Action<List<string>> _onLines;
        private readonly object _lock = new object();
        private readonly StringBuilder _currentLine = new StringBuilder();
        private readonly List<string> _pendingLines = new List<string>();
        private readonly Timer _flushTimer;

        private bool _atLineStart = true;
        private bool _flushScheduled;
        private bool _disposed;

        // 100ms 合并一次；数值越小越"实时"，但 UI 负担越大
        private const int FlushIntervalMs = 100;

        // 队列上限：超过丢最旧的一半，防止内存膨胀
        private const int MaxPendingLines = 20000;

        public LogWriter(Action<List<string>> onLines)
        {
            _onLines = onLines;
            _flushTimer = new Timer(_ => FlushBatch(), null,
                Timeout.Infinite, Timeout.Infinite);
        }

        public override Encoding Encoding { get { return Encoding.UTF8; } }

        public override void Write(char c)
        {
            bool needSchedule = false;

            lock (_lock)
            {
                if (c == '\r')
                {
                    _atLineStart = true;
                    return;
                }

                if (c == '\n')
                {
                    _pendingLines.Add(_currentLine.ToString());
                    _currentLine.Clear();
                    _atLineStart = true;

                    if (!_flushScheduled && !_disposed)
                    {
                        _flushScheduled = true;
                        needSchedule = true;
                    }

                    if (_pendingLines.Count > MaxPendingLines)
                    {
                        int drop = _pendingLines.Count - MaxPendingLines / 2;
                        _pendingLines.RemoveRange(0, drop);
                        _pendingLines.Insert(0,
                            "...[丢弃 " + drop + " 行超量日志]...");
                    }
                }
                else
                {
                    if (_atLineStart)
                    {
                        _currentLine.Clear();
                        _atLineStart = false;
                    }
                    _currentLine.Append(c);
                }
            }

            if (needSchedule)
            {
                try { _flushTimer.Change(FlushIntervalMs, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }

        public override void Write(string value)
        {
            if (value == null) return;
            for (int i = 0; i < value.Length; i++)
                Write(value[i]);
        }

        public override void WriteLine(string value)
        {
            Write(value);
            Write('\r');
            Write('\n');
        }

        public override void WriteLine()
        {
            Write('\r');
            Write('\n');
        }

        public override void Flush()
        {
            FlushBatch();
        }

        private void FlushBatch()
        {
            List<string> batch = null;

            lock (_lock)
            {
                _flushScheduled = false;

                if (_currentLine.Length > 0 && !_atLineStart)
                {
                    string partial = _currentLine.ToString().TrimEnd();
                    if (partial.Length > 0)
                        _pendingLines.Add(partial);
                    _currentLine.Clear();
                    _atLineStart = true;
                }

                if (_pendingLines.Count > 0)
                {
                    batch = new List<string>(_pendingLines);
                    _pendingLines.Clear();
                }
            }

            if (batch != null && _onLines != null)
                _onLines(batch);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                try { _flushTimer.Dispose(); } catch { }
                FlushBatch();
            }
            base.Dispose(disposing);
        }
    }
}