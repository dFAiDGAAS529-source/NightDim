using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NightDim.Core;

namespace NightDim.Windows;

public sealed class DisplayManager
{
    private const int WmDisplayChange = 0x007E;
    private const int WmDeviceChange = 0x0219;
    private const int WmDpiChanged = 0x02E0;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private static readonly string SessionIdPrefix = $"session:{Guid.NewGuid():N}:";

    public event EventHandler? DisplaysChanged;

    public void HandleWindowMessage(int message, nint wParam)
    {
        if (message is WmDisplayChange or WmDpiChanged ||
            message == WmDeviceChange && wParam is DbtDevNodesChanged or DbtDeviceArrival or DbtDeviceRemoveComplete)
        {
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<DisplayInfo> Enumerate()
    {
        using var dpi = DpiContext.PerMonitorV2();
        var sources = ReadActiveSources();
        var displays = new List<DisplayInfo>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info) || string.IsNullOrEmpty(info.DeviceName))
                return true;

            var bounds = info.MonitorBounds;
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
                return true;

            var source = sources.GetValueOrDefault(info.DeviceName);
            var id = source?.PersistentId ?? SessionIdPrefix + info.DeviceName.ToUpperInvariant();
            var name = string.IsNullOrWhiteSpace(source?.Name) ? info.DeviceName : source.Name;
            displays.Add(new DisplayInfo(id, info.DeviceName, name, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, (info.Flags & 1) != 0,
                source?.Hdr ?? HdrState.Unknown, source?.CanRecoverGamma ?? false));
            return true;
        }, 0);

        // Some virtual drivers report one interface path for several desktop sources.
        // Avoid ambiguous recovery or an ID collision in the per-display settings.
        var duplicates = displays.GroupBy(display => display.Id)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        return displays.Select(display => duplicates.Contains(display.Id)
                ? display with { Id = SessionIdPrefix + display.DeviceName.ToUpperInvariant(), CanRecoverGamma = false }
                : display)
            .OrderByDescending(display => display.IsPrimary).ThenBy(display => display.X).ThenBy(display => display.Y)
            .ToArray();
    }

    internal static string? ResolveGammaDevice(string displayId)
    {
        string? device = null;
        foreach (var (deviceName, source) in ReadActiveSources())
        {
            if (source.PersistentId != displayId)
                continue;
            if (device is not null || !source.CanRecoverGamma)
                return null;
            device = deviceName;
        }

        return device;
    }

    private static Dictionary<string, DesktopSource> ReadActiveSources()
    {
        const uint onlyActivePaths = 2;
        const int insufficientBuffer = 122;
        var groupedTargets = new Dictionary<string, List<Target>>(StringComparer.OrdinalIgnoreCase);

        // Windows may change the number of paths between the two calls.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(onlyActivePaths, out var pathCount, out var modeCount) != 0 ||
                pathCount == 0)
            {
                break;
            }

            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            var result = QueryDisplayConfig(onlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0);
            if (result == insufficientBuffer)
                continue;
            if (result != 0)
                break;

            foreach (var path in paths.AsSpan(0, (int)pathCount))
            {
                var sourceName = new SourceDeviceName
                {
                    Header = DeviceInfoHeader.Create<SourceDeviceName>(1, path.Source.AdapterId, path.Source.Id)
                };
                if (GetSourceDeviceName(ref sourceName) != 0 || string.IsNullOrEmpty(sourceName.DeviceName))
                    continue;

                if (!groupedTargets.TryGetValue(sourceName.DeviceName, out var targets))
                {
                    targets = [];
                    groupedTargets.Add(sourceName.DeviceName, targets);
                }

                targets.Add(ReadTarget(path.Target));
            }

            break;
        }

        return groupedTargets.ToDictionary(entry => entry.Key, entry => DesktopSource.Create(entry.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Target ReadTarget(PathTarget target)
    {
        var name = new TargetDeviceName
        {
            Header = DeviceInfoHeader.Create<TargetDeviceName>(2, target.AdapterId, target.Id)
        };
        var named = GetTargetDeviceName(ref name) == 0;
        var (hdr, plainSdr) = ReadColorMode(target);
        return new Target(named ? name.DevicePath : null, named ? name.FriendlyName : null, hdr, plainSdr);
    }

    private static (HdrState Hdr, bool PlainSdr) ReadColorMode(PathTarget target)
    {
        var info = new AdvancedColorInfo2
        {
            Header = DeviceInfoHeader.Create<AdvancedColorInfo2>(15, target.AdapterId, target.Id)
        };
        if (GetAdvancedColorInfo2(ref info) == 0)
        {
            return info.ActiveColorMode switch
            {
                0 => (HdrState.Sdr, true),
                1 => (HdrState.Sdr, false), // Wide color gamut with Advanced Color composition.
                2 => (HdrState.Hdr, false),
                _ => (HdrState.Unknown, false)
            };
        }

        var legacy = new AdvancedColorInfo
        {
            Header = DeviceInfoHeader.Create<AdvancedColorInfo>(9, target.AdapterId, target.Id)
        };
        // Older Windows exposes whether Advanced Color is enabled, which can mean HDR or SDR ACM.
        return GetAdvancedColorInfo(ref legacy) == 0 && (legacy.Flags & 2) == 0
            ? (HdrState.Sdr, true)
            : (HdrState.Unknown, false);
    }

    private sealed record Target(string? Path, string? Name, HdrState Hdr, bool PlainSdr);

    private sealed record DesktopSource(string? PersistentId, string Name, HdrState Hdr, bool CanRecoverGamma)
    {
        public static DesktopSource Create(List<Target> targets)
        {
            // Clone paths share one GDI source and a single desktop rectangle.
            var paths = targets.Select(target => target.Path?.ToUpperInvariant()).ToArray();
            var id = paths.All(path => !string.IsNullOrWhiteSpace(path))
                ? "monitor:" + string.Join('|', paths.Order(StringComparer.Ordinal))
                : null;
            var name = string.Join(" / ", targets.Select(target => target.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));
            var hdr = targets.Any(target => target.Hdr == HdrState.Hdr) ? HdrState.Hdr
                : targets.All(target => target.Hdr == HdrState.Sdr) ? HdrState.Sdr : HdrState.Unknown;
            return new DesktopSource(id, name, hdr, id is not null && targets.Count == 1 && targets[0].PlainSdr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorBounds;
        public NativeRect WorkBounds;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSource
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTarget
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public int OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public PathSource Source;
        public PathTarget Target;
        public uint Flags;
    }

    // DISPLAYCONFIG_MODE_INFO has a 48-byte union aligned to 8 bytes. The modes
    // are required output storage for QueryDisplayConfig, but are not read here.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        [FieldOffset(16)] public ulong UnionAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;

        public static DeviceInfoHeader Create<T>(uint type, Luid adapter, uint id) where T : struct =>
            new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), AdapterId = adapter, Id = id };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public int OutputTechnology;
        public ushort EdidManufacturerId;
        public ushort EdidProductCode;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint Encoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo2
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint Encoding;
        public uint BitsPerColorChannel;
        public uint ActiveColorMode;
    }

    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint dc, nint clippingRect, MonitorCallback callback, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
        ref uint modeCount, [Out] ModeInfo[] modes, nint currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceDeviceName(ref SourceDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetDeviceName(ref TargetDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetAdvancedColorInfo(ref AdvancedColorInfo request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetAdvancedColorInfo2(ref AdvancedColorInfo2 request);
}
