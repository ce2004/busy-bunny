using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// The first-run setup guide (spec 19.14): once, after the startup picture, a short list of the
    /// things worth setting up. Enter on one opens Settings at that page; Done closes it. Also on the
    /// File menu, Setup guide, any time.
    /// </summary>
    // Named to sort before DayFlow: features attach in name order, so on the first start this comes before the morning check-in.
    public sealed class BeginnerGuide : IFeature
    {
        private static readonly (string Page, string Text)[] Steps =
        {
            ("Tags", "Tags: your classes and anything else, like PSY or STAT. They come first on every task."),
            ("Classes", "Classes: when each class meets, so Busy Bunny can say how long until the next one."),
            ("Weekday plans", "Weekday plans: what you usually do on each day, so each morning starts half planned."),
            ("Morning and evening", "Morning and evening: the morning check-in and what happens to unfinished tasks overnight."),
            ("Fun", "Fun: how hard Busy Bunny roasts you, and emoji at big moments."),
            ("Bunny and goals", "Bunny and goals: your bunny's name and your daily and weekly goals."),
            ("Reminders", "Reminders: staying in the notification area so reminders still come."),
            ("General", "General: sounds, short mode and the hours you have on a typical day."),
        };

        public void Attach(MainWindow w)
        {
            w.AddMenuItem("File", "Setup _guide...", () => Show(w));
            w.Started += () =>
            {
                if (w.AppSettings.SetupGuideShown) return;
                w.AppSettings.SetupGuideShown = true;
                w.AppSettings.Save();
                Show(w);
            };
        }

        private static void Show(MainWindow w)
        {
            var d = Layout.Dialog(w, "Set up Busy Bunny");
            var intro = new TextBox
            {
                IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Width = 480,
                Text = "Welcome to Busy Bunny. None of this is required, and all of it can wait. Choose a step and press Enter to set it up; you come back here after. Done when you are done. File, Setup guide brings this back.",
            };
            AutomationProperties.SetName(intro, "Welcome");
            var steps = new ListBox { Height = 260, Width = 480 };
            foreach (var (page, text) in Steps) steps.Items.Add(new ListBoxItem { Content = text, Tag = page });
            AutomationProperties.SetName(steps, "Setup steps");
            var open = Layout.Button("_Set up this one", isDefault: true);
            var done = Layout.Button("_Done", isCancel: true);
            void OpenStep()
            {
                if ((steps.SelectedItem as ListBoxItem)?.Tag is not string page) return;
                int at = steps.SelectedIndex;
                w.OpenSettingsAt(page == "General" ? null : page);
                TextViewer.Select(steps, at);
            }
            open.Click += (_, _) => OpenStep();
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(intro);
            panel.Children.Add(steps);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(open);
            buttons.Children.Add(done);
            panel.Children.Add(buttons);
            d.Content = panel;
            d.Loaded += (_, _) => TextViewer.Select(steps, 0);
            d.ShowDialog();
        }

        public void SelfTest(Store store)
        {
            if (Steps.Select(s => s.Page).Distinct().Count() != Steps.Length) throw new System.Exception("setup guide repeats a step");
        }
    }
}
