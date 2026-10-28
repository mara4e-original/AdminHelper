namespace AdminHelper;

public class DiskItem
{
    public string Name { get; set; } = "";
    public string Info { get; set; } = "";
    public double Percent { get; set; }
}

public class ServiceItem
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string StatusText { get; set; } = "";
    public string StartTypeText { get; set; } = "";
    public bool IsRunning { get; set; }
}

/// <summary>Результат эвристической проверки: 0 — норма, 1 — стоит проверить, 2 — подозрительно.</summary>
public record Verdict(int Level, string Reason)
{
    public static string Title(int level) => level switch
    {
        2 => "Подозрительно",
        1 => "Проверить",
        _ => "Норма"
    };
}

public class ProcItem
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double MemoryMb { get; set; }
    public string Path { get; set; } = "";
    public int Level { get; set; }
    public string Verdict => AdminHelper.Verdict.Title(Level);
    public string Reason { get; set; } = "";
    public string Details => string.IsNullOrEmpty(Reason) ? (string.IsNullOrEmpty(Path) ? "—" : Path) : Reason;
}

public class ConnItem
{
    public string Process { get; set; } = "";
    public int Pid { get; set; }
    public string Local { get; set; } = "";
    public string Remote { get; set; } = "";
    public string State { get; set; } = "";
    public bool IsEstablished { get; set; }
    public bool IsListening { get; set; }
}

public class AdapterItem
{
    public string Name { get; set; } = "";
    public string Info { get; set; } = "";
    public string Traffic { get; set; } = "";
}

public enum AutoKind { Registry, StartupFile, Winlogon }

public class AutoItem
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Target { get; set; } = "";
    public string Location { get; set; } = "";
    public int Level { get; set; }
    public bool IsMissing { get; set; }
    public string Verdict => IsMissing && Level < 2 ? "Нет файла" : AdminHelper.Verdict.Title(Level);
    public string Reason { get; set; } = "";
    public string ExePath { get; set; } = "";
    public AutoKind Kind { get; set; }
    public bool IsMachine { get; set; }
    public string RegSubKey { get; set; } = "";
    public string ValueName { get; set; } = "";

    private string Shown => string.IsNullOrEmpty(Target) ? Command : Target;
    public string Details => string.IsNullOrEmpty(Reason) ? Shown : $"{Reason}  ·  {Shown}";
}
