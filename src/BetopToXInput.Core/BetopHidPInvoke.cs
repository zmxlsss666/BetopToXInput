using System.Collections.Generic;

namespace BetopToXInput.Core;

/// <summary>
/// Public façade over the internal P/Invoke helpers. App/Program.cs uses this
/// to enumerate device paths in diagnostic modes.
/// </summary>
public static class BetopHidPInvoke
{
    public static List<string> EnumerateHidPaths(int vendorId, int productId)
    {
        return HidPInvoke.EnumerateHidPaths((ushort)vendorId, (ushort)productId);
    }

    public static List<string> EnumerateAllHidPaths()
    {
        return HidPInvoke.EnumerateAllHidPaths();
    }
}
