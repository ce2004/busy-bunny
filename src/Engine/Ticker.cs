using System;
using System.Collections.Generic;

namespace BusyBunny
{
    /// <summary>
    /// One clock for time-based features (sweep 8.2 E): it runs on the main window's minute tick, so nothing needs
    /// its own timer. A feature registers a check (is it due now?) and what to do. It never runs twice at once: a
    /// run that opens a window and so lets the next minute tick through is skipped, not run again inside itself.
    /// Anything that speaks must still ask Calm.SayUnprompted.
    /// </summary>
    public static class Ticker
    {
        private sealed record Job(string Id, Func<DateTime, bool> Due, Action<DateTime> Run);
        private static readonly List<Job> _jobs = new();
        private static bool _running, _attached;
        private static MainWindow? _window;

        /// <summary>Hooks the minute tick of the main window, once.</summary>
        public static void Attach(MainWindow w)
        {
            if (_attached && _window == w) return;
            _attached = true;
            _window = w;
            w.MinuteTick += () => Tick(DateTime.Now);
        }

        /// <summary>Registers (or replaces, by id) a job: due is asked every minute, run happens when it says true.</summary>
        public static void Every(string id, Func<DateTime, bool> due, Action<DateTime> run)
        {
            _jobs.RemoveAll(j => j.Id == id);
            _jobs.Add(new Job(id, due, run));
        }

        /// <summary>The same, for a job that needs no time.</summary>
        public static void Every(string id, Func<DateTime, bool> due, Action run) => Every(id, due, _ => run());

        public static void Remove(string id) => _jobs.RemoveAll(j => j.Id == id);

        /// <summary>Runs every job that is due. Called once a minute; the self-test calls it with a made-up time.</summary>
        public static void Tick(DateTime now)
        {
            if (_running) return;
            _running = true;
            try
            {
                foreach (var j in _jobs.ToArray())
                {
                    try { if (j.Due(now)) j.Run(now); }
                    catch (Exception e) { ErrorLog.Write("The ticker job " + j.Id + " failed: " + e.GetType().Name + ": " + e.Message); }
                }
            }
            finally { _running = false; }
        }

        public static void SelfTest()
        {
            int runs = 0, inner = 0;
            Every("selftest.a", _ => true, () => { runs++; Tick(DateTime.Now); inner++; });
            Every("selftest.b", _ => throw new Exception("broken"), () => { });
            Every("selftest.c", t => t.Minute == 0, () => runs += 10);
            try
            {
                Tick(new DateTime(2026, 1, 1, 9, 0, 0));
                Tick(new DateTime(2026, 1, 1, 9, 1, 0));
            }
            finally { Remove("selftest.a"); Remove("selftest.b"); Remove("selftest.c"); }
            if (runs != 12 || inner != 2) throw new Exception("ticker: ran " + runs + " (re-entered or skipped)");
        }
    }
}
