using System;
using System.Text;

namespace MFTECmd;

/// <summary>
///     Decodes resident $DATA for the $MFT output's ResidentDataDecoded column: Recycle Bin $I files (always, Issues
///     #260: an emptied Recycle Bin can leave them in free FILE records) and, with --ir, UTF-16 text. Input may be
///     damaged or hostile: anything that doesn't fit gives null
/// </summary>
public static class ResidentDecoder
{
    private const int I1Size = 0x220; //version 1 (Vista to 8.1): header + 260 UTF-16 characters
    private const int I2HeaderSize = 0x1C; //version 2 (Windows 10+): header + a character count + the name
    private const int MaxPathChars = 0x8000;

    /// <summary>True for a file name of a Recycle Bin $I file ($I + 6 characters, plus any extension)</summary>
    public static bool IsRecycleBinInfoName(string fileName)
    {
        return fileName != null && fileName.Length >= 8 && fileName.StartsWith("$I", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     "Version: 2 | Path: C:\x\y.txt | Size: 1234 | Deleted: (deletion time in <paramref name="dateFormat" />)", or
    ///     null when <paramref name="data" /> is not a $I file
    /// </summary>
    public static string DecodeRecycleBinInfo(byte[] data, string dateFormat)
    {
        if (data == null || data.Length < 0x18)
        {
            return null;
        }

        var version = BitConverter.ToInt64(data, 0);
        var size = BitConverter.ToInt64(data, 8);
        var fileTime = BitConverter.ToInt64(data, 0x10);

        string path;
        switch (version)
        {
            case 1:
                if (data.Length < I1Size)
                {
                    return null;
                }

                path = Utf16UntilNul(data, 0x18, 260);
                break;
            case 2:
                if (data.Length < I2HeaderSize)
                {
                    return null;
                }

                var chars = BitConverter.ToInt32(data, 0x18);
                if (chars <= 0 || chars > MaxPathChars || I2HeaderSize + (long)chars * 2 > data.Length)
                {
                    return null;
                }

                path = Utf16UntilNul(data, I2HeaderSize, chars);
                break;
            default:
                return null;
        }

        if (size < 0 || path.Length == 0)
        {
            return null;
        }

        var deleted = fileTime > 0 && fileTime <= DateTime.MaxValue.ToFileTimeUtc()
            ? DateTimeOffset.FromFileTime(fileTime).ToUniversalTime().ToString(dateFormat)
            : $"0x{fileTime:X}";

        return $"Version: {version} | Path: {path} | Size: {size} | Deleted: {deleted}";
    }

    /// <summary>
    ///     UTF-16LE text (with or without a byte order mark), or null when <paramref name="data" /> doesn't look like
    ///     it: an odd length, or characters that aren't printable text
    /// </summary>
    public static string DecodeUtf16Text(byte[] data)
    {
        if (data == null || data.Length < 4 || data.Length % 2 != 0)
        {
            return null;
        }

        var start = data[0] == 0xFF && data[1] == 0xFE ? 2 : 0;
        var text = Encoding.Unicode.GetString(data, start, data.Length - start).TrimEnd('\0');

        if (text.Length == 0)
        {
            return null;
        }

        foreach (var c in text)
        {
            if (c == '\r' || c == '\n' || c == '\t')
            {
                continue;
            }

            if (char.IsControl(c) || char.IsSurrogate(c) || c == '\uFFFD' || c == '\uFFFF')
            {
                return null;
            }
        }

        //plain ASCII read as UTF-16 gives CJK characters: want mostly Latin-1 or a byte order mark
        if (start == 0)
        {
            var latin = 0;
            foreach (var c in text)
            {
                if (c < 0x250)
                {
                    latin++;
                }
            }

            if (latin * 2 < text.Length)
            {
                return null;
            }
        }

        return text;
    }

    private static string Utf16UntilNul(byte[] data, int offset, int maxChars)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < maxChars && offset + i * 2 + 1 < data.Length; i++)
        {
            var c = (char)BitConverter.ToUInt16(data, offset + i * 2);
            if (c == '\0')
            {
                break;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
