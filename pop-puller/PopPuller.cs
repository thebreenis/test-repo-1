using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gfw8Sharing;

namespace Gfw8PopPuller {
 static class Program {
  [STAThread]
  static int Main(string[] args) {
   try {
    AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
    AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
    ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
    if (args.Length > 0 && args[0] == "--apply-update") return PopPullerUpdater.Apply(args);
    if (args.Length == 2 && args[0] == "--verify-bundle") {
     string runtime = AppFiles.Unpack();
     File.WriteAllText(Path.GetFullPath(args[1]), Wire.Json.Serialize(new { verified = true, runtime, version = AppFiles.Version }));
     return 0;
    }
    if (args.Length == 1 && args[0] == "--self-test") return new ReceiptBackend(AppFiles.Unpack()).Games().Length == 108 && AppFiles.TestPopWriting() && PopRefreshPolicy.SelfTest() && PopPullerUpdater.SelfTest() ? 0 : 2;
    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
    bool acquired;
    using (var mutex = new Mutex(true, "Local\\GFW8PopPuller-" + WindowsIdentity.GetCurrent().User.Value, out acquired)) {
     if (!acquired) { MetroDialog.ShowMessage(null, "GFW8 Pop Puller", "GFW8 Pop Puller is already running in the system tray or on the desktop."); return 1; }
     string updateAck = null;
     int ackIndex = Array.IndexOf(args, "--update-ack");
     if (ackIndex >= 0 && ackIndex + 1 < args.Length) updateAck = args[ackIndex + 1];
     try { Application.Run(new MainForm(AppFiles.Unpack(), args.Contains("--minimized"), updateAck)); }
     finally { mutex.ReleaseMutex(); }
    }
    return 0;
   } catch (Exception e) {
    MetroDialog.ShowMessage(null, "GFW8 Pop Puller", "GFW8 Pop Puller could not start: " + e.Message);
    return 1;
   }
  }
 }

 sealed class MetroSetting : CheckBox {
  readonly Color accent;
  public MetroSetting(string label, bool state, Color color) {
   SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
   Text = label; Checked = state; accent = color; AccessibleName = label;
   AccessibleRole = AccessibleRole.CheckButton; BackColor = Color.FromArgb(48, 32, 57);
   ForeColor = Color.White; Font = new Font("Segoe UI", 10); Dock = DockStyle.Fill;
   Margin = new Padding(0, 0, 4, 0);
   CheckedChanged += delegate { Invalidate(); };
  }
  protected override void OnPaint(PaintEventArgs e) {
   e.Graphics.Clear(BackColor);
   using (var stripe = new SolidBrush(accent)) e.Graphics.FillRectangle(stripe, 0, 0, 4, Height);
   int textWidth = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
   int left = Math.Min(Width - 62, 18 + textWidth + 15);
   TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(18, 0, Math.Max(0, left - 30), Height),
    ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
   int top = (Height - 27) / 2;
   var track = new Rectangle(left, top + 2, 46, 23);
   using (var fill = new SolidBrush(Checked ? Color.FromArgb(0, 158, 171) : Color.FromArgb(86, 72, 94)))
   using (var pen = new Pen(Checked ? Color.FromArgb(110, 232, 239) : Color.FromArgb(174, 152, 184), 2))
   using (var knob = new SolidBrush(Color.White)) {
    e.Graphics.FillRectangle(fill, track); e.Graphics.DrawRectangle(pen, track);
    e.Graphics.FillRectangle(knob, left + (Checked ? 26 : 3), top + 6, 15, 15);
   }
  }
 }

 enum ChromeMark { Minimize, Maximize, Close }

 sealed class ChromeButton : Button {
  readonly ChromeMark mark;
  readonly Color normal;
  bool hovered;
  public ChromeButton(ChromeMark glyph, string name, Color background, Action action) {
   mark = glyph; normal = background; AccessibleName = name; TabStop = false;
   Dock = DockStyle.Fill; Margin = new Padding(0); Padding = new Padding(0);
   FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
   SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
   Click += delegate { action(); };
  }
  protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
  protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
  protected override void OnPaint(PaintEventArgs e) {
   Color background = hovered ? (mark == ChromeMark.Close ? Color.FromArgb(222, 54, 61) : Color.FromArgb(75, 53, 83)) : normal;
   using (var fill = new SolidBrush(background)) e.Graphics.FillRectangle(fill, ClientRectangle);
   e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
   float scale = DeviceDpi / 96f, x = (Width - 1) / 2f, y = (Height - 1) / 2f, half = 5.5f * scale;
   using (var pen = new Pen(Color.White, 1.8f * scale)) {
    if (mark == ChromeMark.Minimize) e.Graphics.DrawLine(pen, x - half, y + 2 * scale, x + half, y + 2 * scale);
    else if (mark == ChromeMark.Maximize) e.Graphics.DrawRectangle(pen, x - half, y - half, 2 * half, 2 * half);
    else {
     e.Graphics.DrawLine(pen, x - half, y - half, x + half, y + half);
     e.Graphics.DrawLine(pen, x + half, y - half, x - half, y + half);
    }
   }
  }
 }

 static class PopChrome {
  [DllImport("user32.dll")] static extern bool ReleaseCapture();
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
  public static Panel Create(Form form, string text, Action maximize) {
   var chrome = new Panel { Dock = DockStyle.Fill, BackColor = form.BackColor, Margin = new Padding(0) };
   var title = new Label { Text = text, Font = new Font("Segoe UI Light", 22), ForeColor = Color.White,
    Location = new Point(20, 4), Size = new Size(450, 52), TextAlign = ContentAlignment.MiddleLeft };
   chrome.Controls.Add(title);
   var controlsHost = new Panel { Dock = DockStyle.Right, Width = 148, Padding = new Padding(0, 12, 16, 12),
    BackColor = form.BackColor };
   var controls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
   for (int n = 0; n < 3; n++) controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
   controls.Controls.Add(new ChromeButton(ChromeMark.Minimize, "Minimize", form.BackColor, () => form.WindowState = FormWindowState.Minimized), 0, 0);
   controls.Controls.Add(new ChromeButton(ChromeMark.Maximize, "Maximize or restore", form.BackColor, maximize), 1, 0);
   controls.Controls.Add(new ChromeButton(ChromeMark.Close, "Close", Color.FromArgb(185, 35, 41), () => form.Close()), 2, 0);
   controlsHost.Controls.Add(controls); chrome.Controls.Add(controlsHost);
   MouseEventHandler drag = delegate(object sender, MouseEventArgs e) {
    if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(form.Handle, 0xA1, new IntPtr(2), IntPtr.Zero); }
   };
   chrome.MouseDown += drag; title.MouseDown += drag; title.DoubleClick += delegate { maximize(); };
   return chrome;
  }
 public static Panel CreateDialog(Form form, string text) {
  var chrome = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(48, 32, 57) };
  var title = new Label { Text = text, Dock = DockStyle.Fill, Padding = new Padding(18, 0, 0, 0),
   Font = new Font("Segoe UI Light", 19), ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
  var close = new ChromeButton(ChromeMark.Close, "Close", Color.FromArgb(185, 35, 41), () => form.Close()) { Dock = DockStyle.Right, Width = 52 };
  chrome.Controls.Add(title); chrome.Controls.Add(close);
  MouseEventHandler drag = delegate(object sender, MouseEventArgs e) {
   if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(form.Handle, 0xA1, new IntPtr(2), IntPtr.Zero); }
  };
  chrome.MouseDown += drag; title.MouseDown += drag;
  return chrome;
 }
 }

 sealed class MetroDialog : Form {
  public readonly Panel Body;
  public MetroDialog(string title, Size bodySize) {
   Text = title; BackColor = Color.FromArgb(33, 8, 40); ForeColor = Color.White;
   Font = new Font("Segoe UI", 10); FormBorderStyle = FormBorderStyle.None;
   StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
   ClientSize = new Size(bodySize.Width, bodySize.Height + 52);
   var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
   layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
   layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
   layout.Controls.Add(PopChrome.CreateDialog(this, title), 0, 0);
   Body = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Margin = new Padding(0) };
   layout.Controls.Add(Body, 0, 1); Controls.Add(layout);
  }
  public static void ShowMessage(Form owner, string title, string message) {
   using (var form = new MetroDialog(title, new Size(540, 155))) {
    var label = new Label { Text = message, Location = new Point(20, 18), Size = new Size(500, 82), AutoEllipsis = true };
    var close = new Button { Text = "Close", Location = new Point(400, 108), Width = 120, Height = 34,
     BackColor = Color.FromArgb(0, 158, 171), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
    form.Body.Controls.AddRange(new Control[] { label, close }); form.AcceptButton = close;
    form.ShowDialog(owner);
   }
  }
  public static bool Confirm(Form owner, string title, string message, string action) {
   using (var form = new MetroDialog(title, new Size(540, 170))) {
    var label = new Label { Text = message, Location = new Point(20, 18), Size = new Size(500, 88) };
    var cancel = new Button { Text = "Cancel", Location = new Point(270, 118), Width = 112, Height = 34,
     BackColor = Color.FromArgb(48, 32, 57), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.No };
    var confirm = new Button { Text = action, Location = new Point(390, 118), Width = 130, Height = 34,
     BackColor = Color.FromArgb(185, 35, 41), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Yes };
    form.Body.Controls.AddRange(new Control[] { label, cancel, confirm }); form.AcceptButton = cancel; form.CancelButton = cancel;
    return form.ShowDialog(owner) == DialogResult.Yes;
   }
  }
  public static bool ConfirmRefresh(Form owner, string message) {
   using (var form = new MetroDialog("Refresh pop.bin", new Size(560, 170))) {
    var label = new Label { Text = message, Location = new Point(20, 18), Size = new Size(520, 88) };
    var cancel = new Button { Text = "Keep current", Location = new Point(285, 118), Width = 120, Height = 34,
     BackColor = Color.FromArgb(48, 32, 57), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.No };
    var confirm = new Button { Text = "Get fresh", Location = new Point(413, 118), Width = 125, Height = 34,
     BackColor = Color.FromArgb(0, 158, 171), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Yes };
    form.Body.Controls.AddRange(new Control[] { label, cancel, confirm }); form.AcceptButton = cancel; form.CancelButton = cancel;
    return form.ShowDialog(owner) == DialogResult.Yes;
   }
  }
 }

 sealed class GameItem {
  public int Id;
  public string Name;
  public string Family;
  public Button Button;
  public int ProviderCount;
 }

 enum PopRefreshDecision { Refresh, Confirm, Block }
 static class PopRefreshPolicy {
  internal static PopRefreshDecision Decide(TimeSpan remaining) {
   if (remaining > TimeSpan.FromHours(20)) return PopRefreshDecision.Block;
   if (remaining >= TimeSpan.FromHours(12)) return PopRefreshDecision.Confirm;
   return PopRefreshDecision.Refresh;
  }
  internal static bool SelfTest() {
   return Decide(TimeSpan.FromHours(21)) == PopRefreshDecision.Block &&
    Decide(TimeSpan.FromHours(20)) == PopRefreshDecision.Confirm &&
    Decide(TimeSpan.FromHours(12)) == PopRefreshDecision.Confirm &&
    Decide(TimeSpan.FromHours(12).Subtract(TimeSpan.FromSeconds(1))) == PopRefreshDecision.Refresh;
  }
 }

 sealed class MainForm : Form {
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string text);
  static void SetSearchCue(IntPtr handle, string text) { SendMessage(handle, 0x1501, IntPtr.Zero, text); }
  readonly Preferences preferences;
  readonly ReceiptBackend backend;
  readonly HubClient hub;
  readonly List<GameItem> games = new List<GameItem>();
  readonly HashSet<int> available = new HashSet<int>();
  readonly Dictionary<int,int> providerCounts = new Dictionary<int,int>();
  readonly Dictionary<int,string> lastSeen = LastSeenCache.Load();
  readonly WatchlistState watchlist = WatchlistStore.Load();
  readonly Queue<int> watchQueue = new Queue<int>();
  readonly HashSet<int> watchAttempted = new HashSet<int>();
  readonly RowStyle statusRowStyle = new RowStyle(SizeType.Absolute, 110);
  const int ScrollbarRightInset = 16;
  readonly Label hubStatus = new Label(), availability = new Label(), refresh = new Label();
  readonly Label hubPeak = new Label(), availabilityPeak = new Label();
  readonly Label hubDot = new Label();
  readonly Label hubUsers = new Label();
  readonly Button connectButton = new Button();
  readonly Panel hubStatusPanel = new Panel();
  readonly Label selection = new Label();
  readonly TextBox search = new TextBox();
  readonly FlowLayoutPanel gameList = new FlowLayoutPanel();
  readonly RichTextBox activity = new RichTextBox();
  readonly Button getPop, openPopFolder, watchButton, scanButton, allButton, availableButton, unownedButton, onlyMineButton, codeButton;
  readonly NotifyIcon tray;
  readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
  readonly ToolTip tips = new ToolTip();
  readonly Button updateButton = new Button();
  int selectedGame;
  int activeUsers;
  bool onlyAvailable, onlyUnowned, onlyMine, exiting, scanning, requesting, autoRequesting;
  bool checkingUpdate, applyingUpdate;
  bool everConnected;
  PopPullerRelease availableUpdate;
  string announcedUpdate = "";
  DateTime nextUpdateCheckUtc = DateTime.MaxValue;
  DateTime connectionAttemptSince = DateTime.MinValue;

  static readonly Color Background = Color.FromArgb(33, 8, 40);
  static readonly Color Panel = Color.FromArgb(48, 32, 57);
  static readonly Color Teal = Color.FromArgb(0, 158, 171);
  static readonly Color Violet = Color.FromArgb(135, 9, 213);
  static readonly Color Muted = Color.FromArgb(57, 42, 66);
  static readonly Color TokenIssueRed = Color.FromArgb(156, 39, 50);
  static readonly HashSet<int> KnownTokenIssueGames = new HashSet<int> { 71, 72 };
  const string TokenIssueTooltip = "Known Xbox token issue!\n" +
   "This game can serve a valid pop.bin, but Xbox Live\n" +
   "does not issue a valid token.\n\n" +
   "Xbox sign-in and achievements will not work.\n" +
   "You can still request a pop.bin in case Microsoft\n" +
   "fixes this in the future.";

  public MainForm(string runtime, bool minimized, string updateAck = null) {
   preferences = Preferences.Load(); backend = new ReceiptBackend(runtime); hub = new HubClient(preferences, backend);
   tips.AutoPopDelay = 20000;
   bool trayStartup = minimized && preferences.CloseToTray;
   Text = "GFW8 Pop Puller"; BackColor = Background; ForeColor = Color.White;
   Font = new Font("Segoe UI", 10); FormBorderStyle = FormBorderStyle.None;
   if (trayStartup) { Opacity = 0; ShowInTaskbar = false; }
   StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(1280, 720); MinimumSize = new Size(1024, 576);
   using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("PopPuller.Icon"))
    Icon = icon == null ? Icon.ExtractAssociatedIcon(Application.ExecutablePath) : new Icon(icon);

   var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = new Padding(0) };
   root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
   root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
   root.RowStyles.Add(statusRowStyle);
   root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
   root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
   root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
   root.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
   Controls.Add(root);

   var chrome = PopChrome.Create(this, "GFW8 Pop Puller", ToggleMaximize); root.Controls.Add(chrome, 0, 0);
   var status = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
   status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
   status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
   status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
   status.Controls.Add(HubStatusTile(), 0, 0);
   var availabilityTile = StatusTile("AVAILABILITY", availability, Color.FromArgb(105, 34, 147), "0 of 108 games");
   AddPeakLabel(availabilityTile, availabilityPeak);
   var refreshTile = StatusTile("REFRESH", refresh, Color.FromArgb(59, 22, 77), "--:--");
   status.Controls.Add(availabilityTile, 1, 0);
   status.Controls.Add(refreshTile, 2, 0);
   codeButton = Action("Access Code", Panel); codeButton.Click += delegate {
    if ((Control.ModifierKeys & Keys.Shift) != Keys.None) ShowOwnerPassword();
    else SetCode();
   };
   codeButton.Dock = DockStyle.None; codeButton.Location = new Point(0, 84); codeButton.Size = new Size(142, 24);
   hubStatusPanel.Controls.Add(codeButton);
   LayoutHubStatus();
   root.Controls.Add(status, 0, 1);

   var switches = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Background, Margin = new Padding(0) };
   for (int i = 0; i < 2; i++) switches.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
   switches.Controls.Add(Setting("Start at user login", preferences.StartAtLogin, Violet, value => { preferences.StartAtLogin = value; SaveSettings(); }), 0, 0);
   switches.Controls.Add(Setting("Close to system tray", preferences.CloseToTray, Teal, value => { preferences.CloseToTray = value; SaveSettings(); }), 1, 0);
   root.Controls.Add(switches, 0, 2);

   var catalogue = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16, 5, ScrollbarRightInset, 0), Margin = new Padding(0) };
   catalogue.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
   catalogue.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
   catalogue.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
   catalogue.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
   root.Controls.Add(catalogue, 0, 3);
   var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Margin = new Padding(0) };
   tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
   tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
   tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
   tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
   tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
   tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
   tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
   var title = new Label { Text = "Games", Dock = DockStyle.Fill, Font = new Font("Segoe UI Light", 22), TextAlign = ContentAlignment.MiddleLeft };
   tools.Controls.Add(title, 0, 0);
   search.Dock = DockStyle.Fill; search.BackColor = Panel; search.ForeColor = Color.White; search.BorderStyle = BorderStyle.FixedSingle;
   search.Margin = new Padding(5, 10, 8, 8); search.TextChanged += delegate { RenderGames(); };
   search.HandleCreated += delegate { SetSearchCue(search.Handle, "Search for a game..."); };
   tips.SetToolTip(search, "Search all 108 games"); tools.Controls.Add(search, 1, 0);
   scanButton = Action("Scan My Licenses", Violet); scanButton.Click += async delegate { await Scan(); }; tools.Controls.Add(scanButton, 2, 0);
   allButton = Action("All", Teal); allButton.Click += delegate { onlyAvailable = false; onlyUnowned = false; onlyMine = false; UpdateFilters(); RenderGames(); }; tools.Controls.Add(allButton, 3, 0);
   availableButton = Action("Available", Panel); availableButton.Click += delegate { onlyAvailable = !onlyAvailable; if (!onlyAvailable) onlyUnowned = false; onlyMine = false; UpdateFilters(); RenderGames(); }; tools.Controls.Add(availableButton, 4, 0);
   unownedButton = Action("Unowned", Panel); unownedButton.Click += delegate { onlyUnowned = !onlyUnowned; if (onlyUnowned) onlyAvailable = true; onlyMine = false; UpdateFilters(); RenderGames(); }; tools.Controls.Add(unownedButton, 5, 0);
   onlyMineButton = Action("Only Mine", Panel); onlyMineButton.Click += delegate { onlyMine = !onlyMine; if (onlyMine) { onlyAvailable = false; onlyUnowned = false; } UpdateFilters(); RenderGames(); }; tools.Controls.Add(onlyMineButton, 6, 0);
   catalogue.Controls.Add(tools, 0, 0);

   gameList.Dock = DockStyle.Fill; gameList.AutoScroll = true; gameList.WrapContents = true; gameList.FlowDirection = FlowDirection.LeftToRight;
   gameList.BackColor = Background; gameList.Margin = new Padding(0); gameList.Padding = new Padding(0, 4, 0, 0);
   gameList.Resize += delegate { SizeGameButtons(); }; catalogue.Controls.Add(gameList, 0, 1);
   var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
   bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
   bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
   bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
   bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
   selection.Dock = DockStyle.Fill; selection.Text = "Select an available game to request a fresh receipt."; selection.TextAlign = ContentAlignment.MiddleLeft;
   selection.Font = new Font("Segoe UI", 9); bottom.Controls.Add(selection, 0, 0);
   watchButton = Action("Watch", Panel); watchButton.Enabled = false; watchButton.Click += delegate { ToggleWatch(); }; bottom.Controls.Add(watchButton, 1, 0);
   getPop = Action("Get pop.bin", Teal); getPop.Enabled = false; getPop.Click += async delegate { await RequestSelected(); }; bottom.Controls.Add(getPop, 2, 0);
   openPopFolder = Action("Open pop folder", Panel); openPopFolder.Click += delegate { OpenPopFolder(); }; bottom.Controls.Add(openPopFolder, 3, 0);
   catalogue.Controls.Add(bottom, 0, 2);

   var logPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.FromArgb(27, 19, 33), Padding = new Padding(0, 0, ScrollbarRightInset, 0), Margin = new Padding(0) };
   logPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); logPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
   logPanel.Controls.Add(new Label { Text = "ACTIVITY LOG", Dock = DockStyle.Fill, BackColor = Violet, ForeColor = Color.White, Padding = new Padding(13, 12, 0, 0), Font = new Font("Segoe UI", 10, FontStyle.Bold) }, 0, 0);
   activity.Dock = DockStyle.Fill; activity.ReadOnly = true; activity.BackColor = Color.FromArgb(27, 19, 33);
   activity.ForeColor = Color.FromArgb(226, 216, 229); activity.BorderStyle = BorderStyle.None;
   activity.Font = new Font("Consolas", 10); activity.DetectUrls = false; activity.ScrollBars = RichTextBoxScrollBars.ForcedVertical;
   activity.Margin = new Padding(12, 7, 0, 5);
   logPanel.Controls.Add(activity, 1, 0); root.Controls.Add(logPanel, 0, 4);

   var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Padding = new Padding(16, 0, 16, 0), Margin = new Padding(0) };
   footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
   footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
   footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
   footer.Controls.Add(new Label { Text = "Alec Breen | TheBreenis", AutoSize = true, ForeColor = Color.FromArgb(201, 184, 209), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
   var github = new LinkLabel { Text = "github.com/thebreenis ↗", LinkColor = Color.FromArgb(126, 227, 238), ActiveLinkColor = Color.White, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(12, 0, 0, 0) };
   github.LinkClicked += delegate { Process.Start("https://github.com/thebreenis"); }; footer.Controls.Add(github, 1, 0);
   var help = new LinkLabel { Text = "Help", LinkColor = Color.FromArgb(126, 227, 238), ActiveLinkColor = Color.White, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(18, 0, 0, 0) };
   help.LinkClicked += delegate { ShowHelp(); }; footer.Controls.Add(help, 2, 0);
   updateButton.Text = "Update available"; updateButton.Width = 132; updateButton.Height = 26;
   updateButton.Margin = new Padding(4, 3, 12, 3); updateButton.BackColor = Violet; updateButton.ForeColor = Color.White;
   updateButton.FlatStyle = FlatStyle.Flat; updateButton.FlatAppearance.BorderSize = 0; updateButton.Visible = false;
   updateButton.Click += delegate { ShowUpdateDialog(false); }; footer.Controls.Add(updateButton, 3, 0);
   footer.Controls.Add(new Label { Text = "v" + AppFiles.Version, AutoSize = true, ForeColor = Color.FromArgb(177, 156, 186), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 4, 0);
   root.Controls.Add(footer, 0, 5);

   foreach (var raw in backend.Games()) {
    var row = Wire.Map(raw);
    var game = new GameItem { Id = Convert.ToInt32(row["index"]), Name = Wire.Text(row, "name"), Family = Wire.Text(row, "package_family_name") };
    game.Button = GameButton(game); games.Add(game);
   }
   RenderGames();

   var trayMenu = new ContextMenuStrip();
   trayMenu.Items.Add("Open", null, delegate { OpenWindow(); });
   trayMenu.Items.Add("Close", null, delegate { exiting = true; Close(); });
   tray = new NotifyIcon { Icon = Icon, Text = "GFW8 Pop Puller", ContextMenuStrip = trayMenu, Visible = true };
   tray.DoubleClick += delegate { OpenWindow(); };
   tray.BalloonTipClicked += delegate { if (availableUpdate != null && tray.BalloonTipTitle == "Update available") { OpenWindow(); ShowUpdateDialog(true); } };
   hub.Activity += Log;
   hub.ConnectionChanged += value => Ui(() => {
    if (value) { everConnected = true; connectionAttemptSince = DateTime.MinValue; }
    else if (connectionAttemptSince == DateTime.MinValue) connectionAttemptSince = DateTime.UtcNow;
    UpdateHubStatus();
   });
   hub.AvailabilityChanged += ids => Ui(() => {
    var previous = new HashSet<int>(available);
    available.Clear(); foreach (int id in ids) available.Add(id);
    watchAttempted.RemoveWhere(id => !available.Contains(id));
    foreach (int id in watchlist.Games) if (available.Contains(id) && !previous.Contains(id)) QueueWatched(id);
    availability.Text = available.Count + " of 108 games"; RenderGames();
   });
   hub.ProviderCountsChanged += counts => Ui(() => {
    providerCounts.Clear(); foreach (var pair in counts) providerCounts[pair.Key] = pair.Value;
    RenderGames();
   });
   hub.LastSeenChanged += dates => Ui(() => {
    if (dates.Count == 0) return;
    bool changed = false;
    foreach (var pair in dates) if (!lastSeen.ContainsKey(pair.Key) || String.CompareOrdinal(pair.Value, lastSeen[pair.Key]) > 0) { lastSeen[pair.Key] = pair.Value; changed = true; }
    if (changed) { LastSeenCache.Save(lastSeen); RenderGames(); }
   });
   hub.UsersActiveChanged += count => Ui(() => { activeUsers = count; UpdateHubStatus(); });
   hub.InitialScanRequested += () => Ui(() => { var work = Scan(true); });
   hub.PeaksChanged += (gamesHigh, gamesDay, usersHigh, usersDay) => Ui(() => {
    availabilityPeak.Text = PeakText(gamesHigh, gamesDay, "game", "games");
    hubPeak.Text = PeakText(usersHigh, usersDay, "user", "users");
   });
   hub.OwnCountChanged += count => Ui(RenderGames);
   timer.Interval = 1000; timer.Tick += delegate {
    UpdateRefresh(); UpdateHubStatus(); CheckWatchExpiries();
    if (DateTime.UtcNow >= nextUpdateCheckUtc) { nextUpdateCheckUtc = DateTime.UtcNow.AddHours(6); var work = CheckForUpdates(); }
   }; timer.Start();
   Shown += delegate {
    if (trayStartup) { Hide(); Opacity = 1; ShowInTaskbar = true; }
    SaveSettings(); connectionAttemptSince = DateTime.UtcNow; UpdateHubStatus(); hub.Start();
    Log("GFW8 Pop Puller started. Hub access is limited to individual codes.");
    string updateError = PopPullerUpdater.TakeError();
    if (updateError.Length > 0) {
     Log("Update needs attention: " + updateError);
     tray.ShowBalloonTip(8000, "Update failed", "The previous version was kept. Open Pop Puller for details.", ToolTipIcon.Warning);
    }
    if (updateAck != null) try { File.WriteAllText(updateAck, AppFiles.Version); } catch (Exception e) { Log("Could not confirm update startup: " + e.Message); }
    nextUpdateCheckUtc = DateTime.UtcNow.AddSeconds(10);
    if (!trayStartup && preferences.Code.Length == 0) BeginInvoke((Action)SetCode);
   };
   FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!exiting && preferences.CloseToTray && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
   FormClosed += delegate { timer.Stop(); tray.Visible = false; tray.Dispose(); hub.Stop(); hub.Dispose(); };
  }

  Panel StatusTile(string heading, Label value, Color color, string initial) {
   var panel = new Panel { Dock = DockStyle.Fill, BackColor = color, Margin = new Padding(0) };
   panel.Controls.Add(new Label { Text = heading, Location = new Point(16, 8), AutoSize = true, ForeColor = Color.FromArgb(230, 226, 235), Font = new Font("Segoe UI", 8, FontStyle.Bold) });
   value.Text = initial; value.Location = new Point(16, 22); value.Size = new Size(500, 40); value.Font = new Font("Segoe UI Light", 18); value.BackColor = color;
   value.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; panel.Controls.Add(value);
   panel.Resize += delegate { value.Width = panel.ClientSize.Width - 24; };
   return panel;
  }
  static string PeakText(int count, string day, string singular, string plural) {
   DateTime parsed;
   if (!DateTime.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
    System.Globalization.DateTimeStyles.None, out parsed)) return "";
   return "All-time high: " + count + " " + (count == 1 ? singular : plural) + " on " + parsed.ToString("dd/MM/yyyy");
  }
  static void AddPeakLabel(Panel tile, Label label) {
   label.Location = new Point(16, 63); label.Size = new Size(Math.Max(0, tile.ClientSize.Width - 32), 20);
   label.Font = new Font("Segoe UI", 9); label.ForeColor = Color.FromArgb(234, 225, 239);
   label.BackColor = tile.BackColor; label.AutoEllipsis = false;
   tile.Controls.Add(label);
   tile.Resize += delegate { label.Width = Math.Max(0, tile.ClientSize.Width - 32); };
  }
  Panel HubStatusTile() {
   var color = Color.FromArgb(0, 127, 146);
   hubStatusPanel.Dock = DockStyle.Fill; hubStatusPanel.BackColor = color; hubStatusPanel.Margin = new Padding(0);
   hubStatusPanel.Controls.Add(new Label { Text = "HUB STATUS", Location = new Point(16, 8), AutoSize = true,
    ForeColor = Color.FromArgb(230, 226, 235), Font = new Font("Segoe UI", 8, FontStyle.Bold) });
   hubUsers.Text = "Users Active: --"; hubUsers.Size = new Size(200, 20); hubUsers.Location = new Point(300, 8);
   hubUsers.TextAlign = ContentAlignment.MiddleRight; hubUsers.Font = new Font("Segoe UI", 9);
   hubUsers.ForeColor = Color.FromArgb(234, 245, 247); hubUsers.BackColor = color; hubUsers.Visible = false;
   hubStatusPanel.Controls.Add(hubUsers);
   AddPeakLabel(hubStatusPanel, hubPeak);
   hubDot.Text = "●"; hubDot.Location = new Point(16, 25); hubDot.Size = new Size(28, 34);
   hubDot.Font = new Font("Segoe UI Symbol", 20); hubDot.BackColor = color;
   hubStatusPanel.Controls.Add(hubDot);
   hubStatus.Text = "Connecting to hub"; hubStatus.Location = new Point(48, 24);
   hubStatus.Font = new Font("Segoe UI Light", 18); hubStatus.BackColor = color;
   hubStatus.Size = new Size(330, 35); hubStatusPanel.Controls.Add(hubStatus);
   connectButton.Text = "Connect"; connectButton.Size = new Size(92, 34); connectButton.Visible = false;
   connectButton.BackColor = Color.FromArgb(38, 29, 65); connectButton.ForeColor = Color.White;
   connectButton.FlatStyle = FlatStyle.Flat; connectButton.FlatAppearance.BorderColor = Color.FromArgb(193, 237, 243);
   connectButton.Click += delegate { BeginConnectionAttempt(); hub.ConnectNow(); };
   hubStatusPanel.Controls.Add(connectButton);
   hubStatusPanel.Resize += delegate { LayoutHubStatus(); };
   return hubStatusPanel;
  }
  void LayoutHubStatus() {
   int actionLeft = Math.Max(260, hubStatusPanel.ClientSize.Width - 158);
   hubUsers.Location = new Point(Math.Max(120, hubStatusPanel.ClientSize.Width - hubUsers.Width - 14), 8);
   hubPeak.TextAlign = ContentAlignment.MiddleRight;
   hubPeak.Width = Math.Min(306, Math.Max(0, hubStatusPanel.ClientSize.Width - 30));
   int peakLeft = hubStatusPanel.ClientSize.Width - hubPeak.Width - 14;
   int statusTextRight = hubStatus.Left + TextRenderer.MeasureText(hubStatus.Text, hubStatus.Font).Width;
   hubPeak.Location = new Point(peakLeft, peakLeft > statusTextRight + 12 ? 29 : 63);
   connectButton.Location = new Point(Math.Max(150, actionLeft - connectButton.Width - 8), 27);
   hubStatus.Width = Math.Max(100, (connectButton.Visible ? connectButton.Left : actionLeft) - hubStatus.Left - 8);
   if (codeButton != null) codeButton.Left = actionLeft;
  }
  MetroSetting Setting(string label, bool state, Color accent, Action<bool> changed) {
   var setting = new MetroSetting(label, state, accent);
   setting.CheckedChanged += delegate { changed(setting.Checked); };
   return setting;
  }
  Button Action(string label, Color color) {
   var button = new Button { Text = label, Dock = DockStyle.Fill, Height = 34, Margin = new Padding(3, 7, 3, 7), BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9) };
   button.FlatAppearance.BorderColor = Color.FromArgb(153, 126, 163); button.FlatAppearance.BorderSize = 1;
   return button;
  }
  Button GameButton(GameItem game) {
   var button = new Button { Text = "●  " + game.Name, Height = 49, TextAlign = ContentAlignment.MiddleLeft, BackColor = Muted, ForeColor = Color.FromArgb(216, 203, 221), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9), AutoEllipsis = false, Margin = new Padding(0, 0, 5, 5), Padding = new Padding(6, 0, 3, 0) };
   button.FlatAppearance.BorderSize = 0;
   button.Paint += delegate(object sender, PaintEventArgs e) {
    if (game.ProviderCount < 1) return;
    string label = "From " + game.ProviderCount + (game.ProviderCount == 1 ? " User" : " Users");
    var box = new Rectangle(Math.Max(0, button.ClientSize.Width - 124), 27, 112, 17);
    using (var font = new Font("Segoe UI", 8)) TextRenderer.DrawText(e.Graphics, label, font, box, Color.White,
     TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
   };
   button.Click += delegate { selectedGame = game.Id; selection.Text = game.Name + " · " + GameStatus(game.Id); RenderGames(); };
   return button;
  }
  void SizeGameButtons() {
   int width = Math.Max(140, (gameList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 26) / 4);
   foreach (var game in games) game.Button.Width = width;
  }
  string GameStatus(int id) {
   if (hub.IsOwned(id)) return "Your license";
   if (available.Contains(id)) return "Available now";
   string day;
   if (!lastSeen.TryGetValue(id, out day)) return "Last Seen: Never";
   DateTime date;
   return DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
    ? "Last Seen: " + date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "Last Seen: Never";
  }
  void RenderGames() {
   if (gameList.IsDisposed) return;
   string query = search.Text.Trim();
   foreach (Control control in gameList.Controls) tips.Hide(control);
   gameList.SuspendLayout(); gameList.Controls.Clear();
   foreach (var game in games) {
    bool lit = available.Contains(game.Id);
    bool owned = hub.IsOwned(game.Id);
    if ((onlyAvailable && !lit) || (onlyUnowned && owned) || (onlyMine && !owned) || game.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
    var button = game.Button;
    int count; game.ProviderCount = lit && providerCounts.TryGetValue(game.Id, out count) ? count : 0;
    bool tokenIssue = KnownTokenIssueGames.Contains(game.Id);
    button.BackColor = tokenIssue ? TokenIssueRed : owned ? Color.FromArgb(24, 91, 175) : lit ? Color.FromArgb(7, 93, 103) : lastSeen.ContainsKey(game.Id) ? Color.FromArgb(130, 69, 26) : Muted;
    button.ForeColor = tokenIssue || lit || owned ? Color.White : Color.FromArgb(210, 198, 215);
    button.Text = (watchlist.Games.Contains(game.Id) ? "★  " : tokenIssue ? "⚠  " : "●  ") + game.Name + Environment.NewLine + "    " + GameStatus(game.Id);
    tips.SetToolTip(button, tokenIssue ? TokenIssueTooltip : game.Name + " — " + GameStatus(game.Id));
    button.FlatAppearance.BorderSize = game.Id == selectedGame ? 2 : 0;
    button.FlatAppearance.BorderColor = Color.FromArgb(255, 224, 149);
    gameList.Controls.Add(button);
    button.Invalidate();
   }
   SizeGameButtons(); gameList.ResumeLayout();
   getPop.Enabled = selectedGame > 0 && available.Contains(selectedGame) && !requesting && !autoRequesting;
   watchButton.Enabled = selectedGame > 0;
   watchButton.Text = selectedGame > 0 && watchlist.Games.Contains(selectedGame) ? "Unwatch" : "Watch";
   watchButton.BackColor = selectedGame > 0 && watchlist.Games.Contains(selectedGame) ? Violet : Panel;
  }
  void UpdateFilters() { allButton.BackColor = onlyAvailable || onlyMine ? Panel : Teal; availableButton.BackColor = onlyAvailable ? Teal : Panel; unownedButton.BackColor = onlyUnowned ? Teal : Panel; onlyMineButton.BackColor = onlyMine ? Teal : Panel; }
  void UpdateRefresh() {
   if (!hub.Connected) { refresh.Text = "--:--"; return; }
   int seconds = Math.Max(0, (int)Math.Ceiling((hub.NextRefresh - DateTime.UtcNow).TotalSeconds));
   refresh.Text = String.Format("{0:00}:{1:00}", seconds / 60, seconds % 60);
  }
  void UpdateHubStatus() {
   bool hasCode = preferences.Code.Length > 0;
   if (!hasCode) {
    hubDot.ForeColor = Color.FromArgb(197, 180, 205);
    hubStatus.Text = "Invite code needed";
    connectButton.Visible = false;
   } else if (hub.Connected) {
    hubDot.ForeColor = Color.FromArgb(112, 241, 145);
    hubStatus.Text = "Connected";
    connectButton.Visible = false;
   } else {
    if (connectionAttemptSince == DateTime.MinValue) connectionAttemptSince = DateTime.UtcNow;
    bool timedOut = DateTime.UtcNow - connectionAttemptSince >= TimeSpan.FromMinutes(1);
    hubDot.ForeColor = timedOut ? Color.FromArgb(255, 106, 106)
     : DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % 2 == 0 ? Color.FromArgb(255, 215, 85) : hubStatusPanel.BackColor;
    hubStatus.Text = timedOut ? "Disconnected" : everConnected ? "Reconnecting to hub" : "Connecting to hub";
    connectButton.Visible = timedOut;
   }
   hubUsers.Visible = hub.Connected;
   hubUsers.Text = activeUsers == 1 ? "Users Active: Only You!" : activeUsers > 1 ? "Users Active: " + activeUsers : "Users Active: --";
   LayoutHubStatus();
  }
  void BeginConnectionAttempt() { connectionAttemptSince = DateTime.UtcNow; UpdateHubStatus(); }
  void ToggleMaximize() { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }
  protected override void WndProc(ref Message m) {
   base.WndProc(ref m);
   if (m.Msg == 0x84 && WindowState != FormWindowState.Maximized && m.Result == new IntPtr(1)) {
    Point p = PointToClient(new Point((short)((long)m.LParam & 0xffff), (short)(((long)m.LParam >> 16) & 0xffff)));
    bool left = p.X < 7, right = p.X >= Width - 7, top = p.Y < 7, bottom = p.Y >= Height - 7;
    m.Result = new IntPtr(top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 1);
   }
  }
  void Ui(Action action) { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
  void Log(string message) { Ui(() => { if (activity.TextLength > 50000) activity.Clear(); activity.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine); activity.SelectionStart = activity.TextLength; activity.ScrollToCaret(); }); }
  void SaveSettings() { try { preferences.Save(); } catch (Exception e) { Log("Could not save settings: " + e.Message); } }
  void OpenWindow() { if (IsDisposed) return; Opacity = 1; ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); }
  async Task CheckForUpdates() {
   if (checkingUpdate || applyingUpdate) return;
   checkingUpdate = true;
   try {
    var release = await PopPullerUpdater.Check();
    if (release == null) return;
    availableUpdate = release;
    updateButton.Text = "Update v" + release.Version;
    updateButton.Visible = true;
    if (announcedUpdate != release.Version.ToString()) {
     announcedUpdate = release.Version.ToString();
     Log("Update available: v" + release.Version + ".");
     tray.BalloonTipTitle = "Update available";
     tray.ShowBalloonTip(8000, "Update available", "GFW8 Pop Puller v" + release.Version + " is ready to install.", ToolTipIcon.Info);
    }
   } catch (Exception e) { Log("Update check unavailable: " + e.Message); }
   finally { checkingUpdate = false; }
  }
  void ShowUpdateDialog(bool resumeHidden) {
   if (availableUpdate == null || applyingUpdate) return;
   using (var form = new MetroDialog("Update available", new Size(540, 165))) {
    var label = new Label { Text = "GFW8 Pop Puller v" + availableUpdate.Version + " is available. Download and restart now?",
     Location = new Point(20, 20), Size = new Size(500, 80) };
    var later = new Button { Text = "Later", Location = new Point(270, 112), Width = 112, Height = 34,
     BackColor = Panel, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.No };
    var now = new Button { Text = "Update now", Location = new Point(390, 112), Width = 130, Height = 34,
     BackColor = Teal, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Yes };
    form.Body.Controls.AddRange(new Control[] { label, later, now }); form.AcceptButton = now; form.CancelButton = later;
    if (form.ShowDialog(this) == DialogResult.Yes) { var work = InstallUpdate(resumeHidden); }
   }
  }
  async Task InstallUpdate(bool resumeHidden) {
   if (availableUpdate == null || applyingUpdate) return;
   if (scanning || requesting || autoRequesting) {
    MetroDialog.ShowMessage(this, "Update available", "Please wait for the current receipt request or license scan to finish, then select Update again.");
    return;
   }
   applyingUpdate = true; updateButton.Enabled = false;
   try {
    Log("Downloading GFW8 Pop Puller v" + availableUpdate.Version + ".");
    string downloaded = await PopPullerUpdater.Download(availableUpdate);
    Log("Update downloaded and verified. Restarting.");
    PopPullerUpdater.StartApply(downloaded, Application.ExecutablePath, Process.GetCurrentProcess().Id, resumeHidden, availableUpdate.Sha256);
    exiting = true; Close();
   } catch (Exception e) {
    Log("Update failed: " + e.Message);
    MetroDialog.ShowMessage(this, "Update failed", e.Message);
   } finally { applyingUpdate = false; if (!IsDisposed) updateButton.Enabled = true; }
  }
  void ShowHelp() {
   const string helpText =
    "A pop.bin (proof-of-purchase) file is a game license. It lasts for 24 hours and is not tied to an account. It does not bypass the Store startup check. Trial and Full licenses work the same way.\r\n\r\n" +
    "When a game attempts to sign in to a profile, it sends the pop.bin file to Xbox Live. If Xbox Live validates it, the service returns a token. The token lets the profile authenticate to the game, show presence, and unlock achievements.\r\n\r\n" +
    "Tokens are tied to an account and cannot be used for other games. They last for 4 hours. As long as the pop.bin file is within its 24-hour validity period, the token will continue to refresh.\r\n\r\n" +
    "A valid pop.bin file does not guarantee a valid token. For example, Pac-Man VAIO and Pac-Man Toshiba fail during token issuance. Only Microsoft can fix that issue.\r\n\r\n" +
    "Pop Puller scans Full and Trial game licenses available through Microsoft Store on this Windows profile. When you sign in to multiple Microsoft accounts in Store, their eligible games can appear together in one scan. Pop Puller cannot identify which account owns each game, so it shows them as one combined collection. An invite's first hub connection automatically starts a full 108-game scan. To include another account's games later, sign in to it through Microsoft Store, then select Scan My Licenses.\r\n\r\n" +
    "Watch a game to request a pop.bin automatically when it becomes available. Get pop.bin and Watchlist downloads are placed in the game's LocalState\\Microsoft.Xbox folder, even if the game is not installed yet. Pop Puller also keeps a copy in Downloads and notifies you when a watched pop expires. A failed request can be retried through another available contributor.\r\n\r\n" +
    "Get pop.bin checks the current file first. If more than 20 hours remain, wait or delete it manually before requesting another. From 12 to 20 hours, Pop Puller asks whether you want a fresh one. With less than 12 hours left, it requests a fresh pop without a prompt.";
   using (var form = new MetroDialog("Help", new Size(760, 500))) {
    var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(20, 20, 20, 14) };
    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
    var body = new RichTextBox { Text = helpText, ReadOnly = true, DetectUrls = false, BorderStyle = BorderStyle.None, BackColor = Background, ForeColor = Color.White, Font = new Font("Segoe UI", 11), Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Vertical, TabStop = false };
    var close = new Button { Text = "Close", Width = 110, Height = 34, BackColor = Teal, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
    layout.Controls.Add(body, 0, 0); layout.Controls.Add(close, 0, 1); form.Body.Controls.Add(layout); form.AcceptButton = close;
    form.ShowDialog(this);
   }
  }
  void ShowOwnerPassword() {
   if (!AdminCodeIssuer.IsConfigured) {
    MetroDialog.ShowMessage(this, "Owner tools unavailable", "The owner key is not configured on this Windows profile."); return;
   }
   bool setup = !OwnerPassword.IsConfigured;
   using (var form = new MetroDialog(setup ? "Set Owner Password" : "Password", new Size(450, setup ? 220 : 160))) {
    var label = new Label { Text = setup ? "Choose an owner password for this Windows profile." : "Enter password.", Location = new Point(18, 18), AutoSize = true };
    var field = new TextBox { Location = new Point(18, 55), Width = 414, UseSystemPasswordChar = true,
     BackColor = Panel, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, MaxLength = 64 };
    var confirm = new TextBox { Location = new Point(18, 108), Width = 414, UseSystemPasswordChar = true,
     BackColor = Panel, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, MaxLength = 64 };
    var confirmLabel = new Label { Text = "Confirm password", Location = new Point(18, 86), AutoSize = true };
    int buttonTop = setup ? 165 : 105;
    var cancel = new Button { Text = "Cancel", Location = new Point(190, buttonTop), Width = 110, Height = 34,
     BackColor = Panel, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
    var okay = new Button { Text = "OK", Location = new Point(308, buttonTop), Width = 124, Height = 34,
     BackColor = Teal, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
    form.Body.Controls.AddRange(new Control[] { label, field, cancel, okay });
    if (setup) form.Body.Controls.AddRange(new Control[] { confirmLabel, confirm });
    form.AcceptButton = okay; form.CancelButton = cancel;
    form.Shown += delegate { field.Focus(); };
    if (form.ShowDialog(this) != DialogResult.OK) return;
    if (setup) {
     if (!String.Equals(field.Text, confirm.Text, StringComparison.Ordinal)) {
      MetroDialog.ShowMessage(this, "Password", "Passwords do not match."); return;
     }
     try { OwnerPassword.Set(field.Text); }
     catch (Exception e) { MetroDialog.ShowMessage(this, "Password", e.Message); return; }
    } else if (!OwnerPassword.Verify(field.Text)) {
     MetroDialog.ShowMessage(this, "Password", "Incorrect password."); return;
    }
   }
   ShowOwnerMenu();
  }
  void SetCode() {
   if (hub.Connected && preferences.Code.Length > 0) {
    using (var form = new MetroDialog("Access Code", new Size(540, 190))) {
     var label = new Label { Text = "Access code saved. Connected to hub.", Location = new Point(18, 18), AutoSize = true };
     var field = new TextBox { Text = "************", Location = new Point(18, 55), Width = 504, BackColor = Panel, ForeColor = Color.FromArgb(169, 156, 177), BorderStyle = BorderStyle.FixedSingle, Enabled = false };
     var remove = new Button { Text = "Remove Code and Disconnect", Location = new Point(258, 125), Width = 264, Height = 38, BackColor = Color.FromArgb(185, 35, 41), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
     remove.Click += delegate {
      if (!MetroDialog.Confirm(form, "Disconnect from hub", "Removing this access code will disconnect this app from the hub. You will need an invite code to reconnect. Are you sure?", "Remove code")) return;
      preferences.ClearCode(); hub.ConnectNow(); UpdateHubStatus();
      Log("Access code removed. Disconnected from hub."); form.Close();
     };
     form.Body.Controls.AddRange(new Control[] { label, field, remove });
     form.ShowDialog(this);
    }
    return;
   }
   using (var form = new MetroDialog("Access Code", new Size(510, 160))) {
    var label = new Label { Text = "Enter your invite code.", Location = new Point(18, 18), AutoSize = true };
    var field = new TextBox { Location = new Point(18, 55), Width = 474, UseSystemPasswordChar = true, BackColor = Panel, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    var save = new Button { Text = "Save code", Location = new Point(370, 105), Width = 120, Height = 34, BackColor = Teal, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
    form.Body.Controls.AddRange(new Control[] { label, field, save }); form.AcceptButton = save;
    bool dedication = false, ownerAccess = false;
    field.TextChanged += delegate {
     if (String.Equals(field.Text, "MILKYBARS", StringComparison.OrdinalIgnoreCase)) dedication = true;
     else if (AdminCodeIssuer.IsConfigured && OwnerPassword.Verify(field.Text)) ownerAccess = true;
     else return;
     form.DialogResult = DialogResult.Cancel; form.Close();
    };
    var result = form.ShowDialog(this);
    if (dedication) {
     try { Process.Start(new ProcessStartInfo("https://xct.live") { UseShellExecute = true }); }
     catch (Exception error) { Log("Could not open the website: " + error.Message); }
     return;
    }
    if (ownerAccess) { ShowOwnerMenu(); return; }
    if (result == DialogResult.OK) {
     try { preferences.Code = field.Text.Trim(); Log("Invite code saved privately for this Windows user."); BeginConnectionAttempt(); hub.ConnectNow(); }
     catch (Exception e) { MetroDialog.ShowMessage(this, "Invalid code", e.Message); }
    }
   }
  }
  void ShowCodeGenerator() {
   using (var form = new MetroDialog("Generate Access Code", new Size(560, 226))) {
    var nameLabel = new Label { Text = "Person's gamertag", Location = new Point(18, 18), AutoSize = true };
    var name = new TextBox { Location = new Point(18, 46), Width = 524, BackColor = Panel, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, MaxLength = 60 };
    var codeLabel = new Label { Text = "Invite code", Location = new Point(18, 86), AutoSize = true };
    var code = new TextBox { Location = new Point(18, 114), Width = 524, BackColor = Panel, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, ReadOnly = true };
    var generate = new Button { Text = "Generate Code", Location = new Point(342, 166), Width = 200, Height = 36, BackColor = Teal, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    bool pending = false;
    generate.Click += async delegate {
     if (pending || code.Text.Length > 0) return;
     pending = true; generate.Enabled = false; generate.Text = "Generating…";
     try {
      string issued = await AdminCodeIssuer.Issue(name.Text);
      code.Text = issued;
      try {
       Clipboard.SetText(issued);
       generate.Text = "Copied to clipboard!";
      } catch (ExternalException) {
       generate.Text = "Copy code manually";
       MetroDialog.ShowMessage(form, "Clipboard unavailable", "The code was generated, but the clipboard is unavailable. Select and copy it from the field.");
      }
      Log("Generated an invite code.");
     } catch (Exception e) {
      generate.Text = "Generate Code"; generate.Enabled = true;
      MetroDialog.ShowMessage(form, "Could not generate code", e.Message);
     } finally { pending = false; }
    };
    form.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (pending) e.Cancel = true; };
    form.Body.Controls.AddRange(new Control[] { nameLabel, name, codeLabel, code, generate });
    form.AcceptButton = generate;
    form.ShowDialog(this);
   }
  }
  void ShowOwnerMenu() {
   if (!AdminCodeIssuer.IsConfigured) {
    MetroDialog.ShowMessage(this, "Owner tools unavailable", "The owner key is not configured on this Windows profile."); return;
   }
   int choice = 0;
   using (var form = new MetroDialog("Owner Tools", new Size(440, 180))) {
    form.Body.Controls.Add(new Label { Text = "Manage hub invites", Location = new Point(18, 18), AutoSize = true,
     Font = new Font("Segoe UI", 12) });
    var generate = new Button { Text = "Generate Code", Location = new Point(18, 72), Width = 190, Height = 42,
     BackColor = Teal, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    var remove = new Button { Text = "Remove Users", Location = new Point(228, 72), Width = 190, Height = 42,
     BackColor = Color.FromArgb(166, 42, 57), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    generate.Click += delegate { choice = 1; form.Close(); };
    remove.Click += delegate { choice = 2; form.Close(); };
    form.Body.Controls.AddRange(new Control[] { generate, remove });
    form.ShowDialog(this);
   }
   if (choice == 1) ShowCodeGenerator();
   if (choice == 2) { var work = ShowRemoveUsers(); }
  }
  async Task ShowRemoveUsers() {
   List<AdminUser> users;
   try {
    UseWaitCursor = true;
    users = await AdminCodeIssuer.List();
   } catch (Exception e) {
    MetroDialog.ShowMessage(this, "Could not list users", e.Message); return;
   } finally { UseWaitCursor = false; }
   if (users.Count == 0) { MetroDialog.ShowMessage(this, "Remove Users", "No invite codes have been issued."); return; }
   using (var form = new MetroDialog("Remove Users", new Size(610, 460))) {
    var intro = new Label { Text = "Select the invites to revoke and remove from the leaderboard.",
     Location = new Point(18, 15), Size = new Size(574, 25) };
    var list = new CheckedListBox { Location = new Point(18, 45), Size = new Size(574, 315),
     BackColor = Panel, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
     Font = new Font("Segoe UI", 10), CheckOnClick = true, IntegralHeight = false };
    foreach (var user in users) list.Items.Add(user);
    var cancel = new Button { Text = "Cancel", Location = new Point(333, 382), Width = 112, Height = 36,
     BackColor = Panel, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
    var revoke = new Button { Text = "Revoke selected", Location = new Point(453, 382), Width = 139, Height = 36,
     BackColor = Color.FromArgb(166, 42, 57), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    bool pending = false;
    revoke.Click += async delegate {
     if (pending) return;
     var selected = list.CheckedItems.Cast<AdminUser>().ToList();
     if (selected.Count == 0) { MetroDialog.ShowMessage(form, "Remove Users", "Select at least one user."); return; }
     string names = String.Join(", ", selected.Select(user => user.Label));
     if (!MetroDialog.Confirm(form, "Revoke access", "Revoke access for " + names + "? This disconnects their app and permanently removes their leaderboard statistics and saved license inventory.", "Revoke access")) return;
     pending = true; revoke.Enabled = false;
     try {
      await AdminCodeIssuer.Revoke(selected.Select(user => user.Id));
      Log("Revoked " + selected.Count + " invite" + (selected.Count == 1 ? "" : "s") + " and removed their leaderboard records.");
      pending = false;
      form.Close();
     } catch (Exception e) {
      MetroDialog.ShowMessage(form, "Could not revoke access", e.Message);
      revoke.Enabled = true;
     } finally { pending = false; }
    };
    form.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (pending) e.Cancel = true; };
    form.Body.Controls.AddRange(new Control[] { intro, list, cancel, revoke });
    form.CancelButton = cancel;
    form.ShowDialog(this);
   }
  }
  async Task Scan(bool automatic = false) {
   if (scanning) return; scanning = true; scanButton.Enabled = false;
   scanButton.Text = "Scanning 0/108";
   if (automatic) Log("Automatic first-connection license scan started.");
   try { await hub.ScanLicenses(row => Ui(() => {
    if (!scanning) return;
    int done = Convert.ToInt32(Wire.Get(row, "done")), total = Convert.ToInt32(Wire.Get(row, "total"));
    int saved = Convert.ToInt32(Wire.Get(row, "saved")), denied = Convert.ToInt32(Wire.Get(row, "unlicensed"));
    int errors = Convert.ToInt32(Wire.Get(row, "errors"));
    scanButton.Text = "Scanning " + done + "/" + total;
     if (done == 1 || done % 5 == 0 || done == total)
      Log("Store scan " + done + "/" + total + ": " + saved + " receipts returned, " + denied + " without receipts, " + errors + " errors.");
   })); }
   catch (Exception e) { Log((automatic ? "Automatic license scan stopped: " : "License scan stopped: ") + e.Message); }
   finally { scanning = false; scanButton.Text = "Scan My Licenses"; scanButton.Enabled = true; }
  }
  async Task<TimeSpan?> ExistingValidPop(int id) {
   string family = games.First(game => game.Id == id).Family;
   string path = Path.Combine(AppFiles.GamePopDirectory(family), "pop.bin");
   if (!File.Exists(path)) return null;
   try {
    var file = new FileInfo(path);
    if (file.Length == 0 || file.Length > 1048576) throw new InvalidDataException("The file size is invalid.");
    var verified = await backend.Call("validate", new Dictionary<string,object> {
     { "gameId", id }, { "receipt", Convert.ToBase64String(File.ReadAllBytes(path)) }
    });
    if (Convert.ToInt32(Wire.Get(verified, "gameId")) != id) throw new InvalidDataException("The file belongs to another game.");
    DateTime expiry;
    if (!DateTime.TryParse(Wire.Text(verified, "expiresUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiry))
     throw new InvalidDataException("The expiry time is missing.");
    TimeSpan remaining = expiry.ToUniversalTime() - DateTime.UtcNow;
    return remaining > TimeSpan.Zero ? (TimeSpan?)remaining : null;
   } catch (Exception e) {
    Log("Existing " + games.First(game => game.Id == id).Name + " pop.bin could not be validated: " + e.Message);
    return null;
   }
  }
  static string RemainingText(TimeSpan remaining) {
   int totalMinutes = (int)Math.Ceiling(remaining.TotalMinutes);
   int hours = totalMinutes / 60, minutes = totalMinutes % 60;
   return hours + (hours == 1 ? " hour" : " hours") + (minutes == 0 ? "" : " " + minutes + (minutes == 1 ? " minute" : " minutes"));
  }
  async Task RequestSelected() {
   if (requesting || autoRequesting || selectedGame == 0) return;
   requesting = true; getPop.Enabled = false;
   int id = selectedGame; string name = games.First(game => game.Id == id).Name;
   selection.Text = "Checking the current pop.bin for " + name + "…";
   try {
    TimeSpan? remaining = await ExistingValidPop(id);
    if (remaining.HasValue) {
     PopRefreshDecision decision = PopRefreshPolicy.Decide(remaining.Value);
     string time = RemainingText(remaining.Value);
     if (decision == PopRefreshDecision.Block) {
      MetroDialog.ShowMessage(this, "Current pop.bin is still valid",
       "Your current pop.bin is valid for " + time + ". To prevent unnecessary requests, wait until it has 20 hours or less left, or manually delete the current pop.bin.");
      selection.Text = name + " · current pop.bin valid for " + time;
      return;
     }
     if (decision == PopRefreshDecision.Confirm &&
      !MetroDialog.ConfirmRefresh(this, "Your current pop.bin is still valid for " + time + ". Do you want to request a fresh pop.bin?")) {
      selection.Text = name + " · kept current pop.bin";
      return;
     }
    }
    selection.Text = "Requesting a fresh receipt for " + name + "…";
    var saved = await hub.Request(id);
    SaveWatchExpiry(id, saved);
    bool placed = Wire.Get(saved, "placed") is bool && (bool)Wire.Get(saved, "placed");
    selection.Text = name + (placed ? " · verified pop.bin placed in game folder" : " · verified pop.bin saved in Downloads");
    Log(placed ? "Downloaded and placed " + name + " in its game folder." : "Downloaded " + name + "; game-folder placement needs attention. Open pop folder for the target path.");
   }
   catch (Exception e) { selection.Text = name + " · request failed"; Log("Could not download " + name + ": " + e.Message); }
   finally { requesting = false; RenderGames(); var work = PumpWatchlist(); }
  }
  void ToggleWatch() {
   if (selectedGame == 0) return;
   int id = selectedGame;
   if (!watchlist.Games.Add(id)) {
    watchlist.Games.Remove(id); watchlist.ExpiryUtc.Remove(id); watchlist.Notified.Remove(id);
    watchAttempted.Remove(id); Log("Removed " + games.First(game => game.Id == id).Name + " from Watchlist.");
   } else {
    Log("Watching " + games.First(game => game.Id == id).Name + ".");
    if (available.Contains(id)) QueueWatched(id);
   }
   try { WatchlistStore.Save(watchlist); } catch (Exception e) { Log("Could not save Watchlist: " + e.Message); }
   RenderGames();
  }
  void QueueWatched(int id) {
   if (!watchlist.Games.Contains(id) || !available.Contains(id) || watchAttempted.Contains(id) || watchQueue.Contains(id)) return;
   watchQueue.Enqueue(id);
   var work = PumpWatchlist();
  }
  void SaveWatchExpiry(int id, Dictionary<string,object> saved) {
   if (!watchlist.Games.Contains(id)) return;
   DateTime expires;
   if (!DateTime.TryParse(Wire.Text(saved, "expiresUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires)) {
    Log("The downloaded pop has no readable expiry time."); return;
   }
   watchlist.ExpiryUtc[id] = expires.ToUniversalTime(); watchlist.Notified.Remove(id);
   try { WatchlistStore.Save(watchlist); } catch (Exception e) { Log("Could not save Watchlist expiry: " + e.Message); }
  }
  async Task PumpWatchlist() {
   if (autoRequesting || requesting || !hub.Connected) return;
   autoRequesting = true; RenderGames();
   try {
    while (watchQueue.Count > 0 && hub.Connected) {
     int id = watchQueue.Dequeue();
    if (!watchlist.Games.Contains(id) || !available.Contains(id)) continue;
     watchAttempted.Add(id);
     string name = games.First(game => game.Id == id).Name;
    TimeSpan? remaining = await ExistingValidPop(id);
    if (remaining.HasValue && remaining.Value >= TimeSpan.FromHours(12)) {
     watchlist.ExpiryUtc[id] = DateTime.UtcNow.Add(remaining.Value);
     watchlist.Notified.Remove(id);
     try { WatchlistStore.Save(watchlist); } catch (Exception e) { Log("Could not save Watchlist expiry: " + e.Message); }
     Log("Watchlist kept the current " + name + " pop.bin; " + RemainingText(remaining.Value) + " remain.");
     continue;
    }
     Log("Watchlist requesting " + name + ".");
     try {
      var saved = await hub.Request(id);
      SaveWatchExpiry(id, saved);
      bool placed = Wire.Get(saved, "placed") is bool && (bool)Wire.Get(saved, "placed");
      Log(placed ? "Watchlist placed a verified pop.bin for " + name + " in its game folder." : "Watchlist saved a verified pop.bin for " + name + " in Downloads; game-folder placement needs attention.");
     } catch (Exception e) { Log("Watchlist could not download " + name + ": " + e.Message); }
    }
   } finally { autoRequesting = false; RenderGames(); }
  }
  void CheckWatchExpiries() {
   bool changed = false;
   foreach (int id in watchlist.Games) {
    DateTime expires;
    if (!watchlist.ExpiryUtc.TryGetValue(id, out expires) || expires > DateTime.UtcNow || !watchlist.Notified.Add(id)) continue;
    string name = games.First(game => game.Id == id).Name;
    Log("Watchlist pop.bin expired for " + name + ".");
    tray.BalloonTipIcon = ToolTipIcon.Info;
    tray.ShowBalloonTip(8000, "Watchlist pop expired", name + " needs a fresh pop.bin.", ToolTipIcon.Info);
    changed = true;
   }
   if (changed) try { WatchlistStore.Save(watchlist); } catch (Exception e) { Log("Could not save Watchlist notification state: " + e.Message); }
  }
  void OpenPopFolder() {
   try {
    string folder;
    if (selectedGame > 0) {
     folder = AppFiles.GamePopDirectory(games.First(game => game.Id == selectedGame).Family);
     Directory.CreateDirectory(folder);
    }
    else { AppFiles.PrivateDirectory(AppFiles.DownloadsRoot); folder = AppFiles.DownloadsRoot; }
    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
   } catch (Exception error) { Log("Could not open pop folder: " + error.Message); }
  }
 }
}
