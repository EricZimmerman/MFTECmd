using System;
using System.IO;
using System.Reflection;

namespace MFTECmd;

/// <summary>--co (Issues #259): the console messages are also saved to &lt;yyyyMMddHHmmss&gt;_MFTECmd_&lt;version&gt;_ConsoleLog.txt in the output directory</summary>
internal static class ConsoleLog
{
    /// <summary>The console's template with time and level. {Message:l}, not :lj: with :j the file sink writes values other than
    /// strings as JSON (quoted dates instead of the --dt format, quoted enum names), which the console doesn't</summary>
    public const string Template = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:l}{NewLine}{Exception}";

    /// <summary>The log file in the first output directory given (the time is UTC)</summary>
    /// <returns>null when no output directory was given or the path is not valid (the tool reports that itself)</returns>
    public static string GetPath(DateTimeOffset utcNow, params string[] outputDirectories)
    {
        foreach (var dir in outputDirectories)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
                return Path.Combine(Path.GetFullPath(dir), $"{utcNow:yyyyMMddHHmmss}_MFTECmd_{version}_ConsoleLog.txt");
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }
}
