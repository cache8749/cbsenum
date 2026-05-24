// Shared registry path constants (from CBSEnum_Main.pas)

namespace CBSEnum;

internal static class Constants
{
    public const string CbsKey     = @"Software\Microsoft\Windows\CurrentVersion\Component Based Servicing";
    public const string CbsRootSec = "MACHINE"; // prefix for security API paths

    public const uint CBS_E_INVALID_PACKAGE = 0x800F0805;
    public const uint ERROR_SUCCESS_REBOOT_REQUIRED = 3010;
}
