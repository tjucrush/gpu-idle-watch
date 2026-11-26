using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace GpuWatch
{
    internal sealed class TrayController : IDisposable
    {
        private readonly Icon icon;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem alerts;
        private readonly ToolStripMenuItem corners;
        private readonly NotifyIcon tray;
        private bool disposed;

        public TrayController(Action open, Action toggleAlerts, IEnumerable<KeyValuePair<NoticeCorner, string>> options,
            Action<NoticeCorner> selectCorner, Action exit)
        {
            using (Stream contents = typeof(TrayController).Assembly.GetManifestResourceStream("GpuWatch.ico"))
            {
                if (contents == null) throw new InvalidDataException("缺少显卡监控图标资源");
                using (var source = new Icon(contents, SystemInformation.SmallIconSize))
                    icon = (Icon)source.Clone();
            }

            menu = new ContextMenuStrip();
            try
            {
                int gap = Math.Max(3, menu.Font.Height / 4);
                menu.Padding = new Padding(2, gap, 2, gap);
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = true;
                menu.MinimumSize = new Size(TextRenderer.MeasureText("打开显卡监控", menu.Font).Width + menu.Font.Height * 4, 0);
                var openItem = new ToolStripMenuItem("打开显卡监控", null, (sender, args) => open());
                alerts = new ToolStripMenuItem("空闲提醒", null, (sender, args) => toggleAlerts());
                corners = new ToolStripMenuItem("提醒位置");
                var submenu = (ToolStripDropDownMenu)corners.DropDown;
                submenu.ShowImageMargin = false;
                submenu.ShowCheckMargin = true;
                foreach (var option in options)
                {
                    var item = new ToolStripMenuItem(option.Value) { Tag = option.Key };
                    item.Click += (sender, args) => selectCorner((NoticeCorner)item.Tag);
                    submenu.Items.Add(item);
                }
                var exitItem = new ToolStripMenuItem("退出", null, (sender, args) => exit());
                foreach (var item in new[] { openItem, alerts, corners, exitItem })
                    item.Padding = new Padding(0, gap, gap * 2, gap);
                menu.Items.AddRange(new ToolStripItem[] { openItem, alerts, corners, new ToolStripSeparator(), exitItem });
                if (!SystemInformation.HighContrast)
                {
                    menu.ForeColor = Color.FromArgb(235, 243, 250);
                    menu.Renderer = new TrayRenderer();
                    submenu.ForeColor = menu.ForeColor;
                    submenu.Renderer = menu.Renderer;
                }

                tray = new NotifyIcon { Icon = icon, Text = "GPU 空闲卡监控", ContextMenuStrip = menu };
                tray.MouseClick += (sender, args) => { if (args.Button == MouseButtons.Left) open(); };
                tray.Visible = true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void SetAlertsEnabled(bool enabled)
        {
            if (!disposed) alerts.Checked = enabled;
        }

        public void SetCorner(NoticeCorner corner)
        {
            if (disposed) return;
            foreach (ToolStripMenuItem item in corners.DropDownItems)
                item.Checked = (NoticeCorner)item.Tag == corner;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            menu.Dispose();
            icon.Dispose();
        }

        private sealed class TrayRenderer : ToolStripProfessionalRenderer
        {
            public TrayRenderer() : base(new MenuColors()) { RoundedEdges = false; }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                // Draw in the native check column so the glyph scales with the menu font.
                var box = e.ImageRectangle;
                float side = Math.Min(box.Width, box.Height);
                float x = box.Left + (box.Width - side) / 2f;
                float y = box.Top + (box.Height - side) / 2f;
                var state = e.Graphics.Save();
                try
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var pen = new Pen(Color.FromArgb(99, 227, 184), Math.Max(1.5f, side / 7f)))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        e.Graphics.DrawLines(pen, new[] {
                            new PointF(x + side * .15f, y + side * .50f),
                            new PointF(x + side * .40f, y + side * .74f),
                            new PointF(x + side * .85f, y + side * .24f)
                        });
                    }
                }
                finally { e.Graphics.Restore(state); }
            }
        }

        private sealed class MenuColors : ProfessionalColorTable
        {
            private static readonly Color Panel = Color.FromArgb(24, 33, 47);
            private static readonly Color Line = Color.FromArgb(55, 70, 86);
            private static readonly Color Hover = Color.FromArgb(38, 60, 63);
            public override Color ToolStripDropDownBackground { get { return Panel; } }
            public override Color ImageMarginGradientBegin { get { return Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Panel; } }
            public override Color ImageMarginGradientEnd { get { return Panel; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Panel; } }
        }
    }
}
