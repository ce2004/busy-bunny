using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace BusyBunny
{
    /// <summary>
    /// Keys that work in any program (spec 15.1, 15.2): Control Alt Shift A opens Quick add, Control
    /// Alt Shift R speaks the summary. Control Up and Control Down are never registered. A key some
    /// other program already holds is reported once, and the rest carries on.
    /// </summary>
    public sealed class Hotkeys : IFeature
    {
        private const int WmHotkey = 0x0312;
        private const uint Control = 0x2, Alt = 0x1, Shift = 0x4, NoRepeat = 0x4000;
        private const int IdAdd = 0x4201, IdRead = 0x4202;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private MainWindow? _w;
        private IntPtr _hwnd;
        private readonly HashSet<int> _held = new();
        private readonly List<string> _failed = new();
        private bool _told;

        public void Attach(MainWindow w)
        {
            _w = w;
            w.SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(w).Handle;
                HwndSource.FromHwnd(_hwnd)?.AddHook(Hook);
                Register();
            };
            w.Started += TellOnce;
            w.Closed += (_, _) => Unregister();
        }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings)
        {
            var add = new CheckBox { Content = "Control Alt Shift A opens Quick add from any program", IsChecked = settings.HotkeyQuickAdd, Margin = new Thickness(0, 4, 0, 4) };
            var read = new CheckBox { Content = "Control Alt Shift R reads my day from any program", IsChecked = settings.HotkeyRead, Margin = new Thickness(0, 4, 0, 4) };
            var panel = new StackPanel();
            panel.Children.Add(add);
            panel.Children.Add(read);
            yield return new Reminders.Page("Keys", panel, () =>
            {
                settings.HotkeyQuickAdd = add.IsChecked == true;
                settings.HotkeyRead = read.IsChecked == true;
                Register();
                TellOnce();
            });
        }

        private void Register()
        {
            if (_w == null || _hwnd == IntPtr.Zero) return;
            Unregister();
            _failed.Clear();
            _told = false;
            Try(IdAdd, _w.AppSettings.HotkeyQuickAdd, (uint)'A', "Control Alt Shift A");
            Try(IdRead, _w.AppSettings.HotkeyRead, (uint)'R', "Control Alt Shift R");
        }

        private void Try(int id, bool wanted, uint vk, string name)
        {
            if (!wanted) return;
            if (RegisterHotKey(_hwnd, id, Control | Alt | Shift | NoRepeat, vk)) _held.Add(id);
            else _failed.Add(name);
        }

        private void Unregister()
        {
            foreach (int id in _held) UnregisterHotKey(_hwnd, id);
            _held.Clear();
        }

        private void TellOnce()
        {
            if (_told || _failed.Count == 0 || _w == null) return;
            _told = true;
            _w.Say(string.Join(" and ", _failed) + (_failed.Count == 1 ? " is" : " are") + " already taken by another program, so " + (_failed.Count == 1 ? "it" : "they") + " will not work outside Busy Bunny. Everything else is fine.");
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmHotkey) return IntPtr.Zero;
            int id = wParam.ToInt32();
            if (id != IdAdd && id != IdRead) return IntPtr.Zero;
            handled = true;
            _w?.Dispatcher.BeginInvoke(() => { if (id == IdAdd) QuickAdd(); else Read(); });
            return IntPtr.Zero;
        }

        private void QuickAdd()
        {
            if (_w == null) return;
            bool wasHidden = !_w.IsVisible;
            Reminders.Open();
            _w.OpenQuickAdd();
            // Back to the tray if that is where it was, so adding a task does not leave a window behind.
            if (wasHidden && _w.AppSettings.KeepInTray) _w.Hide();
        }

        private void Read()
        {
            if (_w == null) return;
            string text = _w.Store.Summary(DateTime.Now, _w.AppSettings.ShortMode);
            // A hidden or background window's speech is not reliably heard, so say it as a notification too.
            if (_w.IsActive) _w.Say(text, true);
            else
            {
                Reminders.Notify("Busy Bunny", text);
                if (_w.IsVisible) _w.Say(text, true);
            }
        }
    }
}
