using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GpuWatch
{
    internal sealed class IdleNotice
    {
        private readonly Window main;
        private readonly Action openDashboard;
        private readonly DispatcherTimer timeout;
        private Window popup;

        public IdleNotice(Window mainWindow, Action openDashboard)
        {
            main = mainWindow;
            this.openDashboard = openDashboard;
            timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (sender, args) => Dismiss();
            main.LocationChanged += (sender, args) => Position();
            main.Closed += (sender, args) => Dismiss();
        }

        public bool IsVisible { get { return popup != null && popup.IsVisible; } }

        public void Show(IList<CardView> cards)
        {
            Present(cards, true);
        }

        public void Update(IList<CardView> cards)
        {
            if (IsVisible) Present(cards, false);
        }

        private void Present(IList<CardView> cards, bool restartTimeout)
        {
            var idle = cards.Where(card => card.State == CardState.Idle)
                .OrderByDescending(card => card.RemainingMiB).ThenBy(card => card.Host)
                .ThenBy(card => card.GpuIndex).ToList();
            if (idle.Count == 0) { Dismiss(); return; }

            bool opening = popup == null;
            if (opening) popup = CreateWindow();
            ((TextBlock)popup.FindName("Headline")).Text = idle.Count + " 张显卡空闲";
            ((ItemsControl)popup.FindName("NoticeCards")).ItemsSource = idle.Take(3).ToList();
            ((TextBlock)popup.FindName("OverflowLabel")).Text = idle.Count > 3 ? "另有 " + (idle.Count - 3) + " 张空闲卡" : "";

            if (opening)
            {
                popup.Show();
                if (SystemParameters.ClientAreaAnimation)
                    popup.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                        { FillBehavior = FillBehavior.Stop });
            }
            popup.UpdateLayout();
            Position();
            if (restartTimeout)
            {
                timeout.Stop();
                timeout.Start();
            }
        }

        public void Dismiss()
        {
            timeout.Stop();
            if (popup != null) popup.Close();
        }

        private Window CreateWindow()
        {
            Window window;
            using (var stream = typeof(IdleNotice).Assembly.GetManifestResourceStream("IdleNotice.xaml"))
                window = (Window)XamlReader.Load(stream);
            ((Image)window.FindName("NoticeMark")).Source = AppIcon.Image;
            ((Button)window.FindName("DismissButton")).Click += (sender, args) => Dismiss();
            ((Button)window.FindName("OpenButton")).Click += (sender, args) =>
            {
                Dismiss();
                openDashboard();
            };
            window.Closed += (sender, args) => { timeout.Stop(); popup = null; };
            window.SourceInitialized += (sender, args) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                const int extendedStyle = -20, noActivate = 0x08000000, toolWindow = 0x80;
                SetWindowLong(hwnd, extendedStyle, GetWindowLong(hwnd, extendedStyle) | noActivate | toolWindow);
                Position();
            };
            window.ContentRendered += (sender, args) => Position();
            window.SizeChanged += (sender, args) => Position();
            return window;
        }

        // Native work-area and window rectangles share screen coordinates on mixed-DPI desktops.
        // MonitorFromWindow uses the last restored bounds when the dashboard is minimized.
        private void Position()
        {
            if (popup == null) return;
            var hwnd = new WindowInteropHelper(popup).Handle;
            if (hwnd == IntPtr.Zero) return;
            var source = HwndSource.FromHwnd(hwnd);
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            var monitor = MonitorFromWindow(new WindowInteropHelper(main).Handle, 2);
            NativeRect bounds;
            if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out bounds)) return;
            double scale = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
            int inset = (int)Math.Round(12 * scale);
            int x = Math.Max(info.Work.Left, info.Work.Right - (bounds.Right - bounds.Left) - inset);
            int y = Math.Max(info.Work.Top, info.Work.Bottom - (bounds.Bottom - bounds.Top) - inset);
            if (bounds.Left != x || bounds.Top != y)
            {
                const uint noSize = 0x0001, keepZOrder = 0x0004, noActivation = 0x0010;
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, noSize | keepZOrder | noActivation);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    }
}
