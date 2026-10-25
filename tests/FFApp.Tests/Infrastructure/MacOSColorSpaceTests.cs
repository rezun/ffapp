using System.Runtime.InteropServices;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Core Animation layers exist only on macOS.";
    }
}

public sealed class MacOSColorSpaceTests
{
    private static readonly string QuartzCore = "/System/Library/Frameworks/QuartzCore.framework/QuartzCore";

    [MacOSFact]
    public void TryTagSrgb_TagsMetalLayer()
    {
        var layer = CreateLayer("CAMetalLayer");
        try
        {
            MacOSColorSpace.IsTaggedSrgb(layer).Should().BeFalse("Avalonia's layers start without a color space");
            MacOSColorSpace.TryTagSrgb(layer).Should().BeTrue();
            MacOSColorSpace.IsTaggedSrgb(layer).Should().BeTrue();
        }
        finally { Release(layer); }
    }

    [MacOSFact]
    public void TryTagSrgb_LeavesLayersWithoutColorSpaceAlone()
    {
        var layer = CreateLayer("CALayer");
        try
        {
            MacOSColorSpace.TryTagSrgb(layer).Should().BeFalse();
            MacOSColorSpace.IsTaggedSrgb(layer).Should().BeFalse();
        }
        finally { Release(layer); }
    }

    [Fact]
    public void TryTagSrgb_IgnoresMissingLayer() => MacOSColorSpace.TryTagSrgb(0).Should().BeFalse();

    private static nint CreateLayer(string className)
    {
        NativeLibrary.Load(QuartzCore);
        var layer = Send(objc_getClass(className), sel_registerName("new"));
        layer.Should().NotBe(0);
        return layer;
    }

    private static void Release(nint layer) => Send(layer, sel_registerName("release"));

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);
}
