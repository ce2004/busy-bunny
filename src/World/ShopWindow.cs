using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// The market's windows (sweep 11.1, 11.2, 12.5): aisles she picks from a pick menu, each a list (name first, then the
    /// price and a few words); Enter buys, W adds to the wish list, Space describes by touch and sound. Her things in a room
    /// (move them to another room or storage), themes and the wish list. Points to spend are read first and after every purchase.
    /// </summary>
    public static class ShopWindow
    {
        private const string ForRoom = "Made for this room";

        private static Action Keep(Window owner) => owner is MainWindow mw ? mw.KeepFocus() : () => { };

        public static void Market(Window owner, MainWindow main, string? room = null)
        {
            var s = main.Store;
            var back = Keep(owner);
            var w = Layout.Dialog(owner, room == null ? "The market" : "Shopping for " + Shop.RoomName(room));
            var wallet = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 600, Margin = new Thickness(0, 0, 0, 6) };
            var aisle = new ComboBox();
            var list = new ListBox { Height = 300 };
            VirtualizingPanel.SetIsVirtualizing(list, false);
            var aisles = (room != null ? new[] { ForRoom } : Array.Empty<string>()).Concat(Shop.Aisles).ToList();
            foreach (var a in aisles) aisle.Items.Add(new ComboBoxItem { Content = Cap(a) });
            List<ShopItem> shown = new();

            List<ShopItem> Stock()
            {
                var sale = Shop.OnSale(s, DateTime.Now);
                string a = aisles[Math.Max(0, aisle.SelectedIndex)];
                return (a == ForRoom ? Shop.ForRoom(s, room!, DateTime.Now) : sale.Where(i => i.Aisle == a)).OrderBy(i => i.BasePrice).ThenBy(i => i.Name).ToList();
            }

            string LineFor(ShopItem i)
            {
                int owned = Shop.Count(s, i.Id);
                var bits = new List<string> { i.Name, Shop.Price(s, i) + " points" };
                if (i.Rare) bits.Add("a rare find");
                if (i.Seasonal) bits.Add("only for a few weeks");
                if (owned > 0) bits.Add(owned == 1 ? "you have one" : "you have " + owned);
                if (Shop.Wishes(s).Contains(i.Id)) bits.Add("on your wish list");
                if (i.Set.Length > 0) bits.Add("part of the " + i.Set);
                return string.Join(", ", bits) + ". " + PlaceWorld.First(i.Touch);
            }

            void Fill(int select)
            {
                wallet.Text = PlayTime.SpendLine(s.Data) + ". New stock arrives every Monday.";
                shown = Stock();
                list.Items.Clear();
                foreach (var i in shown)
                {
                    string line = LineFor(i);
                    var item = new ListBoxItem { Content = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, MaxWidth = 580 } };
                    AutomationProperties.SetName(item, line);
                    list.Items.Add(item);
                }
                if (shown.Count == 0) list.Items.Add(new ListBoxItem { Content = "Nothing in this aisle this week. New stock arrives Monday." });
                AutomationProperties.SetName(list, Cap(aisles[Math.Max(0, aisle.SelectedIndex)]) + ", " + PlaceUi.N(shown.Count, "thing"));
                if (select >= 0 && list.Items.Count > 0) list.SelectedIndex = Math.Clamp(select, 0, list.Items.Count - 1);
            }

            ShopItem? Chosen() => list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count ? shown[list.SelectedIndex] : null;

            void Buy()
            {
                if (Chosen() is not ShopItem i) { Speech.Say(w, "Pick something first.", true); return; }
                if (i.Instrument.Length > 0 && Shop.Owned(s, i.Id)) { Speech.Say(w, "You already have " + i.Name + ". Play it from the music room or Play an instrument.", true); return; }
                int at = list.SelectedIndex;
                if (Shop.Buy(s, i.Id, out var said)) { PlaceUi.Play(i.Plays.Length > 0 ? i.Plays : "Singing bells, sparkle"); PlaceEvents.RaiseDid(PlaceIds.Market, "shop"); Fill(at); TextViewer.Select(list, at); main.SaveAndRefresh(); }
                Speech.Say(w, said, true);
            }

            void Wish()
            {
                if (Chosen() is not ShopItem i) return;
                if (Shop.Wishes(s).Contains(i.Id)) { Speech.Say(w, Shop.HowClose(s, i.Id), true); return; }
                int at = list.SelectedIndex;
                Shop.AddWish(s, i.Id);
                Fill(at); TextViewer.Select(list, at);
                Speech.Say(w, "On your wish list. " + Shop.HowClose(s, i.Id), true);
            }

            void Describe()
            {
                if (Chosen() is not ShopItem i) return;
                PlaceUi.Play(i.Plays);
                Speech.Say(w, i.Name + ". " + i.Feel + " Goes in " + (i.Room is "any" ? "any room" : Shop.RoomName(i.Room)) + ".", true);
            }

            aisle.SelectionChanged += (_, _) => Fill(0);
            list.PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.None) return;
                if (e.Key == Key.Enter) { e.Handled = true; Buy(); }
                else if (e.Key == Key.W) { e.Handled = true; Wish(); }
                else if (e.Key == Key.Space) { e.Handled = true; Describe(); }
            };
            list.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    int at = Math.Max(0, list.SelectedIndex);
                    main.UndoLastChange();
                    Fill(at); TextViewer.Select(list, at);
                    Speech.Say(w, PlayTime.SpendLine(s.Data) + ".");
                }
            };

            var panel = new StackPanel { Margin = new Thickness(14), Width = 620 };
            panel.Children.Add(wallet);
            panel.Children.Add(Layout.Row("_Aisle", aisle, 300));
            panel.Children.Add(Layout.Row("_Things: Enter buys, W adds to your wish list, Space describes", list, 600));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var buy = Layout.Button("_Buy"); buy.Click += (_, _) => Buy();
            var wish = Layout.Button("Add to _wish list"); wish.Click += (_, _) => Wish();
            var more = Layout.Button("_Describe"); more.Click += (_, _) => Describe();
            var wishes = Layout.Button("Wish _list"); wishes.Click += (_, _) => { Wishes(w, main); Fill(list.SelectedIndex); };
            row.Children.Add(buy); row.Children.Add(wish); row.Children.Add(more); row.Children.Add(wishes);
            row.Children.Add(Layout.Button("Close", isCancel: true));
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) =>
            {
                aisle.SelectedIndex = 0;
                Fill(0);
                TextViewer.Select(list, 0);
                Speech.Say(w, wallet.Text);
            };
            w.ShowDialog();
            back();
        }

        private static string Cap(string a) => a.Length == 0 ? a : char.ToUpperInvariant(a[0]) + a[1..];

        public static void Wishes(Window owner, MainWindow main)
        {
            var s = main.Store;
            var back = Keep(owner);
            var w = Layout.Dialog(owner, "Your wish list");
            var list = new ListBox { Height = 240 };
            List<string> ids = new();
            void Fill(int at)
            {
                ids = Shop.Wishes(s).Where(id => Shop.Find(id) != null).ToList();
                list.Items.Clear();
                foreach (var id in ids) { string l = Shop.HowClose(s, id); var it = new ListBoxItem { Content = l }; AutomationProperties.SetName(it, l); list.Items.Add(it); }
                if (ids.Count == 0) list.Items.Add(new ListBoxItem { Content = "Nothing on your wish list. In the market, W adds the thing you are on." });
                TextViewer.Select(list, at);
            }
            void Buy()
            {
                int i = list.SelectedIndex;
                if (i < 0 || i >= ids.Count) return;
                string said;
                if (Shop.Buy(s, ids[i], out said)) { PlaceUi.Play(Shop.Find(ids[i])?.Plays); main.SaveAndRefresh(); }
                else said = Shop.HowClose(s, ids[i]);
                Fill(i);
                Speech.Say(w, said, true);
            }
            void Remove()
            {
                int i = list.SelectedIndex;
                if (i < 0 || i >= ids.Count) return;
                string name = Shop.Find(ids[i])!.Name;
                Shop.RemoveWish(s, ids[i]);
                Fill(i);
                Speech.Say(w, name + " is off your wish list. Control Z in the main window puts it back.", true);
            }
            list.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Buy(); }
                else if (e.Key == Key.Delete) { e.Handled = true; Remove(); }
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = PlayTime.SpendLine(s.Data) + ".", Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(Layout.Row("_Wishes: Enter buys when you have enough, Delete takes one off", list, 560));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var buy = Layout.Button("_Buy"); buy.Click += (_, _) => Buy();
            var rem = Layout.Button("_Remove"); rem.Click += (_, _) => Remove();
            row.Children.Add(buy); row.Children.Add(rem); row.Children.Add(Layout.Button("Close", isCancel: true));
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) => { Fill(0); Speech.Say(w, PlayTime.SpendLine(s.Data) + "."); };
            w.ShowDialog();
            back();
        }

        /// <summary>The places a thing can go: built rooms, the yard, the hot tub deck, the boat, storage.</summary>
        public static List<string> Homes(Store s)
        {
            var homes = PlaceIds.Rooms.Where(r => PlaceWorld.HasRoom(s, r)).ToList();
            homes.Add(Shop.Outside);
            if (PlaceWorld.IsOpen(s, PlaceIds.HotTub)) homes.Add("hottub");
            if (PlaceWorld.IsOpen(s, PlaceIds.Pond)) homes.Add("boat");
            homes.Add(Shop.Storage);
            return homes;
        }

        /// <summary>Her things in one room (or storage): Enter describes, M moves one. Nothing is ever lost.</summary>
        public static void Things(Window owner, Store s, string room)
        {
            var w = Layout.Dialog(owner, "Your things in " + Shop.RoomName(room));
            var list = new ListBox { Height = 260 };
            List<int> at = new();
            void Fill(int sel)
            {
                var all = Shop.OwnedItems(s);
                at = Enumerable.Range(0, all.Count).Where(i => all[i].Room == room).ToList();
                list.Items.Clear();
                foreach (int i in at)
                {
                    var item = Shop.Find(all[i].ItemId);
                    string l = (item?.Name ?? all[i].ItemId) + ". " + PlaceWorld.First(item?.Touch ?? "");
                    var it = new ListBoxItem { Content = l }; AutomationProperties.SetName(it, l); list.Items.Add(it);
                }
                if (at.Count == 0) list.Items.Add(new ListBoxItem { Content = "Nothing here yet. The market sells things for every room." });
                TextViewer.Select(list, sel);
            }
            void Move()
            {
                int k = list.SelectedIndex;
                if (k < 0 || k >= at.Count) return;
                var homes = Homes(s).Where(h => h != room).ToList();
                if (PlaceUi.Pick(w, "Move it", "Where it goes", homes.Select(Shop.RoomName).Select(Cap).ToList()) is not int h) return;
                string name = Shop.Find(Shop.OwnedItems(s)[at[k]].ItemId)?.Name ?? "It";
                Shop.Move(s, at[k], homes[h]);
                Fill(k);
                Speech.Say(w, name + " is in " + Shop.RoomName(homes[h]) + " now.", true);
            }
            void Describe()
            {
                int k = list.SelectedIndex;
                if (k < 0 || k >= at.Count || Shop.Find(Shop.OwnedItems(s)[at[k]].ItemId) is not ShopItem i) return;
                PlaceUi.Play(i.Plays);
                Speech.Say(w, i.Name + ". " + i.Feel, true);
            }
            list.PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.None) return;
                if (e.Key == Key.Enter) { e.Handled = true; Describe(); }
                else if (e.Key == Key.M) { e.Handled = true; Move(); }
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row("_Things: Enter describes, M moves to another room or storage", list, 560));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var mv = Layout.Button("_Move"); mv.Click += (_, _) => Move();
            var ds = Layout.Button("_Describe"); ds.Click += (_, _) => Describe();
            row.Children.Add(ds); row.Children.Add(mv); row.Children.Add(Layout.Button("Close", isCancel: true));
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) => Fill(0);
            w.ShowDialog();
        }

        /// <summary>Puts a theme on a room, takes it off, or buys a new one (bought once, then free for every room).</summary>
        public static void PickTheme(Window owner, Store s, string room)
        {
            var owned = Shop.Bag(s).ThemesOwned;
            var current = Shop.ThemeOf(s, room);
            var options = new List<(string Line, Action Do)>();
            foreach (var t in Shop.Themes.Where(t => owned.Contains(t.Id)))
            {
                var th = t;
                options.Add(((current?.Id == t.Id ? "Keep " : "Put on ") + t.Name + ". " + PlaceWorld.First(t.Touch), () =>
                {
                    Shop.SetTheme(s, room, th.Id);
                    PlaceUi.Play(th.Plays);
                    PlaceUi.Say(owner, "The " + th.Name.ToLowerInvariant() + " theme is on. " + th.Feel);
                }));
            }
            if (current != null) options.Add(("Take the theme off", () => { Shop.SetTheme(s, room, null); PlaceUi.Say(owner, "The theme is off. It is still yours for any room."); }));
            foreach (var t in Shop.Themes.Where(t => !owned.Contains(t.Id)))
            {
                var th = t;
                options.Add(("Buy " + t.Name + ", " + Shop.Price(s, t.Price) + " points. " + PlaceWorld.First(t.Touch), () =>
                {
                    if (!Shop.BuyTheme(s, th.Id, out var said, room)) { PlaceUi.Say(owner, said); return; }
                    PlaceUi.Play(th.Plays);
                    PlaceUi.Say(owner, said + " It is on this room now. " + th.Feel);
                }));
            }
            if (PlaceUi.Pick(owner, "Room theme", PlayTime.SpendLine(s.Data) + ". Themes", options.Select(o => o.Line).ToList()) is int i) options[i].Do();
        }
    }
}
