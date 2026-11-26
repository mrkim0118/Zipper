using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Zipper
{
    /// <summary>Startup update check against the latest GitHub release: finds its Setup .exe, downloads it and starts it.</summary>
    static class GitHubUpdater
    {
        const string Repository = "mrkim0118/Zipper";

        public sealed class Release
        {
            public Release(string version, string setupUrl) { Version = version; SetupUrl = setupUrl; }
            public string Version { get; }
            public string SetupUrl { get; }
            public string SetupName => Uri.UnescapeDataString(SetupUrl.Substring(SetupUrl.LastIndexOf('/') + 1));
        }

        public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));

        /// <summary>Returns the latest release when it is newer than this build; null when up to date, offline or rate-limited.</summary>
        public static async Task<Release> FindNewerReleaseAsync()
        {
            try
            {
                string json;
                using (var http = CreateClient(TimeSpan.FromSeconds(10)))
                    json = await http.GetStringAsync("https://api.github.com/repos/" + Repository + "/releases/latest");

                var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!tag.Success || !Version.TryParse(tag.Groups[1].Value.TrimStart('v', 'V'), out var latest) || Normalize(latest) <= CurrentVersion)
                    return null;

                foreach (Match url in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
                {
                    var release = new Release(Normalize(latest).ToString(), url.Groups[1].Value);
                    if (release.SetupName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && release.SetupName.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0)
                        return release;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                // The check is best effort: never bother the user when it cannot run.
            }
            return null;
        }

        /// <summary>Downloads the setup to %TEMP% and starts it. The caller closes the app so the installer can replace its files.</summary>
        public static async Task DownloadAndStartSetupAsync(Release release, IProgress<int> progress = null)
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetFileName(release.SetupName));
            using (var http = CreateClient(TimeSpan.FromMinutes(30)))
            using (var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                using (var source = await response.Content.ReadAsStreamAsync())
                using (var target = File.Create(path))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await target.WriteAsync(buffer, 0, read);
                        received += read;
                        if (total > 0) progress?.Report((int)(received * 100 / total));
                    }
                }
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        static HttpClient CreateClient(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Zipper-Updater");
            return http;
        }

        static Version Normalize(Version version) => new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
