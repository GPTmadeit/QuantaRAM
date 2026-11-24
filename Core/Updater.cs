using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuantaRAM.Core
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string Tag, Name, Notes, PageUrl, AssetUrl, ShaUrl;
        public long Size;
    }

    /// <summary>
    /// Self-updater backed by GitHub Releases. Looks at the latest (non-draft, non-prerelease) release,
    /// downloads the QuantaRAM.exe asset, checks it against the published .sha256, then swaps the
    /// running exe (Windows allows renaming a running image) and restarts.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "GPTmadeit/QuantaRAM";
        public const string AssetName = "QuantaRAM.exe";
        public static string ReleasesPage => $"https://github.com/{Repo}/releases";

        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("QuantaRAM-Updater/" + CurrentText);
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        public static Version Current
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string CurrentText => "v" + Current.ToString(3);

        /// <summary>True when running the published single-file exe (the only layout we can self-replace).</summary>
        public static bool CanSelfUpdate =>
            Environment.ProcessPath != null &&
            !File.Exists(Path.Combine(AppContext.BaseDirectory, "QuantaRAM.dll"));

        static Version ParseTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var t = tag.Trim().TrimStart('v', 'V');
            int dash = t.IndexOfAny(new[] { '-', '+' });
            if (dash > 0) t = t.Substring(0, dash);
            if (!Version.TryParse(t, out var v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        /// <summary>Returns the newer release, or null when up to date / no releases yet.</summary>
        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            var info = new UpdateInfo
            {
                Tag = r.GetProperty("tag_name").GetString(),
                Name = r.TryGetProperty("name", out var n) ? n.GetString() : null,
                Notes = r.TryGetProperty("body", out var b) ? b.GetString() : null,
                PageUrl = r.TryGetProperty("html_url", out var h) ? h.GetString() : ReleasesPage
            };
            info.Version = ParseTag(info.Tag);
            if (info.Version == null || info.Version <= Current) return null;
            if (r.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString();
                    string url = a.GetProperty("browser_download_url").GetString();
                    if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) { info.AssetUrl = url; info.Size = a.GetProperty("size").GetInt64(); }
                    else if (string.Equals(name, AssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) info.ShaUrl = url;
                }
            }
            return info;
        }

        /// <summary>Downloads and verifies the new exe. Returns its local path.</summary>
        public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double> progress, CancellationToken ct = default)
        {
            if (u.AssetUrl == null) throw new InvalidOperationException("release has no QuantaRAM.exe asset");
            string dir = Path.Combine(Path.GetTempPath(), "QuantaRAM-update");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"QuantaRAM-{u.Version}.exe");

            using (var resp = await http.GetAsync(u.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? u.Size;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(path);
                var buf = new byte[81920];
                long done = 0; int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            if (u.ShaUrl != null)
            {
                string expected = (await http.GetStringAsync(u.ShaUrl, ct)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
                string actual;
                await using (var fs = File.OpenRead(path))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (expected != actual)
                {
                    File.Delete(path);
                    throw new InvalidDataException("checksum mismatch: download discarded");
                }
            }
            if (new FileInfo(path).Length < 100_000)
            {
                File.Delete(path);
                throw new InvalidDataException("downloaded file is too small to be valid");
            }
            return path;
        }

        /// <summary>
        /// Swaps the running exe for <paramref name="newExe"/> and starts it. Returns true if the caller
        /// should now exit. Falls back to an elevated helper when the install folder is protected.
        /// </summary>
        public static bool Apply(string newExe)
        {
            string target = Environment.ProcessPath;
            try
            {
                Swap(newExe, target);
            }
            catch (UnauthorizedAccessException)
            {
                // e.g. installed under Program Files: let the new exe do the swap with admin rights
                Process.Start(new ProcessStartInfo(newExe, $"--install-update \"{target}\"") { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            Process.Start(new ProcessStartInfo(target, "--restart --updated") { UseShellExecute = true });
            return true;
        }

        static void Swap(string newExe, string target)
        {
            string old = target + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(target, old);              // allowed while running
            try { File.Copy(newExe, target, true); }
            catch { File.Move(old, target); throw; } // roll back
        }

        /// <summary>Elevated helper mode: invoked as `QuantaRAM.exe --install-update "C:\path\QuantaRAM.exe"`.</summary>
        public static void RunInstallHelper(string target)
        {
            string self = Environment.ProcessPath;
            for (int i = 0; i < 30; i++)
            {
                try { Swap(self, target); break; }
                catch (IOException) when (i < 29) { Thread.Sleep(500); }
            }
            Process.Start(new ProcessStartInfo(target, "--restart --updated") { UseShellExecute = true });
        }

        /// <summary>Removes leftovers of a previous update.</summary>
        public static void Cleanup()
        {
            try
            {
                var old = Environment.ProcessPath + ".old";
                for (int i = 0; i < 10 && File.Exists(old); i++)
                {
                    try { File.Delete(old); } catch { Thread.Sleep(300); }
                }
                string dir = Path.Combine(Path.GetTempPath(), "QuantaRAM-update");
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir).Where(f => !string.Equals(f, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)))
                        try { File.Delete(f); } catch { }
            }
            catch { }
        }

        public static void OpenReleasePage(string url = null)
        {
            try { Process.Start(new ProcessStartInfo(url ?? ReleasesPage) { UseShellExecute = true }); } catch { }
        }
    }
}
