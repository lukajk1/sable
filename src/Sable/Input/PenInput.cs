using System.Runtime.InteropServices;

namespace Sable.Input;

/// <summary>
/// Windows Ink pen input for the raylib window: pressure and contact from WM_POINTER messages.
/// </summary>
/// <remarks>
/// GLFW ignores WM_POINTER, so the window procedure is subclassed to read pen data on the side. Every message is
/// still passed on unchanged: GLFW needs its own messages, and Windows only promotes pen input to mouse messages
/// when WM_POINTER reaches DefWindowProc, which is what keeps the pen working as a cursor.
/// </remarks>
public static unsafe class PenInput
{
    private const int GWLP_WNDPROC = -4;

    private const uint WM_POINTERUPDATE = 0x0245;
    private const uint WM_POINTERDOWN = 0x0246;
    private const uint WM_POINTERUP = 0x0247;
    private const uint WM_POINTERLEAVE = 0x024A;

    private const uint PT_PEN = 3;
    private const uint POINTER_FLAG_INCONTACT = 0x4;
    private const uint PEN_MASK_PRESSURE = 0x1;

    private const string TabletPenServiceProperty = "MicrosoftTabletPenServiceProperty";

    // Press-and-hold right click, pen tap and barrel feedback, and flicks: each one interrupts or delays a stroke.
    private const int TabletDisableFlags = 0x00000001 | 0x00000008 | 0x00000010 | 0x00000100 | 0x00010000;

    private static IntPtr _hwnd;
    private static IntPtr _previousProc;

    /// <summary>True once any pen message has been seen this session.</summary>
    public static bool PenDetected { get; private set; }

    /// <summary>True while the pen tip is touching the tablet.</summary>
    public static bool InContact { get; private set; }

    /// <summary>Pen tip pressure 0..1 while in contact (0 when hovering).</summary>
    public static float Pressure { get; private set; }

    /// <summary>Milliseconds (<see cref="Environment.TickCount64"/>) of the last pen message, so callers can tell pen from mouse.</summary>
    public static long LastPenTime { get; private set; }

    /// <summary>
    /// Hooks the window (HWND from Raylib.GetWindowHandle()). Call once after InitWindow. Safe to call when no pen exists.
    /// </summary>
    public static void Attach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _previousProc != IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            // The docs ask for the atom to exist before the property is set.
            GlobalAddAtomW(TabletPenServiceProperty);
            SetPropW(hwnd, TabletPenServiceProperty, (IntPtr)TabletDisableFlags);
        }
        catch
        {
            // Only gestures stay on; pressure still works.
        }

        try
        {
            IntPtr hook = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc;
            IntPtr previous = SetWindowProc(hwnd, hook);
            if (previous == IntPtr.Zero)
                return;

            _hwnd = hwnd;
            _previousProc = previous;
        }
        catch
        {
            _hwnd = IntPtr.Zero;
            _previousProc = IntPtr.Zero;
        }
    }

    /// <summary>Restores the original window procedure. Call before CloseWindow.</summary>
    public static void Detach()
    {
        if (_previousProc == IntPtr.Zero)
            return;

        try
        {
            SetWindowProc(_hwnd, _previousProc);
            RemovePropW(_hwnd, TabletPenServiceProperty);
        }
        catch
        {
            // The window is about to go away anyway.
        }

        _hwnd = IntPtr.Zero;
        _previousProc = IntPtr.Zero;
        InContact = false;
        Pressure = 0f;
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg is WM_POINTERUPDATE or WM_POINTERDOWN or WM_POINTERUP or WM_POINTERLEAVE)
        {
            // An exception must not unwind into native code, so a bad read just loses this sample.
            try { ReadPointer(msg, (uint)((ulong)wParam & 0xFFFF)); }
            catch { }
        }

        return CallWindowProcW(_previousProc, hwnd, msg, wParam, lParam);
    }

    private static void ReadPointer(uint msg, uint pointerId)
    {
        if (!GetPointerType(pointerId, out uint type) || type != PT_PEN)
            return;

        PenDetected = true;
        LastPenTime = Environment.TickCount64;

        if (msg is WM_POINTERUP or WM_POINTERLEAVE)
        {
            InContact = false;
            Pressure = 0f;
            return;
        }

        if (!GetPointerPenInfo(pointerId, out POINTER_PEN_INFO info))
            return;

        InContact = (info.pointerInfo.pointerFlags & POINTER_FLAG_INCONTACT) != 0;
        if (!InContact)
            Pressure = 0f;
        else if ((info.penMask & PEN_MASK_PRESSURE) != 0)
            Pressure = Math.Clamp(info.pressure / 1024f, 0f, 1f);
        else
            Pressure = 1f; // A pen that reports no pressure still paints at full strength.
    }

    // SetWindowLongPtrW only exists in 64-bit user32; 32-bit exports SetWindowLongW instead.
    private static IntPtr SetWindowProc(IntPtr hwnd, IntPtr proc) =>
        IntPtr.Size == 8
            ? SetWindowLongPtrW(hwnd, GWLP_WNDPROC, proc)
            : (IntPtr)SetWindowLongW(hwnd, GWLP_WNDPROC, (int)proc);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO penInfo);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPropW(IntPtr hWnd, string lpString, IntPtr hData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RemovePropW(IntPtr hWnd, string lpString);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort GlobalAddAtomW(string lpString);
}
