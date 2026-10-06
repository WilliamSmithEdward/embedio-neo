using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;

namespace EmbedIO.AppContainerSmoke;

// Test-only Win32 interop. Never used by shipped packages to change network policy.
[SupportedOSPlatform("windows")]
internal sealed class ContainerProfile : IDisposable
{
    private readonly IntPtr _sid;
    private bool _disposed;
    public string Name { get; } = "EmbedIONeoSmoke554." + Guid.NewGuid().ToString("N");
    public string Sid { get; }
    public string Folder { get; }

    public ContainerProfile()
    {
        Marshal.ThrowExceptionForHR(CreateAppContainerProfile(Name, Name, "Disposable EmbedIO network isolation fixture", IntPtr.Zero, 0, out _sid));
        Sid = new SecurityIdentifier(_sid).Value;
        try
        {
            Marshal.ThrowExceptionForHR(GetAppContainerFolderPath(Sid, out var folder));
            try { Folder = Marshal.PtrToStringUni(folder) ?? throw new InvalidOperationException("Missing profile path"); }
            finally { Marshal.FreeCoTaskMem(folder); }
        }
        catch
        {
            _ = DeleteAppContainerProfile(Name);
            FreeSid(_sid);
            throw;
        }
    }

    public Process Launch(string executable, int port)
    {
        var capabilitySids = new List<IntPtr>();
        IntPtr attributes = IntPtr.Zero, capabilities = IntPtr.Zero, security = IntPtr.Zero;
        var initialized = false;
        try
        {
            foreach (var kind in new[] { WellKnownSidType.WinCapabilityInternetClientSid, WellKnownSidType.WinCapabilityInternetClientServerSid, WellKnownSidType.WinCapabilityPrivateNetworkClientServerSid })
            {
                var sid = new SecurityIdentifier(kind, null);
                var bytes = new byte[sid.BinaryLength];
                sid.GetBinaryForm(bytes, 0);
                var memory = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                capabilitySids.Add(memory);
            }
            capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>() * capabilitySids.Count);
            for (var i = 0; i < capabilitySids.Count; i++)
                Marshal.StructureToPtr(new SidAndAttributes { Sid = capabilitySids[i], Attributes = 4 }, IntPtr.Add(capabilities, i * Marshal.SizeOf<SidAndAttributes>()), false);
            security = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities { Sid = _sid, Capabilities = capabilities, Count = (uint)capabilitySids.Count }, security, false);
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            initialized = true;
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20009, security, (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupInfoEx { Startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 1, ShowWindow = 0 }, Attributes = attributes };
            var command = new StringBuilder($"\"{executable}\" serve {port} \"{Folder}\" \"{Sid}\"");
            Check(CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x08000000, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out var child));
            try { return Process.GetProcessById((int)child.Id); }
            finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (capabilities != IntPtr.Zero) Marshal.FreeHGlobal(capabilities);
            if (security != IntPtr.Zero) Marshal.FreeHGlobal(security);
            foreach (var sid in capabilitySids) Marshal.FreeHGlobal(sid);
        }
    }

    public static string CurrentContainerSid()
    {
        Check(OpenProcessToken((IntPtr)(-1), 8, out var token));
        try
        {
            var buffer = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Check(GetTokenInformation(token, 29, buffer, 4, out _));
                if (Marshal.ReadInt32(buffer) != 1) throw new InvalidOperationException("Child is not an AppContainer");
                Check(GetTokenInformation(token, 31, buffer, (uint)IntPtr.Size, out _));
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Marshal.ThrowExceptionForHR(DeleteAppContainerProfile(Name)); }
        finally { FreeSid(_sid); }
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr Sid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCharacters, YCharacters, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint Id, ThreadId; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int GetAppContainerFolderPath(string sid, out IntPtr path);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, uint length, out uint needed);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, uint count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr needed);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
