using System.IO;
using FluentAssertions;
using MFT;
using NUnit.Framework;
using Usn;

namespace MFTECmd.Test;

/// <summary>
///     UsnRewind with the generated $MFT (root 5-5 and $Extend 11-11 are its only directories) and journals built in
///     memory
/// </summary>
[TestFixture]
public class RewindTests
{
    private const UsnEntry.UpdateReasonFlag Create = UsnEntry.UpdateReasonFlag.FileCreate;
    private const UsnEntry.UpdateReasonFlag Delete = UsnEntry.UpdateReasonFlag.FileDelete | UsnEntry.UpdateReasonFlag.Close;
    private const UsnEntry.UpdateReasonFlag OldName = UsnEntry.UpdateReasonFlag.RenameOldName;
    private const UsnEntry.UpdateReasonFlag NewName = UsnEntry.UpdateReasonFlag.RenameNewName;

    private static Mft GeneratedMft() => MftFile.Load(Path.Combine(TestFiles.Dir, "Generated", "$MFT"), false);

    [Test]
    public void PathsAreAsTheyWereWhenEachRecordWasWritten()
    {
        var entries = new UsnBuilder()
            //a directory the $MFT does not have: created, renamed and moved into $Extend, then deleted
            .Add("Projects", 100, 1, 5, 5, Create, true)
            .Add("notes.txt", 101, 1, 100, 1, Create)
            .Add("Projects", 100, 1, 5, 5, OldName, true)
            .Add("Archive", 100, 1, 11, 11, NewName, true)
            .Add("later.txt", 102, 1, 100, 1, Create)
            .Add("Archive", 100, 1, 11, 11, Delete, true)
            //$Extend had another name until here
            .Add("x.txt", 103, 1, 11, 11, Create)
            .Add("Old", 11, 11, 5, 5, OldName, true)
            .Add("$Extend", 11, 11, 5, 5, NewName, true)
            .Add("y.txt", 104, 1, 11, 11, Create)
            //a parent nothing knows
            .Add("z.txt", 105, 1, 999, 3, Create)
            .Entries();

        var paths = UsnRewind.GetParentPaths(GeneratedMft(), entries);

        paths.Should().Equal(
            ".",
            ".\\Projects",
            ".",
            ".\\Old",
            ".\\Old\\Archive",
            ".\\Old",
            ".\\Old",
            ".",
            ".",
            ".\\$Extend",
            ".\\PathUnknown\\Directory with ID 0x000003E7-00000003");
    }

    [Test]
    public void WithoutChangesThePathsAreTheMftPaths()
    {
        var mft = GeneratedMft();
        var entries = new UsnBuilder()
            .Add("hello.txt", 64, 1, 5, 5, UsnEntry.UpdateReasonFlag.DataExtend)
            .Add("$Quota", 24, 1, 11, 11, UsnEntry.UpdateReasonFlag.BasicInfoChange)
            .Entries();

        var paths = UsnRewind.GetParentPaths(mft, entries);

        paths.Should().Equal(mft.GetFullParentPath("00000005-00000005"), mft.GetFullParentPath("0000000B-0000000B"));
        paths.Should().Equal(".", ".\\$Extend");
    }

    [Test]
    public void ReusedEntryNumbersKeepTheirOwnPaths()
    {
        var entries = new UsnBuilder()
            .Add("first", 200, 1, 5, 5, Create, true)
            .Add("a.txt", 201, 1, 200, 1, Create)
            .Add("first", 200, 1, 5, 5, Delete, true)
            //the same entry, the next sequence number
            .Add("second", 200, 2, 11, 11, Create, true)
            .Add("b.txt", 202, 1, 200, 2, Create)
            .Add("second", 200, 2, 11, 11, Delete, true)
            .Entries();

        var paths = UsnRewind.GetParentPaths(GeneratedMft(), entries);

        paths[1].Should().Be(".\\first");
        paths[4].Should().Be(".\\$Extend\\second");
    }

    [Test]
    public void DirectoryLoopsDoNotHang()
    {
        //garbage: two directories that are each other's parent
        var entries = new UsnBuilder()
            .Add("a", 300, 1, 301, 1, Create, true)
            .Add("b", 301, 1, 300, 1, Create, true)
            .Add("c.txt", 302, 1, 300, 1, Create)
            .Entries();

        var paths = UsnRewind.GetParentPaths(GeneratedMft(), entries);

        paths[2].Should().StartWith(".\\PathUnknown\\Directory with ID 0x0000012C-00000001");
    }
}
