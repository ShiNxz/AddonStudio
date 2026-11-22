namespace AddonStudio.Tests.Support;

/// <summary>A fact that needs a Unix shell (/bin/sh); skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "needs /bin/sh";
        }
    }
}
