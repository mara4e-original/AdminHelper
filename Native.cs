using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace AdminHelper;

internal readonly record struct TcpRow(uint State, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int Pid);

internal static class Native
{
    // ---------- Путь к exe по PID (работает и без прав администратора для большинства процессов) ----------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder text, ref int size);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static string? GetProcessPath(int pid)
    {
        if (pid <= 4) return null;
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    // ---------- TCP-таблица с PID владельца ----------

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, uint reserved);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;

    public static List<TcpRow> GetTcpTable()
    {
        var result = new List<TcpRow>();
        int size = 0;
        uint r = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);

        for (int attempt = 0; attempt < 5 && size > 0; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                r = GetExtendedTcpTable(buf, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                if (r == ErrorInsufficientBuffer) continue;
                if (r != 0) return result;

                int count = Marshal.ReadInt32(buf);
                IntPtr row = buf + 4;
                for (int i = 0; i < count; i++)
                {
                    uint state = (uint)Marshal.ReadInt32(row, 0);
                    int la = Marshal.ReadInt32(row, 4);
                    int lp = Marshal.ReadInt32(row, 8);
                    int ra = Marshal.ReadInt32(row, 12);
                    int rp = Marshal.ReadInt32(row, 16);
                    int pid = Marshal.ReadInt32(row, 20);
                    result.Add(new TcpRow(
                        state,
                        new IPAddress((long)(uint)la), SwapPort(lp),
                        new IPAddress((long)(uint)ra), SwapPort(rp),
                        pid));
                    row += 24;
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return result;
    }

    private static int SwapPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    // ---------- Оформление заголовка окна в тон приложению (Windows 10/11) ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void StyleTitleBar(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));          // тёмный режим заголовка
            int caption = 0x00080A2B;                                         // COLORREF 0x00BBGGRR = #2B0A08
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));        // цвет заголовка (Windows 11)
            int text = 0x00E9F6FF;                                            // #FFF6E9
            DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));           // цвет текста заголовка (Windows 11)
        }
        catch
        {
            // старые версии Windows просто покажут обычный заголовок
        }
    }
}
