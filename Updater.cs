using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdminHelper;

public record UpdateInfo(
    Version Version, string Tag, string Notes, string PageUrl,
    string? AssetUrl, string? AssetName, long AssetSize, string? Sha256);

/// <summary>Проверка обновлений через GitHub Releases (как в MPC-HC: скачать установщик и запустить).</summary>
public static class Updater
{
    // ВАЖНО: впишите сюда свой репозиторий в формате "ник/репозиторий"
    public const string Repo = "mara4e-original/AdminHelper";

    // Имя файла установщика в релизе (его делает build-installer.bat)
    private const string AssetSuffix = "-Setup.exe";

    public static bool IsConfigured => !Repo.Contains("ВАШ_НИК");

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(15));
    private static readonly HttpClient DownloadHttp = CreateClient(Timeout.InfiniteTimeSpan);

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AdminHelper-Updater", CurrentVersion.ToString(3)));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Возвращает последний релиз или null, если релизов ещё нет.</summary>
    public static async Task<UpdateInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        string clean = tag.TrimStart('v', 'V');
        int dash = clean.IndexOf('-');
        if (dash > 0) clean = clean[..dash];
        if (!Version.TryParse(clean, out var ver)) return null;

        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

        string? url = null, name = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string n = a.GetProperty("name").GetString() ?? "";
                if (!n.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                name = n;
                url = a.GetProperty("browser_download_url").GetString();
                size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    var dg = d.GetString() ?? "";
                    if (dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = dg[7..];
                }
                break;
            }
        }

        return new UpdateInfo(ver, tag, notes, page, url, name, size, sha);
    }

    public static bool IsNewer(UpdateInfo info) => info.Version > CurrentVersion;

    /// <summary>Скачивает установщик во временную папку. progress: 0..100.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.AssetUrl) || string.IsNullOrEmpty(info.AssetName))
            throw new InvalidOperationException("В релизе нет файла установщика (*-Setup.exe).");

        // скачиваем только с GitHub по https
        var uri = new Uri(info.AssetUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || !(uri.Host.EndsWith("github.com") || uri.Host.EndsWith("githubusercontent.com")))
            throw new InvalidOperationException("Недоверенный адрес загрузки.");

        string dir = Path.Combine(Path.GetTempPath(), "AdminHelper-Update");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, info.AssetName);

        using (var resp = await DownloadHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.AssetSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(done * 100.0 / total);
            }
        }

        if (!string.IsNullOrEmpty(info.Sha256))
        {
            await using var fs = File.OpenRead(file);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(file); } catch { }
                throw new InvalidOperationException("Контрольная сумма файла не совпала. Установка отменена.");
            }
        }

        return file;
    }

    /// <summary>Запускает установщик; программа должна после этого закрыться.</summary>
    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path, "/SILENT /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true
        });
    }
}
