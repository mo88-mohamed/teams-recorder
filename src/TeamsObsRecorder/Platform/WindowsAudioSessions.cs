using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TeamsObsRecorder.Platform;

/// <summary>
/// Lists the apps with an active audio stream, per direction, using the Windows Core Audio API
/// (the same data as the Volume Mixer). Each app is reported with its parent processes, e.g.
/// "msedgewebview2.exe < ms-teams.exe", because Teams can play and record audio from a helper
/// process; a pattern like *teams* then still matches.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAudioSessions
{
    public static List<string> ActiveApps(bool capture)
    {
        var pids = new HashSet<uint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            Check(enumerator.EnumAudioEndpoints(capture ? EDataFlow.Capture : EDataFlow.Render, DeviceStateActive,
                out var devices));
            Check(devices.GetCount(out var count));
            for (uint i = 0; i < count; i++)
            {
                Check(devices.Item(i, out var device));
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var managerObj) != 0) continue;
                var manager = (IAudioSessionManager2)managerObj;
                if (manager.GetSessionEnumerator(out var sessions) != 0) continue;
                Check(sessions.GetCount(out var sessionCount));
                for (var s = 0; s < sessionCount; s++)
                {
                    if (sessions.GetSession(s, out var control) != 0) continue;
                    if (control is not IAudioSessionControl2 control2) continue;
                    if (control2.GetState(out var state) != 0 || state != AudioSessionState.Active) continue;
                    if (control2.GetProcessId(out var pid) == 0 && pid != 0) pids.Add(pid);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        var tree = ProcessTree.Snapshot();
        return pids.Select(pid => tree.Describe(pid)).Where(n => n.Length > 0).Distinct().ToList();
    }

    private static void Check(int hr)
    {
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    private const uint DeviceStateActive = 1;
    private const int ClsctxAll = 23;

    private enum EDataFlow { Render = 0, Capture = 1 }

    private enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        // Remaining methods are not used; they come after this one in the vtable.
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out AudioSessionState state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(IntPtr groupingId, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
    }
}

/// <summary>Process names and parents from a Toolhelp snapshot.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessTree
{
    private readonly Dictionary<uint, (uint Parent, string Name)> _processes = new();

    public static ProcessTree Snapshot()
    {
        var tree = new ProcessTree();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return tree;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return tree;
            do tree._processes[entry.th32ProcessID] = (entry.th32ParentProcessID, entry.szExeFile);
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return tree;
    }

    /// <summary>"child.exe &lt; parent.exe &lt; grandparent.exe" (up to 4 levels).</summary>
    public string Describe(uint pid)
    {
        var names = new List<string>();
        var seen = new HashSet<uint>();
        while (names.Count < 4 && pid != 0 && seen.Add(pid) && _processes.TryGetValue(pid, out var p))
        {
            names.Add(p.Name);
            pid = p.Parent;
        }
        if (names.Count == 0)
        {
            try { names.Add(Process.GetProcessById((int)pid).ProcessName); } catch { /* exited */ }
        }
        return string.Join(" < ", names);
    }

    private const uint Th32csSnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
