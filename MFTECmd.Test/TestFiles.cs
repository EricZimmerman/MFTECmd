using System.IO;
using System.IO.Compression;
using NUnit.Framework;

namespace MFTECmd.Test;

public static class TestFiles
{
    public static string Dir => Path.Combine(TestContext.CurrentContext.TestDirectory, "TestFiles");

    public static string Generated(string name) => Path.Combine(Dir, "Generated", name);

    public static string StolenSzechuan(string name) => Path.Combine(Dir, "DFIRArtifactMuseum", "Win2012R2", "StolenSzechuan", name);

    /// <summary>Decompresses <paramref name="gz" /> into <paramref name="dir" />, without the .gz</summary>
    public static string Gunzip(string gz, string dir)
    {
        var target = Path.Combine(dir, Path.GetFileNameWithoutExtension(gz));
        using var input = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
        using var output = File.Create(target);
        input.CopyTo(output);
        return target;
    }
}
