using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gfw8Sharing;

namespace Gfw8PopPuller {
 sealed class HubClient : IDisposable {
  readonly Preferences preferences;
  readonly ReceiptBackend backend;
  readonly Dictionary<int,string> gameNames = new Dictionary<int,string>();
  readonly Identity identity;
  readonly SemaphoreSlim sendGate = new SemaphoreSlim(1);
  readonly SemaphoreSlim reconnectSignal = new SemaphoreSlim(0, 1);
  readonly object gate = new object();
  readonly CancellationTokenSource stopping = new CancellationTokenSource();
  ClientWebSocket socket;
  HashSet<int> owned = new HashSet<int>();
  HashSet<int> available = new HashSet<int>();
  TaskCompletionSource<Dictionary<string,object>> waiting;
  TaskCompletionSource<Dictionary<string,object>> leaderboardWaiting;
  int waitingGame;
  bool connected;
  DateTime nextRefresh = DateTime.MinValue;
  DateTime nextInventory = DateTime.MinValue;
  string lastInventoryReport = "";
  string initialScanAttemptedCode = "";
  string initialScanCompletedCode = "";

  public event Action<string> Activity;
  public event Action<bool> ConnectionChanged;
  public event Action<HashSet<int>> AvailabilityChanged;
  public event Action<Dictionary<int,int>> ProviderCountsChanged;
  public event Action<Dictionary<int,string>> LastSeenChanged;
  public event Action<int> UsersActiveChanged;
  public event Action<int,string,int,string> PeaksChanged;
  public event Action<int> OwnCountChanged;
  public event Action InitialScanRequested;
  public bool Connected { get { lock (gate) return connected; } }
  public DateTime NextRefresh { get { lock (gate) return nextRefresh; } }
  public bool IsOwned(int id) { lock (gate) return owned.Contains(id); }
  public HubClient(Preferences prefs, ReceiptBackend receipt) {
   preferences = prefs; backend = receipt;
   foreach (var raw in backend.Games()) {
    var game = Wire.Map(raw);
    gameNames[Convert.ToInt32(game["index"])] = Wire.Text(game, "name");
   }
   AppFiles.PrivateDirectory(AppFiles.Root);
   identity = new Identity(AppFiles.Root);
  }
  void Log(string message) { var handler = Activity; if (handler != null) handler(message); }
  string GameName(int id) { string name; return gameNames.TryGetValue(id, out name) ? name : "game " + id; }
  static string DescribeConnectionError(Exception error) {
   var details = new List<string>();
   for (var current = error; current != null && details.Count < 4; current = current.InnerException) {
    string message = (current.Message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim().TrimEnd('.');
    if (message.Length == 0) continue;
    if (message.Contains("status code '401'") || message.Contains("(401) Unauthorized"))
     return "HTTP 401 during WebSocket handshake; check Access Code";
    message = Regex.Replace(message, @"GFW8H-[A-Za-z0-9_-]+", "[invite code]");
    if (message.Length > 160) message = message.Substring(0, 160) + "…";
    if (!details.Contains(message)) details.Add(message);
   }
   return details.Count == 0 ? error.GetType().Name : String.Join(" | ", details);
  }
  void SetConnected(bool value) {
   lock (gate) { connected = value; if (!value) { available.Clear(); nextRefresh = DateTime.MinValue; } }
   var connection = ConnectionChanged; if (connection != null) connection(value);
   if (!value) {
    var status = AvailabilityChanged; if (status != null) status(new HashSet<int>());
    var counts = ProviderCountsChanged; if (counts != null) counts(new Dictionary<int,int>());
    var users = UsersActiveChanged; if (users != null) users(0);
   }
  }
  public bool IsAvailable(int id) { lock (gate) return available.Contains(id); }
  public void Start() { Task.Run((Func<Task>)Run); }
  public void ConnectNow() {
   try { if (socket != null) socket.Abort(); } catch { }
   try { if (reconnectSignal.CurrentCount == 0) reconnectSignal.Release(); } catch (SemaphoreFullException) { }
  }
  public void Stop() {
   stopping.Cancel();
   try { if (socket != null) socket.Abort(); } catch { }
   lock (gate) {
    if (waiting != null) waiting.TrySetException(new IOException("Disconnected from hub."));
    if (leaderboardWaiting != null) leaderboardWaiting.TrySetException(new IOException("Disconnected from hub."));
   }
  }
  public async Task RefreshInventory(bool reportEvenIfUnchanged = false) {
   bool changed = false;
   try {
    var data = await backend.Call("inventory");
    var rows = Wire.Get(data, "games") as object[] ?? new object[0];
    var next = new HashSet<int>(rows.Select(Wire.Map)
     .Where(row => String.Equals(Wire.Text(row, "licenseType"), "Full", StringComparison.OrdinalIgnoreCase) ||
      String.Equals(Wire.Text(row, "licenseType"), "Trial", StringComparison.OrdinalIgnoreCase))
     .Select(row => Convert.ToInt32(row["gameId"])));
    var report = new List<string> { "Store-accessible inventory: " + next.Count + " Full/Trial games." };
    string reportText = String.Join("\n", report);
    bool reportChanged;
    lock (gate) { reportChanged = reportEvenIfUnchanged || reportText != lastInventoryReport; lastInventoryReport = reportText; }
    lock (gate) { owned = next; nextInventory = DateTime.UtcNow.AddMinutes(5); }
    var count = OwnCountChanged; if (count != null) count(next.Count);
    if (reportChanged) foreach (string line in report) Log(line);
    changed = true;
   } catch (Exception e) {
    lock (gate) { owned.Clear(); nextInventory = DateTime.UtcNow.AddMinutes(1); lastInventoryReport = ""; }
    var count = OwnCountChanged; if (count != null) count(0);
    Log("Local license inventory unavailable: " + e.Message);
    changed = true;
   }
   if (changed) try { await SendOffer(); } catch { }
  }
  public async Task ScanLicenses(Action<Dictionary<string,object>> progress) {
   string scanCode = preferences.Code;
   Log("Checking Store receipts available to this Windows session.");
   var data = await backend.Call("scan", null, progress);
   ReportScanFailures(data);
   Log("License scan finished.");
   await RefreshInventory(true);
   if (FullScanCompleted(data)) {
    lock (gate) initialScanCompletedCode = scanCode;
    if (scanCode.Length > 0 && scanCode == preferences.Code && Connected)
     try { await Send(new { type = "initialScanComplete" }); }
     catch (Exception e) { Log("Could not confirm the initial scan with the hub: " + e.Message); }
   } else Log("The full scan was incomplete. Scan My Licenses can retry later.");
  }
  bool FullScanCompleted(Dictionary<string,object> data) {
   var summaries = Wire.Get(data, "summaries") as object[] ?? new object[0];
   var failures = Wire.Get(data, "failedAccounts") as object[] ?? new object[0];
   return summaries.Length > 0 && failures.Length == 0 && summaries.All(raw => {
    var row = Wire.Map(raw);
    return Convert.ToInt32(Wire.Get(row, "checked") ?? 0) == gameNames.Count &&
     Convert.ToInt32(Wire.Get(row, "total") ?? 0) == gameNames.Count &&
     Convert.ToInt32(Wire.Get(row, "errors") ?? 0) == 0 &&
     !Convert.ToBoolean(Wire.Get(row, "cancelled") ?? false) &&
     !Convert.ToBoolean(Wire.Get(row, "throttled") ?? false);
   });
  }
  void ReportScanFailures(Dictionary<string,object> data) {
   var rows = Wire.Get(data, "failedAccounts") as object[] ?? new object[0];
   if (Convert.ToInt32(Wire.Get(data, "accountsScanned")) == 0) {
    if (rows.Length > 0) throw new IOException(Wire.Text(Wire.Map(rows[0]), "message"));
    throw new IOException("No account scan could start. See the activity log.");
   }
   foreach (var item in rows) { var row = Wire.Map(item); Log("License scan for " + Wire.Text(row, "email") + " stopped: " + Wire.Text(row, "message")); }
  }
  public Task RefreshOffer() { return SendOffer(); }

  async Task Run() {
   await RefreshInventory();
   while (!stopping.IsCancellationRequested) {
    string code = preferences.Code;
    if (code.Length == 0) { SetConnected(false); await Delay(3000); continue; }
    ClientWebSocket active = null;
    try {
     active = new ClientWebSocket();
     active.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
     active.Options.SetRequestHeader("Authorization", "Bearer " + code);
     socket = active;
     string url = AppFiles.HubUrl.Replace("https://", "wss://").TrimEnd('/') + "/connect";
     await active.ConnectAsync(new Uri(url), stopping.Token);
     SetConnected(true); Log("Connected.");
     await SendOffer();
     Task receiver = Receive(active);
     while (!receiver.IsCompleted && !stopping.IsCancellationRequested) {
      DateTime now = DateTime.UtcNow;
      DateTime refresh, inventory;
      lock (gate) { refresh = nextRefresh; inventory = nextInventory; }
      if (now >= inventory) await RefreshInventory();
      else if (now >= refresh) await SendOffer();
      await Delay(500);
     }
     if (stopping.IsCancellationRequested) active.Abort();
     else await receiver;
    } catch (OperationCanceledException) { break; }
    catch (Exception e) {
     if (!stopping.IsCancellationRequested) {
      string detail = DescribeConnectionError(e);
      Log("Hub disconnected: " + e.GetType().Name + (detail.Length > 0 ? " — " + detail : "") + ". Reconnecting.");
     }
    }
    finally {
     if (active != null) { try { active.Abort(); } catch { } active.Dispose(); }
     if (Object.ReferenceEquals(socket, active)) socket = null;
     SetConnected(false);
     lock (gate) {
      if (waiting != null) waiting.TrySetException(new IOException("Hub disconnected during request."));
      if (leaderboardWaiting != null) leaderboardWaiting.TrySetException(new IOException("Hub disconnected during request."));
     }
    }
    await Delay(4000);
   }
  }
  async Task Delay(int milliseconds) {
   try { await reconnectSignal.WaitAsync(milliseconds, stopping.Token); } catch (OperationCanceledException) { }
  }
  async Task Send(object value) {
   await sendGate.WaitAsync();
   try {
    var active = socket;
    if (active == null || active.State != WebSocketState.Open) throw new IOException("Hub is offline.");
    byte[] bytes = Wire.Utf(Wire.Json.Serialize(value));
    await active.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, stopping.Token);
   } finally { sendGate.Release(); }
  }
  async Task SendOffer() {
   HashSet<int> ids;
   lock (gate) { ids = new HashSet<int>(owned); nextRefresh = DateTime.UtcNow.AddSeconds(60); }
   if (!Connected) return;
   await Send(new { type = "offer", gameIds = ids.OrderBy(id => id).ToArray(), jwk = identity.Jwk });
  }
  async Task Receive(ClientWebSocket active) {
   var buffer = new byte[8192];
   while (active.State == WebSocketState.Open && !stopping.IsCancellationRequested) {
    using (var content = new MemoryStream()) {
     WebSocketReceiveResult result;
     do {
      result = await active.ReceiveAsync(new ArraySegment<byte>(buffer), stopping.Token);
      if (result.MessageType == WebSocketMessageType.Close) return;
      content.Write(buffer, 0, result.Count);
      if (content.Length > 524288) throw new IOException("Hub message exceeded limit.");
     } while (!result.EndOfMessage);
     string text = Encoding.UTF8.GetString(content.ToArray());
     if (text == "pong") continue;
     await Handle(Wire.Map(Wire.Json.DeserializeObject(text)));
    }
   }
  }
  async Task Handle(Dictionary<string,object> message) {
   string type = Wire.Text(message, "type");
   if (type == "leaderboard") {
    TaskCompletionSource<Dictionary<string,object>> target;
    lock (gate) target = leaderboardWaiting;
    if (target != null) target.TrySetResult(Wire.Map(Wire.Get(message, "data")));
    return;
   }
   if (type == "welcome") {
    if (Wire.Get(message, "needsInitialScan") is bool && (bool)message["needsInitialScan"]) {
     string code = preferences.Code;
     if (code.Length == 0) return;
     bool completed, requestScan;
     lock (gate) {
      completed = initialScanCompletedCode == code;
      requestScan = !completed && initialScanAttemptedCode != code;
      if (requestScan) initialScanAttemptedCode = code;
     }
     if (completed) await Send(new { type = "initialScanComplete" });
     else if (requestScan) {
      Log("First hub connection: starting a full 108-game license scan.");
      var handler = InitialScanRequested; if (handler != null) handler();
     }
    }
    return;
   }
   if (type == "initialScanAcknowledged") { Log("The hub recorded the completed first-connection scan."); return; }
   if (type == "queued") return;
   if (type == "availability") {
    var rows = Wire.Get(message, "gameIds") as object[] ?? new object[0];
    var next = new HashSet<int>(rows.Select(Convert.ToInt32));
    var countRows = Wire.Get(message, "providerCounts") as Dictionary<string,object>;
    var providerCounts = new Dictionary<int,int>();
    if (countRows != null) foreach (var pair in countRows) {
     int id, count;
     if (Int32.TryParse(pair.Key, out id) && id >= 1 && id <= 108 &&
      Int32.TryParse(Convert.ToString(pair.Value), out count) && count > 0 && next.Contains(id)) providerCounts[id] = count;
    }
    var countChanged = ProviderCountsChanged; if (countChanged != null) countChanged(providerCounts);
    var history = Wire.Get(message, "lastSeen") as Dictionary<string,object>;
    if (history != null) {
     var dates = new Dictionary<int,string>();
     foreach (var pair in history) {
      int id; DateTime day;
      string value = Convert.ToString(pair.Value);
      if (Int32.TryParse(pair.Key, out id) && id >= 1 && id <= 108 &&
       DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out day)) dates[id] = value;
     }
     var changed = LastSeenChanged; if (changed != null) changed(dates);
    }
    lock (gate) available = next;
    var handler = AvailabilityChanged; if (handler != null) handler(new HashSet<int>(next));
    var users = UsersActiveChanged;
    if (users != null) users(Wire.Get(message, "usersActive") == null ? 0 : Math.Max(0, Convert.ToInt32(message["usersActive"])));
    var peaks = Wire.Get(message, "peaks") as Dictionary<string,object>;
    if (peaks != null) {
     var gamePeak = Wire.Map(Wire.Get(peaks, "games"));
     var userPeak = Wire.Map(Wire.Get(peaks, "users"));
     var changed = PeaksChanged;
     if (changed != null) changed(Math.Max(0, Convert.ToInt32(Wire.Get(gamePeak, "count") ?? 0)),
      Wire.Text(gamePeak, "day"), Math.Max(0, Convert.ToInt32(Wire.Get(userPeak, "count") ?? 0)), Wire.Text(userPeak, "day"));
    }
    return;
   }
   if (type == "pull") {
    int gameId = Convert.ToInt32(message["gameId"]);
    string requestId = Wire.Text(message, "requestId");
    var requester = Wire.Map(Wire.Get(message, "requesterJwk"));
    var serving = Task.Run(async () => await Serve(gameId, requestId, requester));
    return;
   }
   if (type == "retrying") {
    lock (gate) if (waiting != null && waitingGame == Convert.ToInt32(message["gameId"]))
     Log("A contributor could not serve " + GameName(waitingGame) + "; trying another.");
    return;
   }
   if (type == "result") { await AcceptResult(message); return; }
   if (type == "failure" || type == "unavailable" || type == "error") {
    TaskCompletionSource<Dictionary<string,object>> target;
    lock (gate) target = waiting;
    if (target != null) target.TrySetException(new IOException(type == "unavailable" ? "No contributor is available." : Wire.Text(message, "message")));
   }
  }
  async Task Serve(int gameId, string requestId, Dictionary<string,object> requester) {
   string failure = null;
   try {
    bool allowed;
    lock (gate) allowed = owned.Contains(gameId);
    if (!allowed) throw new IOException("Game is not available locally.");
    var data = await backend.Call("receipt", new Dictionary<string,object> { { "gameId", gameId } });
    lock (gate) allowed = owned.Contains(gameId);
    if (!allowed) throw new IOException("License became unavailable during request.");
    string receipt = Wire.Text(data, "receipt");
    string packet = Wire.Encrypt(identity, requester, requestId, new { gameId, receipt });
    await Send(new { type = "result", requestId, packet });
    Log("Served a fresh receipt for " + GameName(gameId) + ".");
   } catch (Exception e) { failure = e.Message; }
   if (failure != null) {
    Log("Could not serve " + GameName(gameId) + ": " + failure);
    try { await Send(new { type = "failure", requestId }); } catch { }
    try { await RefreshInventory(); } catch { }
   }
  }
  async Task AcceptResult(Dictionary<string,object> message) {
   TaskCompletionSource<Dictionary<string,object>> target;
   int game;
   lock (gate) { target = waiting; game = waitingGame; }
   if (target == null || Convert.ToInt32(message["gameId"]) != game) return;
   string id = Wire.Text(message, "requestId");
   Exception failure = null;
   try {
    string nonce;
    var sender = Wire.Map(Wire.Get(message, "providerJwk"));
    var data = Wire.Decrypt(identity, sender, id, Wire.Text(message, "packet"), out nonce);
    if (Convert.ToInt32(data["gameId"]) != game) throw new IOException("Contributor returned another game.");
    var validated = await backend.Call("validate", new Dictionary<string,object> {
     { "gameId", game }, { "receipt", Wire.Text(data, "receipt") }
    });
    if (Convert.ToInt32(validated["gameId"]) != game) throw new IOException("Validated receipt belongs to another game.");
    bool placed; string placementError;
    string path = AppFiles.SaveDownload(backend.PackageFamily(game), Convert.FromBase64String(Wire.Text(validated, "receipt")), out placed, out placementError);
    var saved = new Dictionary<string,object> { { "path", path }, { "placed", placed }, { "placementError", placementError }, { "licenseType", Wire.Text(validated, "licenseType") }, { "expiresUtc", Wire.Text(validated, "expiresUtc") } };
    try { await Send(new { type = "accept", requestId = id }); } catch { /* The local verified pop is still usable. */ }
    target.TrySetResult(saved);
    Log("Saved and verified " + Wire.Text(validated, "licenseType") + " receipt: " + path);
    if (!placed) Log("Could not place pop.bin in the game folder; the verified copy is in Pop Puller Downloads. " + placementError);
   } catch (Exception e) { failure = e; }
   if (failure != null) {
    Log("A contributor returned an unusable receipt for " + GameName(game) + "; trying another if available.");
    try { await Send(new { type = "reject", requestId = id }); }
    catch { target.TrySetException(new IOException("Received receipt failed validation: " + failure.Message)); }
   }
  }
  public async Task<Dictionary<string,object>> RequestLeaderboard() {
   TaskCompletionSource<Dictionary<string,object>> target;
   lock (gate) {
    if (!connected) throw new IOException("Connect to the hub to view the leaderboard.");
    if (leaderboardWaiting != null) throw new IOException("Leaderboard refresh is already in progress.");
    leaderboardWaiting = target = new TaskCompletionSource<Dictionary<string,object>>();
   }
   try {
    await Send(new { type = "leaderboardRequest" });
    if (await Task.WhenAny(target.Task, Task.Delay(12000, stopping.Token)) != target.Task)
     throw new TimeoutException("The leaderboard did not respond. Try Refresh.");
    return await target.Task;
   } finally { lock (gate) if (Object.ReferenceEquals(leaderboardWaiting, target)) leaderboardWaiting = null; }
  }
  public async Task<Dictionary<string,object>> Request(int gameId) {
   TaskCompletionSource<Dictionary<string,object>> target;
   lock (gate) {
    if (!connected || !available.Contains(gameId)) throw new IOException("No contributor is available for this game.");
    if (waiting != null) throw new IOException("Another receipt request is in progress.");
    waitingGame = gameId;
    waiting = target = new TaskCompletionSource<Dictionary<string,object>>();
   }
   try {
    await Send(new { type = "get", gameId, validateAck = true });
    if (await Task.WhenAny(target.Task, Task.Delay(185000, stopping.Token)) != target.Task) throw new TimeoutException("Receipt request timed out.");
    return await target.Task;
   } finally { lock (gate) if (Object.ReferenceEquals(waiting, target)) waiting = null; }
  }
  public void Dispose() { Stop(); identity.Dispose(); sendGate.Dispose(); stopping.Dispose(); }
 }
}
