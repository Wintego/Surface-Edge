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
            if (args.Contains("--self-test") || args.Contains("--verify-controls") || args.Contains("--verify-osd"))
                return Tests.Run(args.Contains("--verify-controls"), args.Contains("--verify-osd"));
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
        internal double Edge = .08, Sensitivity = 1.2;
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
            if (f.Id != id || (side < 0 ? f.X > Edge + .025 : f.X < 1 - Edge - .025))
            { blocked = true; active = false; return 0; }
            if (!active)
            {
                double vertical = Math.Abs(f.Y - startY);
                if (vertical < .025) return 0;
                if (Math.Abs(f.X - startX) > vertical) { blocked = true; return 0; }
                active = true;
                previousY = f.Y;
                return 0;
            }
            double delta = (previousY - f.Y) * 100 * Sensitivity;
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
    }

    internal sealed class Touchpad : IDisposable
    {
        private readonly IntPtr data;
        private readonly Native.ValueCaps[] values;
        private readonly List<Native.ValueCaps> xs;
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
                values = new Native.ValueCaps[count];
                if (Native.HidP_GetValueCaps(0, values, ref count, data) != Native.Success) throw new InvalidOperationException("HID value capabilities unavailable");
                xs = values.Where(v => v.Has(1, 0x30) && v.Link != 0 && v.Max > v.Min).OrderBy(v => v.Link).ToList();
                if (xs.Count == 0) throw new InvalidOperationException("Touchpad X coordinate not found");
                reportLength = caps.InputLength;
                Description = "HID touchpad: reports=" + caps.InputLength + " bytes; contact slots=" + xs.Count + "; X=" + xs[0].Min + ".." + xs[0].Max;
            }
            catch { Marshal.FreeHGlobal(data); throw; }
        }

        private bool Value(byte[] report, ushort page, ushort link, ushort usage, out uint value)
        { return Native.HidP_GetUsageValue(0, page, link, usage, out value, data, report, (uint)report.Length) == Native.Success; }

        private ushort[] Buttons(byte[] report, ushort page, ushort link)
        {
            ushort[] usages = new ushort[32]; uint count = (uint)usages.Length;
            if (Native.HidP_GetUsages(0, page, link, usages, ref count, data, report, (uint)report.Length) != Native.Success) return new ushort[0];
            return usages.Take((int)count).ToArray();
        }

        internal Frame Parse(byte[] report)
        {
            if (report.Length == 0) return null;
            uint count = 0, stamp = 0;
            bool hasCount = false;
            foreach (var v in values)
            {
                if (v.ReportId != report[0]) continue;
                if (v.Has(13, 0x54)) hasCount = Value(report, 13, v.Link, 0x54, out count);
                if (v.Has(13, 0x56)) Value(report, 13, v.Link, 0x56, out stamp);
            }
            if (!hasCount) return null;
            // Hybrid HID frames may span several reports. Count=0 can mean a
            // continuation, not finger-up; never turn it into a new gesture.
            if (count > 0)
            {
                expected = (int)Math.Min(count, 256); received = 0; scan = stamp; anyTip = false;
                pending = new Frame { Count = expected };
            }
            else if (pending == null || received >= expected || stamp != scan)
            { pending = null; expected = received = 0; return new Frame(); }
            foreach (var x in xs)
            {
                if (x.ReportId != report[0]) continue;
                uint xv, yv, id;
                Native.ValueCaps y = values.FirstOrDefault(v => v.Link == x.Link && v.ReportId == x.ReportId && v.Has(1, 0x31));
                if (y.Max <= y.Min || !Value(report, 1, x.Link, 0x30, out xv) || !Value(report, 1, x.Link, 0x31, out yv) || !Value(report, 13, x.Link, 0x51, out id)) continue;
                ushort[] buttons = Buttons(report, 13, x.Link);
                pending.Id = (int)id;
                pending.X = Math.Max(0, Math.Min(1, ((double)xv - x.Min) / (x.Max - x.Min)));
                pending.Y = Math.Max(0, Math.Min(1, ((double)yv - y.Min) / (y.Max - y.Min)));
                pending.Tip = buttons.Contains((ushort)0x42);
                anyTip |= pending.Tip;
                pending.Confident = buttons.Contains((ushort)0x47);
                pending.Click = Buttons(report, 9, 0).Length != 0;
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
                byte id = xs[0].ReportId;
                if (Native.HidP_InitializeReportForID(0, id, data, report, (uint)report.Length) != Native.Success) throw new Exception("Cannot initialize test HID report");
                Action<ushort, ushort, ushort, uint> set = delegate(ushort page, ushort link, ushort usage, uint value)
                {
                    if (Native.HidP_SetUsageValue(0, page, link, usage, value, data, report, (uint)report.Length) != Native.Success) throw new Exception("Cannot set test HID usage " + usage);
                };
                var countCap = values.First(v => v.ReportId == id && v.Has(13, 0x54));
                set(13, countCap.Link, 0x54, (uint)contacts);
                foreach (var x in xs.Where(v => v.ReportId == id).Take(contacts))
                {
                    var y = values.First(v => v.ReportId == id && v.Link == x.Link && v.Has(1, 0x31));
                    set(1, x.Link, 0x30, (uint)(x.Min + (x.Max - x.Min) / 50));
                    set(1, x.Link, 0x31, (uint)(y.Min + (y.Max - y.Min) / 2));
                    set(13, x.Link, 0x51, (uint)x.Link);
                    ushort[] usages = tip ? new ushort[] { 0x42, 0x47 } : new ushort[] { 0x47 };
                    uint n = (uint)usages.Length;
                    if (Native.HidP_SetUsages(0, 13, x.Link, usages, ref n, data, report, (uint)report.Length) != Native.Success) throw new Exception("Cannot set test tip/confidence");
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

    internal sealed class EdgeApp : Form
    {
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly Gesture gesture = new Gesture();
        private readonly Dictionary<IntPtr, Touchpad> devices = new Dictionary<IntPtr, Touchpad>();
        private readonly Dictionary<IntPtr, long> failed = new Dictionary<IntPtr, long>();
        private readonly OutputWorker worker = new OutputWorker();
        private readonly Osd osd = new Osd();
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
        private long packets, frames, lastInput, lastError;
        private long osdRetryAfter;
        private string lastFrame = "No touchpad input yet";
        private readonly string settings = Path.Combine(Program.DataDir, "settings.ini");

        internal EdgeApp(bool diagnose)
        {
            this.diagnose = diagnose;
            ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Text = "SurfaceEdge";
            if (File.Exists(settings))
            {
                string[] lines = File.ReadAllLines(settings); int edge, speed;
                if (lines.Length >= 2 && int.TryParse(lines[0], out edge) && int.TryParse(lines[1], out speed))
                { gesture.Edge = Math.Max(4, Math.Min(15, edge)) / 100.0; gesture.Sensitivity = Math.Max(50, Math.Min(250, speed)) / 100.0; }
            }
            var pause = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
            pause.CheckedChanged += delegate { enabled = pause.Checked; gesture.Reset(); tray.Text = enabled ? "SurfaceEdge - enabled" : "SurfaceEdge - paused"; };
            menu.Items.Add(pause);
            var edgeMenu = new ToolStripMenuItem("Edge width");
            foreach (int width in new[] { 5, 8, 12 })
            {
                int value = width;
                var item = new ToolStripMenuItem(width + "%") { Checked = Math.Abs(gesture.Edge * 100 - width) < .1 };
                item.Click += delegate { gesture.Edge = value / 100.0; SelectItem(edgeMenu, item); SaveSettings(); };
                edgeMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(edgeMenu);
            var speedMenu = new ToolStripMenuItem("Sensitivity");
            foreach (int speed in new[] { 80, 120, 200 })
            {
                int value = speed;
                var item = new ToolStripMenuItem((speed / 100.0).ToString("0.0") + "x") { Checked = Math.Abs(gesture.Sensitivity * 100 - speed) < .1 };
                item.Click += delegate { gesture.Sensitivity = value / 100.0; SelectItem(speedMenu, item); SaveSettings(); };
                speedMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(speedMenu);
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
                catch (Exception e) { NotifyError(e.Message); }
            };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Status / help", null, delegate { ShowStatus(); });
            menu.Items.Add("Open log", null, delegate { Process.Start("notepad.exe", "\"" + Program.LogPath + "\""); });
            menu.Items.Add("Exit", null, delegate { Close(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SurfaceEdge - enabled", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowStatus(); };
            worker.Result += delegate(int side, double value, string error)
            {
                if (closing || !IsHandleCreated) return;
                try { BeginInvoke((Action)delegate { if (closing) return; if (error != null) NotifyError(error); else ShowOsd(side); }); }
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
                        if (error != null) { gesture.Reset(); NotifyError(error); return; }
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
            Program.Log("Started; " + Tests.Architecture() + "; diagnostic=" + diagnose);
            timer.Tick += delegate
            {
                if (clock.ElapsedMilliseconds - lastInput > 350) gesture.Reset();
                if (diagnose && clock.ElapsedMilliseconds >= 15000)
                { Program.Log("Diagnostic complete: packets=" + packets + "; frames=" + frames + "; " + lastFrame); Close(); }
            };
            timer.Start();
            if (!diagnose) tray.ShowBalloonTip(4000, "SurfaceEdge", "Left edge: brightness. Right edge: volume. Swipe up/down with one finger. Right-click this icon for settings.", ToolTipIcon.Info);
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }
        private void SelectItem(ToolStripMenuItem parent, ToolStripMenuItem selected)
        { foreach (ToolStripMenuItem item in parent.DropDownItems) item.Checked = item == selected; }
        private void SaveSettings()
        {
            gesture.Reset();
            try { File.WriteAllLines(settings, new[] { Math.Round(gesture.Edge * 100).ToString(), Math.Round(gesture.Sensitivity * 100).ToString() }, new UTF8Encoding(false)); }
            catch (Exception e) { NotifyError(e.Message); }
        }
        private static bool StartupEnabled()
        { using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) return key != null && key.GetValue("SurfaceEdge") != null; }
        private void NotifyError(string error)
        {
            if (clock.ElapsedMilliseconds - lastError < 5000 && lastError != 0) return;
            lastError = clock.ElapsedMilliseconds;
            tray.ShowBalloonTip(5000, "SurfaceEdge", error, ToolTipIcon.Warning);
        }
        private void ShowOsd(int side)
        {
            if (clock.ElapsedMilliseconds < osdRetryAfter) return;
            try { osd.Display(side); }
            catch (Exception e)
            {
                osdRetryAfter = clock.ElapsedMilliseconds + 10000;
                Program.Log("Native OSD error: " + e);
                NotifyError("Windows " + (side < 0 ? "brightness" : "volume") + " OSD is unavailable. Adjustment still works. See log.");
            }
        }
        private void ShowStatus()
        {
            MessageBox.Show("Left edge: brightness\nRight edge: volume\nUp: increase; down: decrease\n\nStart with ONE finger inside the outer " + Math.Round(gesture.Edge * 100) + "% of the touchpad. Lift to finish.\nTwo fingers and physical clicks cancel the gesture.\nThe mouse pointer can still move.\n\n" + Tests.Architecture() + "\nRaw HID packets: " + packets + "\nParsed frames: " + frames + "\n" + lastFrame + "\n\nLog: " + Program.LogPath, "SurfaceEdge", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0xFF)
            {
                try { ReadInput(m.LParam); }
                catch (Exception e) { gesture.Reset(); Program.Log("Input error: " + e.Message); NotifyError("Touchpad input error. See log."); }
            }
            else if (m.Msg == 0xFE || m.Msg == 0x218)
            {
                foreach (var pad in devices.Values) pad.Dispose();
                devices.Clear(); failed.Clear(); gesture.Reset(); haveDevice = false;
            }
            base.WndProc(ref m);
        }
        private void ReadInput(IntPtr input)
        {
            uint size = 0, headerSize = (uint)Marshal.SizeOf(typeof(Native.Header));
            if (Native.GetRawInputData(input, 0x10000003, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize + 8 || size > 1048576) return;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
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
                    catch (Exception e) { failed[header.Device] = clock.ElapsedMilliseconds + 10000; Program.Log(e.Message); NotifyError(e.Message); return; }
                }
                if (!haveDevice || activeDevice != header.Device) { gesture.Reset(); activeDevice = header.Device; haveDevice = true; }
                byte[] report = new byte[reportSize];
                for (uint i = 0; i < count; i++)
                {
                    Marshal.Copy(IntPtr.Add(buffer, checked((int)(headerSize + 8 + i * reportSize))), report, 0, report.Length);
                    Frame frame = pad.Parse(report);
                    if (frame == null) continue;
                    frames++; lastInput = clock.ElapsedMilliseconds;
                    lastFrame = string.Format("Contacts={0}; tip={1}; confidence={2}; X={3:0.000}; Y={4:0.000}", frame.Count, frame.Tip, frame.Confident, frame.X, frame.Y);
                    if (diagnose) { if (frames <= 10 || frames % 100 == 0) Program.Log(lastFrame); continue; }
                    if (!enabled) continue;
                    bool wasActive = gesture.Active;
                    double delta = gesture.Feed(frame, clock.ElapsedMilliseconds);
                    if (!wasActive && gesture.Active)
                    {
                        targetReady = false; pendingDelta = 0; lastSent = -1;
                        worker.Read(gesture.Side, ++generation);
                    }
                    if (delta == 0) continue;
                    if (!targetReady) pendingDelta += delta;
                    else ChangeTarget(delta);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        private void ChangeTarget(double delta)
        {
            target = Math.Max(gesture.Side < 0 ? 1 : 0, Math.Min(100, target + delta));
            int rounded = (int)Math.Round(target);
            if (rounded != lastSent) { lastSent = rounded; worker.Set(gesture.Side, rounded); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !closing)
            {
                closing = true; timer.Dispose(); worker.Dispose();
                foreach (var pad in devices.Values) pad.Dispose();
                devices.Clear();
                tray.Visible = false; tray.Dispose(); menu.Dispose(); osd.Dispose();
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
        internal static int Run(bool verifyControls, bool verifyOsd)
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
            output.Add("Failures=" + errors);
            File.WriteAllLines(Path.Combine(Program.DataDir, "self-test.txt"), output, new UTF8Encoding(false));
            return errors == 0 ? 0 : 1;
        }
    }
}
