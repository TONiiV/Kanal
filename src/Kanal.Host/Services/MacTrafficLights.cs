using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Kanal.Host.Services;

public static class MacTrafficLights
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint(double x, double y) { public double X = x, Y = y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize(double w, double h) { public double W = w, H = h; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect { public double X, Y, W, H; }

    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint self, nint sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint self, nint sel, long arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Send(nint self, nint sel, CGPoint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Send(nint self, nint sel, CGSize arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Send(nint self, nint sel, CGRect arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(nint self, nint sel);

    // Avalonia ignores ExtendClientAreaTitleBarHeightHint for the title bar view: it stays 28 high,
    // so the lights sit near the top of a taller header. Resize the view, then place each light.
    public static void Apply(Window window, double barHeight)
    {
        // objc_msgSend returns a CGRect in registers on arm64 only; Intel needs objc_msgSend_stret.
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;
        if (window.WindowState == WindowState.FullScreen) return;
        var ns = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (ns == 0) return;
        try
        {
            var standardButton = sel_registerName("standardWindowButton:");
            var bar = Send(Send(ns, standardButton, 0L), sel_registerName("superview"));
            var container = Send(bar, sel_registerName("superview"));
            var outer = SendRect(Send(container, sel_registerName("superview")), sel_registerName("frame"));
            var setFrame = sel_registerName("setFrame:");
            Send(container, setFrame, new CGRect { Y = outer.H - barHeight, W = outer.W, H = barHeight });
            Send(bar, setFrame, new CGRect { W = outer.W, H = barHeight });

            for (var index = 0; index < 3; index++)
            {
                var button = Send(ns, standardButton, (long)index);
                var native = SendRect(button, sel_registerName("bounds"));
                var frame = TrafficLightLayout.Frame(index, barHeight, native.W, native.H);
                Send(button, sel_registerName("setFrameSize:"), new CGSize(frame.Width, frame.Height));
                // Frame and bounds sizes differing is what scales the drawing.
                Send(button, sel_registerName("setBoundsSize:"), new CGSize(native.W, native.H));
                Send(button, sel_registerName("setFrameOrigin:"), new CGPoint(frame.X, frame.Y));
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }
}
