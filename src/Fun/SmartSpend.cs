using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// Bunny's spending (sweep 3.4 and 8.5): every answer from Claude reports the tokens it used, and Busy Bunny adds
    /// them up here at the published prices, so Options, Smarter Bunny can say about how much credit this month has
    /// used (always as an estimate: it only knows this computer). The daily spending cap, off until she turns it on,
    /// stops every Bunny call for the rest of the day once the day's estimate reaches it, and says so plainly.
    /// Both live in the world's bookkeeping (GetKept), so Control Z never changes them.
    /// </summary>
    public sealed class SmartSpend : IFeature
    {
        private const string SpendKey = "smart.spend", CapKey = "smart.cap";

        /// <summary>
        /// Published Claude API prices in dollars per million tokens (platform.claude.com pricing, checked November 2026):
        /// input, output, cache read, cache write (5 minutes). A model not listed is priced as Claude Sonnet 5.5.
        /// </summary>
        public static readonly Dictionary<string, (double In, double Out, double CacheRead, double CacheWrite)> PricesPerMillion = new()
        {
            ["claude-sonnet-5-5"] = (2.00, 10.00, 0.20, 2.50),
            ["claude-opus-5-5"] = (4.00, 20.00, 0.40, 5.00),
            ["claude-haiku-5-5"] = (0.10, 0.50, 0.01, 0.125),
        };

        public sealed class Spend
        {
            /// <summary>Dollars by day, yyyy-MM-dd.</summary>
            public Dictionary<string, double> Dollars { get; set; } = new();
            public Dictionary<string, int> Calls { get; set; } = new();
        }

        public sealed class Cap
        {
            public bool On { get; set; }
            public double Dollars { get; set; } = 1.00;
            public string ToldDay { get; set; } = "";
        }

        /// <summary>Bunny is over today's cap: every Bunny call stops, with this line.</summary>
        public sealed class CapHit : Exception
        {
            public CapHit(double cap) : base("Bunny has used today's spending cap of " + SmartUi.Money(cap) + ", so it is resting until tomorrow. Options, Smarter Bunny changes the cap.") { }
        }

        public void Attach(MainWindow w) { }

        // ---- Counting ----

        public static double Cost(string model, long input, long output, long cacheRead, long cacheWrite)
        {
            var p = PricesPerMillion.TryGetValue(model, out var x) ? x : PricesPerMillion["claude-sonnet-5-5"];
            return (input * p.In + output * p.Out + cacheRead * p.CacheRead + cacheWrite * p.CacheWrite) / 1_000_000.0;
        }

        /// <summary>Adds one answer's tokens, for SmartBunny after every request. Never throws.</summary>
        public static void Record(Anthropic.Models.Messages.Usage? u)
        {
            try
            {
                if (u == null || Engines.Store is not Store s) return;
                Add(s, DateTime.Now, Cost(SmartBunny.ModelId, u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0));
            }
            catch (Exception e) { ErrorLog.Write("Counting Bunny's spending failed: " + e.GetType().Name); }
        }

        public static void Add(Store s, DateTime now, double dollars)
        {
            var sp = s.World.GetKept<Spend>(SpendKey);
            string day = s.Key(now);
            double before = sp.Dollars.GetValueOrDefault(day);
            sp.Dollars[day] = before + dollars;
            sp.Calls[day] = sp.Calls.GetValueOrDefault(day) + 1;
            // A little over a year is plenty for the monthly estimate.
            foreach (var old in sp.Dollars.Keys.Where(k => string.CompareOrdinal(k, s.Key(now.AddDays(-400))) < 0).ToList()) { sp.Dollars.Remove(old); sp.Calls.Remove(old); }
            s.World.SetKept(SpendKey, sp);
            var cap = s.World.GetKept<Cap>(CapKey);
            bool crossed = cap.On && before < cap.Dollars && before + dollars >= cap.Dollars && cap.ToldDay != day;
            if (crossed) { cap.ToldDay = day; s.World.SetKept(CapKey, cap); }
            s.World.Save();
            // Said once, the moment it happens: she needs to know why Bunny went quiet.
            if (crossed && Engines.Window is MainWindow w && ReferenceEquals(w.Store, s))
                w.Dispatcher.BeginInvoke(() => w.Say("Bunny just reached today's spending cap of " + SmartUi.Money(cap.Dollars) + ". It rests until tomorrow; Options, Smarter Bunny changes the cap."));
        }

        public static double Today(Store s, DateTime now) => s.World.GetKept<Spend>(SpendKey).Dollars.GetValueOrDefault(s.Key(now));

        public static (double Dollars, int Calls) Month(Store s, DateTime now)
        {
            var sp = s.World.GetKept<Spend>(SpendKey);
            string prefix = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return (sp.Dollars.Where(kv => kv.Key.StartsWith(prefix)).Sum(kv => kv.Value), sp.Calls.Where(kv => kv.Key.StartsWith(prefix)).Sum(kv => kv.Value));
        }

        // ---- The cap ----

        public static Cap CapOf(Store s) => s.World.GetKept<Cap>(CapKey);

        public static bool CapReached(Store s, DateTime now)
        {
            var cap = CapOf(s);
            return cap.On && Today(s, now) >= cap.Dollars;
        }

        /// <summary>For SmartBunny before a request that can wait: true when today's cap is used up.</summary>
        public static bool CapReached() => Engines.Store is Store s && CapReached(s, DateTime.Now);

        /// <summary>For SmartBunny before a request she is waiting on: throws CapHit with the line to say.</summary>
        public static void Check()
        {
            if (Engines.Store is Store s && CapReached(s, DateTime.Now)) throw new CapHit(CapOf(s).Dollars);
        }

        /// <summary>The credit check, as one paragraph.</summary>
        public static string CreditLine(Store s, DateTime now)
        {
            var (month, calls) = Month(s, now);
            var cap = CapOf(s);
            string line = calls == 0
                ? "Credit check: Busy Bunny has not asked Claude for anything on this computer this month, so it has used about " + SmartUi.Money(0) + "."
                : "Credit check: about " + SmartUi.Money(month) + " of Claude credit used this month, over " + SmartUi.Count(calls, "request") + ", and about " + SmartUi.Money(Today(s, now)) + " today.";
            line += " This is an estimate: Busy Bunny adds up the tokens Claude reported on this computer at the published prices for " + SmartBunny.ModelId +
                ". Your other computer is not counted. Your real balance, and when unused plan credit expires, are at platform.claude.com, under Billing.";
            if (cap.On) line += " The daily spending cap is " + SmartUi.Money(cap.Dollars) + (CapReached(s, now) ? ", and it is reached for today." : ".");
            return line;
        }

        // ---- On Options, Smarter Bunny ----

        /// <summary>The credit check and the daily spending cap, as a part of the Smarter Bunny page.</summary>
        public sealed class Panel
        {
            private readonly Store _store;
            private readonly CheckBox _on;
            private readonly TextBox _amount;
            public FrameworkElement Content { get; }

            public Panel(Store store)
            {
                _store = store;
                var cap = CapOf(store);
                var credit = SmartUi.ReadOnly("Credit check, an estimate", CreditLine(store, DateTime.Now), 440, 110);
                credit.BorderThickness = new Thickness(0);
                _on = new CheckBox { Content = "Use a dail_y spending cap: Bunny stops for the day once it reaches it", IsChecked = cap.On, Margin = new Thickness(0, 6, 0, 4) };
                _amount = new TextBox { Text = cap.Dollars.ToString("0.00", CultureInfo.InvariantCulture) };
                AutomationProperties.SetHelpText(_amount, "In dollars, like 1.50. Counted with the same estimate as the credit check.");
                var p = new StackPanel();
                p.Children.Add(Layout.Row("Credit check, an estimat_e", credit, 440));
                p.Children.Add(_on);
                p.Children.Add(Layout.Row("_Most Bunny may spend in a day, in dollars, like 1.50", _amount, 200));
                Content = p;
            }

            public static double? ParseDollars(string text) =>
                double.TryParse(text.Trim().TrimStart('$'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 0.05 && d <= 500 ? Math.Round(d, 2) : null;

            public string? Validate()
            {
                if (_on.IsChecked != true) return null;
                if (ParseDollars(_amount.Text) == null) { _amount.Focus(); return "The daily spending cap needs a number of dollars from 0.05 to 500, like 1.50."; }
                return null;
            }

            public void Apply()
            {
                var cap = CapOf(_store);
                cap.On = _on.IsChecked == true;
                if (ParseDollars(_amount.Text) is double d) cap.Dollars = d;
                _store.World.SetKept(CapKey, cap);
                _store.World.Save();
            }
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Bunny's spending: " + what); }
            // A million input tokens and a million output tokens on Sonnet 5.5 cost 2 plus 10 dollars.
            Need(Math.Abs(Cost("claude-sonnet-5-5", 1_000_000, 1_000_000, 0, 0) - 12.0) < 1e-9, "the price table");
            Need(Math.Abs(Cost("some-new-model", 0, 1_000_000, 0, 0) - 10.0) < 1e-9, "an unknown model is priced as Sonnet");
            var now = new DateTime(2026, 11, 4, 10, 0, 0);
            Need(!CapReached(store, now), "the cap is off by default");
            Add(store, now, 0.40);
            Add(store, now, 0.40);
            Add(store, now.AddMonths(-1), 5);
            var (month, calls) = Month(store, now);
            Need(Math.Abs(month - 0.80) < 1e-9 && calls == 2, "the month gave " + month + " over " + calls);
            store.World.SetKept(CapKey, new Cap { On = true, Dollars = 1.00 });
            Need(!CapReached(store, now), "under the cap");
            Add(store, now, 0.25);
            Need(CapReached(store, now) && CapOf(store).ToldDay == store.Key(now), "over the cap, said once");
            Need(!CapReached(store, now.AddDays(1)), "the cap starts over the next day");
            // Undo never gives spending back: it is kept bookkeeping.
            store.Remember("test");
            Add(store, now, 1);
            store.Undo();
            Need(Today(store, now) > 2, "undo took spending back");
            Need(Panel.ParseDollars("1.5") == 1.5 && Panel.ParseDollars("$2") == 2 && Panel.ParseDollars("0") == null && Panel.ParseDollars("lots") == null, "reading dollars");
            Need(!CreditLine(store, now).Contains((char)35) && CreditLine(store, now).Contains("estimate"), "the credit line");
            Need(new CapHit(1).Message.Contains("$1.00"), "the cap line");
        }
    }
}
