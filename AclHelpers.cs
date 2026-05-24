// Translation of AclHelpers.pas
// Windows security / ACL helper functions via P/Invoke

using System.Runtime.InteropServices;

namespace CBSEnum;

public static class AclHelpers
{
    // -------------------------------------------------------------------------
    // Constants mirrored from AclHelpers.pas / winnt.h
    // -------------------------------------------------------------------------
    public const uint SE_PRIVILEGE_ENABLED    = 0x00000002;
    public const int  TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const int  TOKEN_QUERY             = 0x0008;

    public const uint SECURITY_BUILTIN_DOMAIN_RID = 0x00000020;
    public const uint DOMAIN_ALIAS_RID_ADMINS     = 0x00000220;

    public const int  KEY_ALL_ACCESS = 0xF003F;

    public const string SE_TAKE_OWNERSHIP_NAME = "SeTakeOwnershipPrivilege";
    public const string SE_BACKUP_NAME         = "SeBackupPrivilege";
    public const string SE_RESTORE_NAME        = "SeRestorePrivilege";
    public const string SE_SECURITY_NAME       = "SeSecurityPrivilege";

    // SE_OBJECT_TYPE values
    public const uint SE_REGISTRY_KEY = 6;

    // Security information flags
    public const uint OWNER_SECURITY_INFORMATION = 0x00000001;
    public const uint DACL_SECURITY_INFORMATION  = 0x00000004;

    // ACL/ACE
    public const byte ACCESS_ALLOWED_ACE_TYPE = 0;
    public const byte OBJECT_INHERIT_ACE      = 1;
    public const byte CONTAINER_INHERIT_ACE   = 2;
    public const uint GRANT_ACCESS            = 1;
    public const uint TRUSTEE_IS_SID          = 0;
    public const uint TRUSTEE_IS_UNKNOWN      = 1;
    public const uint NO_MULTIPLE_TRUSTEE     = 0;

    // -------------------------------------------------------------------------
    // Privilege helpers
    // -------------------------------------------------------------------------

    public record struct PrivToken(nint ProcessToken, long Luid);

    public static bool ClaimPrivilege(string privilegeName, out PrivToken token)
    {
        token = default;
        if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out long luid))
            return false;
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                out nint hToken))
            return false;

        token = new PrivToken(hToken, luid);
        return SetPrivilege(hToken, luid, true);
    }

    public static void ReleasePrivilege(PrivToken token)
    {
        if (token.ProcessToken == 0) return;
        SetPrivilege(token.ProcessToken, token.Luid, false);
        NativeMethods.CloseHandle(token.ProcessToken);
    }

    public static bool SetPrivilege(nint hToken, long luid, bool enable)
    {
        var tp = new NativeMethods.TOKEN_PRIVILEGES
        {
            PrivilegeCount = 1,
            Privileges = new NativeMethods.LUID_AND_ATTRIBUTES[1]
        };
        tp.Privileges[0].Luid = luid;
        tp.Privileges[0].Attributes = enable ? SE_PRIVILEGE_ENABLED : 0;
        return NativeMethods.AdjustTokenPrivileges(hToken, false, ref tp, 0, nint.Zero, nint.Zero);
    }

    // -------------------------------------------------------------------------
    // SID helpers
    // -------------------------------------------------------------------------

    public static nint AllocateSidBuiltinAdministrators()
    {
        var auth = new NativeMethods.SID_IDENTIFIER_AUTHORITY
        { Value = new byte[] { 0, 0, 0, 0, 0, 5 } }; // SECURITY_NT_AUTHORITY
        if (!NativeMethods.AllocateAndInitializeSid(
                ref auth, 2,
                SECURITY_BUILTIN_DOMAIN_RID,
                DOMAIN_ALIAS_RID_ADMINS,
                0, 0, 0, 0, 0, 0, out nint pSid))
            throw new System.ComponentModel.Win32Exception();
        return pSid;
    }

    public static bool IsUserAdmin()
    {
        nint pSid = AllocateSidBuiltinAdministrators();
        try
        {
            NativeMethods.CheckTokenMembership(0, pSid, out bool isMember);
            return isMember;
        }
        finally { NativeMethods.FreeSid(pSid); }
    }

    // -------------------------------------------------------------------------
    // Named-object ownership + DACL helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Changes ownership of a named security object.
    /// Returns the previous owner SID (allocated with LocalAlloc) if the owner was changed,
    /// or <c>null</c> if the new owner was already the owner.
    /// Caller must free <paramref name="previousDescriptor"/> with <see cref="LocalFree"/>.
    /// </summary>
    public static uint SwitchOwnership(
        string objectName, uint objectType, nint newOwner,
        out nint previousOwner, out nint previousDescriptor)
    {
        previousOwner = nint.Zero;
        previousDescriptor = nint.Zero;

        uint err = NativeMethods.GetNamedSecurityInfo(
            objectName, objectType, OWNER_SECURITY_INFORMATION,
            out previousOwner, nint.Zero, nint.Zero, nint.Zero,
            out previousDescriptor);
        if (err != 0) return err;

        if (NativeMethods.EqualSid(previousOwner, newOwner))
        {
            LocalFree(previousDescriptor);
            previousDescriptor = nint.Zero;
            previousOwner = nint.Zero;
            return 0;
        }

        err = NativeMethods.SetNamedSecurityInfo(
            objectName, objectType, OWNER_SECURITY_INFORMATION,
            newOwner, nint.Zero, nint.Zero, nint.Zero);
        return err;
    }

    /// <summary>
    /// Adds explicit access rights for a trustee if not already present.
    /// Returns error code (0 = success).
    /// </summary>
    public static uint AddExplicitPermissions(
        string objectName, uint objectType, nint trustee,
        uint permissions, out uint previousPermissions)
    {
        previousPermissions = 0;
        uint err = NativeMethods.GetNamedSecurityInfoDacl(
            objectName, objectType, DACL_SECURITY_INFORMATION,
            nint.Zero, nint.Zero, out nint pDacl, nint.Zero, out nint pDescriptor);
        if (err != 0) return err;

        try
        {
            nint pNewDacl = EnsureExplicitPermissions(pDacl, trustee, permissions, out previousPermissions);
            if (pNewDacl == nint.Zero) return 0; // already had the permissions

            try
            {
                err = NativeMethods.SetNamedSecurityInfo(
                    objectName, objectType, DACL_SECURITY_INFORMATION,
                    nint.Zero, nint.Zero, pNewDacl, nint.Zero);
            }
            finally { LocalFree(pNewDacl); }

            return err;
        }
        finally { LocalFree(pDescriptor); }
    }

    // Overload that discards previousPermissions
    public static uint AddExplicitPermissions(
        string objectName, uint objectType, nint trustee, uint permissions)
        => AddExplicitPermissions(objectName, objectType, trustee, permissions, out _);

    /// <summary>
    /// Ensures a trustee has the given permissions in a DACL.
    /// Returns a new DACL allocated with LocalAlloc if changes were needed, or <see cref="nint.Zero"/>.
    /// </summary>
    public static nint EnsureExplicitPermissions(
        nint pDacl, nint trustee, uint permissions, out uint previousPermissions)
    {
        previousPermissions = 0;
        if (pDacl != nint.Zero)
        {
            var header = Marshal.PtrToStructure<NativeMethods.ACL>(pDacl);
            for (int i = 0; i < header.AceCount; i++)
            {
                if (!NativeMethods.GetAce(pDacl, i, out nint pAce)) continue;
                var aceHeader = Marshal.PtrToStructure<NativeMethods.ACE_HEADER>(pAce);
                if (aceHeader.AceType != ACCESS_ALLOWED_ACE_TYPE) continue;

                var ace = Marshal.PtrToStructure<NativeMethods.ACCESS_ALLOWED_ACE>(pAce);
                nint aceSid = pAce + Marshal.OffsetOf<NativeMethods.ACCESS_ALLOWED_ACE>(
                    nameof(NativeMethods.ACCESS_ALLOWED_ACE.SidStart)).ToInt32();

                if (!NativeMethods.EqualSid(aceSid, trustee)) continue;
                if ((ace.Mask & permissions) != permissions) continue;

                // Already has the permissions
                previousPermissions = ace.Mask;
                return nint.Zero;
            }
        }

        // Need to add the ACE
        var newAccess = new NativeMethods.EXPLICIT_ACCESS
        {
            grfAccessPermissions = permissions,
            grfAccessMode        = GRANT_ACCESS,
            grfInheritance       = CONTAINER_INHERIT_ACE | OBJECT_INHERIT_ACE,
            Trustee = new NativeMethods.TRUSTEE
            {
                pMultipleTrustee          = nint.Zero,
                MultipleTrusteeOperation  = NO_MULTIPLE_TRUSTEE,
                TrusteeForm               = TRUSTEE_IS_SID,
                TrusteeType               = TRUSTEE_IS_UNKNOWN,
                ptstrName                 = trustee,
            }
        };

        uint err = NativeMethods.SetEntriesInAcl(1, ref newAccess, pDacl, out nint pNewDacl);
        if (err != 0) throw new System.ComponentModel.Win32Exception((int)err);
        return pNewDacl;
    }

    public record struct UnlockResults(bool HadOwnershipChanged, bool HadPermissionsChanged);

    /// <summary>
    /// Standard "unlock" sequence: take ownership → grant previous owner full rights → grant new owner full rights.
    /// </summary>
    public static uint UnlockNamedObject(
        string objectName, uint objectType,
        nint newOwner, uint ensurePermissions,
        out UnlockResults results)
    {
        results = default;
        uint err = SwitchOwnership(objectName, objectType, newOwner,
            out nint previousOwner, out nint previousDesc);
        if (err != 0) return err;

        if (previousOwner != nint.Zero)
        {
            results = results with { HadOwnershipChanged = true };
            AddExplicitPermissions(objectName, objectType, previousOwner, ensurePermissions);
            if (previousDesc != nint.Zero) LocalFree(previousDesc);
        }

        err = AddExplicitPermissions(objectName, objectType, newOwner, ensurePermissions,
            out uint prevPerms);
        if (err == 0)
            results = results with { HadPermissionsChanged = (prevPerms & ensurePermissions) != ensurePermissions };
        return err;
    }

    // -------------------------------------------------------------------------
    // LocalAlloc helpers
    // -------------------------------------------------------------------------
    public static void LocalFree(nint ptr)
    {
        if (ptr != nint.Zero) NativeMethods.LocalFree(ptr);
    }

    // -------------------------------------------------------------------------
    // P/Invoke declarations
    // -------------------------------------------------------------------------
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern nint LocalFree(nint hMem);

        // ---- advapi32 ----

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(nint processHandle, int desiredAccess, out nint tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustTokenPrivileges(
            nint tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState,
            int bufferLength,
            nint previousState,
            nint returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllocateAndInitializeSid(
            ref SID_IDENTIFIER_AUTHORITY pIdentifierAuthority,
            byte nSubAuthorityCount,
            uint nSubAuthority0, uint nSubAuthority1,
            uint nSubAuthority2, uint nSubAuthority3,
            uint nSubAuthority4, uint nSubAuthority5,
            uint nSubAuthority6, uint nSubAuthority7,
            out nint pSid);

        [DllImport("advapi32.dll")]
        public static extern nint FreeSid(nint pSid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CheckTokenMembership(
            nint tokenHandle, nint sidToCheck,
            [MarshalAs(UnmanagedType.Bool)] out bool isMember);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EqualSid(nint pSid1, nint pSid2);

        // Overload 1: retrieve owner
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetNamedSecurityInfoW")]
        public static extern uint GetNamedSecurityInfo(
            string pObjectName, uint objectType, uint securityInfo,
            out nint ppsidOwner, nint ppsidGroup, nint ppDacl,
            nint ppSacl, out nint ppSecurityDescriptor);

        // Overload 2: retrieve DACL
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetNamedSecurityInfoW")]
        public static extern uint GetNamedSecurityInfoDacl(
            string pObjectName, uint objectType, uint securityInfo,
            nint ppsidOwner, nint ppsidGroup, out nint ppDacl,
            nint ppSacl, out nint ppSecurityDescriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern uint SetNamedSecurityInfo(
            string pObjectName, uint objectType, uint securityInfo,
            nint psidOwner, nint psidGroup, nint pDacl, nint pSacl);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetAce(nint pAcl, int dwAceIndex, out nint pAce);

        [DllImport("advapi32.dll")]
        public static extern uint SetEntriesInAcl(
            uint cCountOfExplicitEntries,
            ref EXPLICIT_ACCESS pListOfExplicitEntries,
            nint oldAcl,
            out nint newAcl);

        // ---- Structs ----

        [StructLayout(LayoutKind.Sequential)]
        public struct SID_IDENTIFIER_AUTHORITY
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public LUID_AND_ATTRIBUTES[] Privileges;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID_AND_ATTRIBUTES
        {
            public long Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ACL
        {
            public byte AclRevision;
            public byte Sbz1;
            public ushort AclSize;
            public ushort AceCount;
            public ushort Sbz2;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ACE_HEADER
        {
            public byte AceType;
            public byte AceFlags;
            public ushort AceSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ACCESS_ALLOWED_ACE
        {
            public ACE_HEADER Header;
            public uint Mask;
            public uint SidStart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TRUSTEE
        {
            public nint pMultipleTrustee;
            public uint MultipleTrusteeOperation;
            public uint TrusteeForm;
            public uint TrusteeType;
            public nint ptstrName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EXPLICIT_ACCESS
        {
            public uint grfAccessPermissions;
            public uint grfAccessMode;
            public uint grfInheritance;
            public TRUSTEE Trustee;
        }
    }
}
