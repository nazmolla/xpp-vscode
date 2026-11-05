using System;
using System.Collections.Generic;
using System.Linq;

namespace XppVsCode.Server.Analysis
{
    /// <summary>
    /// Builds call trees and statistics from X++ method enter/exit and SQL events, per thread (each web request or batch task
    /// runs on one thread). Times are milliseconds from the start of the trace.
    /// </summary>
    internal sealed class TraceAnalyzer
    {
        public const double GapMs = 500;

        internal sealed class Node
        {
            public string Method;
            public int Calls;
            public double Inclusive, Exclusive, SqlMs;
            public int SqlCount;
            public readonly Dictionary<string, Node> Children = new Dictionary<string, Node>(StringComparer.Ordinal);
        }

        internal sealed class Request
        {
            public int Thread;
            public double Start, Duration;
            public bool Unfinished;
            public int SqlCount;
            public double SqlMs;
            public Node Root;
            /// <summary>Batch framework work: a batch root, or any request on a thread that ran batch tasks.</summary>
            public bool IsBatch;
        }

        internal sealed class MethodStat
        {
            public int Calls;
            public double Inclusive, Exclusive, Max, SqlMs;
            public int SqlCount;
        }

        internal sealed class SqlStat
        {
            public int Count;
            public double Total, Max;
            public readonly Dictionary<string, int> Callers = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        internal sealed class Gap
        {
            /// <summary>0 when no thread of the process logged anything.</summary>
            public int Thread;
            public double Start, Duration;
            public string Method;
        }

        private sealed class Frame
        {
            public Node Node;
            public double Start, Child;
            public Request Request;
        }

        private sealed class ThreadState
        {
            public readonly List<Frame> Stack = new List<Frame>();
            public readonly Dictionary<string, int> Active = new Dictionary<string, int>(StringComparer.Ordinal);
            public double Last;
        }

        private readonly Dictionary<int, ThreadState> _threads = new Dictionary<int, ThreadState>();
        private double _lastAny = double.NaN;

        public readonly List<Request> Requests = new List<Request>();
        public readonly Dictionary<string, MethodStat> Methods = new Dictionary<string, MethodStat>(StringComparer.Ordinal);
        public readonly Dictionary<string, SqlStat> Sql = new Dictionary<string, SqlStat>(StringComparer.Ordinal);
        public readonly List<Gap> Gaps = new List<Gap>();
        public long XppCalls { get; private set; }

        /// <summary>Call for every event of the trace, before the specific call below, so silences are found.</summary>
        public void Event(int thread, double ms)
        {
            if (!double.IsNaN(_lastAny) && ms - _lastAny >= GapMs)
                Gaps.Add(new Gap { Thread = 0, Start = _lastAny, Duration = ms - _lastAny });
            _lastAny = ms;
            var state = State(thread);
            if (state.Stack.Count > 0 && ms - state.Last >= GapMs)
                Gaps.Add(new Gap { Thread = thread, Start = state.Last, Duration = ms - state.Last, Method = state.Stack[state.Stack.Count - 1].Node.Method });
            state.Last = ms;
        }

        public void Enter(int thread, double ms, string method)
        {
            XppCalls++;
            var state = State(thread);
            Frame frame;
            if (state.Stack.Count == 0)
            {
                var request = new Request { Thread = thread, Start = ms, Root = new Node { Method = method } };
                Requests.Add(request);
                frame = new Frame { Node = request.Root, Start = ms, Request = request };
            }
            else
            {
                var parent = state.Stack[state.Stack.Count - 1];
                if (!parent.Node.Children.TryGetValue(method, out var node))
                    parent.Node.Children[method] = node = new Node { Method = method };
                frame = new Frame { Node = node, Start = ms, Request = parent.Request };
            }
            state.Stack.Add(frame);
            state.Active[method] = state.Active.TryGetValue(method, out int n) ? n + 1 : 1;
        }

        /// <summary>Closes the method's frame; frames above it whose exit is missing (exceptions) close with it.</summary>
        public void Exit(int thread, double ms, string method)
        {
            var state = State(thread);
            int index = state.Stack.FindLastIndex(f => f.Node.Method == method);
            if (index < 0) return;  // entered before the trace started
            while (state.Stack.Count > index) Close(state, ms);
        }

        public void SqlStatement(int thread, double ms, string statement, double durationMs)
        {
            var state = State(thread);
            var frame = state.Stack.Count > 0 ? state.Stack[state.Stack.Count - 1] : null;
            string caller = frame?.Node.Method ?? "(no X++ on the stack)";
            if (!Sql.TryGetValue(statement, out var stat)) Sql[statement] = stat = new SqlStat();
            stat.Count++;
            stat.Total += durationMs;
            stat.Max = Math.Max(stat.Max, durationMs);
            stat.Callers[caller] = stat.Callers.TryGetValue(caller, out int c) ? c + 1 : 1;
            if (frame == null) return;
            frame.Node.SqlCount++;
            frame.Node.SqlMs += durationMs;
            frame.Request.SqlCount++;
            frame.Request.SqlMs += durationMs;
            var method = MethodStatFor(caller);
            method.SqlCount++;
            method.SqlMs += durationMs;
        }

        /// <summary>Closes what is still running when the trace ends; those requests are marked unfinished.</summary>
        public void Finish(double endMs)
        {
            foreach (var state in _threads.Values)
            {
                if (state.Stack.Count > 0) state.Stack[0].Request.Unfinished = true;
                while (state.Stack.Count > 0) Close(state, endMs);
            }
            var batchThreads = new HashSet<int>(Requests.Where(r => r.Root.Method.StartsWith("Batch", StringComparison.Ordinal)).Select(r => r.Thread));
            foreach (var request in Requests) request.IsBatch = batchThreads.Contains(request.Thread);
        }

        private void Close(ThreadState state, double ms)
        {
            var frame = state.Stack[state.Stack.Count - 1];
            state.Stack.RemoveAt(state.Stack.Count - 1);
            double inclusive = ms - frame.Start;
            var node = frame.Node;
            node.Calls++;
            node.Inclusive += inclusive;
            node.Exclusive += inclusive - frame.Child;
            if (state.Stack.Count > 0) state.Stack[state.Stack.Count - 1].Child += inclusive;
            else frame.Request.Duration = inclusive;

            var stat = MethodStatFor(node.Method);
            stat.Calls++;
            stat.Exclusive += inclusive - frame.Child;
            stat.Max = Math.Max(stat.Max, inclusive);
            int active = state.Active[node.Method];
            // Recursive calls count once in inclusive time.
            if (active == 1) stat.Inclusive += inclusive;
            if (active == 1) state.Active.Remove(node.Method); else state.Active[node.Method] = active - 1;
        }

        private MethodStat MethodStatFor(string method)
        {
            if (!Methods.TryGetValue(method, out var stat)) Methods[method] = stat = new MethodStat();
            return stat;
        }

        private ThreadState State(int thread)
        {
            if (!_threads.TryGetValue(thread, out var state)) _threads[thread] = state = new ThreadState();
            return state;
        }

        /// <summary>Methods by exclusive time within one request's tree.</summary>
        public static List<KeyValuePair<string, double>> TopExclusive(Node root, int count)
        {
            var totals = new Dictionary<string, double>(StringComparer.Ordinal);
            var pending = new Stack<Node>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                totals[node.Method] = (totals.TryGetValue(node.Method, out double t) ? t : 0) + node.Exclusive;
                foreach (var child in node.Children.Values) pending.Push(child);
            }
            return totals.OrderByDescending(k => k.Value).Take(count).ToList();
        }

        /// <summary>The heaviest path from the root, keeping only the nodes where time splits or is spent.</summary>
        public static List<Node> HotPath(Node root, int maxLines)
        {
            var path = new List<Node>();
            double total = Math.Max(root.Inclusive, 0.001), shown = root.Inclusive;
            var node = root;
            for (int depth = 0; depth < 200 && node.Children.Count > 0 && path.Count < maxLines; depth++)
            {
                var next = node.Children.Values.OrderByDescending(c => c.Inclusive).First();
                if (next.Inclusive < total * 0.05) break;
                bool last = next.Children.Count == 0 || next.Children.Values.Max(c => c.Inclusive) < total * 0.05;
                if (next.Inclusive < shown * 0.9 || next.Exclusive >= total * 0.05 || last)
                {
                    path.Add(next);
                    shown = next.Inclusive;
                }
                node = next;
            }
            return path;
        }

        /// <summary>The node and its children down to <paramref name="minMs"/>, at most <paramref name="budget"/> nodes, for the report.</summary>
        public static Dictionary<string, object> TreeJson(Node node, double minMs, ref int budget)
        {
            budget--;
            var json = new Dictionary<string, object>
            {
                ["m"] = node.Method,
                ["n"] = node.Calls,
                ["incl"] = Math.Round(node.Inclusive, 1),
                ["excl"] = Math.Round(node.Exclusive, 1),
            };
            if (node.SqlCount > 0)
            {
                json["sqlN"] = node.SqlCount;
                json["sqlMs"] = Math.Round(node.SqlMs, 1);
            }
            var children = new List<object>();
            int hidden = 0;
            foreach (var child in node.Children.Values.OrderByDescending(c => c.Inclusive))
            {
                if (child.Inclusive >= minMs && budget > 0) children.Add(TreeJson(child, minMs, ref budget));
                else hidden++;
            }
            if (children.Count > 0) json["c"] = children;
            if (hidden > 0) json["hidden"] = hidden;
            return json;
        }
    }
}
