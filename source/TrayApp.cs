using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Wubuntu")]
[assembly: AssemblyDescription("A small tray companion for WSL Ubuntu")]

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
        internal readonly TrayMenu Menu;
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
            Menu = new TrayMenu();
            StatusItem = new MenuRow("Starting\u2026") { Font = new Font(Menu.Font, FontStyle.Bold), ToolTipText = "Open session.log" };
            IdentityItem = new MenuRow("Ubuntu") { Enabled = false };
            RestartItem = new MenuRow("Restart WSL");
            ExitItem = new MenuRow("Exit");
            Menu.Items.AddRange(new ToolStripItem[] { StatusItem, IdentityItem, new MenuSeparator(), RestartItem, new MenuSeparator(), ExitItem });
            StatusItem.Click += delegate { OpenLog(); };
            RestartItem.Click += async delegate { await RestartAsync(); };
            ExitItem.Click += async delegate { await controller.ExitAsync(); };
            Menu.Opening += delegate { Theme(); RefreshState(); };
            Menu.Closed += delegate { Menu.MeasureRows(); };
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
            ShowErrorAndClose("startup", showError);
        }

        internal async System.Threading.Tasks.Task RestartAsync(Action<string> showError = null)
        {
            if (controller.Busy || controller.ExitReady) return;
            if (await controller.RestartAsync()) return;
            ShowErrorAndClose("restart", showError);
        }

        private void ShowErrorAndClose(string operation, Action<string> showError)
        {
            timer.Stop();
            Menu.Enabled = false;
            log.Write("INFO", "Showing " + operation + " error dialog");
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
            Menu.ApplyTheme(dark);
        }

        private void RefreshState()
        {
            if (disposed) return;
            string status = controller.State == RunState.Running ? "Ready" : controller.State.ToString();
            if (controller.State == RunState.Starting || controller.State == RunState.Restarting || controller.State == RunState.Stopping) status += "\u2026";
            StatusItem.Text = status;
            IdentityItem.Text = controller.Distribution ?? "Ubuntu";
            if (!String.IsNullOrWhiteSpace(controller.UserName)) IdentityItem.Text += " \u00b7 " + controller.UserName;
            IdentityItem.ToolTipText = IdentityItem.Text;
            StatusItem.Tag = images[controller.State];
            StatusItem.ToolTipText = controller.LastError == null ? "Open session.log" : controller.LastError + "\nClick to open session.log";
            RestartItem.Enabled = !controller.Busy;
            ExitItem.Enabled = !controller.Busy;
            if (!Menu.Visible) Menu.MeasureRows();
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

    // Keep native menu input/dismissal, with one layout for rows and separators.
    internal sealed class TrayMenu : ContextMenuStrip
    {
        private float dpiScale;
        private Color border;
        private static readonly System.Windows.Forms.Layout.LayoutEngine rowLayout = new MenuLayout();

        public override System.Windows.Forms.Layout.LayoutEngine LayoutEngine { get { return rowLayout; } }
        protected override Padding DefaultPadding { get { return new Padding(1, Pixels(8), 1, Pixels(8)); } }

        internal TrayMenu()
        {
            ShowImageMargin = ShowCheckMargin = false;
            ShowItemToolTips = true;
            AutoSize = false;
            CanOverflow = false;
            Font = new Font("Segoe UI", 10f);
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = graphics.DpiX / 96f;
            Padding = DefaultPadding;
        }

        internal int Pixels(int value) { return (int)Math.Round(value * dpiScale); }

        // Prepare the size before Show positions the popup; defer resizing an open menu until it closes.
        internal void MeasureRows()
        {
            int width = Pixels(210);
            using (Graphics graphics = CreateGraphics())
            foreach (ToolStripItem item in Items)
            {
                if (item is MenuSeparator) continue;
                int textWidth = TextRenderer.MeasureText(graphics, item.Text, item.Font, Size.Empty, MenuRow.TextFlags).Width;
                width = Math.Max(width, textWidth + Pixels(item.Tag is Image ? 60 : 32));
            }
            width = Math.Min(width, Math.Min(Pixels(420), Screen.FromPoint(Cursor.Position).WorkingArea.Width - Padding.Horizontal));
            SuspendLayout();
            int height = Padding.Vertical;
            foreach (ToolStripItem item in Items)
            {
                item.AutoSize = false;
                item.Margin = Padding.Empty;
                item.Padding = Padding.Empty;
                item.Size = new Size(width, Pixels(item is MenuSeparator ? 13 : 34));
                height += item.Height;
            }
            Size = new Size(width + Padding.Horizontal, height);
            ResumeLayout(true);
        }

        internal void ApplyTheme(bool dark)
        {
            Renderer = new MenuRenderer(dark);
            BackColor = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250);
            ForeColor = dark ? Color.WhiteSmoke : Color.FromArgb(30, 30, 30);
            border = dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(215, 215, 215);
            if (IsHandleCreated) ApplyWindowFrame();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyWindowFrame();
        }

        private void ApplyWindowFrame()
        {
            // Windows 11 owns antialiased corners and the drop shadow. Older Windows
            // keeps the standard rectangular popup; no custom region breaks its shadow.
            if (Environment.OSVersion.Version.Build < 22000) return;
            int corners = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
            int color = ColorTranslator.ToWin32(border);
            DwmSetWindowAttribute(Handle, 34, ref color, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        // The default dropdown engine reserves asymmetric icon/text gutters even
        // with image margins hidden. Arrange our six items inside one shared inset.
        private sealed class MenuLayout : System.Windows.Forms.Layout.LayoutEngine
        {
            public override bool Layout(object container, LayoutEventArgs e)
            {
                TrayMenu menu = (TrayMenu)container;
                int top = menu.Padding.Top;
                foreach (ToolStripItem item in menu.Items)
                {
                    Rectangle bounds = new Rectangle(menu.Padding.Left, top,
                        Math.Max(0, menu.ClientSize.Width - menu.Padding.Horizontal), item.Height);
                    MenuRow row = item as MenuRow;
                    if (row != null) row.Arrange(bounds);
                    else ((MenuSeparator)item).Arrange(bounds);
                    top += item.Height;
                }
                return false;
            }
        }
    }

    internal sealed class MenuSeparator : ToolStripItem
    {
        internal MenuSeparator() { AccessibleRole = AccessibleRole.Separator; }
        public override bool CanSelect { get { return false; } }
        internal void Arrange(Rectangle bounds) { SetBounds(bounds); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color color = ((ToolStripProfessionalRenderer)Owner.Renderer).ColorTable.SeparatorDark;
            using (Pen pen = new Pen(color))
                e.Graphics.DrawLine(pen, 0, Height / 2, Width - 1, Height / 2);
        }
    }

    // Each row paints within the same rectangle; status artwork is inline, not a menu gutter.
    internal sealed class MenuRow : ToolStripMenuItem
    {
        internal const TextFormatFlags TextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        internal MenuRow(string text) : base(text) { }
        internal void Arrange(Rectangle bounds)
        {
            // ToolStripMenuItem subtracts the owner's left padding in SetBounds.
            bounds.X += Owner.Padding.Left;
            SetBounds(bounds);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            bool dark = Owner != null && Owner.BackColor.GetBrightness() < 0.5f;
            Color background = Owner == null ? SystemColors.Menu : Owner.BackColor;
            if (Selected && Enabled) background = dark ? Color.FromArgb(53, 53, 53) : Color.FromArgb(232, 232, 232);
            using (SolidBrush brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, Size));
            Color color = Enabled ? (dark ? Color.WhiteSmoke : Color.FromArgb(30, 30, 30)) : (dark ? Color.FromArgb(158, 158, 158) : Color.FromArgb(105, 105, 105));
            float scale = e.Graphics.DpiX / 96f;
            int inset = (int)Math.Round(16 * scale);
            Image artwork = Tag as Image;
            int artworkSpace = artwork == null ? 0 : (int)Math.Round(28 * scale);
            Rectangle textBounds = new Rectangle(inset, 0, Math.Max(0, Width - inset * 2 - artworkSpace), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, color, TextFlags | TextFormatFlags.EndEllipsis);
            if (artwork != null)
            {
                int textWidth = Math.Min(textBounds.Width, TextRenderer.MeasureText(e.Graphics, Text, Font, System.Drawing.Size.Empty, TextFlags).Width);
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
        private Color Highlight { get { return dark ? Color.FromArgb(53, 53, 53) : Color.FromArgb(232, 232, 232); } }
        public override Color ToolStripDropDownBackground { get { return Background; } }
        public override Color ImageMarginGradientBegin { get { return Background; } }
        public override Color ImageMarginGradientMiddle { get { return Background; } }
        public override Color ImageMarginGradientEnd { get { return Background; } }
        public override Color MenuItemSelected { get { return Highlight; } }
        public override Color MenuItemBorder { get { return Highlight; } }
        public override Color MenuBorder { get { return dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(215, 215, 215); } }
        public override Color SeparatorDark { get { return dark ? Color.FromArgb(77, 77, 77) : Color.FromArgb(215, 215, 215); } }
        public override Color SeparatorLight { get { return Background; } }
    }

}

