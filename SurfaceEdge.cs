using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SurfaceEdge
{
    internal static class Program
    {
        internal static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfaceEdge");
        internal static string LogPath { get { return Path.Combine(DataDir, "SurfaceEdge.log"); } }
        private static readonly object LogLock = new object();

        internal static void Log(string text)
        {
            lock (LogLock)
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 262144) File.Delete(LogPath);
                    File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " " + text + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Directory.CreateDirectory(DataDir);
            if (args.Contains("--self-test") || args.Contains("--verify-controls") || args.Contains("--verify-osd") || args.Contains("--verify-haptics"))
                return Tests.Run(args.Contains("--verify-controls"), args.Contains("--verify-osd"), args.Contains("--verify-haptics"));
            bool created;
            using (var mutex = new Mutex(true, "Local\\SurfaceEdge.SingleInstance", out created))
            {
                if (!created) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { Log(e.Exception.ToString()); };
                try
                {
                    using (var app = new EdgeApp(args.Contains("--diagnose"))) Application.Run(app);
                    return 0;
                }
                catch (Exception e)
                {
                    Log(e.ToString());
                    MessageBox.Show(e.Message + "\n\n" + LogPath, "SurfaceEdge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
        }
    }

    internal sealed class Frame
    {
        internal int Count, Id;
        internal bool Tip, Confident, Click;
        internal double X, Y;
    }

    // A gesture must BEGIN at an edge. Multi-touch, clicks and leaving the strip
    // cancel it until all fingers lift; entering from the middle does not arm it.
    internal sealed class Gesture
    {
        // Fixed geometry: the outer 5% of the pad arms a swipe, and the finger may
        // drift 2.5% further inward before the gesture is dropped.
        internal const double Edge = .05, Exit = .025, Speed = .5, Arm = .025;
        private bool down, blocked, active;
        private int id, side;
        private double startX, startY, previousY;
        private long last;
        internal void Reset() { down = blocked = active = false; side = 0; last = 0; }
        internal int Side { get { return side; } }
        internal bool Active { get { return active; } }

        internal double Feed(Frame f, long now)
        {
            if (last != 0 && now - last > 350) Reset();
            last = now;
            if (f.Count == 0 || (f.Count == 1 && !f.Tip)) { Reset(); return 0; }
            if (f.Count != 1 || !f.Confident || f.Click) { down = blocked = true; active = false; return 0; }
            if (blocked) return 0;
            if (!down)
            {
                down = true; id = f.Id; startX = f.X; startY = previousY = f.Y;
                side = f.X <= Edge ? -1 : (f.X >= 1 - Edge ? 1 : 0);
                blocked = side == 0;
                return 0;
            }
            if (f.Id != id || (side < 0 ? f.X > Edge + Exit : f.X < 1 - Edge - Exit))
            { blocked = true; active = false; return 0; }
            if (!active)
            {
                double vertical = Math.Abs(f.Y - startY);
                if (vertical < Arm) return 0;
                if (Math.Abs(f.X - startX) > vertical) { blocked = true; return 0; }
                active = true;
                previousY = f.Y;
                return 0;
            }
            double delta = (previousY - f.Y) * 100 * Speed;
            previousY = f.Y;
            return delta;
        }
    }

    internal static class Native
    {
        internal const uint Success = 0x00110000;
        [StructLayout(LayoutKind.Sequential)] internal struct RawDevice { internal ushort Page, Usage; internal uint Flags; internal IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)] internal struct Header { internal uint Type, Size; internal IntPtr Device, WParam; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceList { internal IntPtr Device; internal uint Type; }
        [StructLayout(LayoutKind.Sequential)] internal struct Caps
        {
            internal ushort Usage, Page, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
            internal ushort Nodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
        }
        [StructLayout(LayoutKind.Explicit, Size = 72)] internal struct ValueCaps
        {
            [FieldOffset(0)] internal ushort Page;
            [FieldOffset(2)] internal byte ReportId;
            [FieldOffset(6)] internal ushort Link;
            [FieldOffset(12)] internal byte IsRange;
            [FieldOffset(18)] internal ushort BitSize;
            [FieldOffset(40)] internal int Min;
            [FieldOffset(44)] internal int Max;
            [FieldOffset(56)] internal ushort Usage;
            [FieldOffset(58)] internal ushort UsageMax;
            internal bool Has(ushort page, ushort usage) { return Page == page && (IsRange == 0 ? Usage == usage : Usage <= usage && usage <= UsageMax); }
        }
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputDeviceList([Out] DeviceList[] list, ref uint count, uint size);
        [DllImport("hid.dll")] internal static extern uint HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll")] internal static extern uint HidP_GetValueCaps(int type, [Out] ValueCaps[] caps, ref ushort count, IntPtr data);
        [DllImport("hid.dll")] internal static extern uint HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr data, byte[] report, uint length);
        [DllImport("hid.dll")] internal static extern uint HidP_GetUsages(int type, ushort page, ushort link, [Out] ushort[] usages, ref uint count, IntPtr data, byte[] report, uint length);
        [DllImport("hid.dll")] internal static extern uint HidP_InitializeReportForID(int type, byte id, IntPtr data, byte[] report, uint length);
        [DllImport("hid.dll")] internal static extern uint HidP_SetUsageValue(int type, ushort page, ushort link, ushort usage, uint value, IntPtr data, byte[] report, uint length);
        [DllImport("hid.dll")] internal static extern uint HidP_SetUsages(int type, ushort page, ushort link, ushort[] usages, ref uint count, IntPtr data, byte[] report, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
        internal const uint GenericWrite = 0x40000000, ShareAll = 3, OpenExisting = 3;
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { internal uint Size; internal Guid Class; internal uint Flags; internal IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct LinkNode { internal ushort Usage, Page, Parent, Children, NextSibling, FirstChild; internal uint Bits; internal IntPtr Context; }
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] internal static extern bool HidD_GetPreparsedData(IntPtr file, out IntPtr data);
        [DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern bool HidD_GetFeature(IntPtr file, byte[] report, int length);
        [DllImport("hid.dll")] internal static extern bool HidD_SetFeature(IntPtr file, byte[] report, int length);
        [DllImport("hid.dll")] internal static extern bool HidD_SetOutputReport(IntPtr file, byte[] report, int length);
        [DllImport("hid.dll")] internal static extern uint HidP_GetLinkCollectionNodes([Out] LinkNode[] nodes, ref uint count, IntPtr data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr window, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
        [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
    }

    internal sealed class Touchpad : IDisposable
    {
        // Touchpad reports arrive several hundred times a second, so everything the
        // parser needs is resolved once here and the hot path stays allocation-free.
        private struct Slot { internal Native.ValueCaps X, Y; }
        private readonly IntPtr data;
        private readonly Slot[] slots;
        private readonly Native.ValueCaps[] counts, stamps;
        private readonly ushort[] usages = new ushort[32];
        private int expected, received;
        private Frame pending;
        private bool anyTip;
        private uint scan;
        private readonly int reportLength;
        internal readonly string Description;

        internal Touchpad(IntPtr device)
        {
            uint size = 0;
            if (Native.GetRawInputDeviceInfo(device, 0x20000005, IntPtr.Zero, ref size) == uint.MaxValue || size == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot read touchpad HID descriptor");
            data = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (Native.GetRawInputDeviceInfo(device, 0x20000005, data, ref size) == uint.MaxValue) throw new InvalidOperationException("HID descriptor unavailable");
                Native.Caps caps;
                if (Native.HidP_GetCaps(data, out caps) != Native.Success || caps.Page != 13 || caps.Usage != 5)
                    throw new InvalidOperationException("Not a precision touchpad");
                ushort count = caps.InputValues;
                var values = new Native.ValueCaps[count];
                if (Native.HidP_GetValueCaps(0, values, ref count, data) != Native.Success) throw new InvalidOperationException("HID value capabilities unavailable");
                // Pair every contact's X with the Y in the same link collection, so
                // Parse never has to search the capability table again.
                var found = new List<Slot>();
                foreach (var x in values.Where(v => v.Has(1, 0x30) && v.Link != 0 && v.Max > v.Min).OrderBy(v => v.Link))
                {
                    Native.ValueCaps y = values.FirstOrDefault(v => v.Link == x.Link && v.ReportId == x.ReportId && v.Has(1, 0x31));
                    if (y.Max > y.Min) found.Add(new Slot { X = x, Y = y });
                }
                if (found.Count == 0) throw new InvalidOperationException("Touchpad X coordinate not found");
                slots = found.ToArray();
                counts = values.Where(v => v.Has(13, 0x54)).ToArray();
                stamps = values.Where(v => v.Has(13, 0x56)).ToArray();
                reportLength = caps.InputLength;
                Description = "HID touchpad: reports=" + caps.InputLength + " bytes; contact slots=" + slots.Length + "; X=" + slots[0].X.Min + ".." + slots[0].X.Max;
            }
            catch { Marshal.FreeHGlobal(data); throw; }
        }

        private bool Value(byte[] report, ushort page, ushort link, ushort usage, out uint value)
        { return Native.HidP_GetUsageValue(0, page, link, usage, out value, data, report, (uint)report.Length) == Native.Success; }

        // Fills the shared usage buffer and returns how many entries are live.
        private int Buttons(byte[] report, ushort page, ushort link)
        {
            uint count = (uint)usages.Length;
            if (Native.HidP_GetUsages(0, page, link, usages, ref count, data, report, (uint)report.Length) != Native.Success) return 0;
            return (int)count;
        }
        private bool Pressed(int count, ushort usage)
        {
            for (int i = 0; i < count; i++) if (usages[i] == usage) return true;
            return false;
        }

        internal Frame Parse(byte[] report)
        {
            if (report.Length == 0) return null;
            byte id = report[0];
            uint count = 0, stamp = 0;
            bool hasCount = false;
            foreach (var v in counts) if (v.ReportId == id) hasCount = Value(report, 13, v.Link, 0x54, out count);
            if (!hasCount) return null;
            foreach (var v in stamps) if (v.ReportId == id) Value(report, 13, v.Link, 0x56, out stamp);
            // Hybrid HID frames may span several reports. Count=0 can mean a
            // continuation, not finger-up; never turn it into a new gesture.
            if (count > 0)
            {
                expected = (int)Math.Min(count, 256); received = 0; scan = stamp; anyTip = false;
                pending = new Frame { Count = expected };
            }
            else if (pending == null || received >= expected || stamp != scan)
            { pending = null; expected = received = 0; return new Frame(); }
            bool click = Buttons(report, 9, 0) != 0;
            for (int i = 0; i < slots.Length; i++)
            {
                Native.ValueCaps x = slots[i].X, y = slots[i].Y;
                if (x.ReportId != id) continue;
                uint xv, yv, contact;
                if (!Value(report, 1, x.Link, 0x30, out xv) || !Value(report, 1, x.Link, 0x31, out yv) || !Value(report, 13, x.Link, 0x51, out contact)) continue;
                int buttons = Buttons(report, 13, x.Link);
                pending.Id = (int)contact;
                pending.X = Math.Max(0, Math.Min(1, ((double)xv - x.Min) / (x.Max - x.Min)));
                pending.Y = Math.Max(0, Math.Min(1, ((double)yv - y.Min) / (y.Max - y.Min)));
                pending.Tip = Pressed(buttons, 0x42);
                anyTip |= pending.Tip;
                pending.Confident = Pressed(buttons, 0x47);
                pending.Click = click;
                received++;
                if (received >= expected)
                {
                    Frame result = pending; pending = null;
                    // Contact Count includes lifted fingers. An all-up multi-contact
                    // frame can be the last report, with no subsequent Count=0.
                    if (!anyTip) result.Count = 0;
                    return result;
                }
            }
            return expected > 1 ? new Frame { Count = expected } : null;
        }

        internal void TestParser()
        {
            Action<int, bool> check = delegate(int contacts, bool tip)
            {
                byte[] report = new byte[reportLength];
                byte id = slots[0].X.ReportId;
                if (Native.HidP_InitializeReportForID(0, id, data, report, (uint)report.Length) != Native.Success) throw new Exception("Cannot initialize test HID report");
                Action<ushort, ushort, ushort, uint> set = delegate(ushort page, ushort link, ushort usage, uint value)
                {
                    if (Native.HidP_SetUsageValue(0, page, link, usage, value, data, report, (uint)report.Length) != Native.Success) throw new Exception("Cannot set test HID usage " + usage);
                };
                var countCap = counts.First(v => v.ReportId == id);
                set(13, countCap.Link, 0x54, (uint)contacts);
                foreach (var slot in slots.Where(s => s.X.ReportId == id).Take(contacts))
                {
                    Native.ValueCaps x = slot.X, y = slot.Y;
                    set(1, x.Link, 0x30, (uint)(x.Min + (x.Max - x.Min) / 50));
                    set(1, x.Link, 0x31, (uint)(y.Min + (y.Max - y.Min) / 2));
                    set(13, x.Link, 0x51, (uint)x.Link);
                    ushort[] pressed = tip ? new ushort[] { 0x42, 0x47 } : new ushort[] { 0x47 };
                    uint n = (uint)pressed.Length;
                    if (Native.HidP_SetUsages(0, 13, x.Link, pressed, ref n, data, report, (uint)report.Length) != Native.Success) throw new Exception("Cannot set test tip/confidence");
                }
                Frame frame = Parse(report);
                if (frame == null || frame.Count != (tip ? contacts : 0)) throw new Exception("Wrong parsed contact count");
                if (tip && (!frame.Tip || !frame.Confident || Math.Abs(frame.X - .02) > .002 || Math.Abs(frame.Y - .5) > .002)) throw new Exception("Wrong parsed coordinates or flags");
            };
            check(1, true); check(1, false); check(2, true); check(2, false); check(0, false);
        }
        public void Dispose() { Marshal.FreeHGlobal(data); }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    internal static class Controls
    {
        internal static double Brightness(double? target)
        {
            string className = target.HasValue ? "WmiMonitorBrightnessMethods" : "WmiMonitorBrightness";
            using (var search = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM " + className + " WHERE Active = TRUE"))
            using (var results = search.Get())
            {
                foreach (ManagementObject monitor in results)
                using (monitor)
                {
                    if (!target.HasValue) return Convert.ToDouble(monitor["CurrentBrightness"]);
                    byte value = (byte)Math.Max(1, Math.Min(100, Math.Round(target.Value)));
                    object status = monitor.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, value });
                    if (status != null && Convert.ToUInt32(status) != 0) throw new InvalidOperationException("WmiSetBrightness failed: " + status);
                    return value;
                }
            }
            throw new InvalidOperationException("Built-in display brightness is unavailable");
        }

        internal static double Volume(double? target, bool unmute = true)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device = null; object endpoint = null;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out endpoint));
                var volume = (IAudioEndpointVolume)endpoint;
                if (target.HasValue)
                {
                    Guid context = Guid.Empty;
                    float scalar = (float)(Math.Max(0, Math.Min(100, target.Value)) / 100);
                    Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(scalar, ref context));
                    // Moving upward from silence should also work after pressing Mute.
                    if (unmute && scalar > 0) Marshal.ThrowExceptionForHR(volume.SetMute(false, ref context));
                }
                float current;
                Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out current));
                return current * 100;
            }
            finally
            {
                if (endpoint != null) Marshal.ReleaseComObject(endpoint);
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    // Coalesce writes off the input thread: WMI can be slower than the touchpad.
    internal sealed class OutputWorker : IDisposable
    {
        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread thread;
        private double? brightness, volume;
        private readonly Queue<KeyValuePair<int, int>> reads = new Queue<KeyValuePair<int, int>>();
        private bool stopped;
        internal event Action<int, double, string> Result;
        internal event Action<int, int, double, string> Baseline;
        internal OutputWorker() { thread = new Thread(Run) { IsBackground = true, Name = "SurfaceEdge controls" }; thread.Start(); }
        internal void Set(int side, double value)
        {
            lock (gate) { if (side < 0) brightness = value; else volume = value; }
            wake.Set();
        }
        internal void Read(int side, int token)
        {
            lock (gate) { reads.Enqueue(new KeyValuePair<int, int>(side, token)); }
            wake.Set();
        }
        private void Run()
        {
            while (true)
            {
                wake.WaitOne();
                Thread.Sleep(40);
                double? b, v; KeyValuePair<int, int>[] requests;
                lock (gate) { if (stopped) return; b = brightness; v = volume; brightness = volume = null; requests = reads.ToArray(); reads.Clear(); }
                if (b.HasValue) Apply(-1, b.Value);
                if (v.HasValue) Apply(1, v.Value);
                // Read after pending writes, so a quick second swipe starts from
                // the first swipe's final value, not a stale hardware value.
                foreach (var request in requests)
                {
                    double value = 0; string error = null;
                    try { value = request.Key < 0 ? Controls.Brightness(null) : Controls.Volume(null); }
                    catch (Exception e) { error = e.Message; Program.Log("Read error: " + error); }
                    if (Baseline != null) Baseline(request.Key, request.Value, value, error);
                }
            }
        }
        private void Apply(int side, double value)
        {
            try { double actual = side < 0 ? Controls.Brightness(value) : Controls.Volume(value); if (Result != null) Result(side, actual, null); }
            catch (Exception e) { Program.Log("Control error: " + e.Message); if (Result != null) Result(side, value, e.Message); }
        }
        public void Dispose()
        {
            lock (gate) { if (stopped) return; stopped = true; }
            wake.Set();
            if (thread.Join(1500)) wake.Dispose();
        }
    }

    internal sealed class Osd : IDisposable
    {
        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
        }
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context,
            ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellServiceProvider provider);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ShowFlyout(IntPtr self, int kind, uint command);
        private IntPtr flyout;
        private ShowFlyout show;

        // Undocumented ImmersiveShell flyout service, called only on the UI STA.
        // Source: qwerty12/4b3f41eb61724cd9e8f2bb5cc15c33c2, BrightnessSetter.ahk.
        // twinui!CAudioFlyoutController::ShowInternal routes kind 0 to volume,
        // kind 3 to brightness (verified on Windows 11 26200.9168 ARM64).
        // The shell reads the current level; no media keys or control writes.
        internal void Display(int side)
        {
            try
            {
                if (flyout == IntPtr.Zero)
                {
                    Guid clsid = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
                    Guid iid = typeof(IShellServiceProvider).GUID;
                    IShellServiceProvider provider = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out provider));
                        Guid service = new Guid("41F9D2FB-7834-4AB6-8B1B-73E74064B465");
                        iid = service;
                        Marshal.ThrowExceptionForHR(provider.QueryService(ref service, ref iid, out flyout));
                        if (flyout == IntPtr.Zero) throw new InvalidOperationException("Windows OSD service is unavailable");
                        show = Marshal.GetDelegateForFunctionPointer<ShowFlyout>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(flyout), 3 * IntPtr.Size));
                    }
                    finally { if (provider != null) Marshal.ReleaseComObject(provider); }
                }
                Marshal.ThrowExceptionForHR(show(flyout, side < 0 ? 3 : 0, 0));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (flyout != IntPtr.Zero) { Marshal.Release(flyout); flyout = IntPtr.Zero; }
            show = null;
        }
    }

    // Touchpad vibration goes through the HID Simple Haptic Controller (usage page
    // 0x0E) that Windows Precision Touchpads publish beside their digitizer
    // collection. Playing a waveform on demand needs a Manual Trigger control.
    // Pads that publish only Intensity keep the actuator under firmware control,
    // and the one lever software still has there is the intensity setting itself.
    internal sealed class Haptics : IDisposable
    {
        internal enum Kind { Arm, Step, Limit }
        private const ushort PageHaptics = 0x0E, PageOrdinal = 0x0A;
        private const ushort UsageController = 0x01, UsageManualTrigger = 0x21, UsageIntensity = 0x23, UsageRepeat = 0x24, UsageRetrigger = 0x25;
        private const ushort WaveformClick = 0x1003, WaveformPress = 0x1006;
        private static readonly IntPtr Invalid = new IntPtr(-1);

        private readonly object gate = new object(), io = new object();
        private IntPtr query = Invalid, write = Invalid, data = IntPtr.Zero;
        private Native.ValueCaps trigger, triggerIntensity, featureIntensity;
        private bool hasTrigger, triggerInFeature, hasTriggerIntensity, hasFeatureIntensity, hasRepeat, hasRetrigger;
        private Native.ValueCaps repeat, retrigger;
        private int outputLength, featureLength;
        private uint stepWaveform, limitWaveform;
        private byte savedIntensity = 50;
        private Thread thread;
        private AutoResetEvent wake;
        private Kind queued;
        private bool pending, stopped;
        internal string Description = "Haptics: not started";
        internal int Strength = 60;
        internal bool Available { get { return hasTrigger || hasFeatureIntensity; } }
        internal bool Triggered { get { return hasTrigger; } }

        internal void Open()
        {
            Guid guid;
            Native.HidD_GetHidGuid(out guid);
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == Invalid) { Description = "Haptics: HID enumeration failed"; return; }
            try
            {
                var iface = new Native.InterfaceData();
                iface.Size = (uint)Marshal.SizeOf(typeof(Native.InterfaceData));
                for (uint index = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref iface); index++)
                {
                    string path = DevicePath(set, ref iface);
                    if (path == null || !Inspect(path)) continue;
                    lock (gate)
                    {
                        if (stopped) { Close(); return; }
                        wake = new AutoResetEvent(false);
                        thread = new Thread(Run) { IsBackground = true, Name = "SurfaceEdge haptics" };
                        thread.Start();
                    }
                    return;
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            Description = "Haptics: no HID haptic controller on this system";
        }

        private static string DevicePath(IntPtr set, ref Native.InterfaceData iface)
        {
            uint needed;
            Native.SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out needed, IntPtr.Zero);
            if (needed < 8 || needed > 4096) return null;
            IntPtr detail = Marshal.AllocHGlobal((int)needed);
            try
            {
                // cbSize counts the fixed header only, not the path that follows it.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, needed, out needed, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
            }
            finally { Marshal.FreeHGlobal(detail); }
        }

        private static Native.ValueCaps[] Values(IntPtr parsed, int type, ushort count)
        {
            if (count == 0) return new Native.ValueCaps[0];
            var caps = new Native.ValueCaps[count];
            return Native.HidP_GetValueCaps(type, caps, ref count, parsed) == Native.Success ? caps.Take(count).ToArray() : new Native.ValueCaps[0];
        }

        private static bool Find(Native.ValueCaps[] caps, ushort usage, int reportId, out Native.ValueCaps found)
        {
            foreach (var cap in caps)
                if (cap.Has(PageHaptics, usage) && (reportId < 0 || cap.ReportId == reportId)) { found = cap; return true; }
            found = new Native.ValueCaps();
            return false;
        }

        private static bool HasController(IntPtr parsed, Native.Caps caps)
        {
            if (caps.Nodes == 0) return false;
            uint count = caps.Nodes;
            var nodes = new Native.LinkNode[count];
            if (Native.HidP_GetLinkCollectionNodes(nodes, ref count, parsed) != Native.Success) return false;
            for (int i = 0; i < count; i++)
                if (nodes[i].Page == PageHaptics && nodes[i].Usage == UsageController) return true;
            return false;
        }

        private bool Inspect(string path)
        {
            IntPtr file = Native.CreateFile(path, 0, Native.ShareAll, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
            if (file == Invalid) return false;
            IntPtr parsed = IntPtr.Zero;
            IntPtr writer = Invalid;
            bool keep = false;
            try
            {
                if (!Native.HidD_GetPreparsedData(file, out parsed)) return false;
                Native.Caps caps;
                if (Native.HidP_GetCaps(parsed, out caps) != Native.Success || !HasController(parsed, caps)) return false;
                Native.ValueCaps[] output = Values(parsed, 1, caps.OutputValues), feature = Values(parsed, 2, caps.FeatureValues);
                hasTrigger = Find(output, UsageManualTrigger, -1, out trigger);
                triggerInFeature = false;
                if (!hasTrigger && Find(feature, UsageManualTrigger, -1, out trigger)) { hasTrigger = triggerInFeature = true; }
                hasFeatureIntensity = Find(feature, UsageIntensity, -1, out featureIntensity);
                if (!hasTrigger && !hasFeatureIntensity)
                { Description = "Haptics: controller found but it exposes no writable control"; return false; }
                Native.ValueCaps[] same = triggerInFeature ? feature : output;
                hasTriggerIntensity = hasTrigger && Find(same, UsageIntensity, trigger.ReportId, out triggerIntensity);
                hasRepeat = hasTrigger && Find(same, UsageRepeat, trigger.ReportId, out repeat);
                hasRetrigger = hasTrigger && Find(same, UsageRetrigger, trigger.ReportId, out retrigger);
                outputLength = caps.OutputLength;
                featureLength = caps.FeatureLength;
                writer = Native.CreateFile(path, Native.GenericWrite, Native.ShareAll, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
                if (writer == Invalid)
                { Description = "Haptics: controller found but not writable (Windows error " + Marshal.GetLastWin32Error() + ")"; return false; }
                if (hasFeatureIntensity)
                {
                    uint level;
                    if (ReadValue(file, parsed, featureIntensity, UsageIntensity, out level)) savedIntensity = (byte)level;
                }
                if (hasTrigger)
                {
                    uint guess = (uint)Math.Max(trigger.Min, Math.Min(trigger.Max, 3));
                    stepWaveform = Ordinal(parsed, file, feature, WaveformClick, guess);
                    limitWaveform = Ordinal(parsed, file, feature, WaveformPress, stepWaveform);
                    Description = "Haptics: waveform trigger on " + (triggerInFeature ? "feature" : "output") + " report " + trigger.ReportId
                        + "; step ordinal=" + stepWaveform + "; limit ordinal=" + limitWaveform + "; intensity control=" + (hasTriggerIntensity || hasFeatureIntensity);
                }
                else
                    Description = "Haptics: firmware-driven pad; no waveform trigger in the HID descriptor. "
                        + "Only the intensity setting (feature report " + featureIntensity.ReportId + ", now " + savedIntensity + ") can be written.";
                query = file; write = writer; data = parsed; keep = true;
                Program.Log(Description);
                return true;
            }
            finally
            {
                if (!keep)
                {
                    if (writer != Invalid) Native.CloseHandle(writer);
                    if (parsed != IntPtr.Zero) Native.HidD_FreePreparsedData(parsed);
                    Native.CloseHandle(file);
                    hasTrigger = triggerInFeature = hasTriggerIntensity = hasFeatureIntensity = hasRepeat = hasRetrigger = false;
                }
            }
        }

        // The waveform list is a feature report of ordinal usages holding waveform
        // ids; Manual Trigger takes that ordinal, not the id itself.
        private uint Ordinal(IntPtr parsed, IntPtr file, Native.ValueCaps[] feature, ushort wanted, uint fallback)
        {
            foreach (byte report in feature.Where(v => v.Page == PageOrdinal).Select(v => v.ReportId).Distinct())
            {
                byte[] buffer = new byte[featureLength];
                buffer[0] = report;
                if (!Native.HidD_GetFeature(file, buffer, buffer.Length)) continue;
                foreach (var cap in feature.Where(v => v.Page == PageOrdinal && v.ReportId == report))
                {
                    ushort first = cap.Usage, last = cap.IsRange == 0 ? cap.Usage : cap.UsageMax;
                    for (ushort usage = first; usage <= last && usage != 0; usage++)
                    {
                        uint value;
                        if (Native.HidP_GetUsageValue(2, PageOrdinal, cap.Link, usage, out value, parsed, buffer, (uint)buffer.Length) == Native.Success && value == wanted)
                            return usage;
                    }
                }
            }
            return fallback;
        }

        private bool ReadValue(IntPtr file, IntPtr parsed, Native.ValueCaps cap, ushort usage, out uint value)
        {
            value = 0;
            byte[] buffer = new byte[featureLength];
            buffer[0] = cap.ReportId;
            return Native.HidD_GetFeature(file, buffer, buffer.Length)
                && Native.HidP_GetUsageValue(2, PageHaptics, cap.Link, usage, out value, parsed, buffer, (uint)buffer.Length) == Native.Success;
        }

        private uint Level(Kind kind)
        {
            int level = kind == Kind.Limit ? Strength + 40 : kind == Kind.Arm ? Strength * 3 / 4 : Strength;
            return (uint)Math.Max(1, Math.Min(100, level));
        }

        internal void Play(Kind kind)
        {
            // Device I/O belongs to the haptics thread; callers only leave a note.
            lock (gate)
            {
                if (stopped || thread == null) return;
                queued = kind; pending = true;
                wake.Set();
            }
        }

        private void Run()
        {
            while (true)
            {
                wake.WaitOne();
                Kind kind;
                lock (gate) { if (stopped) return; if (!pending) continue; kind = queued; pending = false; }
                Fire(kind);
            }
        }

        internal bool Fire(Kind kind)
        {
            lock (io)
            {
                if (write == Invalid) return false;
                try { return hasTrigger ? Trigger(kind) : Retune(kind); }
                catch (Exception e) { Program.Log("Haptics error: " + e.Message); return false; }
            }
        }

        private bool Trigger(Kind kind)
        {
            int type = triggerInFeature ? 2 : 1;
            int length = triggerInFeature ? featureLength : outputLength;
            if (length == 0) return false;
            byte[] report = NewReport(type, trigger.ReportId, length);
            uint waveform = kind == Kind.Limit ? limitWaveform : stepWaveform;
            if (Native.HidP_SetUsageValue(type, PageHaptics, trigger.Link, UsageManualTrigger, waveform, data, report, (uint)report.Length) != Native.Success) return false;
            // A single pulse: play once, no repeats, no retrigger period.
            if (hasTriggerIntensity) Native.HidP_SetUsageValue(type, PageHaptics, triggerIntensity.Link, UsageIntensity, Level(kind), data, report, (uint)report.Length);
            if (hasRepeat) Native.HidP_SetUsageValue(type, PageHaptics, repeat.Link, UsageRepeat, 0, data, report, (uint)report.Length);
            if (hasRetrigger) Native.HidP_SetUsageValue(type, PageHaptics, retrigger.Link, UsageRetrigger, 0, data, report, (uint)report.Length);
            return triggerInFeature ? Native.HidD_SetFeature(write, report, report.Length) : Native.HidD_SetOutputReport(write, report, report.Length);
        }

        // Firmware-driven pads expose no trigger. Rewriting the intensity setting is
        // the only control left: pads whose firmware previews a new level answer with
        // a short tick, the rest stay silent. The user's level is always restored.
        private bool Retune(Kind kind)
        {
            if (!hasFeatureIntensity) return false;
            if (!WriteIntensity((byte)Level(kind))) return false;
            Thread.Sleep(15);
            return WriteIntensity(savedIntensity);
        }

        // Collections that carry no default-valued items reject
        // HidP_InitializeReportForID; a zeroed buffer with the report id is enough.
        private byte[] NewReport(int type, byte id, int length)
        {
            byte[] report = new byte[length];
            if (Native.HidP_InitializeReportForID(type, id, data, report, (uint)report.Length) != Native.Success)
            { Array.Clear(report, 0, report.Length); report[0] = id; }
            return report;
        }

        private bool WriteIntensity(byte level)
        {
            if (featureLength == 0) return false;
            byte[] report = NewReport(2, featureIntensity.ReportId, featureLength);
            if (Native.HidP_SetUsageValue(2, PageHaptics, featureIntensity.Link, UsageIntensity, level, data, report, (uint)report.Length) != Native.Success) return false;
            return Native.HidD_SetFeature(write, report, report.Length);
        }

        private void Close()
        {
            lock (io)
            {
                // Never leave the pad on a level the user did not choose.
                if (!hasTrigger && hasFeatureIntensity && write != Invalid) { try { WriteIntensity(savedIntensity); } catch (Exception) { } }
                if (data != IntPtr.Zero) { Native.HidD_FreePreparsedData(data); data = IntPtr.Zero; }
                if (write != Invalid) { Native.CloseHandle(write); write = Invalid; }
                if (query != Invalid) { Native.CloseHandle(query); query = Invalid; }
                hasTrigger = hasFeatureIntensity = false;
            }
        }

        public void Dispose()
        {
            Thread worker;
            lock (gate)
            {
                if (stopped) return;
                stopped = true; worker = thread;
                if (wake != null) wake.Set();
            }
            if (worker != null) worker.Join(500);
            Close();
            lock (gate) { if (wake != null) { wake.Dispose(); wake = null; } }
        }
    }

    internal sealed class EdgeApp : Form
    {
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly Gesture gesture = new Gesture();
        private readonly Dictionary<IntPtr, Touchpad> devices = new Dictionary<IntPtr, Touchpad>();
        private readonly Dictionary<IntPtr, long> failed = new Dictionary<IntPtr, long>();
        private readonly OutputWorker worker = new OutputWorker();
        private readonly Osd osd = new Osd();
        private readonly Haptics haptics = new Haptics();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly bool diagnose;
        private IntPtr activeDevice;
        private bool haveDevice, enabled = true, closing;
        private double target;
        private double pendingDelta;
        private bool targetReady;
        private int generation;
        private int lastSent = -1;
        private long packets, frames, lastInput;
        private long osdRetryAfter;
        private long lastPulse;
        private int lastPulseValue;
        private bool atLimit, hapticsEnabled;
        private string lastFrame = "No touchpad input yet";
        private IntPtr inputBuffer;
        private int inputCapacity;
        private byte[] reportBuffer = new byte[0];

        internal EdgeApp(bool diagnose)
        {
            this.diagnose = diagnose;
            ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Text = "SurfaceEdge";
            var pause = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
            pause.CheckedChanged += delegate { enabled = pause.Checked; gesture.Reset(); tray.Text = enabled ? "SurfaceEdge - enabled" : "SurfaceEdge - paused"; };
            menu.Items.Add(pause);
            // Nothing is stored on disk: the toggle lasts for the session only.
            var feedbackItem = new ToolStripMenuItem("Haptic feedback") { Checked = hapticsEnabled, CheckOnClick = true };
            feedbackItem.CheckedChanged += delegate { hapticsEnabled = feedbackItem.Checked; };
            menu.Items.Add(feedbackItem);
            var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupEnabled() };
            startup.Click += delegate
            {
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                    {
                        if (startup.Checked) key.DeleteValue("SurfaceEdge", false);
                        else key.SetValue("SurfaceEdge", "\"" + Application.ExecutablePath + "\"");
                    }
                    startup.Checked = StartupEnabled();
                }
                catch (Exception e) { Program.Log("Startup entry error: " + e.Message); }
            };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open log", null, delegate { Process.Start("notepad.exe", "\"" + Program.LogPath + "\""); });
            menu.Items.Add("Exit", null, delegate { Close(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SurfaceEdge - enabled", ContextMenuStrip = menu, Visible = true };
            worker.Result += delegate(int side, double value, string error)
            {
                if (closing || !IsHandleCreated) return;
                try { BeginInvoke((Action)delegate { if (!closing && error == null) ShowOsd(side); }); }
                catch (InvalidOperationException) { }
            };
            worker.Baseline += delegate(int side, int token, double value, string error)
            {
                if (closing || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (closing || !enabled || !gesture.Active || token != generation) return;
                        if (error != null) { gesture.Reset(); return; }
                        target = value; targetReady = true;
                        if (pendingDelta != 0) ChangeTarget(pendingDelta);
                        else ShowOsd(side);
                        pendingDelta = 0;
                    });
                }
                catch (InvalidOperationException) { }
            };
            IntPtr hwnd = Handle;
            var registration = new Native.RawDevice { Page = 13, Usage = 5, Flags = 0x100 | 0x2000, Target = hwnd };
            if (!Native.RegisterRawInputDevices(new[] { registration }, 1, (uint)Marshal.SizeOf(typeof(Native.RawDevice))))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot register touchpad input");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { haptics.Open(); } catch (Exception e) { Program.Log("Haptics error: " + e); }
                if (closing || !IsHandleCreated) return;
                try { BeginInvoke((Action)delegate { if (!closing) ApplyHaptics(feedbackItem); }); }
                catch (InvalidOperationException) { }
            });
            Program.Log("Started; " + Tests.Architecture() + "; diagnostic=" + diagnose);
            timer.Tick += delegate
            {
                if (clock.ElapsedMilliseconds - lastInput > 350) gesture.Reset();
                if (diagnose && clock.ElapsedMilliseconds >= 15000)
                { Program.Log("Diagnostic complete: packets=" + packets + "; frames=" + frames + "; " + lastFrame); Close(); }
            };
            timer.Start();
        }

        // A pad that only accepts intensity writes plays nothing on most firmware, so
        // it starts off; a pad with a real waveform trigger starts on. The menu
        // toggle overrides that for as long as the process runs.
        private void ApplyHaptics(ToolStripMenuItem item)
        {
            if (!haptics.Available)
            {
                hapticsEnabled = false;
                item.Checked = false; item.Enabled = false;
                item.Text = "Haptic feedback (no haptic touchpad)";
                return;
            }
            item.Checked = haptics.Triggered;
            if (!haptics.Triggered) item.Text = "Haptic feedback (this pad may play nothing)";
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }
        private static bool StartupEnabled()
        { using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) return key != null && key.GetValue("SurfaceEdge") != null; }
        private void ShowOsd(int side)
        {
            if (clock.ElapsedMilliseconds < osdRetryAfter) return;
            try { osd.Display(side); }
            catch (Exception e)
            {
                osdRetryAfter = clock.ElapsedMilliseconds + 10000;
                Program.Log("Native OSD error: " + e);
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0xFF)
            {
                try { ReadInput(m.LParam); }
                catch (Exception e) { gesture.Reset(); Program.Log("Input error: " + e.Message); }
            }
            else if (m.Msg == 0xFE || m.Msg == 0x218)
            {
                foreach (var pad in devices.Values) pad.Dispose();
                devices.Clear(); failed.Clear(); gesture.Reset(); haveDevice = false;
            }
            base.WndProc(ref m);
        }
        // Runs on every WM_INPUT, so both buffers are kept and grown instead of
        // being allocated and freed hundreds of times a second.
        private void ReadInput(IntPtr input)
        {
            uint size = 0, headerSize = (uint)Marshal.SizeOf(typeof(Native.Header));
            if (Native.GetRawInputData(input, 0x10000003, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize + 8 || size > 1048576) return;
            if (inputCapacity < (int)size)
            {
                IntPtr grown = Marshal.AllocHGlobal((int)size);
                if (inputBuffer != IntPtr.Zero) Marshal.FreeHGlobal(inputBuffer);
                inputBuffer = grown; inputCapacity = (int)size;
            }
            IntPtr buffer = inputBuffer;
            if (Native.GetRawInputData(input, 0x10000003, buffer, ref size, headerSize) == uint.MaxValue) return;
            var header = (Native.Header)Marshal.PtrToStructure(buffer, typeof(Native.Header));
            if (header.Type != 2) return;
            packets++;
            uint reportSize = (uint)Marshal.ReadInt32(buffer, (int)headerSize);
            uint count = (uint)Marshal.ReadInt32(buffer, (int)headerSize + 4);
            if (reportSize == 0 || (ulong)reportSize * count + headerSize + 8 > size) return;
            Touchpad pad;
            if (!devices.TryGetValue(header.Device, out pad))
            {
                long retry;
                if (failed.TryGetValue(header.Device, out retry) && clock.ElapsedMilliseconds < retry) return;
                try { pad = new Touchpad(header.Device); devices.Add(header.Device, pad); Program.Log(pad.Description); }
                catch (Exception e) { failed[header.Device] = clock.ElapsedMilliseconds + 10000; Program.Log(e.Message); return; }
            }
            if (!haveDevice || activeDevice != header.Device) { gesture.Reset(); activeDevice = header.Device; haveDevice = true; }
            if (reportBuffer.Length != reportSize) reportBuffer = new byte[reportSize];
            byte[] report = reportBuffer;
            for (uint i = 0; i < count; i++)
            {
                Marshal.Copy(IntPtr.Add(buffer, checked((int)(headerSize + 8 + i * reportSize))), report, 0, report.Length);
                Frame frame = pad.Parse(report);
                if (frame == null) continue;
                frames++; lastInput = clock.ElapsedMilliseconds;
                if (diagnose)
                {
                    lastFrame = string.Format("Contacts={0}; tip={1}; confidence={2}; X={3:0.000}; Y={4:0.000}", frame.Count, frame.Tip, frame.Confident, frame.X, frame.Y);
                    if (frames <= 10 || frames % 100 == 0) Program.Log(lastFrame);
                    continue;
                }
                if (!enabled) continue;
                bool wasActive = gesture.Active;
                double delta = gesture.Feed(frame, clock.ElapsedMilliseconds);
                if (!wasActive && gesture.Active)
                {
                    targetReady = false; pendingDelta = 0; lastSent = -1;
                    atLimit = false; lastPulseValue = -1000; lastPulse = 0;
                    worker.Read(gesture.Side, ++generation);
                    Pulse(Haptics.Kind.Arm);
                }
                if (delta == 0) continue;
                if (!targetReady) pendingDelta += delta;
                else ChangeTarget(delta);
            }
        }
        private void ChangeTarget(double delta)
        {
            target = Math.Max(gesture.Side < 0 ? 1 : 0, Math.Min(100, target + delta));
            int rounded = (int)Math.Round(target);
            if (rounded == lastSent) return;
            lastSent = rounded;
            worker.Set(gesture.Side, rounded);
            Feedback(rounded);
        }
        // One tick per unit would be a continuous buzz at swipe speed; a tick every
        // couple of units, rate limited, reads as a ratchet under the finger.
        private void Feedback(int value)
        {
            bool limit = value >= 100 || value <= (gesture.Side < 0 ? 1 : 0);
            if (limit)
            {
                if (atLimit) return;
                atLimit = true; lastPulseValue = value; lastPulse = clock.ElapsedMilliseconds;
                Pulse(Haptics.Kind.Limit);
                return;
            }
            atLimit = false;
            if (Math.Abs(value - lastPulseValue) < 2 || clock.ElapsedMilliseconds - lastPulse < 55) return;
            lastPulseValue = value; lastPulse = clock.ElapsedMilliseconds;
            Pulse(Haptics.Kind.Step);
        }
        private void Pulse(Haptics.Kind kind) { if (hapticsEnabled) haptics.Play(kind); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !closing)
            {
                closing = true; timer.Dispose(); worker.Dispose();
                foreach (var pad in devices.Values) pad.Dispose();
                devices.Clear();
                if (inputBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(inputBuffer); inputBuffer = IntPtr.Zero; inputCapacity = 0; }
                tray.Visible = false; tray.Dispose(); menu.Dispose(); osd.Dispose(); haptics.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Tests
    {
        internal static string Architecture()
        {
            ushort process, native;
            if (!Native.IsWow64Process2(Process.GetCurrentProcess().Handle, out process, out native)) return "Architecture check failed";
            return string.Format("Process={0}; native=0x{1:X4}; emulated=0x{2:X4}", RuntimeInformation.ProcessArchitecture, native, process);
        }
        private static Frame Finger(double x, double y) { return new Frame { Count = 1, Id = 7, Tip = true, Confident = true, X = x, Y = y }; }
        internal static int Run(bool verifyControls, bool verifyOsd, bool verifyHaptics)
        {
            var output = new List<string>(); int errors = 0;
            Action<string, Action> test = delegate(string name, Action action)
            {
                try { action(); output.Add("PASS " + name); }
                catch (Exception e) { errors++; output.Add("FAIL " + name + ": " + e.Message); }
            };
            Action<bool> assert = delegate(bool value) { if (!value) throw new Exception("Assertion failed"); };
            output.Add(Architecture());
            test("Native ARM64 process", delegate
            {
                ushort process, native;
                assert(RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                    && Native.IsWow64Process2(Process.GetCurrentProcess().Handle, out process, out native)
                    && native == 0xAA64 && process == 0);
            });
            test("HID ABI sizes", delegate { assert(Marshal.SizeOf(typeof(Native.ValueCaps)) == 72 && Marshal.SizeOf(typeof(Native.Caps)) == 64 && Marshal.SizeOf(typeof(Native.Header)) == 24); });
            test("Left edge up increases", delegate { var g = new Gesture(); g.Feed(Finger(.02, .8), 1); g.Feed(Finger(.02, .75), 20); assert(g.Side == -1 && g.Feed(Finger(.02, .65), 40) > 0); });
            test("Right edge down decreases", delegate { var g = new Gesture(); g.Feed(Finger(.98, .2), 1); g.Feed(Finger(.98, .25), 20); assert(g.Side == 1 && g.Feed(Finger(.98, .35), 40) < 0); });
            test("Center cannot arm by entering edge", delegate { var g = new Gesture(); g.Feed(Finger(.5, .8), 1); g.Feed(Finger(.02, .7), 20); assert(g.Feed(Finger(.02, .4), 40) == 0 && !g.Active); });
            test("Tap / small movement ignored", delegate { var g = new Gesture(); g.Feed(Finger(.02, .8), 1); assert(g.Feed(Finger(.02, .79), 20) == 0 && !g.Active); });
            test("Multi-touch cancels until lift", delegate { var g = new Gesture(); g.Feed(Finger(.02, .8), 1); g.Feed(new Frame { Count = 2 }, 20); g.Feed(Finger(.02, .7), 40); assert(g.Feed(Finger(.02, .4), 60) == 0); g.Feed(new Frame(), 80); g.Feed(Finger(.02, .8), 100); g.Feed(Finger(.02, .7), 120); assert(g.Feed(Finger(.02, .6), 140) > 0); });
            test("Leaving edge cancels", delegate { var g = new Gesture(); g.Feed(Finger(.02, .8), 1); g.Feed(Finger(.02, .7), 20); g.Feed(Finger(.3, .6), 40); assert(g.Feed(Finger(.02, .4), 60) == 0 && !g.Active); });
            test("Palm rejected", delegate { var g = new Gesture(); var f = Finger(.02, .8); f.Confident = false; g.Feed(f, 1); g.Feed(Finger(.02, .7), 20); assert(!g.Active); });
            test("Click rejected", delegate { var g = new Gesture(); var f = Finger(.02, .8); f.Click = true; g.Feed(f, 1); g.Feed(Finger(.02, .7), 20); assert(!g.Active); });
            test("Finger ID change rejected", delegate { var g = new Gesture(); g.Feed(Finger(.02, .8), 1); var f = Finger(.02, .6); f.Id = 8; g.Feed(f, 20); assert(!g.Active); });
            test("Timeout re-arms without jump", delegate { var g = new Gesture(); g.Feed(Finger(.02, .8), 1); g.Feed(Finger(.02, .7), 20); assert(g.Feed(Finger(.02, .1), 1000) == 0 && !g.Active); });
            test("Worker shutdown is idempotent", delegate { var worker = new OutputWorker(); worker.Dispose(); worker.Dispose(); });
            test("Brightness read (no change)", delegate { double value = Controls.Brightness(null); assert(value >= 0 && value <= 100); output.Add("Brightness=" + value); });
            test("Volume read (no change)", delegate { double value = Controls.Volume(null); assert(value >= 0 && value <= 100.01); output.Add("Volume=" + value.ToString("0.0")); });
            if (verifyOsd)
            {
                test("Native brightness and volume OSD calls; final volume, mute and brightness unchanged", delegate
                {
                    var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                    IMMDevice device = null; object endpoint = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
                        Guid iid = typeof(IAudioEndpointVolume).GUID;
                        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out endpoint));
                        var audio = (IAudioEndpointVolume)endpoint;
                        float before, after; bool mutedBefore, mutedAfter;
                        Marshal.ThrowExceptionForHR(audio.GetMasterVolumeLevelScalar(out before));
                        Marshal.ThrowExceptionForHR(audio.GetMute(out mutedBefore));
                        double brightness = Controls.Brightness(null);
                        using (var osd = new Osd())
                        {
                            foreach (int side in new[] { -1, 1 })
                            {
                                osd.Display(side);
                                Thread.Sleep(200);
                                osd.Display(side);
                                osd.Dispose(); osd.Dispose();
                                osd.Display(side);
                                Thread.Sleep(1500);
                                Marshal.ThrowExceptionForHR(audio.GetMasterVolumeLevelScalar(out after));
                                Marshal.ThrowExceptionForHR(audio.GetMute(out mutedAfter));
                                assert(Math.Abs(before - after) < .0001 && mutedBefore == mutedAfter && Controls.Brightness(null) == brightness);
                            }
                        }
                        Marshal.ThrowExceptionForHR(audio.GetMasterVolumeLevelScalar(out after));
                        Marshal.ThrowExceptionForHR(audio.GetMute(out mutedAfter));
                        assert(Math.Abs(before - after) < .0001 && mutedBefore == mutedAfter && Controls.Brightness(null) == brightness);
                        output.Add("OSD visibility and appearance require visual confirmation on the desktop.");
                    }
                    finally
                    {
                        if (endpoint != null) Marshal.ReleaseComObject(endpoint);
                        if (device != null) Marshal.ReleaseComObject(device);
                        Marshal.ReleaseComObject(enumerator);
                    }
                });
            }
            if (verifyControls)
            {
                test("Brightness write current value", delegate { double value = Controls.Brightness(null); Controls.Brightness(value); assert(Math.Abs(Controls.Brightness(null) - Math.Max(1, value)) <= 1); });
                test("Volume write current value (mute preserved)", delegate { double value = Controls.Volume(null); assert(Math.Abs(Controls.Volume(value, false) - value) < .01); });
            }
            test("Touchpad HID descriptor", delegate
            {
                // Windows can expose Precision Touchpad input through device handle 0.
                try { using (var pad = new Touchpad(IntPtr.Zero)) { output.Add(pad.Description); pad.TestParser(); output.Add("PASS Synthetic HID reports: coordinates, tip, confidence, multi-contact release"); return; } }
                catch (Exception) { }
                uint count = 0, size = (uint)Marshal.SizeOf(typeof(Native.DeviceList));
                if (Native.GetRawInputDeviceList(null, ref count, size) == uint.MaxValue) throw new Exception("Device enumeration failed");
                var list = new Native.DeviceList[count];
                if (Native.GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) throw new Exception("Device enumeration failed");
                foreach (var device in list)
                {
                    if (device.Type != 2) continue;
                    try { using (var pad = new Touchpad(device.Device)) { output.Add(pad.Description); pad.TestParser(); output.Add("PASS Synthetic HID reports: coordinates, tip, confidence, multi-contact release"); return; } }
                    catch (Exception) { }
                }
                throw new Exception("No readable touchpad descriptor");
            });
            test("Haptic controller discovery", delegate
            {
                using (var probe = new Haptics())
                {
                    probe.Open();
                    output.Add(probe.Description);
                    probe.Dispose();
                }
            });
            if (verifyHaptics)
            {
                test("Haptic pulses (rest a finger on the touchpad while this runs)", delegate
                {
                    using (var probe = new Haptics())
                    {
                        probe.Open();
                        if (!probe.Available) throw new Exception("This touchpad exposes no writable haptic control");
                        int sent = 0;
                        foreach (Haptics.Kind kind in new[] { Haptics.Kind.Arm, Haptics.Kind.Step, Haptics.Kind.Step, Haptics.Kind.Step, Haptics.Kind.Limit })
                        { if (probe.Fire(kind)) sent++; Thread.Sleep(400); }
                        output.Add("Pulses the device accepted: " + sent + " of 5");
                        output.Add(probe.Triggered
                            ? "The pad accepts waveform triggers, so every pulse should be felt."
                            : "The pad has no waveform trigger; pulses went out as intensity writes, which many pads do not play at all.");
                        assert(sent == 5);
                    }
                });
            }
            output.Add("Failures=" + errors);
            File.WriteAllLines(Path.Combine(Program.DataDir, "self-test.txt"), output, new UTF8Encoding(false));
            return errors == 0 ? 0 : 1;
        }
    }
}
