using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gfw8Sharing;

namespace Gfw8PopPuller {
 sealed class LeaderboardWindow : Form {
  readonly HubClient hub;
  readonly Dictionary<int,string> gameNames;
  readonly Label totalServed = new Label(), online = new Label(), contributors = new Label();
  readonly Label userPeak = new Label(), gamePeak = new Label(), heading = new Label(), message = new Label();
  readonly Button back = new Button(), rankings = new Button(), games = new Button(), refresh = new Button();
  readonly DataGridView grid = new DataGridView();
  readonly Timer timer = new Timer();
  Dictionary<string,object> snapshot;
  string page = "rankings";
  string selectedContributor = "";
  int selectedGame;
  bool loading;

  static readonly Color Background = Color.FromArgb(33, 8, 40);
  static readonly Color Panel = Color.FromArgb(48, 32, 57);
  static readonly Color Teal = Color.FromArgb(0, 142, 157);
  static readonly Color Violet = Color.FromArgb(105, 34, 147);

  public LeaderboardWindow(HubClient client, Dictionary<int,string> names) {
   hub = client; gameNames = names;
   Text = "GFW8 Pop Puller · Leaderboard";
   BackColor = Background; ForeColor = Color.White; Font = new Font("Segoe UI", 10);
   FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterParent;
   ShowInTaskbar = false; ClientSize = new Size(1160, 680);
   var work = Screen.PrimaryScreen.WorkingArea;
   ClientSize = new Size(Math.Min(ClientSize.Width, work.Width - 30), Math.Min(ClientSize.Height, work.Height - 30));
   var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = new Padding(0) };
   foreach (int height in new[] { 60, 50, 88, 61, 45 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
   layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
   layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
   Controls.Add(layout);
   layout.Controls.Add(PopChrome.Create(this, "Leaderboard", ToggleMaximize), 0, 0);

   var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(13, 5, 0, 4), Margin = new Padding(0) };
   Style(back, "← Back", Panel, 85); back.Click += delegate { GoBack(); };
   Style(rankings, "Leaderboard", Teal, 130); rankings.Click += delegate { page = "rankings"; Render(); };
   Style(games, "Browse games", Panel, 125); games.Click += delegate { page = "games"; Render(); };
   Style(refresh, "Refresh", Violet, 100); refresh.Click += async delegate { await LoadSnapshot(); };
   nav.Controls.AddRange(new Control[] { back, rankings, games, refresh }); layout.Controls.Add(nav, 0, 1);

   var summary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
   for (int i = 0; i < 3; i++) summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
   summary.Controls.Add(Tile("VERIFIED POPS SERVED", totalServed, Teal), 0, 0);
   summary.Controls.Add(Tile("USERS CONNECTED", online, Violet), 1, 0);
   summary.Controls.Add(Tile("CONTRIBUTORS RECORDED", contributors, Panel), 2, 0);
   layout.Controls.Add(summary, 0, 2);

   var peaks = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
   peaks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); peaks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
   peaks.Controls.Add(Tile("ALL-TIME HIGH · USERS CONNECTED", userPeak, Color.FromArgb(36, 71, 97), 15), 0, 0);
   peaks.Controls.Add(Tile("ALL-TIME HIGH · UNIQUE GAMES", gamePeak, Color.FromArgb(80, 49, 108), 15), 1, 0);
   layout.Controls.Add(peaks, 0, 3);

   heading.Dock = DockStyle.Fill; heading.Font = new Font("Segoe UI Light", 18);
   heading.Padding = new Padding(16, 1, 0, 0); heading.TextAlign = ContentAlignment.MiddleLeft;
   layout.Controls.Add(heading, 0, 4);
   grid.Dock = DockStyle.Fill; grid.Margin = new Padding(14, 0, 14, 0);
   grid.BackgroundColor = Background; grid.BorderStyle = BorderStyle.None;
   grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
   grid.AllowUserToResizeRows = false; grid.ReadOnly = true;
   grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
   grid.RowHeadersVisible = false; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
   grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersHeight = 35;
   grid.ColumnHeadersDefaultCellStyle.BackColor = Panel;
   grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(220, 207, 225);
   grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9, FontStyle.Bold);
   grid.DefaultCellStyle.BackColor = Color.FromArgb(54, 33, 63);
   grid.DefaultCellStyle.ForeColor = Color.White;
   grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 112, 133);
   grid.DefaultCellStyle.SelectionForeColor = Color.White;
   grid.DefaultCellStyle.Padding = new Padding(8, 2, 8, 2);
   grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(62, 40, 71);
   grid.RowTemplate.Height = 38; grid.CellDoubleClick += OpenSelected;
   layout.Controls.Add(grid, 0, 5);
   message.Dock = DockStyle.Fill; message.ForeColor = Color.FromArgb(205, 188, 214);
   message.Padding = new Padding(17, 7, 0, 0); message.Text = "Loading leaderboard…";
   layout.Controls.Add(message, 0, 6);
   Shown += async delegate { await LoadSnapshot(); };
   hub.ConnectionChanged += HubConnectionChanged;
   FormClosed += delegate { hub.ConnectionChanged -= HubConnectionChanged; timer.Stop(); timer.Dispose(); };
   timer.Interval = 60000; timer.Tick += async delegate { await LoadSnapshot(); }; timer.Start();
  }

  void ToggleMaximize() {
   WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
  }

  static void Style(Button button, string text, Color color, int width) {
   button.Text = text; button.Width = width; button.Height = 35;
   button.Margin = new Padding(2, 2, 7, 2); button.BackColor = color;
   button.ForeColor = Color.White; button.FlatStyle = FlatStyle.Flat;
   button.FlatAppearance.BorderColor = Color.FromArgb(156, 126, 169);
   button.Font = new Font("Segoe UI", 9);
  }
  static Panel Tile(string label, Label value, Color color, int valueSize = 25) {
   var panel = new Panel { Dock = DockStyle.Fill, BackColor = color, Margin = new Padding(0, 0, 4, 0) };
   panel.Controls.Add(new Label { Text = label, Location = new Point(16, 8), AutoSize = true,
    Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.FromArgb(230, 220, 237) });
   value.Text = "—"; value.Location = new Point(16, 27); value.Size = new Size(500, 46);
   value.Font = new Font("Segoe UI Light", valueSize); value.BackColor = color;
   value.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
   panel.Controls.Add(value); panel.Resize += delegate { value.Width = Math.Max(50, panel.ClientSize.Width - 32); };
   return panel;
  }
  void HubConnectionChanged(bool connected) {
   if (!connected && IsHandleCreated) BeginInvoke((Action)(() => { if (!IsDisposed) Close(); }));
  }
  async Task LoadSnapshot() {
   if (loading || IsDisposed) return;
   loading = true; refresh.Enabled = false; message.Text = "Refreshing leaderboard…";
   try {
    snapshot = await hub.RequestLeaderboard();
    if (IsDisposed) return;
    Render();
    message.Text = "Double-click a contributor or game to see details. Updated " + DateTime.Now.ToString("HH:mm:ss") + ".";
   } catch (Exception error) {
    if (!IsDisposed) message.Text = "Leaderboard unavailable: " + error.Message;
   } finally { if (!IsDisposed) { loading = false; refresh.Enabled = true; } }
  }
  static int Number(Dictionary<string,object> row, string key) { return Convert.ToInt32(Wire.Get(row, key) ?? 0); }
  static long Long(Dictionary<string,object> row, string key) { return Convert.ToInt64(Wire.Get(row, key) ?? 0L); }
  static bool Flag(Dictionary<string,object> row, string key) { return Wire.Get(row, key) is bool && (bool)row[key]; }
  static List<int> Ids(Dictionary<string,object> row, string key) {
   return (Wire.Get(row, key) as object[] ?? new object[0]).Select(Convert.ToInt32).ToList();
  }
  List<Dictionary<string,object>> Rows() {
   return (Wire.Get(snapshot, "rows") as object[] ?? new object[0]).Select(Wire.Map).ToList();
  }
  string GameName(int id) { string value; return gameNames.TryGetValue(id, out value) ? value : "Game " + id; }
  static string Day(string raw) {
   DateTime date;
   return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)
    ? date.ToString("dd/MM/yyyy") : "—";
  }
  static string Duration(long milliseconds) {
   var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
   return (span.Days > 0 ? span.Days + "d " : "") + span.Hours + "h " + span.Minutes + "m";
  }
  void Columns(params string[] titles) {
   grid.Columns.Clear(); grid.Rows.Clear();
   foreach (string title in titles) grid.Columns.Add(title.Replace(" ", ""), title);
  }
  void AddRow(object tag, params object[] cells) {
   int index = grid.Rows.Add(cells); grid.Rows[index].Tag = tag;
  }
  void Render() {
   if (snapshot == null || IsDisposed) return;
   var rows = Rows();
   totalServed.Text = Number(snapshot, "totalServed").ToString("N0");
   online.Text = Number(snapshot, "onlineCount").ToString("N0");
   contributors.Text = rows.Count.ToString("N0");
   var peaks = Wire.Map(Wire.Get(snapshot, "peaks"));
   var users = Wire.Map(Wire.Get(peaks, "users"));
   var titles = Wire.Map(Wire.Get(peaks, "games"));
   userPeak.Text = Number(users, "count") + " users on " + Day(Wire.Text(users, "day"));
   gamePeak.Text = Number(titles, "count") + " games on " + Day(Wire.Text(titles, "day"));
   back.Enabled = page == "contributor" || page == "game";
   rankings.BackColor = page == "rankings" ? Teal : Panel;
   games.BackColor = page == "games" ? Teal : Panel;
   grid.SuspendLayout();
   try {
    if (page == "rankings") {
     heading.Text = "Contributors";
     Columns("Contributor", "Pops served", "Most served game", "Total time connected", "Status", "Games offered now", "First offer recorded");
     for (int i = 0; i < rows.Count; i++) {
      var row = rows[i]; var best = Wire.Map(Wire.Get(row, "mostServedGame"));
      string game = best.Count == 0 ? "—" : Wire.Text(best, "name") + " (" + Number(best, "count") + ")";
      AddRow(Wire.Text(row, "id"), (i + 1) + ". " + Wire.Text(row, "label"), Number(row, "served"), game,
       Wire.Get(row, "connectedMs") == null ? "—" : Duration(Long(row, "connectedMs")),
       Flag(row, "online") ? "Connected" : "Offline", Ids(row, "offeredGameIds").Count,
       Day(Wire.Text(row, "firstInventory")));
     }
    } else if (page == "games") {
     heading.Text = "Games";
     Columns("Game", "Offering now", "Last recorded");
     foreach (var game in gameNames.OrderBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase)) {
      int current = rows.Count(row => Ids(row, "offeredGameIds").Contains(game.Key));
      int recorded = rows.Count(row => Ids(row, "lastRecordedGameIds").Contains(game.Key));
      AddRow(game.Key, game.Value, current, recorded);
     }
    } else if (page == "contributor") {
     var row = rows.FirstOrDefault(item => Wire.Text(item, "id") == selectedContributor);
     if (row == null) { page = "rankings"; Render(); return; }
     heading.Text = Wire.Text(row, "label") + " · " + Ids(row, "offeredGameIds").Count + " offered now · " +
      Ids(row, "lastRecordedGameIds").Count + " last recorded";
     Columns("Game", "Status", "Last inventory change");
     var current = new HashSet<int>(Ids(row, "offeredGameIds"));
     var recorded = new HashSet<int>(Ids(row, "lastRecordedGameIds"));
     foreach (int id in current.Union(recorded).OrderBy(GameName))
      AddRow(id, GameName(id), current.Contains(id) ? "Offering now" : "Last recorded",
       Day(Wire.Text(row, "lastInventoryChange")));
    } else if (page == "game") {
     heading.Text = GameName(selectedGame);
     Columns("Contributor", "Status");
     foreach (var row in rows) {
      var current = Ids(row, "offeredGameIds"); var recorded = Ids(row, "lastRecordedGameIds");
      if (current.Contains(selectedGame) || recorded.Contains(selectedGame))
       AddRow(Wire.Text(row, "id"), Wire.Text(row, "label"), current.Contains(selectedGame) ? "Offering now" : "Last recorded");
     }
    }
    foreach (DataGridViewColumn column in grid.Columns) column.MinimumWidth = page == "rankings" ? 100 : 150;
    if (grid.Rows.Count > 0) grid.ClearSelection();
   } finally { grid.ResumeLayout(); }
  }
  void OpenSelected(object sender, DataGridViewCellEventArgs e) {
   if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
   object tag = grid.Rows[e.RowIndex].Tag;
   if (page == "rankings" || page == "game") { selectedContributor = Convert.ToString(tag); page = "contributor"; }
   else { selectedGame = Convert.ToInt32(tag); page = "game"; }
   Render();
  }
  void GoBack() {
   if (page == "contributor") page = "rankings";
   else if (page == "game") page = "games";
   Render();
  }
 }
}
