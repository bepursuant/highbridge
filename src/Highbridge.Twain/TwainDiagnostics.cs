using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NTwain;
using NTwain.Data;

namespace Highbridge.Twain;

/// <summary>Text descriptions of the TWAIN setup, for the log and the "diagnose scanner" report.</summary>
[SupportedOSPlatform("windows")]
internal static class TwainDiagnostics
{
    /// <summary>Env var read by TWAINDSM.dll (not the old twain_32.dll) when it loads; it then logs every TWAIN call.</summary>
    public const string DsmLogVariable = "TWAINDSM_LOG";

    private static readonly PropertyInfo? IdentityProperty =
        typeof(DataSource).GetProperty("Identity", BindingFlags.Instance | BindingFlags.NonPublic);

    public static string DescribeEnvironment()
    {
        var info = PlatformInfo.Current;
        string newDsm = Path.Combine(Environment.SystemDirectory, "TWAINDSM.dll");
        string oldDsm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "twain_32.dll");
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{(Environment.Is64BitProcess ? "64" : "32")}-bit process on {RuntimeInformation.OSDescription} ({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit OS). ");
        text.Append(CultureInfo.InvariantCulture, $"TWAIN manager in use: {info.ExpectedDsmPath} ({(info.UseNewWinDSM ? "TWAINDSM, TWAIN 2" : "old twain_32.dll, TWAIN 1.x")}), exists: {info.DsmExists}. ");
        text.Append(CultureInfo.InvariantCulture, $"Candidates: {DescribeFile(newDsm)}; {DescribeFile(oldDsm)}. ");
        string? dsmLog = Environment.GetEnvironmentVariable(DsmLogVariable);
        text.Append(dsmLog is null ? "TWAINDSM log: off." : $"TWAINDSM log (TWAINDSM.dll only): {dsmLog}.");
        return text.ToString();
    }

    /// <summary>Installed 32/64-bit data sources (*.ds) under %WINDIR%\twain_32 or twain_64.</summary>
    public static IReadOnlyList<string> InstalledDataSources()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.Is64BitProcess ? "twain_64" : "twain_32");
        if (!Directory.Exists(folder))
        {
            return [$"{folder} does not exist"];
        }

        try
        {
            var files = Directory.EnumerateFiles(folder, "*.ds", SearchOption.AllDirectories).Select(DescribeFile).ToList();
            return files.Count > 0 ? files : [$"no *.ds files in {folder}"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"could not list {folder}: {ex.Message}"];
        }
    }

    /// <summary>TWAIN managers and data sources loaded into this process; after MSG_OPENDS this names the source's .ds file.</summary>
    public static IReadOnlyList<string> LoadedTwainModules()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.Modules.Cast<ProcessModule>()
                .Where(m => m.FileName is { } f
                    && (f.EndsWith(".ds", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(f).Equals("twain_32.dll", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(f).Equals("TWAINDSM.dll", StringComparison.OrdinalIgnoreCase)))
                .Select(m => DescribeFile(m.FileName))
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return [$"could not list loaded modules: {ex.Message}"];
        }
    }

    public static bool IsWiaCompatibilityLayer(DataSource source) =>
        source.Name.StartsWith("WIA-", StringComparison.OrdinalIgnoreCase);

    public static string DescribeSource(DataSource source)
    {
        var v = source.Version;
        string functionalities = IdentityProperty?.GetValue(source) is TWIdentity id ? id.DataFunctionalities.ToString() : "unknown";
        string kind = IsWiaCompatibilityLayer(source)
            ? "Windows WIA-TWAIN compatibility layer (wiatwain.ds), not a vendor TWAIN driver"
            : "vendor TWAIN driver";
        return $"'{source.Name}' (id {source.Id}): manufacturer '{source.Manufacturer}', family '{source.ProductFamily}', " +
            $"version {v.Major}.{v.Minor} '{v.Info}', TWAIN protocol {source.ProtocolVersion}, groups {source.DataGroup}, " +
            $"functionalities {functionalities}; {kind}";
    }

    /// <summary>The condition code (TWCC) for the last failed call to the source; read it right after the failure.</summary>
    public static string SourceStatus(DataSource source)
    {
        try
        {
            var status = source.GetStatus();
            return $"TWCC {status.ConditionCode} ({(int)status.ConditionCode})";
        }
        catch (Exception ex)
        {
            return $"TWCC unavailable ({ex.GetType().Name})";
        }
    }

    public static string ManagerStatus(TwainSession session)
    {
        try
        {
            var status = session.GetStatus();
            return $"TWCC {status.ConditionCode} ({(int)status.ConditionCode})";
        }
        catch (Exception ex)
        {
            return $"TWCC unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>Every capability the source reports (or a list of common ones), with what it supports and its values.</summary>
    public static void DumpCapabilities(DataSource source, StringBuilder report)
    {
        var caps = source.Capabilities;
        List<CapabilityId> ids;
        try
        {
            ids = caps.CapSupportedCaps.IsSupported ? caps.CapSupportedCaps.GetValues().ToList() : [];
        }
        catch (Exception ex)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  CAP_SUPPORTEDCAPS failed: {ex.Message}");
            ids = [];
        }

        if (ids.Count == 0)
        {
            report.AppendLine("  CAP_SUPPORTEDCAPS is empty or unsupported; probing common capabilities instead.");
            ids = [.. CommonCaps];
        }

        foreach (var id in ids.Distinct())
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {id} (0x{(ushort)id:X4}): {DescribeCapability(source, id)}");
        }
    }

    public static string DescribeCapability(DataSource source, CapabilityId id)
    {
        var caps = source.Capabilities;
        string supports;
        try
        {
            supports = caps.QuerySupport(id)?.ToString() ?? "MSG_QUERYSUPPORT not answered";
        }
        catch (Exception ex)
        {
            supports = $"MSG_QUERYSUPPORT threw {ex.GetType().Name}";
        }

        string values;
        try
        {
            var raw = caps.GetValuesRaw(id);
            values = raw.ContainerType switch
            {
                ContainerType.OneValue => $"one value {Format(id, raw.OneValue)}",
                ContainerType.Enum => $"enum [{FormatList(id, raw.CollectionValues)}], current #{raw.EnumCurrentIndex} {FormatAt(id, raw.CollectionValues, raw.EnumCurrentIndex)}, default #{raw.EnumDefaultIndex} {FormatAt(id, raw.CollectionValues, raw.EnumDefaultIndex)}",
                ContainerType.Array => $"array [{FormatList(id, raw.CollectionValues)}]",
                ContainerType.Range => $"range {Format(id, raw.RangeMinValue)}..{Format(id, raw.RangeMaxValue)} step {Format(id, raw.RangeStepSize)}, current {Format(id, raw.RangeCurrentValue)}, default {Format(id, raw.RangeDefaultValue)}",
                _ => $"MSG_GET failed ({SourceStatus(source)})",
            };
        }
        catch (Exception ex)
        {
            values = $"MSG_GET threw {ex.GetType().Name}: {ex.Message}";
        }

        return $"{values}; supports {supports}";
    }

    /// <summary>Visible top-level windows of this process, to spot a driver dialog waiting for the user.</summary>
    public static IReadOnlyList<string> VisibleProcessWindows()
    {
        var windows = new List<string>();
        uint pid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            uint thread = GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == pid && IsWindowVisible(hwnd))
            {
                var title = new StringBuilder(256);
                var cls = new StringBuilder(256);
                _ = GetWindowText(hwnd, title, title.Capacity);
                _ = GetClassName(hwnd, cls, cls.Capacity);
                windows.Add($"'{title}' (class {cls}, native thread {thread})");
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public static string Format(CapabilityId id, object? value)
    {
        if (value is null)
        {
            return "null";
        }

        try
        {
            object? named = id switch
            {
                CapabilityId.ICapXferMech => (XferMech)Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                CapabilityId.ICapPixelType => (PixelType)Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                CapabilityId.ICapUnits => (Unit)Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                CapabilityId.ICapPixelFlavor => (PixelFlavor)Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                CapabilityId.ICapCompression => (CompressionType)Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                CapabilityId.CapDuplex => (Duplex)Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                CapabilityId.CapSupportedCaps => (CapabilityId)Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                _ => null,
            };
            return named?.ToString() ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }
    }

    private static string FormatList(CapabilityId id, IList<object>? values)
    {
        if (values is null)
        {
            return string.Empty;
        }

        const int max = 40;
        string list = string.Join(", ", values.Take(max).Select(v => Format(id, v)));
        return values.Count > max ? $"{list}, ... ({values.Count} values)" : list;
    }

    private static string FormatAt(CapabilityId id, IList<object>? values, int index) =>
        values is not null && index >= 0 && index < values.Count ? $"({Format(id, values[index])})" : "(out of range)";

    private static string DescribeFile(string path)
    {
        if (!File.Exists(path))
        {
            return $"{path} (missing)";
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return $"{path} (version {info.FileVersion ?? "?"}, '{info.CompanyName}', '{info.FileDescription}')";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{path} ({ex.Message})";
        }
    }

    private static readonly CapabilityId[] CommonCaps =
    [
        CapabilityId.ICapXferMech, CapabilityId.ICapPixelType, CapabilityId.ICapBitDepth, CapabilityId.ICapUnits,
        CapabilityId.ICapXResolution, CapabilityId.ICapYResolution, CapabilityId.ICapPixelFlavor, CapabilityId.ICapCompression,
        CapabilityId.CapFeederEnabled, CapabilityId.CapFeederLoaded, CapabilityId.CapAutoFeed, CapabilityId.CapAutoScan,
        CapabilityId.CapDuplex, CapabilityId.CapDuplexEnabled, CapabilityId.CapXferCount, CapabilityId.CapIndicators,
        CapabilityId.CapUIControllable, CapabilityId.CapDeviceOnline, CapabilityId.CapPaperDetectable,
    ];

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
}
