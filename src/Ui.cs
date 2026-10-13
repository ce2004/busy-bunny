using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace BusyBunny
{
    public sealed class Settings
    {
        public bool SoundsOn { get; set; } = true;
        public int SoundVolume { get; set; } = 80;
        public int SoundKey { get; set; } = 10;
        public Dictionary<string, string> SoundChoices { get; set; } = new();
        /// <summary>The sound pack last picked in the Sounds window (Classic, Bunny pack and so on).</summary>
        public string SoundPack { get; set; } = "Classic";
        /// <summary>The seasonal pack hint already spoken, like Halloween 2026.</summary>
        public string PackHinted { get; set; } = "";
        public bool ShortMode { get; set; }
        public bool LowEnergy { get; set; }
        public bool ShuffleMode { get; set; }
        public bool CheckForUpdatesAtStart { get; set; } = true;
        public int UrgentWarnAbove { get; set; } = 5;
        public bool SetupGuideShown { get; set; }
        public bool MorningCheckIn { get; set; } = true;
        public string EndOfDay { get; set; } = "week";
        public string LastResetDay { get; set; } = "";
        public string LastRhythmDay { get; set; } = "";
        public bool KeepInTray { get; set; } = true;
        public bool TrayHintShown { get; set; }
        public bool HotkeyQuickAdd { get; set; } = true;
        public bool HotkeyRead { get; set; } = true;
        public int FollowUpDays { get; set; } = 3;
        /// <summary>The last day each nudge was given, so each comes once a day.</summary>
        public Dictionary<string, string> NudgeDates { get; set; } = new();
        /// <summary>The roast dial (spec 12.2): 0 off, 1 gentle, 2 teasing, 3 spicy, 4 savage.</summary>
        public int RoastLevel { get; set; } = 2;
        /// <summary>Emoji at big moments only (spec 12.3).</summary>
        public bool EmojisOn { get; set; } = true;
        public bool FinalsMode { get; set; }
        /// <summary>The season the finals offer was last made for, like 2026-12, so it is made once.</summary>
        public string FinalsOffered { get; set; } = "";
        /// <summary>Phrases and facts already used, by group, so they do not repeat until most have been.</summary>
        public Dictionary<string, List<int>> PhraseUsed { get; set; } = new();
        /// <summary>Surprise unlocks: Set:Drama, Emoji:Animals and so on.</summary>
        public List<string> Unlocked { get; set; } = new();

        public static string FilePath => Path.Combine(Store.Folder, "settings.json");

        public static Settings Load()
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
            catch { }
            return new();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Store.Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        /// <summary>Puts these settings into the sound player.</summary>
        public void Apply()
        {
            Sounds.Enabled = SoundsOn;
            Sounds.Volume = Math.Clamp(SoundVolume, 10, 100);
            Sounds.Key = Math.Clamp(SoundKey, 0, 11);
            Sounds.Choice = e => SoundChoices.TryGetValue(e.ToString(), out var c) ? c : Sounds.DefaultName;
        }
    }

    /// <summary>
    /// Says something through the screen reader without moving focus (spec 3.3), using a UI
    /// Automation notification, which NVDA reads politely after what it is already saying.
    /// </summary>
    public static class Speech
    {
        public static void Say(UIElement from, string text, bool interrupt = false)
        {
            try
            {
                var peer = UIElementAutomationPeer.FromElement(from) ?? UIElementAutomationPeer.CreatePeerForElement(from);
                peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
                    interrupt ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.All,
                    text, "BusyBunny");
            }
            catch { }
        }
    }

    public static class Layout
    {
        /// <summary>A label and its control: the label's access key moves to the control, and the control is named after it.</summary>
        public static FrameworkElement Row(string label, Control c, double width = 320)
        {
            c.Width = width;
            AutomationProperties.SetName(c, label.Replace("_", ""));
            var l = new Label { Content = label, Target = c, Padding = new Thickness(0, 6, 0, 2) };
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            p.Children.Add(l);
            p.Children.Add(c);
            return p;
        }

        public static Button Button(string text, bool isDefault = false, bool isCancel = false) =>
            new() { Content = text, IsDefault = isDefault, IsCancel = isCancel, MinWidth = 90, Margin = new Thickness(0, 8, 8, 0), Padding = new Thickness(10, 3, 10, 3) };

        public static Window Dialog(Window owner, string title)
        {
            var w = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                FontSize = 15,
            };
            // A window that has closed (Busy Bunny closing for an update) cannot own one: then it stands alone.
            if (PresentationSource.FromVisual(owner) != null)
            {
                w.Owner = owner;
                w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            return w;
        }
    }
}
