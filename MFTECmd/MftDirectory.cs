using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;
#if !NET9_0_OR_GREATER
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using Path = Alphaleonis.Win32.Filesystem.Path;
#endif

namespace MFTECmd;

/// <summary>
///     -d (issue #109): every $MFT under a folder into one CSV, JSON, bodyfile and file listing. Each $MFT is processed
///     on its own into a temporary folder in the output folder, then the outputs are joined (one header; the rows say
///     which $MFT they came from in SourceFile). Only one $MFT is in memory at a time
/// </summary>
internal static class MftDirectory
{
    /// <summary>Files under <paramref name="dir" /> named $MFT* (not $MFTMirr), sorted; unreadable folders are listed in <paramref name="errors" /></summary>
    public static List<string> FindMftFiles(string dir, List<string> errors)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(dir);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files;
            string[] subDirs;
            try
            {
                files = Directory.GetFiles(current);
                subDirs = Directory.GetDirectories(current);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                errors.Add($"Unable to read folder {current}: {e.Message}");
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("$MFT", StringComparison.OrdinalIgnoreCase) &&
                    name.StartsWith("$MFTMirr", StringComparison.OrdinalIgnoreCase) == false)
                {
                    found.Add(file);
                }
            }

            foreach (var sub in subDirs)
            {
                pending.Push(sub);
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>A temporary folder for $MFT number <paramref name="index" />'s output, inside <paramref name="outDir" /></summary>
    public static string TempDir(string outDir, DateTimeOffset runTime, int index)
    {
        return Path.Combine(TempRoot(outDir, runTime), index.ToString("D4"));
    }

    public static string TempRoot(string outDir, DateTimeOffset runTime)
    {
        return Path.Combine(outDir, $".MFTECmd_{runTime:yyyyMMddHHmmss}_d");
    }

    /// <summary>
    ///     Joins the outputs in each of <paramref name="tempRoot" />'s numbered folders into <paramref name="outDir" />,
    ///     by file name, in folder order. CSVs keep only the first file's header line
    /// </summary>
    /// <returns>The joined files</returns>
    public static List<string> Combine(string tempRoot, string outDir)
    {
        var joined = new List<string>();
        if (Directory.Exists(tempRoot) == false)
        {
            return joined;
        }

        var byName = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Directory.GetDirectories(tempRoot).OrderBy(t => t, StringComparer.Ordinal))
        {
            foreach (var file in Directory.GetFiles(part))
            {
                var name = Path.GetFileName(file);
                if (byName.TryGetValue(name, out var list) == false)
                {
                    list = new List<string>();
                    byName.Add(name, list);
                }

                list.Add(file);
            }
        }

        foreach (var entry in byName)
        {
            var target = Path.Combine(outDir, entry.Key);
            var isCsv = entry.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
            {
                for (var i = 0; i < entry.Value.Count; i++)
                {
                    using var input = new FileStream(entry.Value[i], FileMode.Open, FileAccess.Read);
                    if (i > 0 && isCsv)
                    {
                        SkipFirstLine(input);
                    }

                    input.CopyTo(output);
                }
            }

            joined.Add(target);
        }

        return joined;
    }

    public static void DeleteTemp(string tempRoot)
    {
        try
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
        catch (Exception e)
        {
            Log.Warning("Could not delete the temporary folder {Dir}: {Message}", tempRoot, e.Message);
        }
    }

    /// <summary>Moves past the byte order mark and the header line</summary>
    private static void SkipFirstLine(Stream input)
    {
        int b;
        while ((b = input.ReadByte()) != -1)
        {
            if (b == '\n')
            {
                return;
            }
        }
    }

    public static string Plural(int count, string one) => count == 1 ? $"1 {one}" : $"{count:N0} {one}s";
}
