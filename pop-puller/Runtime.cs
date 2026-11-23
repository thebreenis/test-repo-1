using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Gfw8Sharing;

namespace Gfw8PopPuller {
 static class AppFiles {
  internal const string Version = "0.1.26";
  internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GFW8PopPuller");
  internal static readonly string DownloadsRoot = Path.Combine(Root, "Downloads");
  internal static readonly string HubUrl = "https://gfw8-pop-puller-hub.alec2dabreen.workers.dev";
  internal static string Runtime;
  internal static void PrivateDirectory(string path) {
   Directory.CreateDirectory(path);
   var user = WindowsIdentity.GetCurrent().User;
   var acl = new DirectorySecurity();
   acl.SetOwner(user); acl.SetAccessRuleProtection(true, false);
   foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
    acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
      InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
   Directory.SetAccessControl(path, acl);
  }
  internal static string DownloadDirectory(string family) {
   CheckFamily(family);
   string game = Path.Combine(DownloadsRoot, family);
   string local = Path.Combine(game, "LocalState");
   string xbox = Path.Combine(local, "Microsoft.Xbox");
   PrivateDirectory(DownloadsRoot); PrivateDirectory(game); PrivateDirectory(local); PrivateDirectory(xbox);
   return xbox;
  }
  internal static void CheckFamily(string family) {
   if (String.IsNullOrEmpty(family) || !System.Text.RegularExpressions.Regex.IsMatch(family, @"\A[A-Za-z0-9][A-Za-z0-9._-]{1,199}\z"))
    throw new InvalidDataException("Invalid game package family name.");
  }
  internal static string GamePopDirectory(string family) {
   CheckFamily(family);
   return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family, "LocalState", "Microsoft.Xbox");
  }
  static string WritePop(string folder, byte[] bytes) {
   Directory.CreateDirectory(folder);
   string target = Path.Combine(folder, "pop.bin");
   string temporary = Path.Combine(folder, ".pop-" + Guid.NewGuid().ToString("N") + ".tmp");
   try {
    File.WriteAllBytes(temporary, bytes);
    if (File.Exists(target)) File.Replace(temporary, target, null);
    else File.Move(temporary, target);
    return target;
   } finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }
  internal static string SaveDownload(string family, byte[] bytes, out bool placed, out string placementError) {
   placed = false; placementError = "";
   if (bytes == null || bytes.Length == 0 || bytes.Length > 1048576) throw new InvalidDataException("Invalid receipt size.");
   string staged = WritePop(DownloadDirectory(family), bytes);
   try {
    string target = WritePop(GamePopDirectory(family), bytes);
    placed = true;
    return target;
   } catch (IOException e) { placementError = e.Message; return staged; }
     catch (UnauthorizedAccessException e) { placementError = e.Message; return staged; }
  }
  internal static bool TestPopWriting() {
   string root = Path.Combine(Path.GetTempPath(), "GFW8PopPuller-test-" + Guid.NewGuid().ToString("N"));
   try {
    string file = WritePop(Path.Combine(root, "LocalState", "Microsoft.Xbox"), new byte[] { 1, 2, 3 });
    WritePop(Path.GetDirectoryName(file), new byte[] { 4, 5 });
    return File.ReadAllBytes(file).SequenceEqual(new byte[] { 4, 5 });
   } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
  }
  static string Hash(Stream stream) {
   using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
  }
  static string SafePath(string root, string name) {
   string path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
   if (!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Invalid runtime entry.");
   return path;
  }
  internal static string Unpack() {
   PrivateDirectory(Root);
   string runtimeRoot = Path.Combine(Root, "Runtime"); PrivateDirectory(runtimeRoot);
   using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("PopPuller.Payload")) {
    if (input == null) throw new InvalidDataException("Runtime bundle missing.");
    string hash = Hash(input); input.Position = 0;
    string target = Path.Combine(runtimeRoot, Version + "-" + hash.Substring(0, 16));
    using (var zip = new ZipArchive(input, ZipArchiveMode.Read)) {
     bool complete = Directory.Exists(target);
     if (complete) foreach (var entry in zip.Entries) {
      string path = SafePath(target, entry.FullName);
      if (!File.Exists(path)) { complete = false; break; }
      using (var expected = entry.Open()) using (var actual = File.OpenRead(path))
       if (Hash(expected) != Hash(actual)) { complete = false; break; }
     }
     if (!complete) {
      if (Directory.Exists(target)) target += "-" + Guid.NewGuid().ToString("N");
      PrivateDirectory(target);
      foreach (var entry in zip.Entries) {
       string path = SafePath(target, entry.FullName);
       PrivateDirectory(Path.GetDirectoryName(path));
       using (var source = entry.Open()) using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
      }
     }
    }
    Runtime = target;
    return target;
   }
  }
 }

 static class LastSeenCache {
  static readonly string FilePath = Path.Combine(AppFiles.Root, "last-seen.json");
  public static Dictionary<int,string> Load() {
   var result = new Dictionary<int,string>();
   if (!File.Exists(FilePath)) return result;
   try {
    var rows = Wire.Map(Wire.Json.DeserializeObject(File.ReadAllText(FilePath)));
    foreach (var pair in rows) {
     int id; DateTime day;
     string value = pair.Value as string;
     if (Int32.TryParse(pair.Key, out id) && id >= 1 && id <= 108 &&
      DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) result[id] = value;
    }
   } catch { /* An invalid local cache must not prevent launch. */ }
   return result;
  }
  public static void Save(Dictionary<int,string> dates) {
   try {
    AppFiles.PrivateDirectory(AppFiles.Root);
    var rows = new Dictionary<string,string>();
    foreach (var pair in dates) rows[pair.Key.ToString(CultureInfo.InvariantCulture)] = pair.Value;
    string temp = FilePath + ".tmp";
    File.WriteAllText(temp, Wire.Json.Serialize(rows));
    if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
    else File.Move(temp, FilePath);
   } catch { /* History still remains in the hub. */ }
  }
 }

 sealed class WatchlistState {
  public readonly HashSet<int> Games = new HashSet<int>();
  public readonly Dictionary<int,DateTime> ExpiryUtc = new Dictionary<int,DateTime>();
  public readonly HashSet<int> Notified = new HashSet<int>();
 }

 static class WatchlistStore {
  static readonly string FilePath = Path.Combine(AppFiles.Root, "watchlist.json");
  public static WatchlistState Load() {
   var state = new WatchlistState();
   if (!File.Exists(FilePath)) return state;
   try {
    var data = Wire.Map(Wire.Json.DeserializeObject(File.ReadAllText(FilePath)));
    foreach (var raw in Wire.Get(data, "games") as object[] ?? new object[0]) {
     int id = Convert.ToInt32(raw); if (id >= 1 && id <= 108) state.Games.Add(id);
    }
    foreach (var pair in Wire.Map(Wire.Get(data, "expiryUtc"))) {
     int id; DateTime time;
     if (Int32.TryParse(pair.Key, out id) && state.Games.Contains(id) &&
      DateTime.TryParseExact(Convert.ToString(pair.Value), "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time))
      state.ExpiryUtc[id] = time.ToUniversalTime();
    }
    foreach (var raw in Wire.Get(data, "notified") as object[] ?? new object[0]) {
     int id = Convert.ToInt32(raw); if (state.Games.Contains(id)) state.Notified.Add(id);
    }
   } catch { return new WatchlistState(); }
   return state;
  }
  public static void Save(WatchlistState state) {
   AppFiles.PrivateDirectory(AppFiles.Root);
   var expiry = new Dictionary<string,string>();
   foreach (var pair in state.ExpiryUtc) if (state.Games.Contains(pair.Key))
    expiry[pair.Key.ToString(CultureInfo.InvariantCulture)] = pair.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
   string temporary = FilePath + ".tmp";
   File.WriteAllText(temporary, Wire.Json.Serialize(new { games = state.Games.OrderBy(x => x).ToArray(), expiryUtc = expiry, notified = state.Notified.OrderBy(x => x).ToArray() }));
   if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
   else File.Move(temporary, FilePath);
  }
 }

 sealed class AdminUser {
  public string Id;
  public string Label;
  public bool Connected;
  public int Served;
  public override string ToString() { return Label + (Connected ? "  ·  Connected" : "  ·  Offline") + "  ·  " + Served + " pops served"; }
 }

 static class OwnerPassword {
  static readonly string FilePath = Path.Combine(AppFiles.Root, "owner-password-v2.dpapi.bin");
  static readonly byte[] Entropy = Wire.Utf("GFW8 Pop Puller owner password v2");
  internal static bool IsConfigured { get { return File.Exists(FilePath); } }
  internal static void Set(string password) {
   if (String.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 128)
    throw new InvalidDataException("Choose a password of 8 to 128 characters.");
   AppFiles.PrivateDirectory(AppFiles.Root);
   byte[] plain = Wire.Utf(password);
   try {
    byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    string temporary = FilePath + ".tmp";
    try {
     File.WriteAllBytes(temporary, encrypted);
     if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
     else File.Move(temporary, FilePath);
    } finally { if (File.Exists(temporary)) File.Delete(temporary); }
   } finally { Array.Clear(plain, 0, plain.Length); }
  }
  internal static bool Verify(string password) {
   if (!IsConfigured || password == null) return false;
   byte[] plain = null;
   try {
    plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
    byte[] candidate = Wire.Utf(password);
    try {
     int difference = plain.Length ^ candidate.Length;
     for (int i = 0; i < Math.Min(plain.Length, candidate.Length); i++) difference |= plain[i] ^ candidate[i];
     return difference == 0;
    } finally { Array.Clear(candidate, 0, candidate.Length); }
   } catch (CryptographicException) { return false; }
   finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
  }
 }

 static class AdminCodeIssuer {
  static readonly string KeyPath = Path.Combine(AppFiles.Root, "admin-key.dpapi.bin");
  internal static bool IsConfigured { get { return File.Exists(KeyPath); } }
  static async Task<Dictionary<string,object>> Call(HttpMethod method, string route, object body = null) {
   if (!File.Exists(KeyPath)) throw new InvalidOperationException("The owner key is not configured on this Windows profile.");
   byte[] plain;
   try {
    plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Wire.Utf("GFW8 Pop Puller admin v1"), DataProtectionScope.CurrentUser);
   } catch (CryptographicException e) {
    throw new InvalidOperationException("The owner key cannot be opened by this Windows profile.", e);
   }
   try {
    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
    using (var request = new HttpRequestMessage(method, AppFiles.HubUrl.TrimEnd('/') + route)) {
     request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(plain));
     if (body != null) request.Content = new StringContent(Wire.Json.Serialize(body), Encoding.UTF8, "application/json");
     using (var response = await client.SendAsync(request)) {
      if (!response.IsSuccessStatusCode) throw new IOException("The hub did not accept the owner request (HTTP " + (int)response.StatusCode + ").");
      return Wire.Map(Wire.Json.DeserializeObject(await response.Content.ReadAsStringAsync()));
     }
    }
   } finally { Array.Clear(plain, 0, plain.Length); }
  }
  internal static async Task<string> Issue(string gamertag) {
   gamertag = (gamertag ?? "").Trim();
   if (gamertag.Length < 1 || gamertag.Length > 60 || gamertag.Any(Char.IsControl))
    throw new InvalidDataException("Enter a gamertag of 1 to 60 characters.");
   var result = await Call(HttpMethod.Post, "/admin/issue", new { label = gamertag });
   string code = Wire.Text(result, "code");
   if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"\AGFW8H-[A-Za-z0-9_-]{43}\z"))
    throw new InvalidDataException("The hub did not return a valid access code.");
   return code;
  }
  internal static async Task<List<AdminUser>> List() {
   var result = await Call(HttpMethod.Get, "/admin/codes");
   var rows = Wire.Get(result, "codes") as object[] ?? new object[0];
   return rows.Select(raw => {
    var row = Wire.Map(raw);
    return new AdminUser { Id = Wire.Text(row, "id"), Label = Wire.Text(row, "label"),
     Connected = Convert.ToBoolean(Wire.Get(row, "connected") ?? false),
     Served = Convert.ToInt32(Wire.Get(row, "served") ?? 0) };
   }).Where(user => System.Text.RegularExpressions.Regex.IsMatch(user.Id, @"\A[0-9a-f-]{36}\z")).ToList();
  }
  internal static async Task Revoke(IEnumerable<string> ids) {
   var selected = ids.Distinct().ToArray();
   if (selected.Length == 0) throw new InvalidDataException("Select at least one invite to remove.");
   var result = await Call(HttpMethod.Post, "/admin/revoke", new { ids = selected });
   if (Convert.ToInt32(Wire.Get(result, "revoked") ?? 0) != selected.Length)
    throw new IOException(Wire.Text(result, "error") == "" ? "The hub did not revoke every selected invite." : Wire.Text(result, "error"));
  }
 }

 sealed class Preferences {
  static readonly string FilePath = Path.Combine(AppFiles.Root, "preferences.json");
  static readonly string CodePath = Path.Combine(AppFiles.Root, "access-protected.bin");
  public bool StartAtLogin;
  public bool CloseToTray = true;
  public string Code {
   get {
    if (!File.Exists(CodePath)) return "";
    try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(CodePath), Wire.Utf("GFW8 Pop Puller access v1"), DataProtectionScope.CurrentUser)); }
    catch { return ""; }
   }
   set {
    if (!System.Text.RegularExpressions.Regex.IsMatch(value ?? "", @"^GFW8H-[A-Za-z0-9_-]{43}$")) throw new InvalidDataException("Enter an individual GFW8H access code.");
    AppFiles.PrivateDirectory(AppFiles.Root);
    File.WriteAllBytes(CodePath, ProtectedData.Protect(Wire.Utf(value), Wire.Utf("GFW8 Pop Puller access v1"), DataProtectionScope.CurrentUser));
   }
  }
  public void ClearCode() { if (File.Exists(CodePath)) File.Delete(CodePath); }
  public static Preferences Load() {
   var result = new Preferences();
   if (!File.Exists(FilePath)) return result;
   try {
    var data = Wire.Map(Wire.Json.DeserializeObject(File.ReadAllText(FilePath)));
    result.StartAtLogin = Wire.Get(data, "startAtLogin") is bool && (bool)data["startAtLogin"];
    result.CloseToTray = Wire.Get(data, "closeToTray") is bool && (bool)data["closeToTray"];
   } catch { /* Use safe defaults if local preferences are damaged. */ }
   return result;
  }
  public void Save() {
   AppFiles.PrivateDirectory(AppFiles.Root);
   File.WriteAllText(FilePath, Wire.Json.Serialize(new { startAtLogin = StartAtLogin, closeToTray = CloseToTray }));
   using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
    if (StartAtLogin) key.SetValue("GFW8PopPuller", "\"" + System.Windows.Forms.Application.ExecutablePath + "\"" + (CloseToTray ? " --minimized" : ""));
    else key.DeleteValue("GFW8PopPuller", false);
   }
  }
 }

 sealed class ReceiptBackend {
  readonly SemaphoreSlim gate = new SemaphoreSlim(1);
  readonly string runtime;
  public ReceiptBackend(string root) { runtime = root; }
  public object[] Games() {
   var data = Wire.Map(Wire.Json.DeserializeObject(File.ReadAllText(Path.Combine(runtime, "backend", "games.json"))));
   return Wire.Get(data, "games") as object[] ?? new object[0];
  }
  public string PackageFamily(int gameId) {
   foreach (var raw in Games()) {
    var row = Wire.Map(raw);
    if (Convert.ToInt32(row["index"]) == gameId) return Wire.Text(row, "package_family_name");
   }
   throw new InvalidDataException("Game is not in the catalogue.");
  }
  public async Task<Dictionary<string,object>> Call(string action, Dictionary<string,object> extra = null, Action<Dictionary<string,object>> progress = null) {
   await gate.WaitAsync();
   try { return await Task.Run(() => CallNow(action, extra, progress)); }
   finally { gate.Release(); }
  }
  static void PublishProgress(string path, ref int seen, Action<Dictionary<string,object>> progress) {
   if (progress == null || !File.Exists(path)) return;
   string[] lines;
   try {
    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
    using (var reader = new StreamReader(input, Encoding.UTF8)) lines = reader.ReadToEnd().Split('\n');
   }
   catch (IOException) { return; }
   int complete = lines.Length - 1;
   while (seen < complete) {
    try {
     var row = Wire.Map(Wire.Json.DeserializeObject(lines[seen].TrimEnd('\r')));
     if (Wire.Text(row, "kind") == "bulk") progress(row);
    } catch { /* A progress message must not interrupt the receipt worker. */ }
    seen++;
   }
  }
  Dictionary<string,object> CallNow(string action, Dictionary<string,object> extra, Action<Dictionary<string,object>> progress) {
   string jobs = Path.Combine(AppFiles.Root, "Jobs"); AppFiles.PrivateDirectory(jobs);
   string job = Path.Combine(jobs, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
   AppFiles.PrivateDirectory(job);
   try {
    var request = new Dictionary<string,object> { { "action", action } };
    request["accounts"] = new object[0];
    if (extra != null) foreach (var item in extra) request[item.Key] = item.Value;
    string input = Path.Combine(job, "request-private.json");
    File.WriteAllText(input, Wire.Json.Serialize(request));
    string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe");
    var start = new ProcessStartInfo(powershell, "-NoProfile -MTA -ExecutionPolicy Bypass -File \"" + Path.Combine(runtime, "SharingBackend.ps1") + "\" -RequestPath \"" + input + "\"") {
     UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
    };
    using (var process = Process.Start(start)) {
     var output = process.StandardOutput.ReadToEndAsync();
     var error = process.StandardError.ReadToEndAsync();
     if (progress == null) process.WaitForExit();
     else {
      int seen = 0;
      string events = Path.Combine(job, "events-private.jsonl");
      while (!process.WaitForExit(150)) PublishProgress(events, ref seen, progress);
      PublishProgress(events, ref seen, progress);
     }
     File.WriteAllText(Path.Combine(job, "worker-private.log"), output.Result + Environment.NewLine + error.Result);
    }
    string result = Path.Combine(job, "result-private.json");
    if (!File.Exists(result)) throw new IOException("Receipt worker stopped without a result.");
    var row = Wire.Map(Wire.Json.DeserializeObject(File.ReadAllText(result)));
    if (!(Wire.Get(row, "success") is bool) || !(bool)row["success"]) throw new IOException(Wire.Text(row, "message"));
    return Wire.Map(Wire.Get(row, "data"));
   } finally {
    try { Directory.Delete(job, true); } catch { /* A failed worker may retain an open diagnostic file. */ }
   }
  }
 }
}
