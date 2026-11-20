using System;
using System.IO;
using System.Text;
using FluentAssertions;
using NUnit.Framework;

namespace MFTECmd.Test;

/// <summary>ResidentDataDecoded: Recycle Bin $I files (Issues #260) and UTF-16 text, on built and damaged input</summary>
[TestFixture]
public class ResidentDecoderTests
{
    private const string Format = "yyyy-MM-dd HH:mm:ss.fffffff";
    private static readonly DateTime Deleted = new(2026, 11, 20, 12, 34, 56, 789, DateTimeKind.Utc);

    private static byte[] InfoV1(string path, long size, long fileTime)
    {
        var b = new byte[0x220];
        BitConverter.GetBytes(1L).CopyTo(b, 0);
        BitConverter.GetBytes(size).CopyTo(b, 8);
        BitConverter.GetBytes(fileTime).CopyTo(b, 0x10);
        Encoding.Unicode.GetBytes(path).CopyTo(b, 0x18);
        return b;
    }

    private static byte[] InfoV2(string path, long size, long fileTime, int? chars = null)
    {
        var name = Encoding.Unicode.GetBytes(path + "\0");
        var b = new byte[0x1C + name.Length];
        BitConverter.GetBytes(2L).CopyTo(b, 0);
        BitConverter.GetBytes(size).CopyTo(b, 8);
        BitConverter.GetBytes(fileTime).CopyTo(b, 0x10);
        BitConverter.GetBytes(chars ?? path.Length + 1).CopyTo(b, 0x18);
        name.CopyTo(b, 0x1C);
        return b;
    }

    [Test]
    public void Version1()
    {
        ResidentDecoder.DecodeRecycleBinInfo(InfoV1(@"C:\Users\x\a.txt", 1234, Deleted.ToFileTimeUtc()), Format)
            .Should().Be(@"Version: 1 | Path: C:\Users\x\a.txt | Size: 1234 | Deleted: 2026-11-20 12:34:56.7890000");
    }

    [Test]
    public void Version2()
    {
        ResidentDecoder.DecodeRecycleBinInfo(InfoV2(@"C:\Temp\é ü\b.docx", 0, Deleted.ToFileTimeUtc()), Format)
            .Should().Be(@"Version: 2 | Path: C:\Temp\é ü\b.docx | Size: 0 | Deleted: 2026-11-20 12:34:56.7890000");
    }

    [Test]
    public void BadTimestampIsShownAsHex()
    {
        ResidentDecoder.DecodeRecycleBinInfo(InfoV2(@"C:\a", 1, -5), Format).Should().EndWith("Deleted: 0xFFFFFFFFFFFFFFFB");
        ResidentDecoder.DecodeRecycleBinInfo(InfoV2(@"C:\a", 1, long.MaxValue), Format).Should().EndWith("Deleted: 0x7FFFFFFFFFFFFFFF");
    }

    [Test]
    public void NotAnInfoFile()
    {
        ResidentDecoder.DecodeRecycleBinInfo(null, Format).Should().BeNull();
        ResidentDecoder.DecodeRecycleBinInfo(new byte[0x17], Format).Should().BeNull();
        ResidentDecoder.DecodeRecycleBinInfo(Encoding.ASCII.GetBytes("this is just some text in a file"), Format).Should().BeNull();

        var v3 = InfoV2(@"C:\a", 1, 1);
        v3[0] = 3;
        ResidentDecoder.DecodeRecycleBinInfo(v3, Format).Should().BeNull();

        //negative size, an empty path
        ResidentDecoder.DecodeRecycleBinInfo(InfoV2(@"C:\a", -1, 1), Format).Should().BeNull();
        ResidentDecoder.DecodeRecycleBinInfo(InfoV1("", 1, 1), Format).Should().BeNull();
    }

    [Test]
    public void DamagedLengths()
    {
        //a version 1 file cut short, a version 2 count past the end, zero, negative and huge
        ResidentDecoder.DecodeRecycleBinInfo(InfoV1(@"C:\a", 1, 1).AsSpan(0, 0x100).ToArray(), Format).Should().BeNull();
        ResidentDecoder.DecodeRecycleBinInfo(InfoV2(@"C:\a", 1, 1, 50), Format).Should().BeNull();
        ResidentDecoder.DecodeRecycleBinInfo(InfoV2(@"C:\a", 1, 1, 0), Format).Should().BeNull();
        ResidentDecoder.DecodeRecycleBinInfo(InfoV2(@"C:\a", 1, 1, -1), Format).Should().BeNull();
        ResidentDecoder.DecodeRecycleBinInfo(InfoV2(@"C:\a", 1, 1, int.MaxValue), Format).Should().BeNull();
        ResidentDecoder.DecodeRecycleBinInfo(InfoV2(@"C:\a", 1, 1).AsSpan(0, 0x1B).ToArray(), Format).Should().BeNull();
    }

    [Test]
    public void PathWithoutTerminatorStopsAtTheCount()
    {
        var b = InfoV2(@"C:\abc", 1, 1);
        b[^2] = (byte)'z'; //the NUL becomes a character: the count still bounds the name
        ResidentDecoder.DecodeRecycleBinInfo(b, Format).Should().Contain(@"Path: C:\abcz |");
    }

    [Test]
    public void InfoFileNames()
    {
        ResidentDecoder.IsRecycleBinInfoName("$IAB12CD.txt").Should().BeTrue();
        ResidentDecoder.IsRecycleBinInfoName("$iab12cd").Should().BeTrue();
        ResidentDecoder.IsRecycleBinInfoName("$I30").Should().BeFalse();
        ResidentDecoder.IsRecycleBinInfoName("$RAB12CD.txt").Should().BeFalse();
        ResidentDecoder.IsRecycleBinInfoName(null).Should().BeFalse();
    }

    [Test]
    public void Utf16Text()
    {
        var text = "[.ShellClassInfo]\r\nLocalizedResourceName=@shell32.dll,-21770\r\n";
        ResidentDecoder.DecodeUtf16Text(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text))).Should().Be(text);
        ResidentDecoder.DecodeUtf16Text(Encoding.Unicode.GetBytes("Grüße, Kayıt İşlemi\0\0")).Should().Be("Grüße, Kayıt İşlemi");
    }

    [Test]
    public void NotUtf16Text()
    {
        ResidentDecoder.DecodeUtf16Text(null).Should().BeNull();
        ResidentDecoder.DecodeUtf16Text(new byte[3]).Should().BeNull();
        ResidentDecoder.DecodeUtf16Text(new byte[8]).Should().BeNull(); //only NULs
        ResidentDecoder.DecodeUtf16Text(Encoding.ASCII.GetBytes("plain ASCII text here!")).Should().BeNull(); //reads as CJK
        ResidentDecoder.DecodeUtf16Text(new byte[] { 0x01, 0x00, 0x02, 0x00 }).Should().BeNull(); //control characters
        ResidentDecoder.DecodeUtf16Text(new byte[] { 0x00, 0xD8, 0x41, 0x00 }).Should().BeNull(); //a lone surrogate
    }

    [Test]
    public void RandomBytesNeverThrow()
    {
        var rnd = new Random(260);
        for (var i = 0; i < 20_000; i++)
        {
            var b = new byte[rnd.Next(0, 700)];
            rnd.NextBytes(b);
            if (b.Length >= 8 && rnd.Next(2) == 0)
            {
                b[0] = (byte)rnd.Next(1, 3); //a valid version, so the length checks are reached
                Array.Clear(b, 1, 7);
            }

            var act = () =>
            {
                ResidentDecoder.DecodeRecycleBinInfo(b, Format);
                ResidentDecoder.DecodeUtf16Text(b);
            };
            act.Should().NotThrow();
        }
    }
}

internal static class ByteArrayExtensions
{
    public static byte[] Concat(this byte[] a, byte[] b)
    {
        using var ms = new MemoryStream();
        ms.Write(a, 0, a.Length);
        ms.Write(b, 0, b.Length);
        return ms.ToArray();
    }
}
