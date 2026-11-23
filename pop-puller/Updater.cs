using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Gfw8Sharing;

namespace Gfw8PopPuller {
 sealed class PopPullerRelease {
  public Version Version;
  public Uri Download;
  public string Sha256;
  public long Size;
 }

 static class PopPullerUpdater {
  const string LatestUrl = "https://api.github.com/repos/thebreenis/test-repo-1/releases/latest";
  const string ReleasePrefix = "https://github.com/thebreenis/test-repo-1/releases/download/";
  const long MaximumDownload = 50L * 1024 * 1024;
  static readonly Regex VersionTag = new Regex(@"\Av[0-9]+(?:\.[0-9]+){2,3}\z", RegexOptions.CultureInvariant);
  static readonly Regex AssetName = new Regex(@"\AGFW8[. ]Pop[. ]Puller[. ]v[0-9]+(?:\.[0-9]+){2,3}\.exe\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
  static readonly Regex Digest = new Regex(@"\Asha256:([0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant);

  static HttpClient Client() {
   var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
   client.DefaultRequestHeaders.UserAgent.ParseAdd("GFW8PopPuller/" + AppFiles.Version);
   client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
   return client;
  }

  internal static PopPullerRelease ParseLatest(string json, Version current) {
   var release = Wire.Map(Wire.Json.DeserializeObject(json));
   string tag = Wire.Text(release, "tag_name");
   Version latest;
   if (!VersionTag.IsMatch(tag) || !Version.TryParse(tag.Substring(1), out latest) || latest.CompareTo(current) <= 0 ||
    Wire.Get(release, "draft") is bool && (bool)Wire.Get(release, "draft") ||
    Wire.Get(release, "prerelease") is bool && (bool)Wire.Get(release, "prerelease")) return null;
   var assets = Wire.Get(release, "assets") as object[] ?? new object[0];
   foreach (var item in assets) {
    var asset = Wire.Map(item);
    string name = Wire.Text(asset, "name");
    string url = Wire.Text(asset, "browser_download_url");
    var digest = Digest.Match(Wire.Text(asset, "digest"));
    Uri uri;
    long size;
    if (!AssetName.IsMatch(name) || !name.EndsWith("v" + latest + ".exe", StringComparison.OrdinalIgnoreCase) ||
     !url.StartsWith(ReleasePrefix + tag + "/", StringComparison.OrdinalIgnoreCase) ||
     !Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
     !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
     !digest.Success || !Int64.TryParse(Convert.ToString(Wire.Get(asset, "size")), out size) ||
     size < 1 || size > MaximumDownload) continue;
    return new PopPullerRelease { Version = latest, Download = uri, Sha256 = digest.Groups[1].Value.ToLowerInvariant(), Size = size };
   }
   return null;
  }

  internal static async Task<PopPullerRelease> Check() {
   using (var client = Client())
   using (var response = await client.GetAsync(LatestUrl)) {
    response.EnsureSuccessStatusCode();
    return ParseLatest(await response.Content.ReadAsStringAsync(), new Version(AppFiles.Version));
   }
  }

  static string HashFile(string path) {
   using (var file = File.OpenRead(path))
   using (var sha = SHA256.Create())
    return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
  }

  internal static async Task<string> Download(PopPullerRelease release) {
   string root = Path.Combine(AppFiles.Root, "Updates");
   AppFiles.PrivateDirectory(AppFiles.Root);
   AppFiles.PrivateDirectory(root);
   string folder = Path.Combine(root, "v" + release.Version + "-" + Guid.NewGuid().ToString("N"));
   AppFiles.PrivateDirectory(folder);
   string path = Path.Combine(folder, "GFW8 Pop Puller v" + release.Version + ".exe");
   try {
    using (var client = Client())
    using (var response = await client.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead)) {
     response.EnsureSuccessStatusCode();
     long length = response.Content.Headers.ContentLength ?? -1;
     if (length > MaximumDownload || length > 0 && length != release.Size) throw new InvalidDataException("The update size does not match the release.");
     using (var input = await response.Content.ReadAsStreamAsync())
     using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
      var buffer = new byte[65536];
      long total = 0;
      int count;
      while ((count = await input.ReadAsync(buffer, 0, buffer.Length)) > 0) {
       total += count;
       if (total > MaximumDownload || total > release.Size) throw new InvalidDataException("The update is larger than expected.");
       await output.WriteAsync(buffer, 0, count);
      }
      if (total != release.Size) throw new InvalidDataException("The update download is incomplete.");
     }
    }
    if (!String.Equals(HashFile(path), release.Sha256, StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("The update hash does not match the GitHub release.");
    return path;
   } catch {
    try { if (File.Exists(path)) File.Delete(path); Directory.Delete(folder); } catch { }
    throw;
   }
  }

  static string Quote(string argument) { return "\"" + argument.Replace("\"", "\\\"") + "\""; }

  internal static void StartApply(string downloaded, string oldExe, int oldPid, bool resumeHidden, string expectedHash) {
   if (!String.Equals(HashFile(downloaded), expectedHash, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("The staged update changed before launch.");
   if (!File.Exists(oldExe) || !String.Equals(FileVersionInfo.GetVersionInfo(oldExe).ProductName, "GFW8 Pop Puller", StringComparison.Ordinal))
    throw new InvalidDataException("The currently running Pop Puller EXE could not be verified.");
   var start = new ProcessStartInfo(downloaded, "--apply-update " + Quote(oldExe) + " " + oldPid + " " + expectedHash + " " + (resumeHidden ? "1" : "0")) {
    UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(downloaded)
   };
   using (var process = Process.Start(start)) if (process == null) throw new IOException("Could not launch the update helper.");
  }

  static string Place(string source, string folder, string hash) {
   string destination = Path.Combine(folder, "GFW8 Pop Puller v" + AppFiles.Version + ".exe");
   string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".new";
   try {
    File.Copy(source, temporary);
    if (!String.Equals(HashFile(temporary), hash, StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("The installed update failed verification.");
    if (File.Exists(destination)) {
     if (String.Equals(HashFile(destination), hash, StringComparison.OrdinalIgnoreCase)) return destination;
     File.Replace(temporary, destination, null);
    } else File.Move(temporary, destination);
    return destination;
   } finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
  }

  internal static int Apply(string[] args) {
   int oldPid;
   if (args.Length != 5 || !Int32.TryParse(args[2], out oldPid) || oldPid == Process.GetCurrentProcess().Id ||
    !Regex.IsMatch(args[3], @"\A[0-9a-fA-F]{64}\z") || args[4] != "0" && args[4] != "1") return 2;
   string oldExe = null;
   string source = System.Windows.Forms.Application.ExecutablePath;
   string hash = args[3].ToLowerInvariant();
   bool hidden = args[4] == "1";
   bool launched = false;
   try {
    oldExe = Path.GetFullPath(args[1]);
    if (!File.Exists(oldExe) || !String.Equals(FileVersionInfo.GetVersionInfo(oldExe).ProductName, "GFW8 Pop Puller", StringComparison.Ordinal) ||
     !String.Equals(Path.GetFileName(source), "GFW8 Pop Puller v" + AppFiles.Version + ".exe", StringComparison.OrdinalIgnoreCase) ||
     !String.Equals(HashFile(source), hash, StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("The update or previous EXE did not pass verification.");
    try { using (var old = Process.GetProcessById(oldPid)) if (!old.WaitForExit(30000)) throw new TimeoutException("The previous version did not close."); }
    catch (ArgumentException) { /* The previous process already exited. */ }
    string installed;
    try { installed = Place(source, Path.GetDirectoryName(oldExe), hash); }
    catch (Exception e) {
     if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw;
     string folder = Path.Combine(AppFiles.Root, "App");
     AppFiles.PrivateDirectory(AppFiles.Root); AppFiles.PrivateDirectory(folder);
     installed = Place(source, folder, hash);
    }
    string ack = Path.Combine(Path.GetDirectoryName(source), "started.txt");
    var start = new ProcessStartInfo(installed, (hidden ? "--minimized " : "") + "--update-ack " + Quote(ack)) { UseShellExecute = false };
    using (var next = Process.Start(start)) {
     if (next == null) throw new IOException("The new version did not launch.");
     launched = true;
     for (int i = 0; i < 80; i++) {
      if (File.Exists(ack) && String.Equals(File.ReadAllText(ack), AppFiles.Version, StringComparison.Ordinal)) {
       if (!String.Equals(oldExe, installed, StringComparison.OrdinalIgnoreCase))
        try { File.Delete(oldExe); } catch (Exception e) { WriteError("New version started, but the old EXE could not be removed: " + e.Message); }
       return 0;
      }
      if (next.HasExited) break;
      Thread.Sleep(250);
     }
     if (!next.HasExited) {
      try { next.Kill(); next.WaitForExit(5000); } catch { }
     }
     launched = !next.HasExited;
     throw new IOException("The new version did not confirm startup. The previous EXE was kept.");
    }
   } catch (Exception e) {
    WriteError(e.Message);
    if (!launched && oldExe != null && File.Exists(oldExe))
     try { Process.Start(new ProcessStartInfo(oldExe, hidden ? "--minimized" : "") { UseShellExecute = false }); } catch { }
    return 1;
   }
  }

  static void WriteError(string message) {
   try { AppFiles.PrivateDirectory(AppFiles.Root); File.WriteAllText(Path.Combine(AppFiles.Root, "last-update-error.txt"), message); } catch { }
  }

  internal static string TakeError() {
   string path = Path.Combine(AppFiles.Root, "last-update-error.txt");
   if (!File.Exists(path)) return "";
   try { string message = File.ReadAllText(path); File.Delete(path); return message; }
   catch { return ""; }
  }

  internal static bool SelfTest() {
   string url = ReleasePrefix + "v9.9.9/GFW8.Pop.Puller.v9.9.9.exe";
   string json = Wire.Json.Serialize(new { tag_name = "v9.9.9", draft = false, prerelease = false,
    assets = new[] { new { name = "GFW8.Pop.Puller.v9.9.9.exe", browser_download_url = url,
     digest = "sha256:" + new string('a', 64), size = 12345 } } });
   var release = ParseLatest(json, new Version("0.1.25"));
   bool metadata = release != null && release.Version.ToString() == "9.9.9" &&
    ParseLatest(json, new Version("9.9.9")) == null &&
    ParseLatest(json.Replace("github.com", "example.com"), new Version("0.1.25")) == null;
   string testRoot = Path.Combine(Path.GetTempPath(), "GFW8PopPuller-update-test-" + Guid.NewGuid().ToString("N"));
   try {
    Directory.CreateDirectory(testRoot);
    string hash = HashFile(System.Windows.Forms.Application.ExecutablePath);
    string installed = Place(System.Windows.Forms.Application.ExecutablePath, testRoot, hash);
    return metadata && File.Exists(installed) && String.Equals(HashFile(installed), hash, StringComparison.OrdinalIgnoreCase);
   } finally { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); }
  }
 }
}
