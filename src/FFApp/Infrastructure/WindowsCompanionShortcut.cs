using System.Runtime.Versioning;
using Velopack.Locators;
using Velopack.Windows;

namespace FFApp.Infrastructure;

/// <summary>Registers the bundled FFRux alongside Velopack's primary FFApp shortcut.</summary>
internal static class WindowsCompanionShortcut
{
    private const string ShortcutName = "FFRux.lnk";
    private const string AlternateShortcutName = "FFRux (FFApp).lnk";

    public static void Create()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var locator = VelopackLocator.Current;
        if (locator.IsPortable || locator.CurrentlyInstalledVersion is null || locator.AppContentDir is not { } content)
            return;

        // Velopack's current directory is replaced during updates; never link to a versioned staging folder.
        var executable = Path.Combine(content, "companion", "FFRux", "FFRux.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The bundled FFRux executable is missing.", executable);

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(programs);
        var path = Path.Combine(programs, ShortcutName);
        // A previous standalone FFRux installation has its own shortcut and uninstall lifecycle.
        if (File.Exists(path) && !Targets(path, executable))
            path = Path.Combine(programs, AlternateShortcutName);

        using var shortcut = new ShellLink
        {
            Target = executable,
            WorkingDirectory = Path.GetDirectoryName(executable),
            IconPath = executable,
            Description = "FFRux",
        };
        shortcut.SetAppUserModelId(WindowsAppUserModel.FFRuxId);
        shortcut.Save(path);
    }

    public static void Remove()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var locator = VelopackLocator.Current;
        if (locator.IsPortable || locator.AppContentDir is not { } content)
            return;

        var executable = Path.Combine(content, "companion", "FFRux", "FFRux.exe");
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        foreach (var name in new[] { ShortcutName, AlternateShortcutName })
        {
            var path = Path.Combine(programs, name);
            if (File.Exists(path) && Targets(path, executable))
                File.Delete(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool Targets(string shortcutPath, string executable)
    {
        using var shortcut = new ShellLink(shortcutPath);
        return string.Equals(shortcut.Target, executable, StringComparison.OrdinalIgnoreCase);
    }
}
