using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>
    /// 简单的阶段依赖图调度器。
    /// 某个阶段的依赖全部成功后，立即在当前线程池启动它。
    /// 任一阶段失败 → 抛出 → 调用方可以配合取消令牌终止其它阶段。
    /// </summary>
    public class TaskGraph
    {
        private class Node
        {
            public string Name;
            public double Weight;
            public Func<CancellationToken, Task> Work;
            public string[] DependsOn = new string[0];
            public SubTaskItem Ui;
            public Task Task;
        }

        private readonly List<Node> _nodes = new List<Node>();
        private readonly TaskGroup _group;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _cts;

        public TaskGraph(TaskGroup group, Action<string> log = null,
                         CancellationTokenSource cts = null)
        {
            _group = group;
            _log = log;
            _cts = cts ?? new CancellationTokenSource();
        }

        public TaskGraph Add(string name, double weight,
                     Func<CancellationToken, Task> work,
                     params string[] dependsOn)
        {
            // ★ 删掉 var ui = _group?.AddStage(name, weight);
            _nodes.Add(new Node
            {
                Name = name,
                Weight = weight,
                Work = work,
                DependsOn = dependsOn ?? new string[0],
                Ui = null, // 让 InstallXXX 自己管 UI 阶段
            });
            return this;
        }

        public async Task RunAsync()
        {
            if (_nodes.Count == 0) return;

            var byName = _nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);

            // 校验依赖
            foreach (var n in _nodes)
                foreach (var dep in n.DependsOn)
                    if (!byName.ContainsKey(dep))
                        throw new InvalidOperationException(
                            $"阶段 '{n.Name}' 依赖未知阶段 '{dep}'");

            try
            {
                while (_nodes.Any(n => n.Task == null))
                {
                    // ★ Bug 1 修复：先检查失败节点，再检查取消。
                    //   失败节点触发 cancel 后，下一轮开头不能先抛 OperationCanceledException。
                    var failedNode = _nodes.FirstOrDefault(n =>
                        n.Task != null && n.Task.IsFaulted);

                    if (failedNode != null)
                    {
                        _log?.Invoke($"[Graph] Aborting: '{failedNode.Name}' failed");

                        // 通知其他还在跑的任务退出（它们内部会检查 _cts.Token）
                        try { _cts.Cancel(); } catch { }

                        // 等所有已启动的结束，避免后台任务继续跑
                        var started = _nodes
                            .Where(n => n.Task != null)
                            .Select(n => n.Task)
                            .ToArray();
                        try { await Task.WhenAll(started); } catch { }

                        // 抛原始异常（不是 OperationCanceledException）
                        var innerEx = failedNode.Task.Exception?.InnerException
                                   ?? failedNode.Task.Exception;
                        throw new Exception(
                            $"阶段 '{failedNode.Name}' 失败: {innerEx?.Message}", innerEx);
                    }

                    // 外部取消（用户点了取消按钮，或 InstallXXX 内部 throw）
                    _cts.Token.ThrowIfCancellationRequested();

                    // ★ Bug 2 修复：依赖只要 IsCompleted 就算满足。
                    //   失败/取消由上面的 failedNode 检查统一处理，这里不重复判。
                    var ready = _nodes.Where(n =>
                        n.Task == null &&
                        n.DependsOn.All(d => byName[d].Task != null && byName[d].Task.IsCompleted))
                        .ToList();

                    if (ready.Count == 0)
                    {
                        var pending = _nodes.Where(n => n.Task == null).Select(n => n.Name);
                        throw new InvalidOperationException(
                            "存在循环依赖或死锁: " + string.Join(", ", pending));
                    }

                    foreach (var n in ready)
                    {
                        var node = n;
                        _log?.Invoke($"[Graph] Start: {node.Name}");
                        node.Task = Task.Run(async () =>
                        {
                            try
                            {
                                await node.Work(_cts.Token);
                                _group?.CompleteStage(node.Ui);   // node.Ui 为 null 时无操作
                                _log?.Invoke($"[Graph] Done : {node.Name}");
                            }
                            catch (OperationCanceledException)
                            {
                                _group?.FailStage(node.Ui, "Cancelled");
                                _log?.Invoke($"[Graph] Cancel: {node.Name}");
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _group?.FailStage(node.Ui, ex.Message);
                                _log?.Invoke($"[Graph] Fail : {node.Name}: {ex.Message}");
                                // ★ 不在这里 _cts.Cancel()，由外层 failedNode 分支统一处理
                                throw;
                            }
                        });
                    }

                    var running = _nodes
                        .Where(n => n.Task != null && !n.Task.IsCompleted)
                        .Select(n => n.Task)
                        .ToList();

                    if (running.Count > 0)
                        await Task.WhenAny(running);
                    else
                        break;
                }

                // 所有节点已启动，等它们完成
                var allTasks = _nodes
                    .Where(n => n.Task != null)
                    .Select(n => n.Task)
                    .ToArray();
                await Task.WhenAll(allTasks);
                _log?.Invoke("[Graph] All stages completed");
            }
            finally
            {
                // 观察所有 Task 的异常，避免 UnobservedTaskException
                foreach (var n in _nodes)
                {
                    if (n.Task != null && n.Task.IsFaulted)
                    {
                        _ = n.Task.ContinueWith(t => { var _ = t.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
            }
        }
    }
}