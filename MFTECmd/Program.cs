using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Boot;
using CsvHelper.Configuration;
using Exceptionless;
using MFT;
using MFT.Attributes;
using MFT.Other;
using NLog;
using NLog.Config;
using NLog.Targets;
using RawCopy;
using SDS;
using Secure;
using ServiceStack;
using ServiceStack.Text;
using Usn;
using CsvWriter = CsvHelper.CsvWriter;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace MFTECmd;

public class Program
{
    private static Logger _logger;

    private static Mft _mft;
    
    private static string Header =
        $"MFTECmd version {Assembly.GetExecutingAssembly().GetName().Version}" +
        "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
        "\r\nhttps://github.com/EricZimmerman/MFTECmd";

    private static string Footer = @"Examples: MFTECmd.exe -f ""C:\Temp\SomeMFT"" --csv ""c:\temp\out"" --csvf MyOutputFile.csv" +
                                   "\r\n\t " +
                                   @" MFTECmd.exe -f ""C:\Temp\SomeMFT"" --csv ""c:\temp\out""" + "\r\n\t " +
                                   @" MFTECmd.exe -f ""C:\Temp\SomeMFT"" --json ""c:\temp\jsonout""" + "\r\n\t " +
                                   @" MFTECmd.exe -f ""C:\Temp\SomeMFT"" --body ""c:\temp\bout"" --bdl c" + "\r\n\t " +
                                   @" MFTECmd.exe -f ""C:\Temp\SomeMFT"" --de 5-5" + "\r\n\t " +
                                   "\r\n\t" +
                                   "  Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes\r\n";


    private static string[] _args;

    private static CsvWriter _bodyWriter;
    private static CsvWriter _csvWriter;
    private static CsvWriter _fileListWriter;
    private static List<MFTRecordOut> _mftOutRecords;
    private static List<JEntryOut> _jOutRecords;

    private static readonly string BaseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    private static RootCommand _rootCommand;

    private static async Task Main(string[] args)
    {
        ExceptionlessClient.Default.Startup("88KHFwswzxfnYGejAlsVDao47ySGliI6vFbQPt9C");

        SetupNLog();

        _logger = LogManager.GetLogger("MFTECmd");
        _args = args;
    
        _rootCommand = new RootCommand
        {
            new Option<string>(
                "-f",
                "File to process ($MFT | $J | $Boot | $SDS). Required"),

            new Option<string>(
                "-m",
                "$MFT file to use when -f points to a $J file (Use this to resolve parent path in $J CSV output).\r\n"),

            new Option<string>(
                "--json",
                "Directory to save JSON formatted results to. This or --csv required unless --de or --body is specified"),
                
            new Option<string>(
                "--jsonf",
                "File name to save JSON formatted results to. When present, overrides default name"),
           
            new Option<string>(
                "--csv",
                "Directory to save CSV formatted results to. This or --json required unless --de or --body is specified"),
                
            new Option<string>(
                "--csvf",
                "File name to save CSV formatted results to. When present, overrides default name\r\n"),
                
            new Option<string>(
                "--body",
                "Directory to save bodyfile formatted results to. --bdl is also required when using this option"),

            new Option<string>(
                "--bodyf",
                "File name to save body formatted results to. When present, overrides default name"),

            new Option<string>(
                "--bdl",
                "Drive letter (C, D, etc.) to use with bodyfile. Only the drive letter itself should be provided"),

            new Option<bool>(
                "--blf",
                getDefaultValue:()=>false,
                "When true, use LF vs CRLF for newlines. Default is FALSE\r\n"),

            new Option<string>(
                "--dd",
                "Directory to save exported FILE record. --do is also required when using this option"),

            new Option<string>(
                "--do",
                "Offset of the FILE record to dump as decimal or hex. Ex: 5120 or 0x1400 Use --de or --vl 1 to see offsets\r\n"),

            new Option<string>(
                "--de",
                getDefaultValue:()=>"",
                "Dump full details for entry/sequence #. Format is 'Entry' or 'Entry-Seq' as decimal or hex. Example: 5, 624-5 or 0x270-0x5."),

            new Option<bool>(
                "--fls",
                getDefaultValue:()=>false,
                "When true, displays contents of directory specified by --de. Ignored when --de points to a file"),
            
            new Option<string>(
                "--ds",
                
                "Dump full details for Security Id as decimal or hex. Example: 624 or 0x270\r\n"),
            
            new Option<string>(
                "--dt",
                getDefaultValue:()=>"yyyy-MM-dd HH:mm:ss",
                "The custom date/time format to use when displaying time stamps. See https://goo.gl/CNVq0k for options"),
                
            new Option<bool>(
                "--sn",
                getDefaultValue:()=>false,
                "Include DOS file name types. Default is FALSE"),
            
            new Option<bool>(
                "--fl",
                getDefaultValue:()=>false,
                "Generate condensed file listing. Requires --csv. Default is FALSE"),
                        
            new Option<bool>(
                "--at",
                getDefaultValue:()=>false,
                "When true, include all timestamps from 0x30 attribute vs only when they differ from 0x10. Default is FALSE\r\n"),
            
            new Option<bool>(
                "--vss",
                getDefaultValue:()=>false,
                "Process all Volume Shadow Copies that exist on drive specified by -f . Default is FALSE"),
            
            new Option<bool>(
                "--dedupe",
                getDefaultValue:()=>false,
                "Deduplicate -f & VSCs based on SHA-1. First file found wins. Default is FALSE\r\n"),
            
            new Option<bool>(
                "--debug",
                getDefaultValue:()=>false,
                "Show debug information during processing"),
            
            new Option<bool>(
                "--trace",
                getDefaultValue:()=>false,
                "Show trace information during processing"),
                
        };
            
            _rootCommand.Description = Header + "\r\n\r\n" +Footer;

            // rootCommand.Handler = System.CommandLine.NamingConventionBinder.CommandHandler.Create<string,string,string,string,string,string,string,string,string,bool,string,string,string,bool,string,string,bool,bool,bool,bool,bool,bool,bool>(DoWork);
            //
            // rootCommand.Handler =System.CommandLine.Invocation.CommandHandler.Create(
            //     (string f, string m, string json, string jsonf, string csv, string csvf, string body, string bodyf, string bdl, bool blf, string dd, string @do, string de, bool fls, string ds, string dt, bool sn, bool fl , bool at, bool vss, bool dedupe, bool debug, bool trace) =>
            // {
            //
            // });
            
            _rootCommand.Handler = System.CommandLine.NamingConventionBinder.CommandHandler.Create(
                DoWork);
            
            await _rootCommand.InvokeAsync(args);
       
       
    }

    private static void DoWork(string f, string m, string json, string jsonf, string csv, string csvf, string body, string bodyf, string bdl, bool blf, string dd, string @do, string de, bool fls, string ds, string dt, bool sn, bool fl , bool at, bool vss, bool dedupe, bool debug, bool trace)
    {
        

        if (f.IsNullOrEmpty())
        {
            var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
            var hc = new HelpContext(helpBld,_rootCommand,Console.Out);

            helpBld.Write(hc);
                    
            _logger.Warn($"File '{f}' not found. Exiting\r\n");
            return;
        }

        if (f.IsNullOrEmpty() == false)
        {
            if (Path.GetDirectoryName(f)?.Length == 3)
            {
                //OK
            }
            else if (f.ToUpperInvariant().Contains("$EXTEND\\$USNJRNL"))
            {
                //OK
            }
            else if (!File.Exists(f))
            {
                //the path is off the root of the drive, so it works for things like $Boot, $MFT, etc
                _logger.Warn($"File '{f}' not found. Exiting");
                return;
            }
        }


        _logger.Info(Header);
        _logger.Info("");
        _logger.Info($"Command line: {string.Join(" ", _args)}\r\n");

        if (IsAdministrator() == false)
        {
            _logger.Fatal("Warning: Administrator privileges not found!\r\n");
        }

        if (debug)
        {
            LogManager.Configuration.LoggingRules.First().EnableLoggingForLevel(LogLevel.Debug);
        }

        if (trace)
        {
            LogManager.Configuration.LoggingRules.First().EnableLoggingForLevel(LogLevel.Trace);
        }

        LogManager.ReconfigExistingLoggers();


        if (vss & (IsAdministrator() == false))
        {
            _logger.Error("--vss is present, but administrator rights not found. Exiting\r\n");
            return;
        }

        //determine file type
        var ft = GetFileType(f);
        _logger.Warn($"File type: {ft}\r\n");

        if (csv.IsNullOrEmpty() == false)
        {
            if (Directory.ExistsDrive(Path.GetFullPath(csv)) == false)
            {
                _logger.Error("Destination location not available. Verify drive letter and try again. Exiting\r\n");
                return;
            }
        }

        if (json.IsNullOrEmpty() == false)
        {
            if (Directory.ExistsDrive(Path.GetFullPath(json)) == false)
            {
                _logger.Error("Destination location not available. Verify drive letter and try again. Exiting\r\n");
                return;
            }
        }

        if (body.IsNullOrEmpty() == false)
        {
            if (Directory.ExistsDrive(Path.GetFullPath(body)) == false)
            {
                _logger.Error("Destination location not available. Verify drive letter and try again. Exiting\r\n");
                return;
            }
        }

        
        switch (ft)
        {
            case FileType.Mft:
                if (csv.IsNullOrEmpty() &&
                    json.IsNullOrEmpty() &&
                    de.IsNullOrEmpty() &&
                    body.IsNullOrEmpty() &&
                    dd.IsNullOrEmpty())
                {
                    var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
                    var hc = new HelpContext(helpBld,_rootCommand,Console.Out);

                    helpBld.Write(hc);

                    _logger.Warn("--csv, --json, --body, --dd, or --de is required. Exiting");
                    return;
                }

                if (body.IsNullOrEmpty() == false &&
                    bdl.IsNullOrEmpty())
                {
                    var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
                    var hc = new HelpContext(helpBld,_rootCommand,Console.Out);

                    helpBld.Write(hc);

                    _logger.Warn("--bdl is required when using --body. Exiting");
                    return;
                }

                if (@do.IsNullOrEmpty() == false)
                {
                    if (dd.IsNullOrEmpty())
                    {
                        var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
                        var hc = new HelpContext(helpBld,_rootCommand,Console.Out);

                        helpBld.Write(hc);
                        
                        _logger.Warn("--dd option missing. Exiting\r\n");
                        return;
                    }
                    if (Directory.ExistsDrive(Path.GetFullPath(dd)) == false)
                    {
                        _logger.Error("Destination location not available. Verify drive letter and try again. Exiting\r\n");
                        return;
                    }
                }



                if (dd.IsNullOrEmpty() == false &&
                    @do.IsNullOrEmpty())
                {
                    var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
                    var hc = new HelpContext(helpBld,_rootCommand,Console.Out);

                    helpBld.Write(hc);

                    _logger.Warn("--do is required when using --dd. Exiting");
                    return;
                }

                ProcessMft(f,vss,dedupe,body,bdl,bodyf,blf,csv,csvf,json,jsonf,fl,dt,dd,@do,fls,sn,at);
                break;
            case FileType.LogFile:
                _logger.Warn("$LogFile not supported yet. Exiting");
                return;
            case FileType.UsnJournal:
                if (csv.IsNullOrEmpty() && json.IsNullOrEmpty()
                   )
                {
                    var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
                    var hc = new HelpContext(helpBld,_rootCommand,Console.Out);

                    helpBld.Write(hc);

                    _logger.Warn("--csv or --json is required. Exiting");
                    return;
                }

                if (m.IsNullOrEmpty() == false)
                {
                    //mft was supplied, does it exist?
                    if (File.Exists(m) == false)
                    {
                        _logger.Error($"MFT file '{m}' does not exist! Verify path and try again. Exiting\r\n");
                        return;
                    }

                    var ftm = GetFileType(m);

                    if (ftm != FileType.Mft)
                    {
                        _logger.Error($"File '{m}' is not an MFT file!! Verify path and try again. Exiting\r\n");
                        return;
                    }

                    ProcessMft(m,vss,dedupe,body,bdl,bodyf,blf,csv,csvf,json,jsonf,fl,dt,dd,@do,fls,sn,at);

                }

                ProcessJ(f, vss, dedupe, csv, csvf, json, jsonf, dt);
                break;
            case FileType.Boot:
                ProcessBoot(f, vss, dedupe, csv, csvf);
                break;
            case FileType.Sds:
                if (csv.IsNullOrEmpty() &&
                    ds.IsNullOrEmpty())

                {
                    var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
                    var hc = new HelpContext(helpBld,_rootCommand,Console.Out);

                    helpBld.Write(hc);

                    _logger.Warn("--csv or --ds is required. Exiting");
                    return;
                }

                ProcessSds(f, vss, dedupe, csv, csvf, ds);
                break;

            default:
                //unknown
                _logger.Error(
                    $"Unknown file type! Send '{f}' to saericzimmerman@gmail.com for assistance. Exiting");
                return;
        }
    }

    private static void ProcessBoot(string f, bool vss, bool dedupe, string csv, string csvf)
    {
        var sw = new Stopwatch();
        sw.Start();
        try
        {
            Boot.Boot bf;

            var bootFiles = new Dictionary<string, Boot.Boot>();

            try
            {
                bf = BootFile.Load(f);
                bootFiles.Add(f, bf);

                var ll = new List<string>();

                if (vss)
                {
                    var dl = Path.GetPathRoot(Path.GetFullPath(f));

                    var vssInfos = Helper.GetVssInfoViaWmi(dl);

                    foreach (var vssInfo in vssInfos)
                    {
                        var vsp = $"{Helper.GetRawVolumePath(vssInfo.VssNumber)}\\$Boot";
                        ll.Add(vsp);
                    }
                    var rawFiles = Helper.GetFiles(ll, dedupe);
                        
                    foreach (var rawCopyReturn in rawFiles) 
                    { 
                        bf = new Boot.Boot(rawCopyReturn.FileStream); 
                        bootFiles.Add(rawCopyReturn.InputFilename, bf);
                    }
                }

                  
            }
            catch (Exception)
            {
                try
                {
                    _logger.Warn($"'{f}' is in use. Rerouting...\r\n");

                    var ll = new List<string> {f};

                    if (vss)
                    {
                        var dl = Path.GetPathRoot(Path.GetFullPath(f));

                        var vssInfos = Helper.GetVssInfoViaWmi(dl);

                        foreach (var vssInfo in vssInfos)
                        {
                            var vsp = $"{Helper.GetRawVolumePath(vssInfo.VssNumber)}\\$Boot";
                            ll.Add(vsp);
                        }
                    }

                    var rawFiles = Helper.GetFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        bf = new Boot.Boot(rawCopyReturn.FileStream);
                        bootFiles.Add(rawCopyReturn.InputFilename, bf);
                    }
                }
                catch (Exception e)
                {
                    _logger.Error($"There was an error loading the file! Error: {e.Message}");
                    return;
                }
            }

            sw.Stop();

            var extra = string.Empty;

            if (bootFiles.Count > 1)
            {
                extra = " (and VSCs)";
            }

            _logger.Info(
                $"\r\nProcessed '{f}'{extra} in {sw.Elapsed.TotalSeconds:N4} seconds\r\n");

            StreamWriter swCsv = null;
        

            if (csv.IsNullOrEmpty() == false)
            {
                if (Directory.Exists(csv) == false)
                {
                    _logger.Warn(
                        $"Path to '{csv}' doesn't exist. Creating...");

                    try
                    {
                        Directory.CreateDirectory(csv);
                    }
                    catch (Exception)
                    {
                        _logger.Fatal(
                            $"Unable to create directory '{csv}'. Does a file with the same name exist? Exiting");
                        return;
                    }
                }

                var outName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_MFTECmd_$Boot_Output.csv";

                if (csvf.IsNullOrEmpty() == false)
                {
                    outName = Path.GetFileName(csvf);
                }

                var outFile = Path.Combine(csv, outName);

                _logger.Warn($"CSV output will be saved to '{outFile}'\r\n");

                swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

                _csvWriter = new CsvWriter(swCsv,CultureInfo.InvariantCulture);

                var foo = _csvWriter.Context.AutoMap<BootOut>();

                _csvWriter.Context.RegisterClassMap(foo);
                _csvWriter.WriteHeader<BootOut>();
                _csvWriter.NextRecord();
            }

            foreach (var b in bootFiles)
            {
                _logger.Error($"Boot file: '{b.Key}'");
                _logger.Info($"Boot entry point: {b.Value.BootEntryPoint}");
                _logger.Info($"File system signature: {b.Value.FileSystemSignature}");
                _logger.Info($"\r\nBytes per sector: {b.Value.BytesPerSector:N0}");
                _logger.Info($"Sectors per cluster: {b.Value.SectorsPerCluster:N0}");
                _logger.Info($"Cluster size: {b.Value.BytesPerSector * b.Value.SectorsPerCluster:N0}");
                _logger.Info($"\r\nTotal sectors: {b.Value.TotalSectors:N0}");
                _logger.Info($"Reserved sectors: {b.Value.ReservedSectors:N0}");
                _logger.Info($"\r\n$MFT cluster block #: {b.Value.MftClusterBlockNumber:N0}");
                _logger.Info($"$MFTMirr cluster block #: {b.Value.MirrorMftClusterBlockNumber:N0}");
                _logger.Info($"\r\nFILE entry size: {b.Value.MftEntrySize:N0}");
                _logger.Info($"Index entry size: {b.Value.IndexEntrySize:N0}");
                _logger.Info($"\r\nVolume serial number raw: 0x{b.Value.VolumeSerialNumberRaw:X}");
                _logger.Info($"Volume serial number: {b.Value.GetVolumeSerialNumber()}");
                _logger.Info($"Volume serial number 32-bit: {b.Value.GetVolumeSerialNumber(true)}");
                _logger.Info($"Volume serial number 32-bit reversed: {b.Value.GetVolumeSerialNumber(true, true)}");
                _logger.Info($"\r\nSector signature: {b.Value.GetSectorSignature()}\r\n");

                _logger.Trace(b.Value.Dump);


                var bo = new BootOut
                {
                    EntryPoint = b.Value.BootEntryPoint,
                    Signature = b.Value.FileSystemSignature,
                    BytesPerSector = b.Value.BytesPerSector,
                    SectorsPerCluster = b.Value.SectorsPerCluster,
                    ReservedSectors = b.Value.ReservedSectors,
                    TotalSectors = b.Value.TotalSectors,
                    MftClusterBlockNumber = b.Value.MftClusterBlockNumber,
                    MftMirrClusterBlockNumber = b.Value.MirrorMftClusterBlockNumber,
                    MftEntrySize = b.Value.MftEntrySize,
                    IndexEntrySize = b.Value.IndexEntrySize,
                    VolumeSerialNumberRaw = $"0x{b.Value.VolumeSerialNumberRaw:X}",
                    VolumeSerialNumber = b.Value.GetVolumeSerialNumber(),
                    VolumeSerialNumber32 = b.Value.GetVolumeSerialNumber(true),
                    VolumeSerialNumber32Reverse = b.Value.GetVolumeSerialNumber(true, true),
                    SectorSignature = b.Value.GetSectorSignature(),
                    SourceFile = b.Key
                };

                _csvWriter?.WriteRecord(bo);
                _csvWriter?.NextRecord();
            }

            swCsv?.Flush();
            swCsv?.Close();

                
        }
        catch (Exception e)
        {
            _logger.Error($"There was an error loading the file! Error: {e.Message}");
        }
    }

    private static void ProcessJ(string f, bool vss, bool dedupe, string csv, string csvf,string json, string jsonf, string dt)
    {
        var sw = new Stopwatch();
        sw.Start();
        Usn.Usn j;

        var jFiles = new Dictionary<string, Usn.Usn>();

        try
        {
            _logger.Trace("Initializing $J");

            try
            {
                j = UsnFile.Load(f);
                jFiles.Add(f, j);

                var ll = new List<string>();

                if (vss)
                {
                    var dl = Path.GetPathRoot(Path.GetFullPath(f));

                    var vssInfos = Helper.GetVssInfoViaWmi(dl);

                    foreach (var vssInfo in vssInfos)
                    {
                        var vsp = $"{Helper.GetRawVolumePath(vssInfo.VssNumber)}\\$Extend\\$UsnJrnl:$J";
                        ll.Add(vsp);
                    }

                    var rawFiles = Helper.GetFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        var start = UsnFile.FindStartingOffset(rawCopyReturn.FileStream);

                        j = new Usn.Usn(rawCopyReturn.FileStream, start);
                        jFiles.Add(rawCopyReturn.InputFilename, j);
                    }
                }

                 
            }
            catch (Exception)
            {
                try
                {
                    _logger.Warn($"'{f}' is in use. Rerouting...\r\n");

                    var ll = new List<string> {f};

                    if (vss)
                    {
                        var dl = Path.GetPathRoot(f);

                        var vssInfos = Helper.GetVssInfoViaWmi(dl);

                        foreach (var vssInfo in vssInfos)
                        {
                            var vsp = $"{Helper.GetRawVolumePath(vssInfo.VssNumber)}\\$Extend\\$UsnJrnl:$J";
                            ll.Add(vsp);
                        }
                    }

                    var rawFiles = Helper.GetFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        var start = UsnFile.FindStartingOffset(rawCopyReturn.FileStream);
                        j = new Usn.Usn(rawCopyReturn.FileStream, start);
                        jFiles.Add(rawCopyReturn.InputFilename, j);
                    }
                }
                catch (Exception e)
                {
                    _logger.Error($"There was an error loading the file! Error: {e.Message}");
                    return;
                }
            }

            var dateTimeOffset = DateTimeOffset.UtcNow;

            sw.Stop();

            var extra = string.Empty;

            if (jFiles.Count > 1)
            {
                extra = " (and VSCs)";
            }

            _logger.Info(
                $"\r\nProcessed '{f}'{extra} in {sw.Elapsed.TotalSeconds:N4} seconds");
            Console.WriteLine();

            if (json.IsNullOrEmpty() == false)
            {
                _jOutRecords = new List<JEntryOut>();
            }

            foreach (var jFile in jFiles)
            {
                _logger.Info($"Usn entries found in '{jFile.Key}': {jFile.Value.UsnEntries.Count:N0}");

                if (csv.IsNullOrEmpty() == false)
                {
                    StreamWriter swCsv;

                    if (Directory.Exists(csv) == false)
                    {
                        _logger.Warn(
                            $"Path to '{csv}' doesn't exist. Creating...");

                        try
                        {
                            Directory.CreateDirectory(csv);
                        }
                        catch (Exception)
                        {
                            _logger.Fatal(
                                $"Unable to create directory '{csv}'. Does a file with the same name exist? Exiting");
                            return;
                        }
                    }

                    string outName;
                    string outFile;

                    if (jFile.Key.StartsWith("\\\\.\\"))
                    {
                        var vssNumber = Helper.GetVssNumberFromPath(jFile.Key);
                        var vssTime = Helper.GetVssCreationDate(vssNumber);

                        outName =
                            $"{dateTimeOffset:yyyyMMddHHmmss}_VSS{vssNumber}_{vssTime:yyyyMMddHHmmss_fffffff}_MFTECmd_$J_Output.csv";

                        if (csvf.IsNullOrEmpty() == false)
                        {
                            outName =
                                $"VSS{vssNumber}_{vssTime:yyyyMMddHHmmss}_{Path.GetFileName(csvf)}";
                        }
                    }
                    else
                    {
                        //normal file
                        outName = $"{dateTimeOffset:yyyyMMddHHmmss}_MFTECmd_$J_Output.csv";

                        if (csvf.IsNullOrEmpty() == false)
                        {
                            outName = Path.GetFileName(csvf);
                        }
                    }

                    outFile = Path.Combine(csv, outName);

                    _logger.Warn($"\tCSV output will be saved to '{outFile}'");

                    swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

                    _csvWriter = new CsvWriter(swCsv,CultureInfo.InvariantCulture);

                    var foo = _csvWriter.Context.AutoMap<JEntryOut>();

                    foo.Map(t => t.UpdateTimestamp).Convert(t =>
                        $"{t.Value.UpdateTimestamp.ToString(dt)}");

                    _csvWriter.Context.RegisterClassMap(foo);

                    _csvWriter.WriteHeader<JEntryOut>();
                    _csvWriter.NextRecord();

                    foreach (var jUsnEntry in jFile.Value.UsnEntries)
                    {
                        var parentPath = string.Empty;

                        if (_mft != null)
                        {
                            parentPath = _mft.GetFullParentPath($"{jUsnEntry.ParentFileReference.EntryNumber:X8}-{jUsnEntry.ParentFileReference.SequenceNumber:X8}");
                        }

                        var jOut = new JEntryOut
                        {
                            Name = jUsnEntry.Name,
                            UpdateTimestamp = jUsnEntry.UpdateTimestamp,
                            EntryNumber = jUsnEntry.FileReference.EntryNumber,
                            SequenceNumber = jUsnEntry.FileReference.SequenceNumber,

                            ParentEntryNumber = jUsnEntry.ParentFileReference.EntryNumber,
                            ParentSequenceNumber = jUsnEntry.ParentFileReference.SequenceNumber,

                            ParentPath = parentPath,

                            UpdateSequenceNumber = jUsnEntry.UpdateSequenceNumber,
                            UpdateReasons = jUsnEntry.UpdateReasons.ToString().Replace(", ", "|"),
                            FileAttributes = jUsnEntry.FileAttributes.ToString().Replace(", ", "|"),
                            OffsetToData = jUsnEntry.OffsetToData,
                            SourceFile = jFile.Key
                        };

                        _csvWriter.WriteRecord(jOut);
                        _csvWriter.NextRecord();
                    }

                    swCsv.Flush();
                    swCsv.Close();
                }
                else
                {

                    foreach (var jUsnEntry in jFile.Value.UsnEntries)
                    {
                        var parentPath = string.Empty;

                        if (_mft != null)
                        {
                            parentPath = _mft.GetFullParentPath($"{jUsnEntry.ParentFileReference.EntryNumber:X8}-{jUsnEntry.ParentFileReference.SequenceNumber:X8}");
                        }

                        var jOut = new JEntryOut
                        {
                            Name = jUsnEntry.Name,
                            UpdateTimestamp = jUsnEntry.UpdateTimestamp,
                            EntryNumber = jUsnEntry.FileReference.EntryNumber,
                            SequenceNumber = jUsnEntry.FileReference.SequenceNumber,

                            ParentEntryNumber = jUsnEntry.ParentFileReference.EntryNumber,
                            ParentSequenceNumber = jUsnEntry.ParentFileReference.SequenceNumber,

                            ParentPath = parentPath,

                            UpdateSequenceNumber = jUsnEntry.UpdateSequenceNumber,
                            UpdateReasons = jUsnEntry.UpdateReasons.ToString().Replace(", ", "|"),
                            FileAttributes = jUsnEntry.FileAttributes.ToString().Replace(", ", "|"),
                            OffsetToData = jUsnEntry.OffsetToData,
                            SourceFile = jFile.Key
                        };

                        _jOutRecords?.Add(jOut);
                        
                    }

                    if (json.IsNullOrEmpty() == false)
                    {
                        string outName;
                        string outFile;

                        if (Directory.Exists(json) == false)
                        {
                            _logger.Warn(
                                $"Path to '{json}' doesn't exist. Creating...");

                            try
                            {
                                Directory.CreateDirectory(json);
                            }
                            catch (Exception)
                            {
                                _logger.Fatal(
                                    $"Unable to create directory '{json}'. Does a file with the same name exist? Exiting");
                                return;
                            }
                        }

                        if (jFile.Key.StartsWith("\\\\.\\"))
                        {
                            var vssNumber = Helper.GetVssNumberFromPath(jFile.Key);
                            var vssTime = Helper.GetVssCreationDate(vssNumber);

                            outName =
                                $"{dateTimeOffset:yyyyMMddHHmmss}_VSS{vssNumber}_{vssTime:yyyyMMddHHmmss_fffffff}_MFTECmd_$J_Output.json";

                            if (jsonf.IsNullOrEmpty() == false)
                            {
                                outName =
                                    $"VSS{vssNumber}_{vssTime:yyyyMMddHHmmss}_{Path.GetFileName(jsonf)}";
                            }
                        }
                        else
                        {
                            //normal file
                            outName = $"{dateTimeOffset:yyyyMMddHHmmss}_MFTECmd_$J_Output.json";

                            if (jsonf.IsNullOrEmpty() == false)
                            {
                                outName = Path.GetFileName(jsonf);
                            }
                        }

                        outFile = Path.Combine(json, outName);

                        _logger.Warn($"\tJSON output will be saved to '{outFile}'");

                        try
                        {
                            JsConfig.DateHandler = DateHandler.ISO8601;

                            using (var sWrite =
                                   new StreamWriter(new FileStream(outFile, FileMode.OpenOrCreate, FileAccess.Write)))
                            {
                                if (_jOutRecords != null)
                                {
                                    foreach (var jOutRecord in _jOutRecords)
                                    {
                                        sWrite.WriteLine(jOutRecord.ToJson());
                                    }
                                }

                                sWrite.Flush();
                                sWrite.Close();
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Error(
                                $"\r\nError exporting to JSON. Please report to saericzimmerman@gmail.com.\r\n\r\nError: {e.Message}");
                        }

                    }

                }

                Console.WriteLine();
            }
        }
        catch (Exception e)
        {
            _logger.Error(
                $"There was an error processing $J data! Last offset processed: 0x{Usn.Usn.LastOffset:X}. Error: {e.Message}");
        }
    }

    private static void ProcessSds(string f, bool vss, bool dedupe, string csv, string csvf, string ds)
    {
        var sw = new Stopwatch();
        sw.Start();
        try
        {
            Sds sds;

            var sdsFiles = new Dictionary<string, Sds>();

            try
            {
                sds = SdsFile.Load(f);
                sdsFiles.Add(f, sds);

                var ll = new List<string>();

                if (vss)
                {
                    var dl = Path.GetPathRoot(Path.GetFullPath(f));

                    var vssInfos = Helper.GetVssInfoViaWmi(dl);

                    foreach (var vssInfo in vssInfos)
                    {
                        var vsp = $"{Helper.GetRawVolumePath(vssInfo.VssNumber)}\\$Secure:$SDS";
                        ll.Add(vsp);
                    }
                    var rawFiles = Helper.GetFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        sds = new Sds(rawCopyReturn.FileStream);
                        sdsFiles.Add(rawCopyReturn.InputFilename, sds);
                    }
                }

                   
            }
            catch (Exception)
            {
                try
                {
                    _logger.Warn($"'{f}' is in use. Rerouting...\r\n");

                    var ll = new List<string> {f};

                    if (vss)
                    {
                        var dl = Path.GetPathRoot(f);

                        var vssInfos = Helper.GetVssInfoViaWmi(dl);

                        foreach (var vssInfo in vssInfos)
                        {
                            var vsp = $"{Helper.GetRawVolumePath(vssInfo.VssNumber)}\\$Secure:$SDS";
                            ll.Add(vsp);
                        }
                    }

                    var rawFiles = Helper.GetFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        sds = new Sds(rawCopyReturn.FileStream);
                        sdsFiles.Add(rawCopyReturn.InputFilename, sds);
                    }
                }
                catch (Exception e)
                {
                    _logger.Error($"There was an error loading the file! Error: {e.Message}");
                    return;
                }
            }

            sw.Stop();

            var extra = string.Empty;

            if (sdsFiles.Count > 1)
            {
                extra = " (and VSCs)";
            }

            _logger.Info(
                $"\r\nProcessed '{f}'{extra} in {sw.Elapsed.TotalSeconds:N4} seconds");
            Console.WriteLine();

            var dt = DateTimeOffset.UtcNow;

            foreach (var sdsFile in sdsFiles)
            {
                _logger.Info($"SDS entries found in '{sdsFile.Key}': {sdsFile.Value.SdsEntries.Count:N0}");

                if (csv.IsNullOrEmpty() == false)
                {
                    StreamWriter swCsv;

                    if (Directory.Exists(csv) == false)
                    {
                        _logger.Warn(
                            $"Path to '{csv}' doesn't exist. Creating...");

                        try
                        {
                            Directory.CreateDirectory(csv);
                        }
                        catch (Exception)
                        {
                            _logger.Fatal(
                                $"Unable to create directory '{csv}'. Does a file with the same name exist? Exiting");
                            return;
                        }
                    }

                    string outName;
                    string outFile;

                    if (sdsFile.Key.StartsWith("\\\\.\\"))
                    {
                        var vssNumber = Helper.GetVssNumberFromPath(sdsFile.Key);
                        var vssTime = Helper.GetVssCreationDate(vssNumber);

                        outName =
                            $"{dt:yyyyMMddHHmmss}_VSS{vssNumber}_{vssTime:yyyyMMddHHmmss_fffffff}_MFTECmd_$SDS_Output.csv";

                        if (csvf.IsNullOrEmpty() == false)
                        {
                            outName =
                                $"VSS{vssNumber}_{vssTime:yyyyMMddHHmmss}_{Path.GetFileName(csvf)}";
                        }
                    }
                    else
                    {
                        //normal file
                        outName = $"{dt:yyyyMMddHHmmss}_MFTECmd_$SDS_Output.csv";

                        if (csvf.IsNullOrEmpty() == false)
                        {
                            outName = Path.GetFileName(csvf);
                        }
                    }

                    outFile = Path.Combine(csv, outName);

                    _logger.Warn($"\tCSV output will be saved to '{outFile}'");

                    swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

                    _csvWriter = new CsvWriter(swCsv,CultureInfo.InvariantCulture);

                    var foo = _csvWriter.Context.AutoMap<SdsOut>();

                    _csvWriter.Context.RegisterClassMap(foo);

                    _csvWriter.WriteHeader<SdsOut>();
                    _csvWriter.NextRecord();

                    foreach (var sdsEntry in sdsFile.Value.SdsEntries)
                    {
                        _logger.Debug($"Processing Id '{sdsEntry.Id}'");

                        var sdO = new SdsOut
                        {
                            Hash = sdsEntry.Hash,
                            Id = sdsEntry.Id,
                            Offset = sdsEntry.Offset,
                            OwnerSid = sdsEntry.SecurityDescriptor.OwnerSid,
                            GroupSid = sdsEntry.SecurityDescriptor.GroupSid,
                            Control = sdsEntry.SecurityDescriptor.Control.ToString().Replace(", ", "|"),
                            FileOffset = sdsEntry.FileOffset,
                            SourceFile = sdsFile.Key
                        };

                        if (sdsEntry.SecurityDescriptor.Sacl != null && sdsEntry.SecurityDescriptor.Sacl.RawBytes.Length> 0)
                        {
                            sdO.SaclAceCount = sdsEntry.SecurityDescriptor.Sacl.AceCount;
                            var uniqueAce = new HashSet<string>();
                            foreach (var saclAceRecord in sdsEntry.SecurityDescriptor.Sacl.AceRecords)
                            {
                                uniqueAce.Add(saclAceRecord.AceType.ToString());
                            }

                            sdO.UniqueSaclAceTypes = string.Join("|", uniqueAce);
                        }

                        if (sdsEntry.SecurityDescriptor.Dacl != null)
                        {
                            sdO.DaclAceCount = sdsEntry.SecurityDescriptor.Dacl.AceCount;
                            var uniqueAce = new HashSet<string>();
                            foreach (var daclAceRecord in sdsEntry.SecurityDescriptor.Dacl.AceRecords)
                            {
                                uniqueAce.Add(daclAceRecord.AceType.ToString());
                            }

                            sdO.UniqueDaclAceTypes = string.Join("|", uniqueAce);
                        }

                        _logger.Trace(sdsEntry.SecurityDescriptor);

                        _csvWriter.WriteRecord(sdO);
                        _csvWriter.NextRecord();
                    }

                    swCsv.Flush();
                    swCsv.Close();

                    Console.WriteLine();
                }
            }


            if (ds.IsNullOrEmpty() == false)
            {
                bool valOk;
                int secId;

                if (ds.ToUpperInvariant().StartsWith("0X"))
                {
                    var rawNum = ds.ToUpperInvariant().Replace("0X", "");

                    valOk = int.TryParse(rawNum, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out secId);
                }
                else
                {
                    valOk = int.TryParse(ds, out secId);
                }

                if (valOk == false)
                {
                    _logger.Warn(
                        $"Could not parse '{ds}' to valid value. Exiting");
                    return;
                }

                foreach (var sds1 in sdsFiles)
                {
                    var sd = sds1.Value.SdsEntries.FirstOrDefault(t => t.Id == secId);

                    if (sd == null)
                    {
                        _logger.Warn($"Could not find entry with Id: {secId}");
                        continue;
                    }

                    _logger.Info("");
                    _logger.Fatal($"Details for security record # {sd.Id} (0x{sd.Id:X}), Found in '{sds1.Key}'");
                    _logger.Info($"Hash value: {sd.Hash}, Offset: 0x{sd.Offset:X}");
                    _logger.Info($"Control flags: {sd.SecurityDescriptor.Control.ToString().Replace(", ", " | ")}");
                    _logger.Info("");

                    if (sd.SecurityDescriptor.OwnerSidType == Helpers.SidTypeEnum.UnknownOrUserSid)
                    {
                        _logger.Info($"Owner SID: {sd.SecurityDescriptor.OwnerSid}");
                    }
                    else
                    {
                        _logger.Info(
                            $"Owner SID: {Helpers.GetDescriptionFromEnumValue(sd.SecurityDescriptor.OwnerSidType)}");
                    }

                    if (sd.SecurityDescriptor.GroupSidType == Helpers.SidTypeEnum.UnknownOrUserSid)
                    {
                        _logger.Info($"Group SID: {sd.SecurityDescriptor.GroupSid}");
                    }
                    else
                    {
                        _logger.Info(
                            $"Group SID: {Helpers.GetDescriptionFromEnumValue(sd.SecurityDescriptor.GroupSidType)}");
                    }

                    if (sd.SecurityDescriptor.Dacl != null)
                    {
                        _logger.Info("");

                        _logger.Error("Discretionary Access Control List");
                        DumpAcl(sd.SecurityDescriptor.Dacl);
                    }

                    if (sd.SecurityDescriptor.Sacl != null)
                    {
                        _logger.Info("");

                        _logger.Error("System Access Control List");
                        DumpAcl(sd.SecurityDescriptor.Sacl);
                    }
                }
            }
        }
        catch (Exception e)
        {
            _logger.Error(
                $"There was an error loading the file! Error: {e.Message}");
        }
    }

    private static void DumpAcl(XAclRecord acl)
    {
        _logger.Info($"ACE record count: {acl.AceRecords.Count:N0}");
        _logger.Info($"ACL type: {acl.AclType}");
        _logger.Info("");
        var i = 0;
        foreach (var aceRecord in acl.AceRecords)
        {
            _logger.Warn($"------------ Ace record #{i} ------------");
            _logger.Info($"Type: {aceRecord.AceType}");
            _logger.Info($"Flags: {aceRecord.AceFlags.ToString().Replace(", ", " | ")}");
            _logger.Info($"Mask: {aceRecord.Mask.ToString().Replace(", ", " | ")}");

            if (aceRecord.SidType == Helpers.SidTypeEnum.UnknownOrUserSid)
            {
                _logger.Info($"SID: {aceRecord.Sid}");
            }
            else
            {
                _logger.Info($"SID: {Helpers.GetDescriptionFromEnumValue(aceRecord.SidType)}");
            }

            i += 1;
            _logger.Info("");
        }
    }

    private static void ProcessMft(string file, bool vss, bool dedupe, string body, string bdl,string bodyf, bool blf, string csv, string csvf, string json, string jsonf, bool fl, string dt, string dd, string @do, bool fls, bool includeShort, bool alltimestamp)
    {
        var mftFiles = new Dictionary<string, Mft>();

        Mft localMft;


        var sw = new Stopwatch();
        sw.Start();
        try
        {
            _mft = MftFile.Load(file);
            mftFiles.Add(file, _mft);

            var ll = new List<string>();

            if (vss)
            {
                var dl = Path.GetPathRoot(Path.GetFullPath(file));

                var vssInfos = Helper.GetVssInfoViaWmi(dl);

                foreach (var vssInfo in vssInfos)
                {
                    var vsp = $"{Helper.GetRawVolumePath(vssInfo.VssNumber)}\\$MFT";
                    ll.Add(vsp);
                }

                var rawFiles = Helper.GetFiles(ll, dedupe);

                foreach (var rawCopyReturn in rawFiles)
                {
                    localMft = new Mft(rawCopyReturn.FileStream);
                    mftFiles.Add(rawCopyReturn.InputFilename, localMft);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            _logger.Warn($"'{file}' is in use. Rerouting...\r\n");

            var ll = new List<string> {file};

            if (vss)
            {
                var dl = Path.GetPathRoot(file);

                var vssInfos = Helper.GetVssInfoViaWmi(dl);

                foreach (var vssInfo in vssInfos)
                {
                    var vsp = $"{Helper.GetRawVolumePath(vssInfo.VssNumber)}\\$MFT";
                    ll.Add(vsp);
                }
            }

            try
            {
                var rawFiles = Helper.GetFiles(ll, dedupe);

                foreach (var rawCopyReturn in rawFiles)
                {
                    localMft = new Mft(rawCopyReturn.FileStream);
                    mftFiles.Add(rawCopyReturn.InputFilename, localMft);
                }

                _mft = mftFiles.First().Value;
            }
            catch (Exception e)
            {
                _logger.Error($"There was an error loading the file! Error: {e.Message}");
                return;
            }
        }
        catch (Exception e)
        {
            _logger.Error($"There was an error loading the file! Error: {e.Message}");
            return;
        }

        sw.Stop();

        var extra = string.Empty;

        if (mftFiles.Count > 1)
        {
            extra = " (and VSCs)";
        }

        _logger.Info(
            $"Processed '{file}'{extra} in {sw.Elapsed.TotalSeconds:N4} seconds\r\n");

        var dateTimeOffset = DateTimeOffset.UtcNow;

        foreach (var mftFile in mftFiles)
        {
            _logger.Info(
                $"{mftFile.Key}: FILE records found: {mftFile.Value.FileRecords.Count:N0} (Free records: {mftFile.Value.FreeFileRecords.Count:N0}) File size: {Helper.BytesToString(mftFile.Value.FileSize)}");

            StreamWriter swBody = null;
            StreamWriter swCsv = null;
            StreamWriter swFileList = null;
                

            if (body.IsNullOrEmpty() == false)
            {
                bdl =
                    bdl.Substring(0, 1);

                if (Directory.Exists(body) == false)
                {
                    _logger.Warn(
                        $"Path to '{body}' doesn't exist. Creating...");
                    try
                    {
                        Directory.CreateDirectory(body);
                    }
                    catch (Exception)
                    {
                        _logger.Fatal(
                            $"Unable to create directory '{body}'. Does a file with the same name exist? Exiting");
                        return;
                    }
                }

                string outName;
                string outFile;

                if (mftFile.Key.StartsWith("\\\\.\\"))
                {
                    var vssNumber = Helper.GetVssNumberFromPath(mftFile.Key);
                    var vssTime = Helper.GetVssCreationDate(vssNumber);

                    outName =
                        $"{dateTimeOffset:yyyyMMddHHmmss}_VSS{vssNumber}_{vssTime:yyyyMMddHHmmss_fffffff}_MFTECmd_$MFT_Output.body";

                    if (bodyf.IsNullOrEmpty() == false)
                    {
                        outName =
                            $"VSS{vssNumber}_{vssTime:yyyyMMddHHmmss}_{Path.GetFileName(bodyf)}";
                    }
                }
                else
                {
                    //normal file
                    outName = $"{dateTimeOffset:yyyyMMddHHmmss}_MFTECmd_$MFT_Output.body";

                    if (bodyf.IsNullOrEmpty() == false)
                    {
                        outName = Path.GetFileName(bodyf);
                    }
                }

                outFile = Path.Combine(body, outName);

                _logger.Warn($"\tBodyfile output will be saved to '{outFile}'");

                try
                {
                    swBody = new StreamWriter(outFile, false, Encoding.UTF8, 4096 * 4);
                    if (blf)
                    {
                        swBody.NewLine = "\n";
                    }

                    var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                    {
                        Delimiter = "|"
                            
                    };

                    if (blf)
                    {
                        config.NewLine = "\n";
                    }

                    _bodyWriter = new CsvWriter(swBody,config);

                    var foo = _bodyWriter.Context.AutoMap<BodyFile>();
                    foo.Map(t => t.Md5).Index(0);
                    foo.Map(t => t.Name).Index(1);
                    foo.Map(t => t.Inode).Index(2);
                    foo.Map(t => t.Mode).Index(3);
                    foo.Map(t => t.Uid).Index(4);
                    foo.Map(t => t.Gid).Index(5);
                    foo.Map(t => t.Size).Index(6);
                    foo.Map(t => t.AccessTime).Index(7);
                    foo.Map(t => t.ModifiedTime).Index(8);
                    foo.Map(t => t.RecordModifiedTime).Index(9);
                    foo.Map(t => t.CreatedTime).Index(10);

                    _bodyWriter.Context.RegisterClassMap(foo);
                }
                catch (Exception e)
                {
                    _logger.Error(
                        $"\r\nError setting up bodyfile export. Please report to saericzimmerman@gmail.com.\r\n\r\nError: {e.Message}");
                    _bodyWriter = null;
                }
            }

            if (json.IsNullOrEmpty() == false)
            {
                _mftOutRecords = new List<MFTRecordOut>();
                   
            }

            if (csv.IsNullOrEmpty() == false ||
                json.IsNullOrEmpty() == false)
            {
                if (csv.IsNullOrEmpty() == false)
                {
                    if (Directory.Exists(csv) == false)
                    {
                        _logger.Warn(
                            $"Path to '{csv}' doesn't exist. Creating...");
                        try
                        {
                            Directory.CreateDirectory(csv);
                        }
                        catch (Exception)
                        {
                            _logger.Fatal(
                                $"Unable to create directory '{csv}'. Does a file with the same name exist? Exiting");
                            return;
                        }
                    }

                    string outName;
                    string outFile;

                    if (mftFile.Key.StartsWith("\\\\.\\"))
                    {
                        var vssNumber = Helper.GetVssNumberFromPath(mftFile.Key);
                        var vssTime = Helper.GetVssCreationDate(vssNumber);

                        outName =
                            $"{dateTimeOffset:yyyyMMddHHmmss}_VSS{vssNumber}_{vssTime:yyyyMMddHHmmss_fffffff}_MFTECmd_$MFT_Output.csv";

                        if (csvf.IsNullOrEmpty() == false)
                        {
                            outName =
                                $"VSS{vssNumber}_{vssTime:yyyyMMddHHmmss}_{Path.GetFileName(csvf)}";
                        }
                    }
                    else
                    {
                        //normal file
                        outName = $"{dateTimeOffset:yyyyMMddHHmmss}_MFTECmd_$MFT_Output.csv";

                        if (csvf.IsNullOrEmpty() == false)
                        {
                            outName = Path.GetFileName(csvf);
                        }
                    }

                    outFile = Path.Combine(csv, outName);

                    _logger.Warn($"\tCSV output will be saved to '{outFile}'");

                    if (fl)
                    {
                        var outFileFl = outFile.Replace("$MFT_Output", "$MFT_Output_FileListing");

                        if (csvf.IsNullOrEmpty() == false)
                        {
                            outFileFl = Path.Combine(Path.GetDirectoryName(outFileFl),$"{Path.GetFileNameWithoutExtension(outFileFl)}_FileListing{Path.GetExtension(outFileFl)}");
                        }

                        _logger.Warn($"\tCSV file listing output will be saved to '{outFileFl}'");

                        swFileList = new StreamWriter(outFileFl, false, Encoding.UTF8);
                        _fileListWriter = new CsvWriter(swFileList,CultureInfo.InvariantCulture);

                        var foo = _fileListWriter.Context.AutoMap<FileListEntry>();

                        _fileListWriter.Context.RegisterClassMap(foo);
                        foo.Map(t => t.Created0x10).Convert(t =>
                            $"{t.Value.Created0x10?.ToString(dt)}");
                        foo.Map(t => t.LastModified0x10).Convert(t =>
                            $"{t.Value.LastModified0x10?.ToString(dt)}");

                        _fileListWriter.WriteHeader<FileListEntry>();
                        _fileListWriter.NextRecord();
                    }

                      
                    try
                    {
                        swCsv = new StreamWriter(outFile, false, Encoding.UTF8, 4096 * 4);

                        _csvWriter = new CsvWriter(swCsv,CultureInfo.InvariantCulture);

                        var foo = _csvWriter.Context.AutoMap<MFTRecordOut>();

                        foo.Map(t => t.EntryNumber).Index(0);
                        foo.Map(t => t.SequenceNumber).Index(1);
                        foo.Map(t => t.InUse).Index(2);
                        foo.Map(t => t.ParentEntryNumber).Index(3);
                        foo.Map(t => t.ParentSequenceNumber).Index(4);
                        foo.Map(t => t.ParentPath).Index(5);
                        foo.Map(t => t.FileName).Index(6);
                        foo.Map(t => t.Extension).Index(7);
                        foo.Map(t => t.FileSize).Index(8);
                        foo.Map(t => t.ReferenceCount).Index(9);
                        foo.Map(t => t.ReparseTarget).Index(10);

                        foo.Map(t => t.IsDirectory).Index(11);
                        foo.Map(t => t.HasAds).Index(12);
                        foo.Map(t => t.IsAds).Index(13);
                        foo.Map(t => t.Timestomped).Index(14).Name("SI<FN");
                        foo.Map(t => t.uSecZeros).Index(15);
                        foo.Map(t => t.Copied).Index(16);
                        foo.Map(t => t.SiFlags).Convert(t => t.Value.SiFlags.ToString().Replace(", ", "|"))
                            .Index(17);
                        foo.Map(t => t.NameType).Index(18);

                        foo.Map(t => t.Created0x10).Convert(t =>
                            $"{t.Value.Created0x10?.ToString(dt)}").Index(19);
                        foo.Map(t => t.Created0x30).Convert(t =>
                            $"{t.Value.Created0x30?.ToString(dt)}").Index(20);

                        foo.Map(t => t.LastModified0x10).Convert(t =>
                                $"{t.Value.LastModified0x10?.ToString(dt)}")
                            .Index(21);
                        foo.Map(t => t.LastModified0x30).Convert(t =>
                                $"{t.Value.LastModified0x30?.ToString(dt)}")
                            .Index(22);

                        foo.Map(t => t.LastRecordChange0x10).Convert(t =>
                                $"{t.Value.LastRecordChange0x10?.ToString(dt)}")
                            .Index(23);
                        foo.Map(t => t.LastRecordChange0x30).Convert(t =>
                                $"{t.Value.LastRecordChange0x30?.ToString(dt)}")
                            .Index(24);

                        foo.Map(t => t.LastAccess0x10).Convert(t =>
                                $"{t.Value.LastAccess0x10?.ToString(dt)}")
                            .Index(25);

                        foo.Map(t => t.LastAccess0x30).Convert(t =>
                                $"{t.Value.LastAccess0x30?.ToString(dt)}")
                            .Index(26);

                        foo.Map(t => t.UpdateSequenceNumber).Index(27);
                        foo.Map(t => t.LogfileSequenceNumber).Index(28);
                        foo.Map(t => t.SecurityId).Index(29);

                        foo.Map(t => t.ObjectIdFileDroid).Index(30);
                        foo.Map(t => t.LoggedUtilStream).Index(31);
                        foo.Map(t => t.ZoneIdContents).Index(32);

                        foo.Map(t => t.FnAttributeId).Ignore();
                        foo.Map(t => t.OtherAttributeId).Ignore();

                        _csvWriter.Context.RegisterClassMap(foo);

                        _csvWriter.WriteHeader<MFTRecordOut>();
                        _csvWriter.NextRecord();
                    }
                    catch (Exception e)
                    {
                        _logger.Error(
                            $"\r\nError setting up CSV export. Please report to saericzimmerman@gmail.com.\r\n\r\nError: {e.Message}");
                        _csvWriter = null;
                    }
                }
            }

            if (swBody != null || swCsv != null || _mftOutRecords != null)
            {
                try
                {
                    ProcessRecords(mftFile.Value.FileRecords,includeShort,alltimestamp,bdl);
                    ProcessRecords(mftFile.Value.FreeFileRecords,includeShort,alltimestamp,bdl);
                }
                catch (Exception ex)
                {
                    _logger.Error(
                        $"\r\nError exporting data. Please report to saericzimmerman@gmail.com.\r\n\r\nError: {ex.Message}");
                }
            }

            swCsv?.Flush();
            swCsv?.Close();
                
            swFileList?.Flush();
            swFileList?.Close();

            swBody?.Flush();
            swBody?.Close();

            if (json.IsNullOrEmpty() == false)
            {
                //write json

                string outName;
                string outFile;

                if (Directory.Exists(json) == false)
                {
                    _logger.Warn(
                        $"Path to '{json}' doesn't exist. Creating...");

                    try
                    {
                        Directory.CreateDirectory(json);
                    }
                    catch (Exception)
                    {
                        _logger.Fatal(
                            $"Unable to create directory '{json}'. Does a file with the same name exist? Exiting");
                        return;
                    }
                }

                if (mftFile.Key.StartsWith("\\\\.\\"))
                {
                    var vssNumber = Helper.GetVssNumberFromPath(mftFile.Key);
                    var vssTime = Helper.GetVssCreationDate(vssNumber);

                    outName =
                        $"{dateTimeOffset:yyyyMMddHHmmss}_VSS{vssNumber}_{vssTime:yyyyMMddHHmmss_fffffff}_MFTECmd_$MFT_Output.json";

                    if (jsonf.IsNullOrEmpty() == false)
                    {
                        outName =
                            $"VSS{vssNumber}_{vssTime:yyyyMMddHHmmss}_{Path.GetFileName(jsonf)}";
                    }
                }
                else
                {
                    //normal file
                    outName = $"{dateTimeOffset:yyyyMMddHHmmss}_MFTECmd_$MFT_Output.json";

                    if (jsonf.IsNullOrEmpty() == false)
                    {
                        outName = Path.GetFileName(jsonf);
                    }
                }

                outFile = Path.Combine(json, outName);

                _logger.Warn($"\tJSON output will be saved to '{outFile}'");

                try
                {
                    JsConfig.DateHandler = DateHandler.ISO8601;

                    using (var sWrite =
                           new StreamWriter(new FileStream(outFile, FileMode.OpenOrCreate, FileAccess.Write)))
                    {
                        if (_mftOutRecords != null)
                        {
                            foreach (var mftOutRecord in _mftOutRecords)
                            {
                                sWrite.WriteLine(mftOutRecord.ToJson());
                            }
                        }

                        sWrite.Flush();
                        sWrite.Close();
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(
                        $"\r\nError exporting to JSON. Please report to saericzimmerman@gmail.com.\r\n\r\nError: {e.Message}");
                }
            }

            Console.WriteLine();
        }


        #region ExportRecord

        if (dd.IsNullOrEmpty() == false)
        {
            _logger.Info("");

            bool offsetOk;
            long offset;

            if (@do.StartsWith("0x"))
            {
                offsetOk = long.TryParse(@do.Replace("0x", ""),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
            }
            else
            {
                offsetOk = long.TryParse(@do, out offset);
            }

            if (offsetOk)
            {
                using var b = new BinaryReader(File.OpenRead(file));
                b.BaseStream.Seek(offset * _mft.FileRecords.Values.First().AllocatedRecordSize, 0); // offset is the FILE entry, so we need to multiply it by the size of the record, typically 1024, but dont assume that 

                var fileBytes = b.ReadBytes(1024);

                var outFile = $"MFTECmd_FILE_Offset0x{offset:X}.bin";
                var outFull = Path.Combine(dd, outFile);

                File.WriteAllBytes(outFull, fileBytes);

                _logger.Warn($"FILE record at offset 0x{offset:X} dumped to '{outFull}'\r\n");
            }
            else
            {
                _logger.Warn(
                    $"Could not parse '{@do}' to valid value. Exiting");
                return;
            }
        }

        #endregion

        #region DumpEntry

        if (@do.IsNullOrEmpty() == false)
        {
            _logger.Info("");

            FileRecord fr = null;

            var segs = @do.Split('-');

            bool entryOk;
            bool seqOk;
            int entry;
            int seq;

            var key = string.Empty;

            if (segs.Length == 2)
            {
                _logger.Warn(
                    $"Could not parse '{@do}' to valid values. Format is Entry#-Sequence# in either decimal or hex format. Exiting");
                return;
            }

            if (segs.Length == 1)
            {
                if (@do.StartsWith("0x"))
                {
                    var seg0 = segs[0].Replace("0x", "");

                    entryOk = int.TryParse(seg0, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out entry);
                }
                else
                {
                    entryOk = int.TryParse(segs[0], out entry);
                }
                //try to find correct one

                var ff = _mft.FileRecords.Keys.Where(t => t.StartsWith($"{entry:X8}-")).ToList();

                if (ff.Count == 0)
                {
                    ff = _mft.FreeFileRecords.Keys.Where(t => t.StartsWith($"{entry:X8}-")).ToList();
                }

                if (ff.Count == 1)
                {
                    key = ff.First();
                }
                else if (ff.Count > 1)
                {
                    //more than one, dump and return
                    Console.WriteLine();
                    _logger.Warn(
                        "More than one FILE record found. Please specify one of the values below and try again!");
                    Console.WriteLine();

                    foreach (var f in ff)
                    {
                        _logger.Info(f);
                    }

                    Environment.Exit(-1);
                }
                else
                {
                    Console.WriteLine();
                    _logger.Warn(
                        "Could not find FILE record with specified Entry #. Use the --csv option and verify");
                    Console.WriteLine();

                    Environment.Exit(-1);
                }
            }


            if (key.Length == 0)
            {
                if (@do.StartsWith("0x"))
                {
                    var seg0 = segs[0].Replace("0x", "");
                    var seg1 = segs[1].Replace("0x", "");

                    entryOk = int.TryParse(seg0, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out entry);
                    seqOk = int.TryParse(seg1, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out seq);
                }
                else
                {
                    entryOk = int.TryParse(segs[0], out entry);
                    seqOk = int.TryParse(segs[1], out seq);
                }

                if (entryOk == false || seqOk == false)
                {
                    _logger.Warn(
                        $"Could not parse '{@do}' to valid values. Exiting");
                    return;
                }

                key = $"{entry:X8}-{seq:X8}";
            }


            if (_mft.FileRecords.ContainsKey(key))
            {
                fr = _mft.FileRecords[key];
            }
            else if (_mft.FreeFileRecords.ContainsKey(key))
            {
                fr = _mft.FreeFileRecords[key];
            }

            if (fr == null)
            {
                _logger.Warn(
                    $"Could not find file record with entry/seq '{@do}'. Exiting");
                return;
            }

            if (fr.IsDirectory() && fls)
            {
                var dlist = _mft.GetDirectoryContents(key);
                var fn = fr.Attributes.FirstOrDefault(t => t.AttributeType == AttributeType.FileName) as FileName;
                var name = key;
                if (fn != null)
                {
                    var pp = _mft.GetFullParentPath(key);
                    name = $"{pp}";
                }

                _logger.Warn($"Contents for '{name}':\r\n");
                _logger.Info("Key\t\t\tName");
                foreach (var parentMapEntry in dlist)
                {
                    if (parentMapEntry.IsDirectory)
                    {
                        _logger.Error($"{parentMapEntry.Key,-16}\t{parentMapEntry.FileName} ");
                    }
                    else
                    {
                        _logger.Info($"{parentMapEntry.Key,-16}\t{parentMapEntry.FileName} ");
                    }
                }

                _logger.Info("");
            }
            else
            {
                _logger.Warn($"Dumping details for file record with key '{key}'\r\n");

                _logger.Info(fr);
            }
        }

        #endregion
    }

    private static FileType GetFileType(string file)
    {
        const int logFileSig = 0x52545352;
        const int mftSig = 0x454C4946;
        const int sdsSig = 0x32FEC6CB;
        const int bootSig = 0x5346544E;

        _logger.Debug($"Opening '{file}' and checking header");

        var buff = new byte[50];

        try
        {
            try
            {
                using (var br = new BinaryReader(new FileStream(file, FileMode.Open, FileAccess.Read)))
                {
                    buff = br.ReadBytes(50);
                    _logger.Trace($"Raw bytes: {BitConverter.ToString(buff)}");
                }
            }
            catch (Exception)
            {
                var ll = new List<string> {file};

                try
                {
                    var rawFiles = Helper.GetFiles(ll);

                    rawFiles.First().FileStream.Read(buff, 0, 50);
                }
                catch (Exception e)
                {
                    _logger.Fatal(
                        $"\r\nError opening file '{file}'. Does it exist? Error: {e.Message} Exiting\r\n");
                    Environment.Exit(-1);
                }
            }

            if (buff.Length < 20)
            {
                _logger.Fatal(
                    $"\r\nNot enough data found in '{file}'. Is the file empty? Exiting\r\n");
                Environment.Exit(-1);
            }

            var sig32 = BitConverter.ToInt32(buff, 0);

            //some usn checks
            var majorVer = BitConverter.ToInt16(buff, 4);
            var minorVer = BitConverter.ToInt16(buff, 6);

            _logger.Debug($"Sig32: 0x{sig32:X}");

            switch (sig32)
            {
                case logFileSig:
                    _logger.Debug("Found $LogFile sig");
                    return FileType.LogFile;

                case mftSig:
                    _logger.Debug("Found $MFT sig");
                    return FileType.Mft;
                case sdsSig:
                    return FileType.Sds;

                case 0x0:
                    //00 for sparse file

                    if (majorVer != 0 || minorVer != 0)
                    {
                        return FileType.Unknown;
                    }

                    _logger.Debug("Found $J sig (0 size) and major/minor == 0)");
                    return FileType.UsnJournal;

                default:
                    var isBootSig = BitConverter.ToInt32(buff, 3);
                    if (isBootSig == bootSig)
                    {
                        _logger.Debug("Found $Boot sig");
                        return FileType.Boot;
                    }

                    if (majorVer == 2 && minorVer == 0)
                    {
                        _logger.Debug("Found $J sig (Major == 2, Minor == 0)");
                        return FileType.UsnJournal;
                    }

                    var zeroOffset = BitConverter.ToUInt64(buff, 8);

                    if (zeroOffset == 0)
                    {
                        _logger.Debug("Found $SDS sig (Offset 0x8 as Int64 == 0");
                        return FileType.Sds;
                    }

                    break;
            }

            _logger.Debug("Failed to find a signature! Returning unknown");
        }
        catch (UnauthorizedAccessException)
        {
            _logger.Fatal(
                $"\r\nCould not access '{file}'. Rerun the program as an administrator.\r\n");
            Environment.Exit(-1);
        }

        return FileType.Unknown;
    }

    private static void ProcessRecords(Dictionary<string, FileRecord> records, bool includeShort, bool alltimestamp, string bdl)
    {
        foreach (var fr in records)
        {
            _logger.Trace(
                $"Dumping record with entry: 0x{fr.Value.EntryNumber:X} at offset 0x:{fr.Value.Offset:X}");

            if (fr.Value.MftRecordToBaseRecord.MftEntryNumber > 0 &&
                fr.Value.MftRecordToBaseRecord.MftSequenceNumber > 0)
            {
                _logger.Debug(
                    $"Skipping entry # 0x{fr.Value.EntryNumber:X}, seq #: 0x{fr.Value.SequenceNumber:X} since it is an extension record.");
                //will get this record via extension records, which were already handled in MFT.dll code
                continue;
            }

            foreach (var attribute in fr.Value.Attributes.Where(t =>
                         t.AttributeType == AttributeType.FileName).OrderBy(t => ((FileName) t).FileInfo.NameType))
            {
                var fn = (FileName) attribute;
                if (includeShort == false &&
                    fn.FileInfo.NameType == NameTypes.Dos)
                {
                    continue;
                }

                var mftr = GetCsvData(fr.Value, fn, null,alltimestamp);

                var ads = fr.Value.GetAlternateDataStreams();

                mftr.HasAds = ads.Any();

                _csvWriter?.WriteRecord(mftr);

                _mftOutRecords?.Add(mftr);

                _csvWriter?.NextRecord();
                 

                if (_fileListWriter != null)
                {
                    _fileListWriter.WriteRecord(new FileListEntry(mftr));
                    _fileListWriter.NextRecord();
                }

                if (_bodyWriter != null)
                {
                    var f = GetBodyData(mftr, true,bdl);

                    _bodyWriter.WriteRecord(f);
                    _bodyWriter.NextRecord();

                    f = GetBodyData(mftr, false,bdl);

                    _bodyWriter.WriteRecord(f);
                    _bodyWriter.NextRecord();
                }


                foreach (var adsInfo in ads)
                {
                    var adsRecord = GetCsvData(fr.Value, fn, adsInfo,alltimestamp);
                    adsRecord.IsAds = true;
                    adsRecord.OtherAttributeId = adsInfo.AttributeId;
                    _csvWriter?.WriteRecord(adsRecord);

                    _mftOutRecords?.Add(adsRecord);

                    _csvWriter?.NextRecord();

                    if (_fileListWriter != null)
                    {
                        _fileListWriter.WriteRecord(new FileListEntry(adsRecord));
                        _fileListWriter.NextRecord();
                    }

                    if (_bodyWriter != null)
                    {
                        var f1 = GetBodyData(adsRecord, true,bdl);

                        _bodyWriter.WriteRecord(f1);
                        _bodyWriter.NextRecord();
                    }
                }
            }
        }
    }

    private static BodyFile GetBodyData(MFTRecordOut mftr, bool getStandardInfo, string bdl)
    {
        var b = new BodyFile
        {
            Name =
                $"{bdl.ToLowerInvariant()}:{mftr.ParentPath.Substring(1)}\\{mftr.FileName}"
                    .Replace("\\", "/"),
            Gid = 0,
            Uid = 0,
            Mode = "r/rrwxrwxrwx",
            Md5 = 0,
            Size = mftr.FileSize
        };

        if (getStandardInfo)
        {
            if (mftr.LastAccess0x10 != null)
            {
                b.AccessTime = mftr.LastAccess0x10.Value.ToUnixTimeSeconds();
            }

            if (mftr.LastModified0x10 != null)
            {
                b.ModifiedTime = mftr.LastModified0x10.Value.ToUnixTimeSeconds();
            }

            if (mftr.LastRecordChange0x10 != null)
            {
                b.RecordModifiedTime = mftr.LastRecordChange0x10.Value.ToUnixTimeSeconds();
            }

            if (mftr.Created0x10 != null)
            {
                b.CreatedTime = mftr.Created0x10.Value.ToUnixTimeSeconds();
            }

            if (mftr.IsDirectory)
            {
                b.Inode = $"{mftr.EntryNumber}-144-{mftr.OtherAttributeId}";
            }
            else
            {
                b.Inode = $"{mftr.EntryNumber}-128-{mftr.OtherAttributeId}";
            }
        }
        else
        {
            b.Name = $"{b.Name} ($FILE_NAME)";
            if (mftr.LastAccess0x30 != null)
            {
                b.AccessTime = mftr.LastAccess0x30.Value.ToUnixTimeSeconds();
            }
            else
            {
                if (mftr.LastAccess0x10 != null)
                {
                    b.AccessTime = mftr.LastAccess0x10.Value.ToUnixTimeSeconds();
                }
            }

            if (mftr.LastModified0x30 != null)
            {
                b.ModifiedTime = mftr.LastModified0x30.Value.ToUnixTimeSeconds();
            }
            else
            {
                if (mftr.LastModified0x10 != null)
                {
                    b.ModifiedTime = mftr.LastModified0x10.Value.ToUnixTimeSeconds();
                }
            }

            if (mftr.LastRecordChange0x30 != null)
            {
                b.RecordModifiedTime = mftr.LastRecordChange0x30.Value.ToUnixTimeSeconds();
            }
            else
            {
                if (mftr.LastRecordChange0x10 != null)
                {
                    b.RecordModifiedTime = mftr.LastRecordChange0x10.Value.ToUnixTimeSeconds();
                }
            }

            if (mftr.Created0x30 != null)
            {
                b.CreatedTime = mftr.Created0x30.Value.ToUnixTimeSeconds();
            }
            else
            {
                if (mftr.Created0x10 != null)
                {
                    b.CreatedTime = mftr.Created0x10.Value.ToUnixTimeSeconds();
                }
            }

            b.Inode = $"{mftr.EntryNumber}-48-{mftr.FnAttributeId}";
        }

        if (mftr.InUse == false)
        {
            b.Name = $"{b.Name} (deleted)";
        }

        return b;
    }

    public static MFTRecordOut GetCsvData(FileRecord fr, FileName fn, AdsInfo adsinfo, bool alltimestamp)
    {
        var mftr = new MFTRecordOut
        {
            EntryNumber = fr.EntryNumber,
            FileName = fn.FileInfo.FileName,
            InUse = fr.IsDeleted() == false,
            ParentPath = _mft.GetFullParentPath(fn.FileInfo.ParentMftRecord.GetKey()),
            SequenceNumber = fr.SequenceNumber,
            IsDirectory = fr.IsDirectory(),
            ParentEntryNumber = fn.FileInfo.ParentMftRecord.MftEntryNumber,
            ParentSequenceNumber = fn.FileInfo.ParentMftRecord.MftSequenceNumber,
            NameType = fn.FileInfo.NameType,
            FnAttributeId = fn.AttributeNumber
        };

        if (mftr.IsDirectory == false)
        {
            mftr.Extension = Path.GetExtension(mftr.FileName);

            var data = fr.Attributes.FirstOrDefault(t => t.AttributeType == AttributeType.Data);

            if (data != null)
            {
                mftr.OtherAttributeId = data.AttributeNumber;
            }
        }

        mftr.FileSize = fr.GetFileSize();

        if (adsinfo != null)
        {
            mftr.FileName = $"{mftr.FileName}:{adsinfo.Name}";
            mftr.FileSize = adsinfo.Size;
            try
            {
                mftr.Extension = Path.GetExtension(adsinfo.Name);
            }
            catch (Exception)
            {
                //sometimes bad chars show up
            }

            if (adsinfo.Name == "Zone.Identifier")
            {
                if (adsinfo.ResidentData != null)
                {
                    mftr.ZoneIdContents = CodePagesEncodingProvider.Instance.GetEncoding(1252).GetString(adsinfo.ResidentData.Data);
                }
                else
                {
                    mftr.ZoneIdContents = "(Zone.Identifier data is non-resident)";
                }
            }
        }

        mftr.ReferenceCount = fr.GetReferenceCount();

        mftr.LogfileSequenceNumber = fr.LogSequenceNumber;

        var oid = (ObjectId_) fr.Attributes.SingleOrDefault(t =>
            t.AttributeType == AttributeType.VolumeVersionObjectId);

        if (oid != null)
        {
            mftr.ObjectIdFileDroid = oid.ObjectId.ToString();
        }

        var lus = (LoggedUtilityStream) fr.Attributes.FirstOrDefault(t =>
            t.AttributeType == AttributeType.LoggedUtilityStream);

        if (lus != null)
        {
            mftr.LoggedUtilStream = lus.Name;
        }

        var rp = fr.GetReparsePoint();
        if (rp != null)
        {
            mftr.ReparseTarget = rp.SubstituteName.Replace(@"\??\", "");
        }

        var si = (StandardInfo) fr.Attributes.SingleOrDefault(t =>
            t.AttributeType == AttributeType.StandardInformation);

        if (si != null)
        {
            mftr.UpdateSequenceNumber = si.UpdateSequenceNumber;

            mftr.Created0x10 = si.CreatedOn;
            mftr.LastModified0x10 = si.ContentModifiedOn;
            mftr.LastRecordChange0x10 = si.RecordModifiedOn;
            mftr.LastAccess0x10 = si.LastAccessedOn;

            mftr.Copied = si.ContentModifiedOn < si.CreatedOn;

            if (alltimestamp || fn.FileInfo.CreatedOn != si.CreatedOn)
            {
                mftr.Created0x30 = fn.FileInfo.CreatedOn;
            }

            if (alltimestamp ||
                fn.FileInfo.ContentModifiedOn != si.ContentModifiedOn)
            {
                mftr.LastModified0x30 = fn.FileInfo.ContentModifiedOn;
            }

            if (alltimestamp ||
                fn.FileInfo.RecordModifiedOn != si.RecordModifiedOn)
            {
                mftr.LastRecordChange0x30 = fn.FileInfo.RecordModifiedOn;
            }

            if (alltimestamp ||
                fn.FileInfo.LastAccessedOn != si.LastAccessedOn)
            {
                mftr.LastAccess0x30 = fn.FileInfo.LastAccessedOn;
            }

            mftr.SecurityId = si.SecurityId;

            mftr.SiFlags = si.Flags;

            if (mftr.Created0x30.HasValue && mftr.Created0x10?.UtcTicks < mftr.Created0x30.Value.UtcTicks)
            {
                mftr.Timestomped = true;
            }

            if (mftr.Created0x10?.Millisecond == 0 || mftr.LastModified0x10?.Millisecond == 0)
            {
                mftr.uSecZeros = true;
            }
        }
        else
        {
            //no si, so update FN timestamps
            mftr.Created0x30 = fn.FileInfo.CreatedOn;
            mftr.LastModified0x10 = fn.FileInfo.ContentModifiedOn;
            mftr.LastRecordChange0x10 = fn.FileInfo.RecordModifiedOn;
            mftr.LastAccess0x10 = fn.FileInfo.LastAccessedOn;
        }

        return mftr;
    }

    public static bool IsAdministrator()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }
            
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void SetupNLog()
    {
        if (File.Exists(Path.Combine(BaseDirectory, "Nlog.config")))
        {
            return;
        }

        var config = new LoggingConfiguration();
        var loglevel = LogLevel.Info;

        var layout = @"${message}";

        var consoleTarget = new ColoredConsoleTarget();

        config.AddTarget("console", consoleTarget);

        consoleTarget.Layout = layout;

        var rule1 = new LoggingRule("*", loglevel, consoleTarget);
        config.LoggingRules.Add(rule1);

        LogManager.Configuration = config;
    }

    private enum FileType
    {
        Mft = 0,
        LogFile = 1,
        UsnJournal = 2,
        Boot = 3,
        Sds = 4,
        Unknown = 99
    }
}

internal class ApplicationArguments
{
    public string File { get; set; }
    public string MftFile { get; set; }
    public string CsvDirectory { get; set; }
    public string JsonDirectory { get; set; }
    public string DateTimeFormat { get; set; }
    public bool IncludeShortNames { get; set; }
    public string DumpEntry { get; set; }
    public bool Fls { get; set; }
    public bool Debug { get; set; }
    public bool Trace { get; set; }
    public bool Vss { get; set; }
    public bool Dedupe { get; set; }
    public bool FileListing { get; set; }

    public string BodyDirectory { get; set; }
    public string BodyDriveLetter { get; set; }
    public bool UseCr { get; set; }

    public string CsvName { get; set; }
    public string JsonName { get; set; }
    public string BodyName { get; set; }

    public string DumpDir { get; set; }
    public string DumpOffset { get; set; }
    public string DumpSecurity { get; set; }

    public bool AllTimeStampsAllTime { get; set; }
}