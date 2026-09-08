using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Wubuntu")]
[assembly: AssemblyDescription("A small tray companion for WSL Ubuntu")]
[assembly: AssemblyVersion("1.0.1.0")]

namespace Wubuntu
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool first;
            // Acquire before opening session.log: duplicate launches cannot truncate it.
            using (Mutex mutex = new Mutex(true, @"Local\Wubuntu", out first))
            {
                if (!first) return;
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    SessionLog log = new SessionLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "session.log"));
                    using (log)
                    using (WslBackend backend = new WslBackend(log))
                    using (TrayContext context = new TrayContext(new Controller(backend, log), log))
                    {
                        log.Write("INFO", "Wubuntu 1.0.1 started");
                        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { context.ReportFailure(e.Exception); };
                        Application.Run(context);
                    }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    internal static class Assets
    {
        internal static Icon Logo()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Wubuntu.ico"))
            using (Icon source = new Icon(stream)) return (Icon)source.Clone();
        }
        internal static Bitmap Status(RunState state)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(state.ToString().ToLowerInvariant() + ".png"))
            using (Bitmap source = new Bitmap(stream)) return new Bitmap(source);
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Controller controller;
        private readonly SessionLog log;
        private readonly NotifyIcon tray;
        internal readonly ContextMenuStrip Menu;
        internal readonly ToolStripMenuItem StatusItem;
        internal readonly ToolStripMenuItem IdentityItem;
        internal readonly ToolStripMenuItem RestartItem;
        internal readonly ToolStripMenuItem ExitItem;
        private readonly Dictionary<RunState, Bitmap> images = new Dictionary<RunState, Bitmap>();
        private readonly System.Windows.Forms.Timer timer;
        private readonly Control dispatcher = new Control();
        private bool disposed;

        internal TrayContext(Controller controller, SessionLog log, bool autoStart = true)
        {
            this.controller = controller;
            this.log = log;
            IntPtr handle = dispatcher.Handle;
            foreach (RunState state in Enum.GetValues(typeof(RunState))) images.Add(state, Assets.Status(state));
            Menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false, ShowItemToolTips = true,
                Font = new Font("Segoe UI", 10f), Padding = new Padding(1) };
            float scale;
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero)) scale = graphics.DpiX / 96f;
            Menu.ImageScalingSize = new Size((int)(20 * scale), (int)(20 * scale));
            StatusItem = new MenuRow("Starting\u2026") { Font = new Font(Menu.Font, FontStyle.Bold), ToolTipText = "Open session.log" };
            IdentityItem = new MenuRow("Ubuntu") { Enabled = false };
            RestartItem = new MenuRow("Restart WSL");
            ExitItem = new MenuRow("Exit");
            foreach (ToolStripMenuItem item in new[] { StatusItem, IdentityItem, RestartItem, ExitItem })
            {
                item.AutoSize = false;
                item.Size = new Size((int)(190 * scale), (int)(32 * scale));
                item.TextAlign = ContentAlignment.MiddleLeft;
            }
            ToolStripSeparator separator = new ToolStripSeparator { AutoSize = false, Height = (int)(7 * scale), Width = (int)(190 * scale) };
            Menu.Items.AddRange(new ToolStripItem[] { StatusItem, IdentityItem, separator, RestartItem, ExitItem });
            Menu.AutoSize = true;
            StatusItem.Click += delegate { OpenLog(); };
            RestartItem.Click += async delegate { await controller.RestartAsync(); };
            ExitItem.Click += async delegate { await controller.ExitAsync(); };
            Menu.Opening += delegate { Theme(); RefreshState(); };
            Menu.Opened += delegate
            {
                foreach (ToolStripItem item in Menu.Items)
                    if (item is MenuRow) item.Width = Menu.ClientSize.Width - 2;
            };
            Theme();
            tray = new NotifyIcon { Icon = Assets.Logo(), Text = "Wubuntu", ContextMenuStrip = Menu, Visible = true };
            controller.Changed += RefreshState;
            timer = new System.Windows.Forms.Timer { Interval = 10000 };
            timer.Tick += async delegate { await controller.CheckAsync(); };
            RefreshState();
            timer.Start();
            if (autoStart) dispatcher.BeginInvoke(new Action(async delegate { await StartAsync(); }));
        }

        internal async System.Threading.Tasks.Task StartAsync(Action<string> showError = null)
        {
            if (await controller.StartAsync()) return;
            timer.Stop();
            Menu.Enabled = false;
            log.Write("INFO", "Showing startup error dialog");
            if (showError != null) showError(controller.LastError);
            else MessageBox.Show(controller.LastError, "Wubuntu", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitThread();
        }

        private void Theme()
        {
            bool dark = false;
            try
            {
                object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                dark = value is int && (int)value == 0;
            }
            catch (Exception) { }
            Menu.Renderer = new MenuRenderer(dark);
            Menu.BackColor = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250);
            Menu.ForeColor = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(30, 30, 30);
        }

        private void RefreshState()
        {
            if (disposed) return;
            string status = controller.State.ToString();
            if (controller.State == RunState.Starting || controller.State == RunState.Restarting || controller.State == RunState.Stopping) status += "\u2026";
            StatusItem.Text = status;
            IdentityItem.Text = controller.Distribution ?? "Ubuntu";
            IdentityItem.ToolTipText = IdentityItem.Text;
            StatusItem.Tag = images[controller.State];
            StatusItem.ToolTipText = controller.LastError == null ? "Open session.log" : controller.LastError + "\nClick to open session.log";
            RestartItem.Enabled = !controller.Busy;
            ExitItem.Enabled = !controller.Busy;
            // The tray artwork deliberately never changes with state.
            if (controller.ExitReady) ExitThread();
        }

        private void OpenLog()
        {
            try { Process.Start(new ProcessStartInfo(log.Path) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                log.Error(ex, "Could not open the log file");
                StatusItem.ToolTipText = "Open manually: " + log.Path;
            }
        }

        internal void ReportFailure(Exception ex) { controller.ReportFailure(ex); }

        protected override void ExitThreadCore()
        {
            log.Write("INFO", "Wubuntu closed");
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                controller.Changed -= RefreshState;
                timer.Stop(); timer.Dispose();
                tray.Visible = false;
                Icon icon = tray.Icon;
                tray.Dispose(); icon.Dispose();
                Menu.Dispose();
                foreach (Bitmap bitmap in images.Values) bitmap.Dispose();
                dispatcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Each row paints within the same rectangle; status artwork is inline, not a menu gutter.
    internal sealed class MenuRow : ToolStripMenuItem
    {
        internal MenuRow(string text) : base(text) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            bool dark = Owner != null && Owner.BackColor.GetBrightness() < 0.5f;
            Color background = Owner == null ? SystemColors.Menu : Owner.BackColor;
            if (Selected && Enabled) background = dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(231, 239, 248);
            using (SolidBrush brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, Size));
            Color color = Enabled ? (dark ? Color.WhiteSmoke : Color.FromArgb(30, 30, 30)) : (dark ? Color.FromArgb(158, 158, 158) : Color.FromArgb(105, 105, 105));
            float scale = e.Graphics.DpiX / 96f;
            int inset = (int)(12 * scale);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(inset, 0, Width - inset * 2, Height), color, flags);
            Image artwork = Tag as Image;
            if (artwork != null)
            {
                int textWidth = TextRenderer.MeasureText(e.Graphics, Text, Font, System.Drawing.Size.Empty, flags).Width;
                int size = (int)(20 * scale);
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(artwork, new Rectangle(inset + textWidth + (int)(8 * scale), (Height - size) / 2, size, size));
            }
        }
    }

    internal sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        internal MenuRenderer(bool dark) : base(new MenuColors(dark)) { RoundedEdges = false; }
    }

    internal sealed class MenuColors : ProfessionalColorTable
    {
        private readonly bool dark;
        internal MenuColors(bool dark) { this.dark = dark; UseSystemColors = false; }
        private Color Background { get { return dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250); } }
        private Color Highlight { get { return dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(231, 239, 248); } }
        public override Color ToolStripDropDownBackground { get { return Background; } }
        public override Color ImageMarginGradientBegin { get { return Background; } }
        public override Color ImageMarginGradientMiddle { get { return Background; } }
        public override Color ImageMarginGradientEnd { get { return Background; } }
        public override Color MenuItemSelected { get { return Highlight; } }
        public override Color MenuItemBorder { get { return Highlight; } }
        public override Color MenuBorder { get { return dark ? Color.FromArgb(68, 68, 68) : Color.FromArgb(210, 210, 210); } }
        public override Color SeparatorDark { get { return MenuBorder; } }
        public override Color SeparatorLight { get { return Background; } }
    }

}

