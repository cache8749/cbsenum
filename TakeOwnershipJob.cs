// Translation of TakeOwnershipJob.pas

using Microsoft.Win32;

namespace CBSEnum;

public sealed class TakeOwnershipJob : ProcessingThread
{
    protected override void Execute()
    {
        TakeRegistryOwnership();
    }

    private void TakeRegistryOwnership()
    {
        Log("Claiming SeTakeOwnershipPrivilege...");
        if (!AclHelpers.ClaimPrivilege(AclHelpers.SE_TAKE_OWNERSHIP_NAME, out var privToken))
            throw new System.ComponentModel.Win32Exception();

        nint pSidAdmin = nint.Zero;
        try
        {
            Log("Getting BUILTIN\\Administrators SID...");
            pSidAdmin = AclHelpers.AllocateSidBuiltinAdministrators();

            TakeRegistryOwnershipOfKey(@"\" + Constants.CbsKey, pSidAdmin);
        }
        finally
        {
            if (pSidAdmin != nint.Zero) AclHelpers.NativeMethods.FreeSid(pSidAdmin);
            Log("Releasing SeTakeOwnershipPrivilege...");
            AclHelpers.ReleasePrivilege(privToken);
        }

        Log("Done.");
    }

    // Called recursively for every subkey. AKey must start with \.
    private void TakeRegistryOwnershipOfKey(string aKey, nint newOwner)
    {
        if (Terminated) return;
        Log("Processing key " + aKey);

        // Full path for security API: "MACHINE\Software\..."
        string secPath = Constants.CbsRootSec + aKey;

        uint err = AclHelpers.SwitchOwnership(
            secPath, AclHelpers.SE_REGISTRY_KEY, newOwner,
            out nint previousOwner, out nint previousDesc);
        if (err != 0) throw new System.ComponentModel.Win32Exception((int)err);

        if (previousOwner != nint.Zero)
        {
            Log("...ownership taken, granting permissions to previous owner");
            AclHelpers.AddExplicitPermissions(
                secPath, AclHelpers.SE_REGISTRY_KEY, previousOwner,
                (uint)AclHelpers.KEY_ALL_ACCESS);
            if (previousDesc != nint.Zero) AclHelpers.LocalFree(previousDesc);
        }
        else
        {
            Log("...already owned.");
        }

        err = AclHelpers.AddExplicitPermissions(
            secPath, AclHelpers.SE_REGISTRY_KEY, newOwner,
            (uint)AclHelpers.KEY_ALL_ACCESS);
        if (err != 0) throw new System.ComponentModel.Win32Exception((int)err);

        // Recurse into subkeys
        using var reg = Registry.LocalMachine.OpenSubKey(aKey.TrimStart('\\'));
        if (reg is null) return;

        foreach (string subkeyName in reg.GetSubKeyNames())
        {
            if (Terminated) return;
            TakeRegistryOwnershipOfKey(aKey + @"\" + subkeyName, newOwner);
        }
    }
}
