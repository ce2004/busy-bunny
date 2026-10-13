using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// Sounds for moving around: menus opening and closing, windows opening and closing, tabs, check
    /// boxes, pick menus, and Tab to the next field. Hooked once for every control of each kind, so
    /// every window gets them. Each can be changed or turned off in Options, Sounds. Also turns the
    /// background music on while Busy Bunny is in front and off the moment it is not.
    /// </summary>
    public static class UiSounds
    {
        private static readonly ConditionalWeakTable<object, object> Hooked = new();
        private static bool _active;
        private static Settings? _settings;

        public static void Hook(Application app, Settings settings)
        {
            _settings = settings;
            app.Activated += (_, _) => { _active = true; UpdateMusic(); };
            app.Deactivated += (_, _) => { _active = false; UpdateMusic(); };

            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((s, e) =>
            {
                if (e.OriginalSource != s || s is not MenuItem mi) return;
                Sounds.Play(mi.Role == MenuItemRole.TopLevelHeader ? Sounds.Event.MenuOpened : Sounds.Event.SubmenuOpened);
            }));
            // Moving from one top menu to the next closes one and opens the other: only leaving the menus is a close.
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuClosedEvent, new RoutedEventHandler((s, e) =>
            {
                if (e.OriginalSource != s || s is not MenuItem { Role: MenuItemRole.TopLevelHeader } mi || ItemsControl.ItemsControlFromItemContainer(mi) is not Menu bar) return;
                bar.Dispatcher.BeginInvoke(() =>
                {
                    foreach (var item in bar.Items) if (bar.ItemContainerGenerator.ContainerFromItem(item) is MenuItem { IsSubmenuOpen: true }) return;
                    Sounds.Play(Sounds.Event.MenuClosed);
                }, System.Windows.Threading.DispatcherPriority.Input);
            }));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, e) => { if (e.OriginalSource == s) Sounds.Play(Sounds.Event.MenuOpened); }));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((s, e) => { if (e.OriginalSource == s) Sounds.Play(Sounds.Event.MenuClosed); }));

            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, args) =>
            {
                if (s is not Window w || w is MainWindow || w is Splash || Hooked.TryGetValue(w, out _)) return;
                Hooked.Add(w, true);
                Sounds.Play(Sounds.Event.WindowOpened);
                w.Closed += (_, _) => Sounds.Play(Sounds.Event.WindowClosed);
            }));

            EventManager.RegisterClassHandler(typeof(TabControl), Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) =>
            {
                if (e.OriginalSource == s && s is TabControl { IsLoaded: true, IsKeyboardFocusWithin: true }) Sounds.Play(Sounds.Event.TabSwitched);
            }));

            EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.CheckedEvent, new RoutedEventHandler((s, _) => { if (ByHand(s)) Sounds.Play(Sounds.Event.CheckedOn); }));
            EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.UncheckedEvent, new RoutedEventHandler((s, _) => { if (ByHand(s)) Sounds.Play(Sounds.Event.CheckedOff); }));

            EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, args) =>
            {
                if (s is not ComboBox c || Hooked.TryGetValue(c, out _)) return;
                Hooked.Add(c, true);
                c.DropDownOpened += (_, _) => Sounds.Play(Sounds.Event.PickOpened);
                c.DropDownClosed += (_, _) => Sounds.Play(Sounds.Event.PickClosed);
            }));

            // Tab and Shift Tab to another field; not Control Tab (that is the tabs), not inside menus.
            EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
            {
                if (s != e.NewFocus || !Keyboard.IsKeyDown(Key.Tab) || (Keyboard.Modifiers & ModifierKeys.Control) != 0 || e.NewFocus is MenuItem) return;
                Sounds.Play(Sounds.Event.MovedField);
            }));
        }

        // A change she made, not one made while a window fills itself in.
        private static bool ByHand(object s) => s is UIElement u && (u.IsKeyboardFocused || u.IsMouseOver);

        /// <summary>Music plays when it is on, sounds are on, and Busy Bunny is the program in front.</summary>
        public static void UpdateMusic()
        {
            if (_settings == null) return;
            Mixer.Music.Level = Math.Clamp(_settings.MusicVolume, 1, 50) / 100f;
            Mixer.Music.Wanted = _active && _settings.MusicOn && _settings.SoundsOn;
        }
    }
}
