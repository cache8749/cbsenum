// Translation of DecouplePackagesJob.pas

using Microsoft.Win32;

namespace CBSEnum;

/// <summary>
/// Removes <c>Owners</c> subkeys from packages in the CBS registry, detaching them
/// from their parent package ("Windows Home", "Windows Professional", etc.)
/// so that DISM can uninstall them independently.
/// </summary>
public sealed class DecouplePackagesJob : ProcessingThread
{
    // null / empty = all packages
    private readonly string[]? _packageNames;

    public DecouplePackagesJob(string[]? packageNames)
    {
        _packageNames = packageNames;
    }

    protected override void Execute()
    {
        DecouplePackages(Constants.CbsKey + @"\Packages", _packageNames);
        DecouplePackages(Constants.CbsKey + @"\PackageNames", _packageNames);
    }

    private void DecouplePackages(string keyPath, string[]? packageNames)
    {
        Log("Trying " + keyPath + "...");

        using var reg = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
        if (reg is null)
        {
            Log("No such key.");
            return;
        }

        foreach (string subkeyName in reg.GetSubKeyNames())
        {
            if (Terminated) break;
            Log(subkeyName + "...");

            bool include = packageNames is null or { Length: 0 }
                || packageNames.Any(n => n.Equals(subkeyName, StringComparison.OrdinalIgnoreCase));
            if (!include) continue;

            try
            {
                reg.DeleteSubKeyTree("Owners", throwOnMissingSubKey: false);
                Log("Owners removed.");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log("Access denied: " + ex.Message);
            }
        }
    }
}
