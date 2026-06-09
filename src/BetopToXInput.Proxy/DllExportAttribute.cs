using System;

namespace BetopToXInput.Proxy;

/// <summary>
/// Marker attribute that makes a static method exportable from the .NET assembly
/// as a native DLL entry point. Provided as a stub so the project compiles
/// out of the box. To produce a real <c>xinput1_3.dll</c>, replace this with
/// the <c>DllExport</c> NuGet package (by 3F) and add the
/// <c>&lt;PackageReference Include="DllExport" Version="1.7.4" /&gt;</c> line
/// to the .csproj; the package swaps in the real attribute and IL-rewrites the
/// assembly to actually export the marked methods.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DllExportAttribute : Attribute
{
    public DllExportAttribute()
    {
    }

    public DllExportAttribute(string exportName)
    {
        ExportName = exportName;
    }

    public DllExportAttribute(string exportName, System.Runtime.InteropServices.CallingConvention callingConvention)
    {
        ExportName = exportName;
        CallingConvention = callingConvention;
    }

    public string? ExportName { get; set; }
    public System.Runtime.InteropServices.CallingConvention CallingConvention { get; set; }
}
