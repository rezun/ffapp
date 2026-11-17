using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace FFApp.Remote;

/// <summary>Stores secrets such as the server agent token outside <c>settings.json</c>.</summary>
public interface ISecretStore
{
    string? Read(string name);
    void Write(string name, string secret);
    void Delete(string name);
}

public static class SecretNames
{
    public static readonly string AgentToken = "server-agent-token";
}

public static class SecretStores
{
    /// <summary>Keychain on macOS, DPAPI on Windows, a file readable only by the user on Linux.</summary>
    public static ISecretStore ForPlatform(string appDataDirectory)
    {
        var directory = Path.Combine(appDataDirectory, "secrets");
        if (OperatingSystem.IsMacOS())
            return new KeychainSecretStore();
        if (OperatingSystem.IsWindows())
            return new DpapiSecretStore(directory);
        return new FileSecretStore(directory);
    }
}

/// <summary>Plain files with mode 0600 in a 0700 directory.</summary>
public sealed class FileSecretStore(string directory) : ISecretStore
{
    private static readonly UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string? Read(string name)
    {
        var path = PathFor(name);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }

    public void Write(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, DirectoryMode);
        var path = PathFor(name);
        File.Delete(path);
        var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = FileMode;
        using var stream = new FileStream(path, options);
        stream.Write(Encoding.UTF8.GetBytes(secret));
    }

    public void Delete(string name) => File.Delete(PathFor(name));

    private string PathFor(string name) => Path.Combine(directory, name);
}

/// <summary>Secrets encrypted for the current Windows user.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string directory) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FFApp secret store");

    public string? Read(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Encrypted for another user or machine: treat as missing so the user can enter it again.
            return null;
        }
    }

    public void Write(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(PathFor(name),
            ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
    }

    public void Delete(string name) => File.Delete(PathFor(name));

    private string PathFor(string name) => Path.Combine(directory, name + ".bin");
}

/// <summary>Generic passwords in the user's login keychain (service "FFApp").</summary>
[SupportedOSPlatform("macos")]
public sealed partial class KeychainSecretStore : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly int ItemNotFound = -25300;
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("FFApp");

    public string? Read(string name)
    {
        var account = Encoding.UTF8.GetBytes(name);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out var length, out var data, out var item);
        if (status == ItemNotFound)
            return null;
        Check(status, "read");
        try
        {
            return Marshal.PtrToStringUTF8(data, (int)length);
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            Release(item);
        }
    }

    public void Write(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var account = Encoding.UTF8.GetBytes(name);
        var password = Encoding.UTF8.GetBytes(secret);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out _, out var data, out var item);
        if (status == ItemNotFound)
        {
            Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                (uint)password.Length, password, out var added), "save");
            Release(added);
            return;
        }
        Check(status, "read");
        try
        {
            Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password), "update");
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            Release(item);
        }
    }

    public void Delete(string name)
    {
        var account = Encoding.UTF8.GetBytes(name);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out _, out var data, out var item);
        if (status == ItemNotFound)
            return;
        Check(status, "read");
        try
        {
            Check(SecKeychainItemDelete(item), "delete");
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            Release(item);
        }
    }

    private static void Release(IntPtr reference)
    {
        if (reference != IntPtr.Zero)
            CFRelease(reference);
    }

    private static void Check(int status, string action)
    {
        if (status != 0)
            throw new InvalidOperationException($"The keychain could not {action} the secret (OSStatus {status}).");
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);
}
