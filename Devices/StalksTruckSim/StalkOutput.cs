using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>
    /// Stalk output bindings. A binding string is either a keyboard key (sent by
    /// <see cref="KeyboardSender"/>) or <see cref="RolePrefix"/> + the name of a
    /// SimHub Control Mapper button role, which Control Mapper turns into a button
    /// on its virtual controller output (vJoy or a SimHub-flashed Arduino) — so
    /// the game sees a controller button instead of a key.
    /// </summary>
    public static class StalkOutput
    {
        public const string RolePrefix = "role:";

        public static bool IsRole(string? binding)
            => binding != null && binding.StartsWith(RolePrefix, StringComparison.OrdinalIgnoreCase);

        public static string RoleName(string binding)
            => IsRole(binding) ? binding.Substring(RolePrefix.Length) : "";

        /// <summary>Display text for a role binding ("🎮 name"), or "" for a key.</summary>
        public static string DisplayName(string? binding)
            => IsRole(binding) ? "🎮 " + RoleName(binding!) : "";
    }

    /// <summary>
    /// Presses Control Mapper button roles in order on its own worker thread, so
    /// repeated taps of one role (stepping a wiper two stages) stay distinct
    /// presses with a gap the controller output can see. Mirrors
    /// <see cref="KeyboardSender"/>'s Tap/Down/Up/ReleaseAll surface.
    /// </summary>
    internal sealed class RoleSender : IDisposable
    {
        private enum Op { Tap, Down, Up }

        private readonly Func<object?> _getInterface;
        private readonly BlockingCollection<(string role, Op op)> _queue = new BlockingCollection<(string, Op)>();
        private readonly HashSet<string> _held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Thread _worker;
        private volatile bool _disposed;

        // Controller outputs are polled by the game; shorter presses can be missed.
        public int HoldMs { get; set; } = 60;
        public int GapMs { get; set; } = 60;

        /// <param name="getInterface">Returns SimHub's ControlMapperInterface (or null).</param>
        public RoleSender(Func<object?> getInterface)
        {
            _getInterface = getInterface;
            _worker = new Thread(Run) { IsBackground = true, Name = "AZOM stalk roles" };
            _worker.Start();
        }

        public void Tap(string role) => Enqueue(role, Op.Tap);

        public void Down(string role)
        {
            lock (_held) _held.Add(role);
            Enqueue(role, Op.Down);
        }

        public void Up(string role)
        {
            lock (_held) _held.Remove(role);
            Enqueue(role, Op.Up);
        }

        public void ReleaseAll()
        {
            string[] held;
            lock (_held) { held = new string[_held.Count]; _held.CopyTo(held); _held.Clear(); }
            foreach (var r in held) Enqueue(r, Op.Up);
        }

        private void Enqueue(string role, Op op)
        {
            if (!string.IsNullOrEmpty(role) && !_disposed && !_queue.IsAddingCompleted)
                _queue.TryAdd((role, op));
        }

        private void Run()
        {
            try
            {
                // Drains what is queued at Dispose (the final role releases) before exiting.
                foreach (var (role, op) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        var cmi = _getInterface() as SimHub.Plugins.ControlMapperInterface;
                        if (cmi == null) continue;
                        switch (op)
                        {
                            case Op.Down: cmi.StartRole(role); break;
                            case Op.Up: cmi.StopRole(role); break;
                            case Op.Tap:
                                cmi.StartRole(role);
                                Thread.Sleep(Math.Max(20, HoldMs));
                                cmi.StopRole(role);
                                Thread.Sleep(Math.Max(0, GapMs));
                                break;
                        }
                    }
                    catch { }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            ReleaseAll();       // enqueued before adding closes, so the worker still sends them
            _disposed = true;
            try { _queue.CompleteAdding(); } catch { }
        }
    }
}
