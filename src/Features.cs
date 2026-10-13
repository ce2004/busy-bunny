using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BusyBunny
{
    /// <summary>
    /// A feature that plugs into the main window: tags, sprints, the game and so on, each in its own
    /// file. Any class implementing this with a constructor taking no arguments is found and attached
    /// when the main window is made, in name order. Attach adds its menu items, keys and tabs and
    /// subscribes to the window's events (StatusChanged, TaskAdded, Refreshed, Started, MinuteTick, NewDay).
    /// </summary>
    public interface IFeature
    {
        void Attach(MainWindow w);

        /// <summary>Pages for Options, Settings, shown after General. Made fresh each time Settings opens.</summary>
        IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) => Array.Empty<ISettingsPage>();

        /// <summary>Checks for the self-test, on a throwaway store holding the sample tasks. Throw to fail.</summary>
        void SelfTest(Store store) { }
    }

    /// <summary>A section of Options, Settings. Nothing changes until OK, which calls Validate on every page, then Apply.</summary>
    public interface ISettingsPage
    {
        /// <summary>The name in the Section pick menu, like Tags.</summary>
        string Name { get; }
        FrameworkElement Content { get; }
        /// <summary>A problem to say aloud (Settings then shows this page), or null when it is fine.</summary>
        string? Validate() => null;
        /// <summary>Called after store.Remember("changing settings"), so changes to the store can be undone.</summary>
        void Apply();
    }

    public static class Features
    {
        private static List<IFeature>? _all;

        public static IReadOnlyList<IFeature> All => _all ??= typeof(IFeature).Assembly.GetTypes()
            .Where(t => typeof(IFeature).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.Name)
            .Select(t => (IFeature)Activator.CreateInstance(t)!)
            .ToList();

        public static T? Get<T>() where T : class, IFeature => All.OfType<T>().FirstOrDefault();
    }
}
