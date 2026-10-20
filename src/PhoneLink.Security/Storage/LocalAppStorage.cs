using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace PhoneLink.Security.Storage;

public static class LocalAppStorage
{
    public static bool HasPackageIdentity { get; } = DetectPackageIdentity();

    public static string DirectoryPath => HasPackageIdentity
        ? ApplicationData.Current.LocalFolder.Path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MercyProductions", "PhoneLink");

    private static bool DetectPackageIdentity()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
        return result switch
        {
            122 => true, // ERROR_INSUFFICIENT_BUFFER: a package name is available.
            15700 => false, // APPMODEL_ERROR_NO_PACKAGE.
            _ => throw new Win32Exception(result, "Package identity could not be determined."),
        };
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
