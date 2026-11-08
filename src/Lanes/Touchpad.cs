using System.Runtime.InteropServices;
using Lanes.Core;
using static Lanes.Native;

namespace Lanes;

// Raw HID reports from every precision touchpad, turned into finger positions in pad widths.
// Each finger is its own link collection holding a tip switch, contact ID and X/Y. The Magic
// Trackpad driver fills only the first ContactCount of them; the rest hold leftover memory,
// sometimes with the tip switch set, so only those slots are read (as in TrackpadClicks).
sealed class Touchpad
{
    sealed class Pad
    {
        public IntPtr Preparsed;
        public ushort LinkCollections;
        public int XMin, XMax, YMin, YMax;
        public double Aspect = 1; // height / width, physical when the device says so
    }

    readonly Action<IntPtr, TouchFrame> _onFrame;
    readonly Dictionary<IntPtr, Pad?> _pads = new();
    readonly ushort[] _usages = new ushort[32];
    readonly List<Finger> _fingers = new(8);
    bool _loggedParseError;

    public Touchpad(Action<IntPtr, TouchFrame> onFrame) => _onFrame = onFrame;

    // Reports that carried a contact count but no readable finger, for the log.
    public int Dropped;

    public void Register(IntPtr hwnd)
    {
        var dev = new[] { new RAWINPUTDEVICE { UsagePage = 0x0D, Usage = 0x05, Flags = RIDEV_INPUTSINK, Target = hwnd } };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Write($"RegisterRawInputDevices failed {Marshal.GetLastWin32Error()}");
    }

    public void OnInput(IntPtr handle)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(handle, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(handle, RID_INPUT, buf, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (header.Type != RIM_TYPEHID) return;
            var pad = GetPad(header.Device);
            if (pad == null) return;
            uint reportSize = (uint)Marshal.ReadInt32(buf, (int)headerSize);
            uint count = (uint)Marshal.ReadInt32(buf, (int)headerSize + 4);
            IntPtr report = buf + (int)headerSize + 8;
            long now = Environment.TickCount64;
            for (uint i = 0; i < count; i++, report += (int)reportSize)
                if (Parse(pad, report, reportSize, out var frame))
                    _onFrame(header.Device, frame with { Time = now });
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    bool Parse(Pad pad, IntPtr report, uint length, out TouchFrame frame)
    {
        frame = default;
        if (HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x54, out uint contacts, pad.Preparsed, report, length) != HIDP_STATUS_SUCCESS)
        {
            if (!_loggedParseError) Log.Write("contact count parse failed");
            _loggedParseError = true;
            return false;
        }
        _fingers.Clear();
        int slots = Math.Min((int)contacts, pad.LinkCollections - 1), readable = 0;
        double w = Math.Max(1, pad.XMax - pad.XMin), h = Math.Max(1, pad.YMax - pad.YMin);
        for (ushort link = 1; link <= slots; link++)
        {
            if (HidP_GetUsageValue(HidP_Input, 0x01, link, 0x31, out uint y, pad.Preparsed, report, length) != HIDP_STATUS_SUCCESS
                || y < pad.YMin || y > pad.YMax
                || HidP_GetUsageValue(HidP_Input, 0x01, link, 0x30, out uint x, pad.Preparsed, report, length) != HIDP_STATUS_SUCCESS
                || x < pad.XMin || x > pad.XMax)
                continue;
            readable++;
            uint n = (uint)_usages.Length;
            if (HidP_GetUsages(HidP_Input, 0x0D, link, _usages, ref n, pad.Preparsed, report, length) != HIDP_STATUS_SUCCESS
                || !_usages.AsSpan(0, (int)n).Contains((ushort)0x42))
                continue;
            HidP_GetUsageValue(HidP_Input, 0x0D, link, 0x51, out uint id, pad.Preparsed, report, length);
            _fingers.Add(new Finger(id, (x - pad.XMin) / w, (y - pad.YMin) / h * pad.Aspect));
        }
        // Fingers whose tip switch is off are lifting: a report of only those is the lift itself
        // (the Magic Trackpad driver sends it with the contact count still set). A report that
        // counts contacts but carries none readable says nothing new.
        if (contacts > 0 && readable == 0) { Dropped++; return false; }
        frame = new TouchFrame(0, _fingers.ToArray());
        return true;
    }

    Pad? GetPad(IntPtr device)
    {
        if (_pads.TryGetValue(device, out var pad)) return pad;
        pad = null;
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size > 0)
        {
            IntPtr p = Marshal.AllocHGlobal((int)size);
            if ((int)GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, p, ref size) > 0) pad = Describe(p);
            else Marshal.FreeHGlobal(p);
        }
        _pads[device] = pad;
        Log.Write($"touchpad {device}: {pad?.LinkCollections} link collections, x {pad?.XMin}..{pad?.XMax}, y {pad?.YMin}..{pad?.YMax}, aspect {pad?.Aspect:0.###}");
        return pad;
    }

    static Pad Describe(IntPtr preparsed)
    {
        var pad = new Pad { Preparsed = preparsed };
        var caps = new byte[64];
        if (HidP_GetCaps(preparsed, caps) == HIDP_STATUS_SUCCESS) pad.LinkCollections = BitConverter.ToUInt16(caps, 44);
        // HIDP_VALUE_CAPS is 72 bytes: LogicalMin/Max at 40/44, PhysicalMin/Max at 48/52.
        var vc = new byte[72 * 16];
        double physW = 0, physH = 0;
        ushort count = 16;
        if (HidP_GetSpecificValueCaps(HidP_Input, 0x01, 0, 0x30, vc, ref count, preparsed) == HIDP_STATUS_SUCCESS && count > 0)
        {
            pad.XMin = BitConverter.ToInt32(vc, 40);
            pad.XMax = BitConverter.ToInt32(vc, 44);
            physW = BitConverter.ToInt32(vc, 52) - BitConverter.ToInt32(vc, 48);
        }
        count = 16;
        if (HidP_GetSpecificValueCaps(HidP_Input, 0x01, 0, 0x31, vc, ref count, preparsed) == HIDP_STATUS_SUCCESS && count > 0)
        {
            pad.YMin = BitConverter.ToInt32(vc, 40);
            pad.YMax = BitConverter.ToInt32(vc, 44);
            physH = BitConverter.ToInt32(vc, 52) - BitConverter.ToInt32(vc, 48);
        }
        pad.Aspect = physW > 0 && physH > 0 ? physH / physW
            : pad.XMax > pad.XMin ? (double)(pad.YMax - pad.YMin) / (pad.XMax - pad.XMin) : 1;
        return pad;
    }
}
