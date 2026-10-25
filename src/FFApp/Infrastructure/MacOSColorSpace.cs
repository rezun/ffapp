using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace FFApp.Infrastructure;

/// <summary>
/// Avalonia renders a window into a CAMetalLayer without a color space, so macOS sends its 8-bit values to the
/// display unconverted and sRGB content looks oversaturated on wide-gamut (Display P3) panels. Tagging the layer
/// as sRGB lets the compositor convert for whichever display shows the window. For SDR video this matches
/// color-managed Windows, browsers and mpv: BT.709 primaries with the sRGB (≈ 2.2) tone curve.
/// </summary>
internal static partial class MacOSColorSpace
{
    // Library names must be constants to appear in LibraryImport attributes.
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly string SrgbNameSymbol = "kCGColorSpaceSRGB";
    private static readonly int MaxFrameAttempts = 60;

    /// <summary>
    /// Tags the window's rendering layer as sRGB once it exists. Avalonia creates the Metal layer when it renders
    /// the first frame, after Opened and Loaded, so the tag is retried on the following frames.
    /// </summary>
    public static void TagSrgbWhenRendered(TopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);
        if (!OperatingSystem.IsMacOS()) return;
        RetryOnFrames(() => TryTagSrgb(topLevel), next => topLevel.RequestAnimationFrame(_ => next()), MaxFrameAttempts);
    }

    internal static void RetryOnFrames(Func<bool> attempt, Action<Action> onNextFrame, int attempts)
    {
        if (attempts <= 0 || attempt()) return;
        onNextFrame(() => RetryOnFrames(attempt, onNextFrame, attempts - 1));
    }

    /// <summary>Tags the window's rendering layer as sRGB. False off macOS or before the layer exists.</summary>
    public static bool TryTagSrgb(TopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);
        if (!OperatingSystem.IsMacOS() || topLevel.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle)
            return false;
        return TryTagSrgb(Send(handle.NSView, Selector("layer")));
    }

    /// <summary>CAMetalLayer and CAOpenGLLayer accept a color space; plain layers do not.</summary>
    internal static bool TryTagSrgb(nint layer)
    {
        if (layer == 0 || !SendBool(layer, Selector("respondsToSelector:"), Selector("setColorspace:"))) return false;
        var srgb = CGColorSpaceCreateWithName(SrgbName());
        if (srgb == 0) return false;
        // The layer retains the color space.
        try { Send(layer, Selector("setColorspace:"), srgb); }
        finally { CGColorSpaceRelease(srgb); }
        return true;
    }

    internal static bool IsTaggedSrgb(nint layer)
    {
        if (layer == 0 || !SendBool(layer, Selector("respondsToSelector:"), Selector("colorspace"))) return false;
        var space = Send(layer, Selector("colorspace"));
        if (space == 0) return false;
        var name = CGColorSpaceCopyName(space);
        if (name == 0) return false;
        try { return CFEqual(name, SrgbName()); }
        finally { CFRelease(name); }
    }

    private static nint SrgbName() =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreGraphics), SrgbNameSymbol));

    private static nint Selector(string name) => sel_registerName(name);

    [LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial void Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SendBool(nint receiver, nint selector, nint argument);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGColorSpaceCreateWithName(nint name);

    [LibraryImport(CoreGraphics)]
    private static partial void CGColorSpaceRelease(nint space);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGColorSpaceCopyName(nint space);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFEqual(nint first, nint second);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
