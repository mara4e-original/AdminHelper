using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AdminHelper;

/// <summary>
/// Эвристики для поиска подозрительного. Это НЕ антивирусные сигнатуры: результат — повод посмотреть внимательнее,
/// а не доказательство заражения.
/// </summary>
public static class Analyzer
{
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string Sys32 = Path.Combine(WinDir, "System32");
    private static readonly string SysWow = Path.Combine(WinDir, "SysWOW64");
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    /// <summary>Системные процессы, которые маскируют чаще всего, и папка, где они должны лежать.</summary>
    private static readonly Dictionary<string, string> SystemProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["svchost"] = Sys32, ["lsass"] = Sys32, ["csrss"] = Sys32, ["winlogon"] = Sys32,
        ["services"] = Sys32, ["smss"] = Sys32, ["wininit"] = Sys32, ["spoolsv"] = Sys32,
        ["taskhostw"] = Sys32, ["conhost"] = Sys32, ["dwm"] = Sys32, ["fontdrvhost"] = Sys32,
        ["sihost"] = Sys32, ["ctfmon"] = Sys32, ["runtimebroker"] = Sys32, ["searchindexer"] = Sys32,
        ["explorer"] = WinDir,
    };

    private static readonly string[] RiskyFolders =
    {
        @"\Temp\", @"\Downloads\", @"\Users\Public\", @"\$Recycle.Bin\", @"\AppData\Local\Temp\"
    };

    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "bitsadmin", "certutil", "cmd"
    };

    private static readonly string[] NastyArgs =
    {
        "-enc", "-encodedcommand", "-w hidden", "-windowstyle hidden", "downloadstring", "downloadfile",
        "invoke-expression", "iex ", "http://", "https://", "frombase64string"
    };

    private static readonly Regex DoubleExt = new(
        @"\.(pdf|docx?|xlsx?|pptx?|jpe?g|png|gif|txt|zip|rar|mp3|mp4)\.(exe|scr|com|bat|cmd|pif|vbs|js)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---------- Подпись файла ----------

    private static readonly Dictionary<string, bool> SigCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True, если у файла есть встроенная цифровая подпись (цепочку доверия не проверяем).</summary>
    public static bool HasSignature(string path)
    {
        lock (SigCache)
        {
            if (SigCache.TryGetValue(path, out bool cached)) return cached;
        }

        bool signed;
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(path);
            signed = true;
        }
        catch
        {
            signed = false;
        }

        lock (SigCache) SigCache[path] = signed;
        return signed;
    }

    // ---------- Общая проверка пути ----------

    private static bool IsTrustedFolder(string path) =>
        path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(ProgramFiles, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(ProgramFilesX86, StringComparison.OrdinalIgnoreCase);

    private static int AnalyzePath(string path, List<string> reasons)
    {
        int level = 0;

        if (DoubleExt.IsMatch(path))
        {
            level = 2;
            reasons.Add("двойное расширение (файл выдаёт себя за документ)");
        }

        if (IsTrustedFolder(path)) return level;

        bool risky = RiskyFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));
        bool appData = path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase);
        if (!risky && !appData) return level;

        bool signed = File.Exists(path) && HasSignature(path);

        if (risky)
        {
            level = Math.Max(level, 1);
            reasons.Add("запущен из временной или пользовательской папки");
            if (!signed)
            {
                level = 2;
                reasons.Add("нет цифровой подписи");
            }
        }
        else if (!signed)
        {
            level = Math.Max(level, 1);
            reasons.Add("лежит в AppData без цифровой подписи");
        }

        return level;
    }

    // ---------- Процессы ----------

    public static Verdict AnalyzeProcess(string name, string? path)
    {
        var reasons = new List<string>();
        int level = 0;
        string bare = Path.GetFileNameWithoutExtension(name);

        if (SystemProcs.TryGetValue(bare, out string? expected))
        {
            if (path != null)
            {
                string dir = Path.GetDirectoryName(path) ?? "";
                bool ok = dir.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                          (expected == Sys32 && dir.Equals(SysWow, StringComparison.OrdinalIgnoreCase));
                if (!ok)
                {
                    level = 2;
                    reasons.Add("системное имя, но файл лежит не в папке Windows");
                }
            }
        }
        else
        {
            var (match, homoglyph) = FindLookalike(bare);
            if (match != null)
            {
                level = Math.Max(level, homoglyph ? 2 : 1);
                reasons.Add($"имя похоже на системный процесс «{match}»");
            }
        }

        if (path != null)
            level = Math.Max(level, AnalyzePath(path, reasons));

        return new Verdict(level, string.Join("; ", reasons));
    }

    private static (string? match, bool homoglyph) FindLookalike(string bare)
    {
        if (bare.Length < 5) return (null, false);

        string lower = bare.ToLowerInvariant();
        string normalized = lower.Replace('0', 'o').Replace('1', 'l');

        foreach (string sys in SystemProcs.Keys)
        {
            if (lower == sys) return (null, false);
            if (normalized == sys) return (sys, true);
            if (Math.Abs(lower.Length - sys.Length) <= 1 && Levenshtein(lower, sys) == 1) return (sys, false);
        }
        return (null, false);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ---------- Автозагрузка ----------

    public static string ExtractExe(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.Length == 0) return "";

        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : command[1..];
        }

        int idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return command[..(idx + 4)];

        return command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    /// <summary>True, если команда указывает на конкретный файл, которого точно нет на диске.</summary>
    public static bool IsMissingFile(string command)
    {
        string exe = ExtractExe(command);
        if (!Path.IsPathRooted(exe)) return false;

        bool quoted = command.TrimStart().StartsWith('"');
        if (!quoted && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

        // если диск сейчас недоступен (флешка, сетевой путь), судить нельзя
        string? root = Path.GetPathRoot(exe);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;

        return !File.Exists(exe) && !Directory.Exists(exe);
    }

    private static string? ResolveShortcut(string lnk)
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            Marshal.FinalReleaseComObject(sc);
            Marshal.FinalReleaseComObject(shell);
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    private static Verdict AnalyzeCommand(string command)
    {
        var reasons = new List<string>();
        int level = 0;

        string exe = ExtractExe(command);
        string exeName = Path.GetFileNameWithoutExtension(exe);
        string lowerCmd = command.ToLowerInvariant();

        if (ScriptHosts.Contains(exeName))
        {
            level = 1;
            reasons.Add("запускает интерпретатор или системную утилиту");
            if (NastyArgs.Any(a => lowerCmd.Contains(a)))
            {
                level = 2;
                reasons.Add("скрытые или сетевые параметры запуска");
            }
        }

        if (IsMissingFile(command))
        {
            level = Math.Max(level, 1);
            reasons.Add("файл не найден, запись можно удалить");
        }
        else if (Path.IsPathRooted(exe) && File.Exists(exe))
        {
            level = Math.Max(level, AnalyzePath(exe, reasons));
        }

        return new Verdict(level, string.Join("; ", reasons));
    }

    public static List<AutoItem> ScanStartup()
    {
        var items = new List<AutoItem>();

        // Ключи реестра
        var keys = new (RegistryKey Root, bool Machine, string Sub, string Label)[]
        {
            (Registry.CurrentUser,  false, @"Software\Microsoft\Windows\CurrentVersion\Run",     "Реестр · Run (пользователь)"),
            (Registry.CurrentUser,  false, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "Реестр · RunOnce (пользователь)"),
            (Registry.LocalMachine, true,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",     "Реестр · Run (все пользователи)"),
            (Registry.LocalMachine, true,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "Реестр · RunOnce (все)"),
            (Registry.LocalMachine, true,  @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Реестр · Run (32-бит)"),
        };

        foreach (var (root, machine, sub, label) in keys)
        {
            try
            {
                using var key = root.OpenSubKey(sub);
                if (key == null) continue;

                foreach (string valueName in key.GetValueNames())
                {
                    if (valueName.Length == 0) continue;
                    string command = key.GetValue(valueName)?.ToString() ?? "";
                    var v = AnalyzeCommand(command);
                    items.Add(new AutoItem
                    {
                        Name = valueName,
                        Command = command,
                        Location = label,
                        Level = v.Level,
                        Reason = v.Reason,
                        ExePath = ExtractExe(command),
                        IsMissing = IsMissingFile(command),
                        Kind = AutoKind.Registry,
                        IsMachine = machine,
                        RegSubKey = sub,
                        ValueName = valueName
                    });
                }
            }
            catch
            {
                // нет доступа к ключу — пропускаем
            }
        }

        // Папки автозагрузки
        var folders = new (string Path, string Label)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup),       "Папка автозагрузки (пользователь)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Папка автозагрузки (все)"),
        };

        foreach (var (folder, label) in folders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    var reasons = new List<string>();
                    int level = 0;
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    string target = "";
                    bool missing = false;
                    string exePath = file;

                    if (ext == ".lnk")
                    {
                        string? t = ResolveShortcut(file);
                        if (!string.IsNullOrEmpty(t))
                        {
                            target = Environment.ExpandEnvironmentVariables(t);
                            string cmd = $"\"{target}\"";
                            var v = AnalyzeCommand(cmd);
                            level = v.Level;
                            if (v.Reason.Length > 0) reasons.Add(v.Reason);
                            missing = IsMissingFile(cmd);
                            if (File.Exists(target)) exePath = target;
                        }
                    }
                    else if (ext is ".bat" or ".cmd" or ".vbs" or ".js" or ".ps1" or ".wsf" or ".hta")
                    {
                        level = 1;
                        reasons.Add("скрипт лежит прямо в папке автозагрузки");
                    }
                    else if (ext is ".exe" or ".scr" or ".com")
                    {
                        level = Math.Max(level, AnalyzePath(file, reasons));
                        if (level == 0)
                        {
                            level = 1;
                            reasons.Add("программа лежит прямо в папке автозагрузки");
                        }
                    }

                    items.Add(new AutoItem
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        Command = file,
                        Target = target,
                        Location = label,
                        Level = level,
                        Reason = string.Join("; ", reasons),
                        ExePath = exePath,
                        IsMissing = missing,
                        Kind = AutoKind.StartupFile
                    });
                }
            }
            catch
            {
            }
        }

        // Winlogon: Shell и Userinit — любимое место закрепления вредоносов
        try
        {
            using var wl = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            if (wl != null)
            {
                string shell = wl.GetValue("Shell")?.ToString() ?? "";
                if (shell.Length > 0 && !shell.Trim().Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new AutoItem
                    {
                        Name = "Winlogon · Shell",
                        Command = shell,
                        Location = "Реестр · Winlogon",
                        Level = 2,
                        Reason = "оболочка Windows изменена (должна быть explorer.exe)",
                        ExePath = ExtractExe(shell),
                        Kind = AutoKind.Winlogon
                    });
                }

                string userinit = wl.GetValue("Userinit")?.ToString() ?? "";
                var parts = userinit.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Any(p => !Path.GetFileName(p).Equals("userinit.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(new AutoItem
                    {
                        Name = "Winlogon · Userinit",
                        Command = userinit,
                        Location = "Реестр · Winlogon",
                        Level = 2,
                        Reason = "кроме userinit.exe запускается что-то ещё",
                        ExePath = parts.FirstOrDefault(p => !Path.GetFileName(p).Equals("userinit.exe", StringComparison.OrdinalIgnoreCase)) ?? "",
                        Kind = AutoKind.Winlogon
                    });
                }
            }
        }
        catch
        {
        }

        return items
            .OrderByDescending(i => i.Level)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
