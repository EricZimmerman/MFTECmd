using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Usn;

namespace MFTECmd.Test;

/// <summary>Runs the built MFTECmd.dll (copied next to the tests by the project reference) as a separate process</summary>
[TestFixture]
public class CliTests
{
    private string _dir;
    private string Out => Path.Combine(_dir, "out");

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mftecmd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_dir, true);
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir
        };
        start.ArgumentList.Add(Path.Combine(TestContext.CurrentContext.TestDirectory, "MFTECmd.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();

        process.WaitForExit(300_000).Should().BeTrue("MFTECmd should finish");

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private string OutFile(string suffix) => Directory.GetFiles(Out, "*" + suffix).Should().ContainSingle().Subject;

    /// <summary>The rows of a CSV file, header first (RFC 4180 quoting, as CsvHelper writes it)</summary>
    private static List<string[]> ReadCsv(string csvFile)
    {
        var text = File.ReadAllText(csvFile).TrimStart('﻿');
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row.ToArray());
                    row.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    private static string[] Header(string csvFile) => ReadCsv(csvFile)[0];

    private static int CsvRows(string csvFile) => ReadCsv(csvFile).Count - 1;

    private static string[] Column(string csvFile, string name)
    {
        var rows = ReadCsv(csvFile);
        var index = Array.IndexOf(rows[0], name);
        index.Should().BeGreaterThanOrEqualTo(0, name);

        rows.Skip(1).Should().OnlyContain(t => t.Length == rows[0].Length, "every row has every column");
        return rows.Skip(1).Select(t => t[index]).ToArray();
    }

    [Test]
    public void WithoutFileItExits1()
    {
        Run().ExitCode.Should().Be(1);
    }

    [Test]
    public void MissingFileExits1()
    {
        var (exitCode, output) = Run("-f", Path.Combine(_dir, "nope"), "--csv", Out);

        exitCode.Should().Be(1);
        output.Should().Contain("not found");
    }

    [Test]
    public void UnknownFileTypeExits1()
    {
        var file = Path.Combine(_dir, "text.txt");
        File.WriteAllText(file, "This is not an NTFS file at all, but it is long enough to be checked");

        var (exitCode, output) = Run("-f", file, "--csv", Out);

        exitCode.Should().Be(1);
        output.Should().Contain("Unknown file type");
    }

    [Test]
    public void JWithoutOutputExits1()
    {
        var j = TestFiles.Gunzip(TestFiles.StolenSzechuan("$J.gz"), _dir);

        Run("-f", j).ExitCode.Should().Be(1);
    }

    [Test]
    public void MftCsvAndJson()
    {
        var (exitCode, output) = Run("-f", TestFiles.Generated("$MFT"), "--csv", Out, "--json", Out);

        exitCode.Should().Be(0, output);

        var csv = OutFile("$MFT_Output.csv");
        Header(csv).Take(8).Should().Equal("EntryNumber", "SequenceNumber", "InUse", "ParentEntryNumber", "ParentSequenceNumber", "ParentPath", "FileName", "Extension");

        //one line per record, plus ADS rows
        var json = File.ReadAllLines(OutFile("$MFT_Output.json"));
        json.Should().HaveCount(CsvRows(csv));
        json.Should().Contain(t => t.Contains("\"FileName\":\"hello.txt\""));
    }

    [Test]
    public void DamagedRecordsHaveParseErrors()
    {
        //hello.txt (entry 64) with its $DATA attribute's type changed to one that does not exist
        var bytes = File.ReadAllBytes(TestFiles.Generated("$MFT"));
        var record = 64 * 1024;
        var at = record + BitConverter.ToUInt16(bytes, record + 0x14);
        while (BitConverter.ToUInt32(bytes, at) != 0x80)
        {
            at += BitConverter.ToInt32(bytes, at + 4);
        }

        BitConverter.GetBytes(0x55).CopyTo(bytes, at);
        var mft = Path.Combine(_dir, "$MFT");
        File.WriteAllBytes(mft, bytes);

        var (exitCode, output) = Run("-f", mft, "--csv", Out);

        exitCode.Should().Be(0, output);
        output.Should().Contain("1 FILE record(s) have damaged attributes");

        var csv = OutFile("$MFT_Output.csv");
        Header(csv).Last().Should().Be("ParseErrors");
        var names = Column(csv, "FileName");
        var errors = Column(csv, "ParseErrors");
        errors[Array.IndexOf(names, "hello.txt")].Should().StartWith("Unknown attribute type 0x55 at 0x");
        errors.Count(t => t.Length > 0).Should().Be(1);
    }

    [Test]
    public void LowMemoryGivesTheSameOutput()
    {
        var j = TestFiles.Gunzip(TestFiles.StolenSzechuan("$J.gz"), _dir);
        var mft = TestFiles.Gunzip(TestFiles.StolenSzechuan("$MFT_directories.gz"), _dir);

        var (exitCode, output) = Run("-f", j, "-m", mft, "--csv", Path.Combine(Out, "server"), "--csvf", "j.csv", "--debug");
        exitCode.Should().Be(0, output);
        output.Should().Contain("Garbage collection: server");

        (exitCode, output) = Run("-f", j, "-m", mft, "--csv", Path.Combine(Out, "lowmem"), "--csvf", "j.csv", "--debug", "--lowmem");
        exitCode.Should().Be(0, output);
        output.Should().Contain("Garbage collection: workstation");

        File.ReadAllText(Path.Combine(Out, "lowmem", "j.csv")).Should().Be(File.ReadAllText(Path.Combine(Out, "server", "j.csv")));

        //--csvf names the $J output; the $MFT from -m keeps its own name (it used to be written to the same file)
        Directory.GetFiles(Path.Combine(Out, "server")).Select(Path.GetFileName).Should().HaveCount(2).And.Contain("j.csv")
            .And.Contain(t => t.EndsWith("_MFTECmd_$MFT_Output.csv"));
        File.ReadLines(Path.Combine(Out, "server", "j.csv")).First().Should().StartWith("Name,Extension");

        //and the exit code comes through
        Run("-f", Path.Combine(_dir, "nope"), "--csv", Out, "--lowmem").ExitCode.Should().Be(1);
    }

    [Test]
    public void JsonReplacesALongerFile()
    {
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, "mft.json"), new string('x', 2_000_000));

        Run("-f", TestFiles.Generated("$MFT"), "--json", Out, "--jsonf", "mft.json").ExitCode.Should().Be(0);

        //the old contents used to stay after the end of the new ones
        File.ReadAllLines(Path.Combine(Out, "mft.json")).Should().OnlyContain(t => t.StartsWith("{") && t.EndsWith("}"));
    }

    [Test]
    public void DumpEntry()
    {
        var (exitCode, output) = Run("-f", TestFiles.Generated("$MFT"), "--de", "64");

        exitCode.Should().Be(0, output);
        output.Should().Contain("hello.txt");

        Run("-f", TestFiles.Generated("$MFT"), "--de", "9999").ExitCode.Should().Be(1);
    }

    [Test]
    public void OtherTypes()
    {
        Run("-f", TestFiles.Generated("$Boot"), "--csv", Out).ExitCode.Should().Be(0);
        CsvRows(OutFile("$Boot_Output.csv")).Should().Be(1);

        Run("-f", TestFiles.Generated("$SDS"), "--csv", Out).ExitCode.Should().Be(0);
        CsvRows(OutFile("$SDS_Output.csv")).Should().BeGreaterThan(0);

        Run("-f", TestFiles.Generated("$I30"), "--csv", Out).ExitCode.Should().Be(0);
        Column(OutFile("$I30_Output.csv"), "FileName").Should().Contain("hello.txt");
    }

    [Test]
    public void JWithoutMftHasNoPaths()
    {
        var j = TestFiles.Gunzip(TestFiles.StolenSzechuan("$J.gz"), _dir);

        var (exitCode, output) = Run("-f", j, "--csv", Out, "--json", Out);

        exitCode.Should().Be(0, output);
        var csv = OutFile("$J_Output.csv");
        CsvRows(csv).Should().Be(82_151);
        Column(csv, "ParentPath").Should().OnlyContain(t => t.Length == 0);

        //--json used to be ignored when --csv was given
        File.ReadLines(OutFile("$J_Output.json")).Count().Should().Be(82_151);
    }

    /// <summary>
    ///     Issue 56: with an $MFT, $J paths are rewound through the journal. 14,031 of these records have a parent that
    ///     the $MFT alone does not know (PathUnknown); usnjrnl_rewind gives the same paths for every record
    /// </summary>
    [Test]
    public void JWithMftRewindsThePaths()
    {
        var j = TestFiles.Gunzip(TestFiles.StolenSzechuan("$J.gz"), _dir);
        var mft = TestFiles.Gunzip(TestFiles.StolenSzechuan("$MFT_directories.gz"), _dir);

        var (exitCode, output) = Run("-f", j, "-m", mft, "--csv", Out, "--json", Out);

        exitCode.Should().Be(0, output);
        output.Should().Contain("14,031 differ from the $MFT alone");

        var csv = OutFile("$J_Output.csv");
        var paths = Column(csv, "ParentPath");
        var usns = Column(csv, "UpdateSequenceNumber");
        paths.Should().HaveCount(82_151);
        paths.Should().NotContain(t => t.Contains("PathUnknown"));

        string PathOf(string usn) => paths[Array.IndexOf(usns, usn)];
        PathOf("0").Should().Be(".\\Windows");
        PathOf("2895568").Should().Be(".\\Windows\\assembly\\temp\\ZRMWJCZVU7");
        PathOf("8942672").Should().Be(".\\Windows\\System32\\spool\\drivers\\x64\\{A9F70739-5120-4608-8545-468F878CF940}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", paths)));
        Convert.ToHexString(hash).Should().BeEquivalentTo("d0469690403fb5c0c7529996c7b935bc17e416de3a157ed9265c050b0f95ed23");

        //the $MFT is exported too, as before
        OutFile("$MFT_Output.csv");
        File.ReadLines(OutFile("$J_Output.json")).Count().Should().Be(82_151);
    }

    [Test]
    public void JWithMftUsesTheJournalForRenames()
    {
        var j = Path.Combine(_dir, "$J");
        File.WriteAllBytes(j, new UsnBuilder()
            .Add("x.txt", 103, 1, 11, 11, UsnEntry.UpdateReasonFlag.FileCreate)
            .Add("Old", 11, 11, 5, 5, UsnEntry.UpdateReasonFlag.RenameOldName, true)
            .Add("$Extend", 11, 11, 5, 5, UsnEntry.UpdateReasonFlag.RenameNewName, true)
            .Add("y.txt", 104, 1, 11, 11, UsnEntry.UpdateReasonFlag.FileCreate)
            .ToArray());

        var (exitCode, output) = Run("-f", j, "-m", TestFiles.Generated("$MFT"), "--csv", Out);

        exitCode.Should().Be(0, output);
        Column(OutFile("$J_Output.csv"), "ParentPath").Should().Equal(".\\Old", ".", ".", ".\\$Extend");
    }

    [Test]
    public void LogFileCsvAndJson()
    {
        var logFile = TestFiles.Gunzip(TestFiles.StolenSzechuan("$LogFile.gz"), _dir);

        var (exitCode, output) = Run("-f", logFile, "--csv", Out, "--json", Out);

        exitCode.Should().Be(0, output);
        output.Should().Contain("File type: LogFile");
        output.Should().Contain("Log records found: 113,738");

        var csv = OutFile("$LogFile_Output.csv");
        Header(csv).Should().Equal("Lsn", "TransactionId", "TransactionFirstLsn", "RedoOperation", "UndoOperation",
            "TargetEntryNumber", "TargetSequenceNumber", "TargetAttributeType", "TargetAttributeName", "TargetPath",
            "EntryNumber", "SequenceNumber", "FileName", "ParentEntryNumber", "ParentSequenceNumber", "ParentPath",
            "CreatedOn", "ContentModifiedOn", "RecordModifiedOn", "LastAccessedOn", "FileSize", "FileAttributes",
            "AttributeType", "Details", "PreviousLsn", "UndoNextLsn", "RecordType", "TargetAttribute", "TargetInferred",
            "TargetVcn", "Lcns", "RecordOffset", "AttributeOffset", "ClusterBlockOffset", "RedoLength", "UndoLength",
            "Truncated", "FileOffset", "SourceFile");
        CsvRows(csv).Should().Be(113_738);
        File.ReadLines(OutFile("$LogFile_Output.json")).Count().Should().Be(113_738);

        var rows = ReadCsv(csv);
        var header = rows[0];
        var byLsn = rows.Skip(1).ToDictionary(t => t[0]);
        rows.Skip(1).Select(t => ulong.Parse(t[0])).Should().BeInAscendingOrder();
        rows.Skip(1).Should().OnlyContain(t => t.Length == header.Length);

        string Value(string lsn, string column) => byLsn[lsn][Array.IndexOf(header, column)];

        //a last access update of $STANDARD_INFORMATION (the $MFT has the same values)
        Value("8527269", "RedoOperation").Should().Be("UpdateResidentValue");
        Value("8527269", "TargetEntryNumber").Should().Be("84870");
        Value("8527269", "AttributeType").Should().Be("StandardInformation");
        Value("8527269", "LastAccessedOn").Should().Be("2022-09-10 21:02:35.8676856");
        Value("8527269", "CreatedOn").Should().BeEmpty();
        Value("8527269", "Details").Should().Be("$STANDARD_INFORMATION timestamps changed: Accessed; previous: Accessed 2020-09-17 16:45:51.2906623");

        //the same change in the directory's index entry for it
        Value("8527292", "RedoOperation").Should().Be("UpdateFileNameRoot");
        Value("8527292", "TargetEntryNumber").Should().Be("1288");
        Value("8527292", "CreatedOn").Should().Be("2020-09-17 16:45:51.2906623");
        Value("8527292", "LastAccessedOn").Should().Be("2022-09-10 21:02:35.8676856");

        Value("10078477", "AttributeType").Should().Be("VolumeVersionObjectId");
        Value("10078477", "Details").Should().Be("Attribute created");
        Value("10078455", "Details").Should().Be("Index entry added (not a file name index)");
        Value("10078455", "EntryNumber").Should().BeEmpty();
        Value("9570436", "Details").Should().Be("Checkpoint (NTFS restart area 1.0): open attribute table at LSN 9565025, attribute names at LSN 9566135");
        Value("9565025", "Details").Should().Be("Checkpoint: open attribute table (211 attributes)");
        Value("8537726", "Details").Should().Be("Attribute opened");

        //the file and attribute of operations on open attributes, from the checkpoints and open records
        string Target(string lsn) => string.Join(" ", new[] { "TargetEntryNumber", "TargetSequenceNumber", "TargetAttributeType", "TargetAttributeName" }.Select(t => Value(lsn, t)));
        Target("10078455").Should().Be("25 1 IndexAllocation $O");
        Target("10078529").Should().Be("84656 2 Data $J");
        Target("12707550").Should().Be("6 6 Data ");
        Target("8537726").Should().Be("1406 1 IndexAllocation $I30");

        //transactions: ids are reused, the first LSN tells them apart
        Value("8527292", "TransactionFirstLsn").Should().Be("8527269");
        Value("10078529", "TransactionFirstLsn").Should().Be("10078438");

        rows.Skip(1).Should().OnlyContain(t => t[Array.IndexOf(header, "TargetPath")].Length == 0, "there is no $MFT");
    }

    [Test]
    public void LogFileWithMftHasTargetPaths()
    {
        var logFile = TestFiles.Gunzip(TestFiles.StolenSzechuan("$LogFile.gz"), _dir);
        var mft = TestFiles.Gunzip(TestFiles.StolenSzechuan("$MFT_directories.gz"), _dir);

        var (exitCode, output) = Run("-f", logFile, "-m", mft, "--csv", Out);

        exitCode.Should().Be(0, output);

        var csv = OutFile("$LogFile_Output.csv");
        var lsns = Column(csv, "Lsn");
        var targets = Column(csv, "TargetPath");
        targets[Array.IndexOf(lsns, "8527292")].Should().Be(".\\Windows\\assembly\\NativeImages_v4.0.30319_64\\System.Runtc259d85b#");
        targets[Array.IndexOf(lsns, "10078477")].Should().Be(".\\Windows\\WinSxS\\amd64_microsoft-windows-d..rvices-domain-files_31bf3856ad364e35_6.3.9600.16384_none_8bc96e4517571480");

        //open attributes: a directory's $I30, and the volume bitmap
        targets[Array.IndexOf(lsns, "8527384")].Should().Be(".\\Windows\\assembly\\NativeImages_v4.0.30319_64");
        targets[Array.IndexOf(lsns, "12707550")].Should().Be(".\\$Bitmap");
    }

    [Test]
    public void LogFileWithoutOutputExits1()
    {
        var logFile = TestFiles.Gunzip(TestFiles.StolenSzechuan("$LogFile.gz"), _dir);

        Run("-f", logFile).ExitCode.Should().Be(1);
    }

    [Test]
    public void TruncatedLogFile()
    {
        //only the restart pages and the start of the log: the rest is reported, not fatal
        var logFile = TestFiles.Gunzip(TestFiles.StolenSzechuan("$LogFile.gz"), _dir);
        var cut = Path.Combine(_dir, "cut");
        Directory.CreateDirectory(cut);
        var cutFile = Path.Combine(cut, "$LogFile");
        using (var input = File.OpenRead(logFile))
        using (var output = File.Create(cutFile))
        {
            var buffer = new byte[0x200000];
            var read = input.Read(buffer, 0, buffer.Length);
            output.Write(buffer, 0, read);
        }

        var (exitCode, text) = Run("-f", cutFile, "--csv", Out);

        exitCode.Should().Be(0, text);
        CsvRows(OutFile("$LogFile_Output.csv")).Should().BeGreaterThan(0);
    }
}
