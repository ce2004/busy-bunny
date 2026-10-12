using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>The Busy Bunny logo, drawn here so it stays sharp at any size: a white bunny on a purple tile, with a green check.</summary>
    public static class Logo
    {
        public static DrawingImage Image() => _image ??= Make();
        private static DrawingImage? _image;

        private static DrawingImage Make()
        {
            var g = new DrawingGroup();
            var white = new SolidColorBrush(Color.FromRgb(0xFF, 0xFD, 0xF8));
            var pink = new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0xB5));
            var dark = new SolidColorBrush(Color.FromRgb(0x2D, 0x2A, 0x3E));
            var tile = new LinearGradientBrush(Color.FromRgb(0x7C, 0x5C, 0xF0), Color.FromRgb(0xB9, 0x8C, 0xF5), new Point(0, 0), new Point(1, 1));
            var outline = new Pen(new SolidColorBrush(Color.FromRgb(0xE4, 0xDC, 0xF5)), 3);

            void Shape(Brush? fill, Pen? pen, Geometry geo) => g.Children.Add(new GeometryDrawing(fill, pen, geo));
            Geometry Oval(double x, double y, double rx, double ry, double angle = 0)
            {
                var e = new EllipseGeometry(new Point(x, y), rx, ry);
                if (angle != 0) e.Transform = new RotateTransform(angle, x, y);
                return e;
            }

            Shape(tile, null, new RectangleGeometry(new Rect(0, 0, 256, 256), 56, 56));
            // Ears, then their pink insides.
            Shape(white, outline, Oval(97, 82, 23, 60, -13));
            Shape(white, outline, Oval(159, 82, 23, 60, 13));
            Shape(pink, null, Oval(98, 86, 11, 42, -13));
            Shape(pink, null, Oval(158, 86, 11, 42, 13));
            // Head, cheeks, eyes with a shine, nose and a small smile.
            Shape(white, outline, Oval(128, 162, 72, 62));
            Shape(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x9E, 0xB5)), null, Oval(88, 182, 13, 8));
            Shape(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x9E, 0xB5)), null, Oval(168, 182, 13, 8));
            Shape(dark, null, Oval(104, 152, 9, 10));
            Shape(dark, null, Oval(152, 152, 9, 10));
            Shape(white, null, Oval(107, 148, 3, 3));
            Shape(white, null, Oval(155, 148, 3, 3));
            Shape(pink, null, Oval(128, 174, 9, 6.5));
            Shape(null, new Pen(dark, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
                Geometry.Parse("M 115,186 Q 121.5,194 128,186 Q 134.5,194 141,186"));
            // The check: a task done.
            Shape(new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0x7A)), new Pen(Brushes.White, 5), Oval(205, 207, 30, 30));
            Shape(null, new Pen(Brushes.White, 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
                Geometry.Parse("M 191,207 L 201,218 L 220,196"));
            g.Freeze();
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }

        /// <summary>The logo as a PNG of the given size.</summary>
        public static byte[] Png(int size)
        {
            var v = new DrawingVisual();
            using (var dc = v.RenderOpen()) dc.DrawImage(Image(), new Rect(0, 0, size, size));
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(v);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        /// <summary>Writes the exe's icon file (native\BusyBunny.ico): PNG pictures at the sizes Windows asks for.</summary>
        public static void WriteIcon(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes) pngs.Add(Png(s));
            using var w = new BinaryWriter(File.Create(path));
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }

    /// <summary>
    /// The startup picture: the logo and the name, for a moment while Busy Bunny opens. It never
    /// takes focus, so NVDA says nothing about it and goes straight to the main window.
    /// </summary>
    public sealed class Splash : Window
    {
        public Splash()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Title = "Busy Bunny";

            var stack = new StackPanel { Margin = new Thickness(44, 36, 44, 30) };
            stack.Children.Add(new Image { Source = Logo.Image(), Width = 176, Height = 176, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock
            {
                Text = "Busy Bunny", FontSize = 34, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 18, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x2A, 0x7A)), HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = Taglines[new Random().Next(Taglines.Length)], FontSize = 16, Margin = new Thickness(0, 6, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x5E, 0x92)), HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Version " + Updater.Current, FontSize = 12, Margin = new Thickness(0, 14, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x90, 0xB5)), HorizontalAlignment = HorizontalAlignment.Center,
            });
            Content = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(28),
                Margin = new Thickness(18),
                Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFB, 0xF4), Color.FromRgb(0xF1, 0xEA, 0xFF), 90),
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.35 },
            };
            Opacity = 0;
            Loaded += (_, _) => BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        }

        private static readonly string[] Taglines = { "Hop to it.", "One carrot at a time.", "Small hops count.", "Let's see what today has." };

        /// <summary>Shows the splash, then the main window once it has been seen for a moment.</summary>
        public static void ShowThen(Window main, Action? afterShown = null)
        {
            var s = new Splash();
            s.Show();
            Sounds.Play(Sounds.Event.Startup);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                main.Show();
                main.Activate();
                s.Close();
                afterShown?.Invoke();
            };
            t.Start();
        }
    }
}
