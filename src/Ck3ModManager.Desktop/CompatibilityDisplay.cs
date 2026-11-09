using static Ck3ModManager.Core.AppText;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public static class CompatibilityDisplay
{
    public static string Label(CompatibilityStatus status) => status switch
    {
        CompatibilityStatus.DeclaredMatch => T("Compatible"),
        CompatibilityStatus.DeclaredMismatch => T("Incompatible"),
        _ => T("Unknown")
    };
}
