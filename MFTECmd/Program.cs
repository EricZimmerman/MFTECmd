using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Boot;
using LogFile;
using CsvHelper.Configuration;
using Exceptionless;
using I30;
using MFT;
using MFT.Attributes;
using MFT.Other;
using RawCopy;
using SDS;
using Secure;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ServiceStack;
using ServiceStack.Text;
using Usn;
using Attribute = MFT.Attributes.Attribute;
using CsvWriter = CsvHelper.CsvWriter;

#if !NET9_0_OR_GREATER
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using Path = Alphaleonis.Win32.Filesystem.Path;
#endif

namespace MFTECmd;

public class Program
{
    private static Mft _mft;

    /// <summary>The $MFT from -f or -m and those from its VSCs (--vss), by source</summary>
    private static Dictionary<string, Mft> _mftFiles;

    private static readonly string Header =
        $"MFTECmd version {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)}" +
        "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
        "\r\nhttps://github.com/EricZimmerman/MFTECmd";

    private static readonly string Footer = @"Examples: MFTECmd.exe -f ""C:\Temp\SomeMFT"" --csv ""c:\temp\out"" --csvf MyOutputFile.csv" +
                                            "\r\n\t " +
                                            @"   MFTECmd.exe -f ""C:\Temp\SomeMFT"" --csv ""c:\temp\out""" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""C:\Temp\SomeMFT"" --json ""c:\temp\jsonout""" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""C:\Temp\SomeMFT"" --body ""c:\temp\bout"" --bdl c" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""C:\Temp\SomeMFT"" --de 5-5" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""C:\Temp\SomeMFT"" --csv ""c:\temp\out"" --dr --fl" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""c:\temp\SomeJ"" --csv ""c:\temp\out""" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""c:\temp\SomeJ"" -m ""C:\Temp\SomeMFT"" --csv ""c:\temp\out""" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""c:\temp\SomeLogFile"" --csv ""c:\temp\out""" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""c:\temp\SomeLogFile"" -m ""C:\Temp\SomeMFT"" --json ""c:\temp\jsonout""" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""c:\temp\SomeBoot""" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""c:\temp\SomeSecure_SDS"" --csv ""c:\temp\out""" + "\r\n\t " +
                                            @"   MFTECmd.exe -f ""c:\temp\SomeI30"" --csv ""c:\temp\out""" + "\r\n\t" +
                                            "\r\n\t" +
                                            "    Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes";

    private static CsvWriter _bodyWriter;
    private static CsvWriter _csvWriter;
    private static CsvWriter _fileListWriter;
    private static List<MFTRecordOut> _mftOutRecords;

    /// <summary>--cutoff, in UTC: only $MFT records whose $SI created or modified time is on or after it</summary>
    private static DateTimeOffset? _cutoff;

    private static RootCommand _rootCommand;

    // JSON time stamps: ISO 8601 in UTC (2024-01-02T03:04:05.6780000Z), as JLECmd writes them, instead of ServiceStack's \/Date(ms)\/
    private const string JsonDateFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private static void ConfigureJsonDates()
    {
        JsConfig.DateHandler = DateHandler.ISO8601;
        JsConfig<DateTimeOffset>.SerializeFn = JsonDate;
        JsConfig<DateTimeOffset?>.SerializeFn = d => d.HasValue ? JsonDate(d.Value) : null;
        JsConfig<DateTime>.SerializeFn = JsonDate;
        JsConfig<DateTime?>.SerializeFn = d => d.HasValue ? JsonDate(d.Value) : null;
    }

    private static string JsonDate(DateTimeOffset d)
    {
        return d.UtcDateTime.ToString(JsonDateFormat, CultureInfo.InvariantCulture);
    }

    private static string JsonDate(DateTime d)
    {
        //timestamps without a kind are already UTC; ToUniversalTime would treat them as local time
        if (d.Kind == DateTimeKind.Unspecified)
        {
            d = DateTime.SpecifyKind(d, DateTimeKind.Utc);
        }

        return d.ToUniversalTime().ToString(JsonDateFormat, CultureInfo.InvariantCulture);
    }

    private static async Task<int> Main(string[] args)
    {
        ConfigureJsonDates();

        ExceptionlessClient.Default.Startup("88KHFwswzxfnYGejAlsVDao47ySGliI6vFbQPt9C");

        var fOpt = new Option<string>("-f")
        {
            Description = "File to process ($MFT | $J | $LogFile | $Boot | $SDS | $I30). Required"
        };
        
        var mOpt = new Option<string>("-m")
        {
            Description = "$MFT file to use when -f points to a $J or $LogFile file, to resolve parent paths. For $J, the paths are as they were when each record was written"
        };
        var csvOpt = new Option<string>(
            "--csv")
        {
            Description = "Directory to save CSV formatted results to. Be sure to include the full path in double quotes. This or --json required unless --de or --body is specified"
        
        };

        var csvfOpt = new Option<string>(
            "--csvf")
        {
            Description   = "File name to save CSV formatted results to. When present, overrides default name\r\n"
        };
        
        var jsonOpt = new Option<string>(
            "--json")
        {
            Description = "Directory to save JSON formatted results to. Be sure to include the full path in double quotes. This or --csv required unless --de or --body is specified"
        
        };

        var jsonfOpt = new Option<string>(
            "--jsonf")
        {
            Description   = "File name to save JSON formatted results to. When present, overrides default name"
        };
        var bodyOpt = new Option<string>(
            "--body")
        {
            Description = "Directory to save bodyfile formatted results to. --bdl is also required when using this option"
        
        };

        var bodyfOpt = new Option<string>(
            "--bodyf")
        {
            Description   = "File name to save body formatted results to. When present, overrides default name"
        };
        
        var bdlOpt = new Option<string>(
            "--bdl")
        {
            Description   = "Drive letter (C, D, etc.) to use with bodyfile. Only the drive letter itself should be provided"
        };
        
        var blfOpt = new Option<bool>(
            "--blf")
        {
            Description   = "When true, use LF vs CRLF for newlines",
            DefaultValueFactory = _ => false
        };
        
        var ddOpt = new Option<string>(
            "--dd")
        {
            Description   = "Directory to save exported $MFT FILE record. --do is also required when using this option"
        };
        var doOpt = new Option<string>(
            "--do")
        {
            Description   = "Offset of the $MFT FILE record to dump as decimal or hex. Ex: 5120 or 0x1400 Use --de or --debug to see offsets"
        };
        var deOpt = new Option<string>(
            "--de")
        {
            Description   = "Dump full details for $MFT entry/sequence #. Format is 'Entry' or 'Entry-Seq' as decimal or hex. Example: 5, 624-5 or 0x270-0x5."
        };
        var drOpt = new Option<bool>(
            "--dr")
        {
            Description   = "When true, dump $MFT resident files to dir specified by --csv or --json, in 'Resident' subdirectory. Files will be named '<EntryNumber>-<SequenceNumber>-<AttributeNumber>_<FileName>.bin'"
        };
        
        var flsOpt = new Option<bool>(
            "--fls")
        {
            Description   = "When true, displays contents of directory from $MFT specified by --de. Ignored when --de points to a file",
            DefaultValueFactory = _ => false
        };
        var dsOpt = new Option<string>(
            "--ds")
        {
            Description   = "Dump full details for Security Id from $SDS as decimal or hex. Example: 624 or 0x270\r\n"
        };
        
        var dtOpt = new Option<string>(
            "--dt"){
            Description =        "The custom date/time format to use when displaying time stamps. See https://goo.gl/CNVq0k for options",
            DefaultValueFactory = _ => "yyyy-MM-dd HH:mm:ss.fffffff"
        };
        
        
        var snOpt = new Option<bool>("--sn")
        {
            Description = "Include DOS file name types in $MFT output",
            DefaultValueFactory = _ => false
        };
        
        var flOpt = new Option<bool>("--fl")
        {
            Description = "Generate condensed file listing of parsed $MFT contents. Requires --csv",
            DefaultValueFactory = _ => false
        };
        
        var atOpt = new Option<bool>("--at")
        {
            Description = "When true, include all timestamps from 0x30 attribute vs only when they differ from 0x10 in the $MFT",
            DefaultValueFactory = _ => false
        };
        
        var rsOpt = new Option<bool>("--rs")
        {
            Description = "When true, recover slack space from FILE records when processing $MFT files. This option has no effect for $I30 files",
            DefaultValueFactory = _ => false
        };
        
        var vssOpt = new Option<bool>("--vss")
        {
            Description = "Process all Volume Shadow Copies that exist on drive specified by -f",
            DefaultValueFactory = _ => false
        };
        var dedupeOpt = new Option<bool>("--dedupe")
        {
            Description = "Deduplicate -f & VSCs based on SHA-1. First file found wins",
            DefaultValueFactory = _ => false
        };
        
        var irOpt = new Option<bool>("--ir")
        {
            Description = "Include resident data in JSON/CSV output",
            DefaultValueFactory = _ => false
        };
        
        var reOpt = new Option<string>(
            "--re")
        {
            Description   = "Comma-separated list of extensions to include for resident data (e.g., '.txt,.ps1,.bat'). If omitted, includes all"
        };
        
        var rmOpt = new Option<int>(
            "--rm")
        {
            Description   = "Maximum size in bytes for resident data to include (max: 1024000)",
            DefaultValueFactory = _ => 1024
        };
        
        var cutoffOpt = new Option<DateTimeOffset?>("--cutoff")
        {
            Description = "Only include $MFT records whose $STANDARD_INFO created or modified time is on or after this date/time (UTC unless an offset is given). Example: 2025-10-19 or 2025-10-19T13:45:30Z"
        };

        var lowMemOpt = new Option<bool>("--lowmem")
        {
            Description = "Use less memory at the cost of speed (workstation instead of server garbage collection): about 30% less peak memory, and about 1.4x slower",
            DefaultValueFactory = _ => false
        };

        var debugOpt = new Option<bool>("--debug")
        {
            Description = "Show debug information during processing",
            DefaultValueFactory = _ => false
        };
        var traceOpt = new Option<bool>("--trace")
        {
            Description = "Show trace information during processing",
            DefaultValueFactory = _ => false
        };
        var coOpt = new Option<bool>("--co")
        {
            Description = "Save all console messages to <timestamp>_MFTECmd_<version>_ConsoleLog.txt in the output directory (the first of --csv, --json, --body given)",
            DefaultValueFactory = _ => false
        };

        _rootCommand = new RootCommand
        {
           fOpt,
           mOpt,
           csvOpt,
           csvfOpt,
           jsonOpt,
           jsonfOpt,
           bodyOpt,
           bodyfOpt,
           bdlOpt,
           blfOpt,
           ddOpt,
           doOpt,
           deOpt,
           drOpt,
           flsOpt,
           dsOpt,
           dtOpt,
           snOpt,
           flOpt,
           atOpt,
           rsOpt,
           vssOpt,
           dedupeOpt,
          
           irOpt,
           reOpt,
           rmOpt,
           cutoffOpt,
           lowMemOpt,
           debugOpt,
           traceOpt,
           coOpt,
        };

        _rootCommand.Description = Header + "\r\n\r\n" + Footer;

        _rootCommand.SetAction(result =>
        {
            if (result.GetValue(lowMemOpt) && GCSettings.IsServerGC && Environment.GetEnvironmentVariable(LowMemMarker) != "1")
            {
                //the GC mode is set when a process starts
                return RunWithWorkstationGc(args);
            }

            var cutoff = result.GetValue(cutoffOpt);
            _cutoff = cutoff.HasValue ? cutoff.Value.ToUniversalTime() : null;

            return DoWork(result.GetValue(fOpt), result.GetValue(mOpt), result.GetValue(jsonOpt),
            result.GetValue(jsonfOpt), result.GetValue(csvOpt), result.GetValue(csvfOpt), result.GetValue(bodyOpt),
            result.GetValue(bodyfOpt),result.GetValue(bdlOpt),result.GetValue(blfOpt),result.GetValue(ddOpt),result.GetValue(doOpt),
            result.GetValue(deOpt),result.GetValue(drOpt),result.GetValue(flsOpt),result.GetValue(dsOpt),result.GetValue(dtOpt),result.GetValue(snOpt),
            result.GetValue(flOpt),result.GetValue(atOpt),result.GetValue(rsOpt),result.GetValue(vssOpt),result.GetValue(dedupeOpt),
            result.GetValue(debugOpt),result.GetValue(traceOpt),result.GetValue(irOpt),result.GetValue(reOpt),result.GetValue(rmOpt),result.GetValue(coOpt));
        });
            
        var exitCode = await _rootCommand.Parse(args).InvokeAsync();

        Log.CloseAndFlush();

        return exitCode;
    }

    /// <summary>Set in the process --lowmem starts, so it never starts another</summary>
    private const string LowMemMarker = "MFTECMD_LOWMEM";

    /// <summary>--lowmem: runs MFTECmd again, with the same arguments, using workstation garbage collection</summary>
    private static int RunWithWorkstationGc(string[] args)
    {
#if NET9_0_OR_GREATER
        var host = Environment.ProcessPath!;
        var start = new ProcessStartInfo(host) { UseShellExecute = false };

        //run as "dotnet MFTECmd.dll": the dll comes first
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment[LowMemMarker] = "1";

        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode;
#else
        //not reached: the .NET Framework build uses workstation garbage collection already
        return 1;
#endif
    }

    private static string _activeDateTimeFormat;
    
    class DateTimeOffsetFormatter : IFormatProvider, ICustomFormatter
    {
        private readonly IFormatProvider _innerFormatProvider;

        public DateTimeOffsetFormatter(IFormatProvider innerFormatProvider)
        {
            _innerFormatProvider = innerFormatProvider;
        }

        public object GetFormat(Type formatType)
        {
            return formatType == typeof(ICustomFormatter) ? this : _innerFormatProvider.GetFormat(formatType);
        }

        public string Format(string format, object arg, IFormatProvider formatProvider)
        {
            if (arg is DateTimeOffset size)
            {
                //an explicit format (the log templates' {Timestamp:HH:mm:ss.fff}) wins over --dt
                return size.ToString(string.IsNullOrEmpty(format) ? _activeDateTimeFormat : format);
            }

            if (arg is IFormattable formattable)
            {
                return formattable.ToString(format, _innerFormatProvider);
            }

            return arg.ToString();
        }
    }
    
    /// <returns>The exit code: 0, or 1 for invalid arguments, a file that could not be read, or a failed export (a damaged file is 0)</returns>
    private static int DoWork(string f, string m, string json, string jsonf, string csv, string csvf, string body, string bodyf, string bdl, bool blf, string dd, string @do, string de, bool dr, bool fls, string ds, string dt, bool sn, bool fl, bool at, bool rs, bool vss, bool dedupe, bool debug, bool trace, bool ir, string re, int rm, bool co)
    {
        var levelSwitch = new LoggingLevelSwitch();

        _activeDateTimeFormat = dt;
        
        var formatter  =
            new DateTimeOffsetFormatter(CultureInfo.CurrentCulture);

        
        var template = "{Message:lj}{NewLine}{Exception}";

        if (debug)
        {
            levelSwitch.MinimumLevel = LogEventLevel.Debug;
            template = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";
        }

        if (trace)
        {
            levelSwitch.MinimumLevel = LogEventLevel.Verbose;
            template = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";
        }
        
        var conf = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: template,formatProvider: formatter)
            .MinimumLevel.ControlledBy(levelSwitch);
      
        var consoleLog = co ? ConsoleLog.GetPath(DateTimeOffset.UtcNow, csv, json, body) : null;
        if (consoleLog != null)
        {
            conf.WriteTo.File(consoleLog, outputTemplate: ConsoleLog.Template, formatProvider: formatter);
        }

        Log.Logger = conf.CreateLogger();
        
        if (f.IsNullOrEmpty())
        {
            ShowHelp("-f is required. Exiting");
            return 1;
        }

        if (Path.GetDirectoryName(f)?.Length == 3)
        {
            //the path is off the root of the drive, so it works for things like $Boot, $MFT, etc
        }
        else if (f.ToUpperInvariant().Contains("$EXTEND\\$USNJRNL"))
        {
            //OK
        }
        else if (!File.Exists(f))
        {
            Log.Warning("File {F} not found. Exiting",f);
            return 1;
        }

        Log.Information("{Header}",Header);
        Console.WriteLine();
        Log.Information("Command line: {Args}",string.Join(" ",Environment.GetCommandLineArgs().Skip(1)));
        Console.WriteLine();

        if (consoleLog != null)
        {
            Log.Information("Console log: {Path}",consoleLog);
            Console.WriteLine();
        }
        else if (co)
        {
            Log.Warning("--co: no output directory was given, so the console messages are not saved");
            Console.WriteLine();
        }
        Log.Debug("Garbage collection: {Mode}", GCSettings.IsServerGC ? "server (use --lowmem for less memory)" : "workstation");

        if (IsAdministrator() == false)
        {
            Log.Warning("Warning: Administrator privileges not found!");
            Console.WriteLine();
        }

        if (vss & (IsAdministrator() == false))
        {
            Log.Error("--vss is present, but administrator rights not found. Exiting");
            Console.WriteLine();
            return 1;
        }

        //determine file type
        var fileType = GetFileType(f);
        if (fileType == null)
        {
            //an empty file is reported, but it is not a failure of the tool; one that could not be opened is
            return _emptyFile ? 0 : 1;
        }

        var ft = fileType.Value;
        Log.Information("File type: {Ft}",ft);
        Console.WriteLine();

        foreach (var (dir, name) in new[] { (csv, "--csv"), (json, "--json"), (body, "--body") })
        {
            if (dir.IsNullOrEmpty() == false && Directory.Exists(Directory.GetDirectoryRoot(Path.GetFullPath(dir))) == false)
            {
                Log.Error("Destination location not available for {Dir} ({Option}). Verify drive letter and try again. Exiting",dir,name);
                Console.WriteLine();
                return 1;
            }
        }

        switch (ft)
        {
            case FileType.I30:
                if (csv.IsNullOrEmpty())
                {
                    ShowHelp("--csv is required. Exiting");
                    return 1;
                }
                
                return ProcessI30(f, csv, csvf, json, jsonf, dt) ? 0 : 1;
                
            case FileType.Mft:
                if (csv.IsNullOrEmpty() &&
                    json.IsNullOrEmpty() &&
                    de.IsNullOrEmpty() &&
                    body.IsNullOrEmpty() &&
                    dd.IsNullOrEmpty())
                {
                    ShowHelp("--csv, --json, --body, --dd, or --de is required. Exiting");
                    return 1;
                }

                if (body.IsNullOrEmpty() == false &&
                    bdl.IsNullOrEmpty())
                {
                    ShowHelp("--bdl is required when using --body. Exiting");
                    return 1;
                }

                if (@do.IsNullOrEmpty() == false)
                {
                    if (dd.IsNullOrEmpty())
                    {
                        ShowHelp("--dd option missing. Exiting");
                        return 1;
                    }

                    if (Directory.Exists(Directory.GetDirectoryRoot(Path.GetFullPath(dd))) == false)
                    {
                        Log.Error("Destination location not available for {Dd}. Verify drive letter and try again. Exiting",dd);
                        Console.WriteLine();
                        return 1;
                    }
                }

                if (dd.IsNullOrEmpty() == false &&
                    @do.IsNullOrEmpty())
                {
                    ShowHelp("--do is required when using --dd. Exiting");
                    return 1;
                }

                var drDir = string.Empty;
                if (dr)
                {
                    var residentDirBase = !json.IsNullOrEmpty() ? json : csv;
                    drDir = Path.Combine(residentDirBase, "Resident");
                }

                return ProcessMft(f, vss, dedupe, body, bdl, bodyf, blf, csv, csvf, json, jsonf, fl, dt, dd, @do, fls, sn, at, de, rs, drDir, ir, re, rm) ? 0 : 1;

            case FileType.LogFile:
            case FileType.UsnJournal:
                if (csv.IsNullOrEmpty() && json.IsNullOrEmpty())
                {
                    ShowHelp("--csv or --json is required. Exiting");
                    return 1;
                }

                var ok = true;

                if (m.IsNullOrEmpty() == false)
                {
                    //the $MFT gives the paths; it is exported too
                    if (File.Exists(m) == false)
                    {
                        Log.Error("MFT file {M} does not exist! Verify path and try again. Exiting",m);
                        Console.WriteLine();
                        return 1;
                    }

                    _emptyFile = false;
                    var ftm = GetFileType(m);

                    if (ftm != FileType.Mft)
                    {
                        Log.Error("File {M} is not an MFT file!! Verify path and try again. Exiting",m);
                        //a file that is not an $MFT is not a failure of the tool; one that could not be opened is
                        return ftm == null && _emptyFile == false ? 1 : 0;
                    }
                    
                    var drDir2 = string.Empty;
                    if (dr)
                    {
                        var residentDirBase = !json.IsNullOrEmpty() ? json : csv;
                        drDir2 = Path.Combine(residentDirBase, "Resident");
                    }

                    //--csvf/--jsonf/--bodyf name the output for -f; the $MFT keeps its default names (it used to overwrite it)
                    if (ProcessMft(m, vss, dedupe, body, bdl, null, blf, csv, null, json, null, fl, dt, dd, @do, fls, sn, at, de, rs, drDir2, ir, re, rm) == false)
                    {
                        ok = false;
                    }

                    if (_mft == null)
                    {
                        //the $MFT could not be loaded: 1 when it could not be read, 0 when it is damaged
                        return ok ? 0 : 1;
                    }
                }

                if (ft == FileType.LogFile)
                {
                    ok &= ProcessLogFile(f, vss, dedupe, csv, csvf, json, jsonf, dt);
                }
                else
                {
                    ok &= ProcessJ(f, vss, dedupe, csv, csvf, json, jsonf, dt);
                }

                return ok ? 0 : 1;

            case FileType.Boot:
                return ProcessBoot(f, vss, dedupe, csv, csvf) ? 0 : 1;

            case FileType.Sds:
                if (csv.IsNullOrEmpty() &&
                    ds.IsNullOrEmpty())
                {
                    ShowHelp("--csv or --ds is required. Exiting");
                    return 1;
                }

                return ProcessSds(f, vss, dedupe, csv, csvf, ds) ? 0 : 1;

            default:
                //unknown
                Log.Error(
                    "Unknown file type! Send {F} to saericzimmerman@gmail.com for assistance. Exiting",f);
                //not a failure of the tool
                return 0;
        }
    }

    /// <summary>
    ///     Logs a file that could not be loaded. A damaged file is not a failure of the tool: true (exit code 0). A file
    ///     that could not be read (access denied, in use and the raw read failed too, IO error) is: false (exit code 1)
    /// </summary>
    /// <param name="e">The error that stopped the load</param>
    /// <param name="firstError">When the raw read was tried after a first error, that first error</param>
    private static bool LoadFailed(Exception e, Exception firstError = null)
    {
        Log.Error(e,"There was an error loading the file! Error: {Message}",e.Message);

        var cause = firstError ?? e;
        var readError = cause is UnauthorizedAccessException || (cause is IOException && cause is not EndOfStreamException);

        return readError == false;
    }

    private static void ShowHelp(string message)
    {
        var help = new CustomHelpAction(new HelpAction());
        help.Invoke(_rootCommand.Parse(message));
    }

    private static bool ProcessI30(string f, string csv, string csvf, string json, string jsonf, string dateFormat)
    {
        var sw = new Stopwatch();
        sw.Start();

        I30.I30 i30 = null;

        try
        {
            i30 = I30File.Load(f);

            ReportProblems(f, "pages or entries", i30.ParseErrors);
            
        }
        catch (Exception e)
        {
            return LoadFailed(e);
        }
        
        sw.Stop();
        
        Console.WriteLine();
        Log.Information(
            "Processed {F} in {TotalSeconds:N4} seconds",f,sw.Elapsed.TotalSeconds);
        Console.WriteLine();

        var dt = DateTimeOffset.UtcNow;
        
        Log.Information("$I30 contains {Active:N0} active and {Slack:N0} slack entries",i30.Entries.Count(t=>t.FromSlack == false),i30.Entries.Count(t=>t.FromSlack));

        if (Directory.Exists(csv) == false)
        {
            Log.Information(
                "Path to {Csv} doesn't exist. Creating...",csv);

            try
            {
                Directory.CreateDirectory(csv);
            }
            catch (Exception e)
            {
                Log.Fatal(e,
                    "Unable to create directory {Csv}. Does a file with the same name exist? Exiting",csv);
                return false;
            }
        }

        var outName = $"{dt:yyyyMMddHHmmss}_MFTECmd_$I30_Output.csv";

        if (csvf.IsNullOrEmpty() == false)
        {
            outName = Path.GetFileName(csvf);
        }
        
        var outFile = Path.Combine(csv, outName);

        Log.Information("\tCSV output will be saved to {OutFile}",outFile);

        var swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

        _csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);
        
        //start here
        
           var foo = _csvWriter.Context.AutoMap<I30Out>();
           
           foo.Map(t => t.CreatedOn).Convert(t => $"{t.Value.CreatedOn?.ToString(dateFormat)}");
           foo.Map(t => t.LastAccessedOn).Convert(t => $"{t.Value.LastAccessedOn?.ToString(dateFormat)}");
           foo.Map(t => t.RecordModifiedOn).Convert(t => $"{t.Value.RecordModifiedOn?.ToString(dateFormat)}");
           foo.Map(t => t.ContentModifiedOn).Convert(t => $"{t.Value.ContentModifiedOn?.ToString(dateFormat)}");

                    _csvWriter.Context.RegisterClassMap(foo);

                    _csvWriter.WriteHeader<I30Out>();
                    _csvWriter.NextRecord();

                    foreach (var index in i30.Entries)
                    {
                        Log.Debug("Processing index entry at offset '{OFfset}'",$"0x{index.AbsoluteOffset:X}");

                        var ieOut = new I30Out
                        {
                        
                            Offset = index.AbsoluteOffset,
                            FromSlack = index.FromSlack,
                            SelfMftEntry = index.MftReferenceSelf?.MftEntryNumber,
                            SelfMftSequence = index.MftReferenceSelf?.MftSequenceNumber,
                            FileName = index.FileInfo.FileName,
                            Flags = index.FileInfo.Flags.ToString().Replace(", ", "|"),
                            NameType = index.FileInfo.NameType,
                            ParentMftEntry = index.FileInfo.ParentMftRecord.MftEntryNumber,
                            ParentMftSequence = index.FileInfo.ParentMftRecord.MftSequenceNumber,
                            CreatedOn = index.FileInfo.CreatedOn,
                            ContentModifiedOn = index.FileInfo.ContentModifiedOn,
                            RecordModifiedOn = index.FileInfo.RecordModifiedOn,
                            LastAccessedOn = index.FileInfo.LastAccessedOn,
                            PhysicalSize = index.FileInfo.PhysicalSize,
                            LogicalSize = index.FileInfo.LogicalSize,
                            
                            SourceFile = f
                        };

                        Log.Verbose("{Entry}",index);

                        _csvWriter.WriteRecord(ieOut);
                        _csvWriter.NextRecord();
                    }
        
        //END HERE 
        
        swCsv.Flush();
        swCsv.Close();

        Console.WriteLine();

        return true;
    }

    private static bool ProcessBoot(string f, bool vss, bool dedupe, string csv, string csvf)
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

                    var rawFiles = Helper.GetRawFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        bf = new Boot.Boot(rawCopyReturn.FileStream);
                        bootFiles.Add(rawCopyReturn.InputFilename, bf);
                    }
                }
            }
            catch (Exception firstError)
            {
                try
                {
                    Log.Warning("{F} is in use. Rerouting...",f);

                    var ll = new List<string> { f };

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

                    var rawFiles = Helper.GetRawFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        bf = new Boot.Boot(rawCopyReturn.FileStream);
                        bootFiles.Add(rawCopyReturn.InputFilename, bf);
                    }
                }
                catch (Exception e)
                {
                    return LoadFailed(e, firstError);
                }
            }

            sw.Stop();

            var extra = string.Empty;

            if (bootFiles.Count > 1)
            {
                extra = " (and VSCs)";
            }

            Console.WriteLine();
            Log.Information(
                "Processed {F}{Extra} in {TotalSeconds:N4} seconds",f,extra,sw.Elapsed.TotalSeconds);
            Console.WriteLine();

            StreamWriter swCsv = null;

            if (csv.IsNullOrEmpty() == false)
            {
                if (Directory.Exists(csv) == false)
                {
                    Log.Information(
                        "Path to {Csv} doesn't exist. Creating...",csv);

                    try
                    {
                        Directory.CreateDirectory(csv!);
                    }
                    catch (Exception ex)
                    {
                        Log.Fatal(
                            ex,"Unable to create directory {Csv}. Does a file with the same name exist? Exiting",csv);
                        return false;
                    }
                }

                var outName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_MFTECmd_$Boot_Output.csv";

                if (csvf.IsNullOrEmpty() == false)
                {
                    outName = Path.GetFileName(csvf);
                }

                var outFile = Path.Combine(csv, outName);

                Log.Information("CSV output will be saved to {OutFile}",outFile);
                Console.WriteLine();

                swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

                _csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

                var foo = _csvWriter.Context.AutoMap<BootOut>();

                _csvWriter.Context.RegisterClassMap(foo);
                _csvWriter.WriteHeader<BootOut>();
                _csvWriter.NextRecord();
            }

            foreach (var b in bootFiles)
            {
                Log.Information("Boot file: {Key}",b.Key);
                Console.WriteLine();
                Log.Information("Boot entry point: {BootEntryPoint}",b.Value.BootEntryPoint);
                Log.Information("File system signature: {FileSystemSignature}",b.Value.FileSystemSignature);
                Console.WriteLine();
                Log.Information("Bytes per sector: {BytesPerSector:N0}",b.Value.BytesPerSector);
                Log.Information("Sectors per cluster: {SectorsPerCluster:N0}",b.Value.SectorsPerCluster);
                Log.Information("Cluster size: {ClusterSize:N0}",b.Value.BytesPerSector * b.Value.SectorsPerCluster);
                Console.WriteLine();
                Log.Information("Total sectors: {TotalSectors:N0}",b.Value.TotalSectors);
                Log.Information("Reserved sectors: {ReservedSectors:N0}",b.Value.ReservedSectors);
                Console.WriteLine();
                Log.Information("$MFT cluster block #: {MftClusterBlockNumber:N0}",b.Value.MftClusterBlockNumber);
                Log.Information("$MFTMirr cluster block #: {MirrorMftClusterBlockNumber:N0}",b.Value.MirrorMftClusterBlockNumber);
                Console.WriteLine();
                Log.Information("FILE entry size: {MftEntrySize:N0}",b.Value.MftEntrySize);
                Log.Information("Index entry size: {IndexEntrySize:N0}",b.Value.IndexEntrySize);
                Console.WriteLine();
                Log.Information("Volume serial number raw: {VolumeSerialNumberRaw}", $"0x{b.Value.VolumeSerialNumberRaw:X}");
                Log.Information("Volume serial number: {GetVolumeSerialNumber}",b.Value.GetVolumeSerialNumber());
                Log.Information("Volume serial number 32-bit: {GetVolumeSerialNumber}",b.Value.GetVolumeSerialNumber(true));
                Log.Information("Volume serial number 32-bit reversed: {GetVolumeSerialNumber}",b.Value.GetVolumeSerialNumber(true, true));
                Console.WriteLine();
                Log.Information("Sector signature: {GetSectorSignature}",b.Value.GetSectorSignature());
                Console.WriteLine();

                Log.Verbose("{Boot}",b.Value.Dump());


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
            Log.Error(e,"There was an error loading the file! Error: {Message}",e.Message);
            return false;
        }

        return true;
    }

    private static bool ProcessJ(string f, bool vss, bool dedupe, string csv, string csvf, string json, string jsonf, string dt)
    {
        var sw = new Stopwatch();
        sw.Start();

        var jFiles = new Dictionary<string, Usn.Usn>();

        try
        {
            Log.Verbose("Initializing $J");

            Usn.Usn j;
            try
            {
                j = UsnFile.Load(f);
                jFiles.Add(f, j);

                ReportProblems(f, "records", j.ParseErrors);

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

                    var rawFiles = Helper.GetRawFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        var start = UsnFile.FindStartingOffset(rawCopyReturn.FileStream);

                        j = new Usn.Usn(rawCopyReturn.FileStream, start);
                        jFiles.Add(rawCopyReturn.InputFilename, j);
                    }
                }
            }
            catch (Exception firstError)
            {
                try
                {
                    Log.Warning("{F} is in use. Rerouting...",f);
                    Console.WriteLine();

                    var ll = new List<string> { f };

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

                    var rawFiles = Helper.GetRawFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        var start = UsnFile.FindStartingOffset(rawCopyReturn.FileStream);
                        j = new Usn.Usn(rawCopyReturn.FileStream, start);
                        jFiles.Add(rawCopyReturn.InputFilename, j);
                    }
                }
                catch (Exception e)
                {
                    return LoadFailed(e, firstError);
                }
            }

            var dateTimeOffset = DateTimeOffset.UtcNow;

            sw.Stop();

            var extra = string.Empty;

            if (jFiles.Count > 1)
            {
                extra = " (and VSCs)";
            }

            Console.WriteLine();
            Log.Information(
                "Processed {F}{Extra} in {TotalSeconds:N4} seconds",f,extra,sw.Elapsed.TotalSeconds);
            Console.WriteLine();

            var ok = true;

            foreach (var jFile in jFiles)
            {
                var entries = jFile.Value.UsnEntries;

                Log.Information("Usn entries found in {Key}: {Count:N0}",jFile.Key,entries.Count);

                string[] parentPaths = null;

                var mft = GetMftFor(jFile.Key);
                if (mft != null)
                {
                    var swRewind = Stopwatch.StartNew();
                    parentPaths = UsnRewind.GetParentPaths(mft, entries);
                    swRewind.Stop();

                    //how many paths the rewind changed from what the $MFT alone gives
                    var current = new Dictionary<string, string>();
                    var changed = 0;
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var key = $"{entries[i].ParentFileReference.EntryNumber:X8}-{entries[i].ParentFileReference.SequenceNumber:X8}";
                        if (current.TryGetValue(key, out var path) == false)
                        {
                            path = mft.GetFullParentPath(key);
                            current.Add(key, path);
                        }

                        if (path != parentPaths[i])
                        {
                            changed++;
                        }
                    }

                    Log.Information("\tParent paths rewound through the journal in {TotalSeconds:N4} seconds. {Changed:N0} differ from the $MFT alone (directories since deleted, reused, renamed or moved)",
                        swRewind.Elapsed.TotalSeconds,changed);
                }

                IEnumerable<JEntryOut> Records() => entries.Select((jUsnEntry, i) => new JEntryOut
                {
                    Name = jUsnEntry.Name,
                    UpdateTimestamp = jUsnEntry.UpdateTimestamp,
                    EntryNumber = jUsnEntry.FileReference.EntryNumber,
                    SequenceNumber = jUsnEntry.FileReference.SequenceNumber,

                    ParentEntryNumber = jUsnEntry.ParentFileReference.EntryNumber,
                    ParentSequenceNumber = jUsnEntry.ParentFileReference.SequenceNumber,

                    ParentPath = parentPaths?[i] ?? string.Empty,

                    UpdateSequenceNumber = jUsnEntry.UpdateSequenceNumber,
                    UpdateReasons = jUsnEntry.UpdateReasons.ToString().Replace(", ", "|"),
                    FileAttributes = jUsnEntry.FileAttributes.ToString().Replace(", ", "|"),
                    OffsetToData = jUsnEntry.OffsetToData,
                    SourceFile = jFile.Key
                });

                if (csv.IsNullOrEmpty() == false)
                {
                    ok &= WriteCsv(csv, GetOutName(jFile.Key, dateTimeOffset, "$J", ".csv", csvf), Records(), map =>
                    {
                        map.Map(t => t.UpdateTimestamp).Convert(t =>
                            $"{t.Value.UpdateTimestamp.ToString(dt)}");
                    });
                }

                if (json.IsNullOrEmpty() == false)
                {
                    ok &= WriteJson(json, GetOutName(jFile.Key, dateTimeOffset, "$J", ".json", jsonf), Records());
                }

                Console.WriteLine();
            }

            return ok;
        }
        catch (Exception e)
        {
            Log.Error(e,
                "There was an error processing $J data! Last offset processed: {LastOffset}. Error: {Message}",$"0x{Usn.Usn.LastOffset:X}",e.Message);
            return false;
        }
    }

    /// <summary>The $MFT for a $J or $LogFile from -f (or one of its VSCs): the one from the same VSC when there is one</summary>
    private static Mft GetMftFor(string sourceKey)
    {
        if (_mft == null)
        {
            return null;
        }

        if (sourceKey.StartsWith("\\\\.\\") && _mftFiles != null)
        {
            var vssNumber = Helper.GetVssNumberFromPath(sourceKey);
            foreach (var mftFile in _mftFiles.Where(t => t.Key.StartsWith("\\\\.\\")))
            {
                if (Helper.GetVssNumberFromPath(mftFile.Key) == vssNumber)
                {
                    return mftFile.Value;
                }
            }
        }

        return _mft;
    }

    /// <summary>The default output file name for <paramref name="kind" /> (such as $J), or the name from --csvf/--jsonf</summary>
    private static string GetOutName(string sourceKey, DateTimeOffset dateTimeOffset, string kind, string extension, string overrideName)
    {
        if (sourceKey.StartsWith("\\\\.\\"))
        {
            var vssNumber = Helper.GetVssNumberFromPath(sourceKey);
            var vssTime = Helper.GetVssCreationDate(vssNumber);

            if (overrideName.IsNullOrEmpty() == false)
            {
                return $"VSS{vssNumber}_{vssTime:yyyyMMddHHmmss}_{Path.GetFileName(overrideName)}";
            }

            return $"{dateTimeOffset:yyyyMMddHHmmss}_VSS{vssNumber}_{vssTime:yyyyMMddHHmmss_fffffff}_MFTECmd_{kind}_Output{extension}";
        }

        if (overrideName.IsNullOrEmpty() == false)
        {
            return Path.GetFileName(overrideName);
        }

        return $"{dateTimeOffset:yyyyMMddHHmmss}_MFTECmd_{kind}_Output{extension}";
    }

    private static bool CreateOutputDirectory(string dir)
    {
        if (Directory.Exists(dir))
        {
            return true;
        }

        Log.Information("Path to {Dir} doesn't exist. Creating...",dir);

        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch (Exception e)
        {
            Log.Fatal(e,"Unable to create directory {Dir}. Does a file with the same name exist? Exiting",dir);
            return false;
        }
    }

    private static bool WriteCsv<T>(string dir, string outName, IEnumerable<T> records, Action<ClassMap<T>> configure)
    {
        if (CreateOutputDirectory(dir) == false)
        {
            return false;
        }

        var outFile = Path.Combine(dir, outName);

        Log.Information("\tCSV output will be saved to {OutFile}",outFile);

        try
        {
            using var swCsv = new StreamWriter(outFile, false, Encoding.UTF8, 4096 * 4);
            using var csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

            var map = csvWriter.Context.AutoMap<T>();
            configure?.Invoke(map);
            csvWriter.Context.RegisterClassMap(map);

            csvWriter.WriteHeader<T>();
            csvWriter.NextRecord();

            foreach (var record in records)
            {
                csvWriter.WriteRecord(record);
                csvWriter.NextRecord();
            }

            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine();
            Log.Error(e,
                "Error exporting to CSV. Please report to saericzimmerman@gmail.com. Error: {Message}",e.Message);
            return false;
        }
    }

    /// <summary>One JSON object per line</summary>
    private static bool WriteJson<T>(string dir, string outName, IEnumerable<T> records)
    {
        if (CreateOutputDirectory(dir) == false)
        {
            return false;
        }

        var outFile = Path.Combine(dir, outName);

        Log.Information("\tJSON output will be saved to {OutFile}",outFile);

        try
        {

            using var sWrite = new StreamWriter(new FileStream(outFile, FileMode.Create, FileAccess.Write));
            foreach (var record in records)
            {
                sWrite.WriteLine(record.ToJson());
            }

            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine();
            Log.Error(e,
                "Error exporting to JSON. Please report to saericzimmerman@gmail.com. Error: {Message}",e.Message);
            return false;
        }
    }

    private static bool ProcessLogFile(string f, bool vss, bool dedupe, string csv, string csvf, string json, string jsonf, string dt)
    {
        var sw = new Stopwatch();
        sw.Start();

        var logFiles = new Dictionary<string, LogFile.LogFile>();

        //the error that made the raw read necessary
        Exception rerouteCause = null;

        try
        {
            var vssPaths = new List<string>();
            if (vss)
            {
                var dl = Path.GetPathRoot(Path.GetFullPath(f));

                foreach (var vssInfo in Helper.GetVssInfoViaWmi(dl))
                {
                    vssPaths.Add($"{Helper.GetRawVolumePath(vssInfo.VssNumber)}\\$LogFile");
                }
            }

            try
            {
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read))
                {
                    logFiles.Add(f, LoadLogFile(fs, f));
                }

                if (vssPaths.Count > 0)
                {
                    foreach (var rawCopyReturn in Helper.GetRawFiles(vssPaths, dedupe))
                    {
                        logFiles.Add(rawCopyReturn.InputFilename, LoadLogFile(rawCopyReturn.FileStream, rawCopyReturn.InputFilename));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                //a live $LogFile is in use
                rerouteCause = e;
                Log.Warning("{F} is in use. Rerouting...",f);
                Console.WriteLine();

                logFiles.Clear();

                var ll = new List<string> { f };
                ll.AddRange(vssPaths);

                foreach (var rawCopyReturn in Helper.GetRawFiles(ll, dedupe))
                {
                    logFiles.Add(rawCopyReturn.InputFilename, LoadLogFile(rawCopyReturn.FileStream, rawCopyReturn.InputFilename));
                }
            }
        }
        catch (Exception e)
        {
            return LoadFailed(e, rerouteCause);
        }

        sw.Stop();

        var extra = logFiles.Count > 1 ? " (and VSCs)" : string.Empty;

        Console.WriteLine();
        Log.Information(
            "Processed {F}{Extra} in {TotalSeconds:N4} seconds",f,extra,sw.Elapsed.TotalSeconds);
        Console.WriteLine();

        var dateTimeOffset = DateTimeOffset.UtcNow;
        var ok = true;

        foreach (var logFile in logFiles)
        {
            var log = logFile.Value;

            Log.Information("{Key}: LFS version {Major}.{Minor}, page size 0x{PageSize:X}, cluster size {ClusterSize:N0}{Clean}",
                logFile.Key,log.MajorVersion,log.MinorVersion,log.PageSize,log.ClusterSize,log.RestartArea.CleanDismount ? ", cleanly dismounted" : string.Empty);

            if (log.Records.Count > 0)
            {
                Log.Information("Log records found: {Count:N0} (LSN {First:N0} to {Last:N0}, current LSN {Current:N0})",
                    log.Records.Count,log.Records.Min(t => t.Lsn),log.Records.Max(t => t.Lsn),log.RestartArea.CurrentLsn);
            }
            else
            {
                Log.Information("Log records found: 0");
            }

            ReportProblems(logFile.Key, "pages or records", log.ParseErrors);

            var rows = new LogFileRows(log, GetMftFor(logFile.Key), logFile.Key);

            IEnumerable<LogFileOut> Records() => log.Records.OrderBy(t => t.Lsn).Select(rows.GetRow);

            if (csv.IsNullOrEmpty() == false)
            {
                ok &= WriteCsv(csv, GetOutName(logFile.Key, dateTimeOffset, "$LogFile", ".csv", csvf), Records(), map =>
                {
                    map.Map(t => t.CreatedOn).Convert(t => $"{t.Value.CreatedOn?.ToString(dt)}");
                    map.Map(t => t.ContentModifiedOn).Convert(t => $"{t.Value.ContentModifiedOn?.ToString(dt)}");
                    map.Map(t => t.RecordModifiedOn).Convert(t => $"{t.Value.RecordModifiedOn?.ToString(dt)}");
                    map.Map(t => t.LastAccessedOn).Convert(t => $"{t.Value.LastAccessedOn?.ToString(dt)}");
                });
            }

            if (json.IsNullOrEmpty() == false)
            {
                ok &= WriteJson(json, GetOutName(logFile.Key, dateTimeOffset, "$LogFile", ".json", jsonf), Records());
            }

            Console.WriteLine();
        }

        return ok;
    }

    /// <summary>
    ///     Loads a $LogFile with the volume's cluster size, which it needs for the target $MFT entry of each operation.
    ///     The FILE records written to the log have their entry numbers in them, so the cluster size that gives
    ///     those entries is the right one (4096, the default for NTFS, when there are none)
    /// </summary>
    private static LogFile.LogFile LoadLogFile(Stream stream, string name)
    {
        const int defaultClusterSize = 4096;

        var log = new LogFile.LogFile(stream, defaultClusterSize);

        var samples = log.Records.Select(t => t.Ntfs).Where(t => t?.FileRecord != null && t.FileRecord.EntryNumber > 0).Take(5000).ToList();
        if (samples.Count == 0)
        {
            return log;
        }

        int Matches(long clusterSize) => samples.Count(t =>
            (t.TargetVcn * clusterSize + t.ClusterBlockOffset * 512L) / (t.TargetBlockSize > 0 ? t.TargetBlockSize * 512 : NtfsLogRecord.DefaultFileRecordSize) == t.FileRecord.EntryNumber);

        var best = defaultClusterSize;
        var bestMatches = Matches(defaultClusterSize);
        for (var clusterSize = 512; clusterSize <= 2 * 1024 * 1024; clusterSize *= 2)
        {
            var matches = Matches(clusterSize);
            if (matches > bestMatches)
            {
                best = clusterSize;
                bestMatches = matches;
            }
        }

        Log.Debug("{Name}: cluster size {ClusterSize:N0} matches {Matches:N0} of {Count:N0} logged FILE records",name,best,bestMatches,samples.Count);

        if (best == defaultClusterSize)
        {
            return log;
        }

        stream.Seek(0, SeekOrigin.Begin);
        return new LogFile.LogFile(stream, best);
    }

    private static bool ProcessSds(string f, bool vss, bool dedupe, string csv, string csvf, string ds)
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

                ReportProblems(f, "entries", sds.ParseErrors);

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

                    var rawFiles = Helper.GetRawFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        sds = new Sds(rawCopyReturn.FileStream);
                        sdsFiles.Add(rawCopyReturn.InputFilename, sds);
                    }
                }
            }
            catch (Exception firstError)
            {
                try
                {
                    Console.WriteLine();
                    Log.Information("{F} is in use. Rerouting...",f);

                    var ll = new List<string> { f };

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

                    var rawFiles = Helper.GetRawFiles(ll, dedupe);

                    foreach (var rawCopyReturn in rawFiles)
                    {
                        sds = new Sds(rawCopyReturn.FileStream);
                        sdsFiles.Add(rawCopyReturn.InputFilename, sds);
                    }
                }
                catch (Exception e)
                {
                    return LoadFailed(e, firstError);
                }
            }

            sw.Stop();

            var extra = string.Empty;

            if (sdsFiles.Count > 1)
            {
                extra = " (and VSCs)";
            }

            Console.WriteLine();
            Log.Information(
                "Processed {F}{Extra} in {TotalSeconds:N4} seconds",f,extra,sw.Elapsed.TotalSeconds);
            Console.WriteLine();

            var dt = DateTimeOffset.UtcNow;

            foreach (var sdsFile in sdsFiles)
            {
                Log.Information("SDS entries found in {Key}: {Count:N0}",sdsFile.Key,sdsFile.Value.SdsEntries.Count);

                if (csv.IsNullOrEmpty() == false)
                {
                    if (Directory.Exists(csv) == false)
                    {
                        Log.Information(
                            "Path to {Csv} doesn't exist. Creating...",csv);

                        try
                        {
                            Directory.CreateDirectory(csv);
                        }
                        catch (Exception e)
                        {
                            Log.Fatal(e,
                                "Unable to create directory {Csv}. Does a file with the same name exist? Exiting",csv);
                            return false;
                        }
                    }

                    string outName;

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

                    var outFile = Path.Combine(csv, outName);

                    Log.Information("\tCSV output will be saved to {OutFile}",outFile);

                    var swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

                    _csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

                    var foo = _csvWriter.Context.AutoMap<SdsOut>();

                    _csvWriter.Context.RegisterClassMap(foo);

                    _csvWriter.WriteHeader<SdsOut>();
                    _csvWriter.NextRecord();

                    foreach (var sdsEntry in sdsFile.Value.SdsEntries)
                    {
                        Log.Debug("Processing Id {Id}",sdsEntry.Id);

                        var sdO = new SdsOut
                        {
                            Hash = sdsEntry.Hash,
                            Id = sdsEntry.Id,
                            Offset = sdsEntry.Offset,
                            OwnerSid = sdsEntry.SecurityDescriptor.OwnerSid,
                            GroupSid = sdsEntry.SecurityDescriptor.GroupSid,
                            Control = sdsEntry.SecurityDescriptor.Control.ToString().Replace(", ", "|"),
                            FileOffset = sdsEntry.FileOffset,
                            SourceFile = sdsFile.Key,
                            FromMirror = sdsEntry.FromMirror
                        };

                        if (sdsEntry.SecurityDescriptor.Sacl != null && sdsEntry.SecurityDescriptor.Sacl.RawBytes.Length > 0)
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

                        Log.Verbose("{Sd}",sdsEntry.SecurityDescriptor);

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
                    Log.Warning(
                        "Could not parse {Ds} to valid value. Exiting",ds);
                    return false;
                }

                foreach (var sds1 in sdsFiles)
                {
                    var sd = sds1.Value.SdsEntries.FirstOrDefault(t => t.Id == secId);

                    if (sd == null)
                    {
                        Log.Warning("Could not find entry with Id: {SecId}",secId);
                        continue;
                    }

                    Console.WriteLine();
                    Log.Information("Details for security record # {Id1} (0x{Id:X}), Found in {Key}",sd.Id,sd.Id,sds1.Key);
                    Log.Information("Hash value: {Hash}, Offset: {Offset}",sd.Hash,$"0x{sd.Offset:X}");
                    Log.Information("Control flags: {Flags}",sd.SecurityDescriptor.Control.ToString().Replace(", ", " | "));
                    Console.WriteLine();

                    if (sd.SecurityDescriptor.OwnerSidType == Helpers.SidTypeEnum.UnknownOrUserSid)
                    {
                        Log.Information("Owner SID: {OwnerSid}",sd.SecurityDescriptor.OwnerSid);
                    }
                    else
                    {
                        Log.Information(
                            "Owner SID: {OwnerSid}",Helpers.GetDescriptionFromEnumValue(sd.SecurityDescriptor.OwnerSidType));
                    }

                    if (sd.SecurityDescriptor.GroupSidType == Helpers.SidTypeEnum.UnknownOrUserSid)
                    {
                        Log.Information("Group SID: {GroupSid}",sd.SecurityDescriptor.GroupSid);
                    }
                    else
                    {
                        Log.Information(
                            "Group SID: {GroupSid}",Helpers.GetDescriptionFromEnumValue(sd.SecurityDescriptor.GroupSidType));
                    }

                    if (sd.SecurityDescriptor.Dacl != null)
                    {
                        Console.WriteLine();

                        Log.Information("Discretionary Access Control List");
                        DumpAcl(sd.SecurityDescriptor.Dacl);
                    }

                    if (sd.SecurityDescriptor.Sacl != null)
                    {
                        Console.WriteLine();

                        Log.Information("System Access Control List");
                        DumpAcl(sd.SecurityDescriptor.Sacl);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Error(e,
                "There was an error loading the file! Error: {Message}",e.Message);
            return false;
        }

        return true;
    }

    private static void DumpAcl(XAclRecord acl)
    {
        Log.Information("ACE record count: {Count:N0}",acl.AceRecords.Count);
        Log.Information("ACL type: {AclType}",acl.AclType);
        Console.WriteLine();
        var i = 0;
        foreach (var aceRecord in acl.AceRecords)
        {
            Log.Information("------------ Ace record #{I} ------------",i);
            Log.Information("Type: {AceType}",aceRecord.AceType);
            Log.Information("Flags: {Flags}",aceRecord.AceFlags.ToString().Replace(", ", " | "));
            Log.Information("Mask: {Mask}",aceRecord.Mask.ToString().Replace(", ", " | "));

            if (aceRecord.SidType == Helpers.SidTypeEnum.UnknownOrUserSid)
            {
                Log.Information("SID: {Sid}",aceRecord.Sid);
            }
            else
            {
                Log.Information("SID: {Sid}",Helpers.GetDescriptionFromEnumValue(aceRecord.SidType));
            }

            i += 1;
            Console.WriteLine();
        }
    }

    
    
    private static bool ProcessMft(string file, bool vss, bool dedupe, string body, string bdl, string bodyf, bool blf, string csv, string csvf, string json, string jsonf, bool fl, string dt, string dd, string @do, bool fls, bool includeShort, bool alltimestamp, string de, bool rs, string drDir, bool includeResident, string residentExt, int residentMaxSize)
    {
        var mftFiles = new Dictionary<string, Mft>();
        _mftFiles = mftFiles;

        Mft localMft;

        var sw = new Stopwatch();
        sw.Start();
        try
        {
            _mft = MftFile.Load(file,rs);
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

                var rawFiles = Helper.GetRawFiles(ll, dedupe);

                foreach (var rawCopyReturn in rawFiles)
                {
                    localMft = new Mft(rawCopyReturn.FileStream,rs);
                    mftFiles.Add(rawCopyReturn.InputFilename, localMft);
                }
            }
        }
        catch (UnauthorizedAccessException firstError)
        {
            Log.Warning("{File} is in use. Rerouting...",file);
            Console.WriteLine();
            
            var ll = new List<string> { file };

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
                var rawFiles = Helper.GetRawFiles(ll, dedupe);

                foreach (var rawCopyReturn in rawFiles)
                {
                    localMft = new Mft(rawCopyReturn.FileStream,rs);
                    mftFiles.Add(rawCopyReturn.InputFilename, localMft);
                }

                _mft = mftFiles.First().Value;
            }
            catch (Exception e)
            {
                return LoadFailed(e, firstError);
            }
        }
        catch (Exception e)
        {
            return LoadFailed(e);
        }

        sw.Stop();

        var extra = string.Empty;

        if (mftFiles.Count > 1)
        {
            extra = " (and VSCs)";
        }

        Log.Information(
            "Processed {File}{Extra} in {TotalSeconds:N4} seconds",file,extra,sw.Elapsed.TotalSeconds);
        Console.WriteLine();
        
        var dateTimeOffset = DateTimeOffset.UtcNow;

        //false when an export failed
        var exported = true;

        foreach (var mftFile in mftFiles)
        {
            Log.Information(
                "{Key}: FILE records found: {FileRecordsCount:N0} (Free records: {FreeFileRecordsCount:N0}) File size: {FileSize}",mftFile.Key,mftFile.Value.FileRecords.Count,mftFile.Value.FreeFileRecords.Count,Helper.BytesToSizeAsString(mftFile.Value.FileSize));

            ReportParseErrors(mftFile.Key, mftFile.Value);

            if (_cutoff.HasValue)
            {
                Log.Information("Only including records created or modified on or after {Cutoff} (--cutoff)", _cutoff.Value.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            StreamWriter swBody = null;
            StreamWriter swCsv = null;
            StreamWriter swFileList = null;

            if (body.IsNullOrEmpty() == false)
            {
                bdl =
                    bdl.Substring(0, 1);

                if (Directory.Exists(body) == false)
                {
                    Log.Information(
                        "Path to {Body} doesn't exist. Creating...",body);
                    try
                    {
                        Directory.CreateDirectory(body);
                    }
                    catch (Exception e)
                    {
                        Log.Fatal(e,
                            "Unable to create directory {Body}. Does a file with the same name exist? Exiting",body);
                        return false;
                    }
                }

                string outName;

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

                var outFile = Path.Combine(body, outName);

                Log.Information("\tBodyfile output will be saved to {OutFile}",outFile);

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

                    _bodyWriter = new CsvWriter(swBody, config);

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
                    Console.WriteLine();
                    Log.Error(e,
                        "Error setting up bodyfile export. Please report to saericzimmerman@gmail.com. Error: {Message}",e.Message);
                    _bodyWriter = null;
                    exported = false;
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
                        Log.Information(
                            "Path to {Csv} doesn't exist. Creating...",csv);
                        try
                        {
                            Directory.CreateDirectory(csv);
                        }
                        catch (Exception e)
                        {
                            Log.Fatal(e,
                                "Unable to create directory {Csv}. Does a file with the same name exist? Exiting",csv);
                            return false;
                        }
                    }

                    string outName;

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

                    var outFile = Path.Combine(csv, outName);

                    Log.Information("\tCSV output will be saved to {OutFile}",outFile);

                    if (fl)
                    {
                        var outFileFl = outFile.Replace("$MFT_Output", "$MFT_Output_FileListing");

                        if (csvf.IsNullOrEmpty() == false)
                        {
                            outFileFl = Path.Combine(Path.GetDirectoryName(outFileFl), $"{Path.GetFileNameWithoutExtension(outFileFl)}_FileListing{Path.GetExtension(outFileFl)}");
                        }

                        Log.Information("\tCSV file listing output will be saved to {OutFileFl}",outFileFl);

                        swFileList = new StreamWriter(outFileFl, false, Encoding.UTF8);
                        _fileListWriter = new CsvWriter(swFileList, CultureInfo.InvariantCulture);

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

                        _csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

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
                        foo.Map(t => t.SourceFile).Index(33);

                        foo.Map(t => t.FnAttributeId).Ignore();
                        foo.Map(t => t.OtherAttributeId).Ignore();

                        //the resident data columns are only written with --ir (issue #58)
                        if (includeResident == false)
                        {
                            foo.Map(t => t.ResidentDataBase64).Ignore();
                            foo.Map(t => t.ResidentDataHex).Ignore();
                            foo.Map(t => t.ResidentDataASCII).Ignore();
                        }

                        _csvWriter.Context.RegisterClassMap(foo);

                        _csvWriter.WriteHeader<MFTRecordOut>();
                        _csvWriter.NextRecord();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine();
                        Log.Error(e,
                            "Error setting up CSV export. Please report to saericzimmerman@gmail.com. Error: {Message}",e.Message);
                        _csvWriter = null;
                        exported = false;
                    }
                }
            }

            if (swBody != null || swCsv != null || _mftOutRecords != null)
            {
                try
                {
                    if (drDir.IsNullOrEmpty() == false)
                    {
                        Log.Information("\tResident data will be saved to {DataDir}",drDir);
                        if (Directory.Exists(drDir) == false)
                        {
                            Directory.CreateDirectory(drDir);
                        }
                    }
                    
                    ProcessRecords(mftFile.Value.FileRecords, includeShort, alltimestamp, bdl,drDir,mftFile.Key, includeResident, residentExt, residentMaxSize);
                    ProcessRecords(mftFile.Value.FreeFileRecords, includeShort, alltimestamp, bdl,drDir,mftFile.Key, includeResident, residentExt, residentMaxSize);
                }
                catch (Exception ex)
                {
                    exported = false;
                    Log.Error(ex,
                        "Error exporting data. Please report to saericzimmerman@gmail.com. Error: {Message}",ex.Message);
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

                if (Directory.Exists(json) == false)
                {
                    Log.Information(
                        "Path to {Json} doesn't exist. Creating...",json);

                    try
                    {
                        Directory.CreateDirectory(json);
                    }
                    catch (Exception e)
                    {
                        Log.Fatal(e,
                            "Unable to create directory {Json}. Does a file with the same name exist? Exiting",json);
                        return false;
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

                var outFile = Path.Combine(json, outName);

                Log.Information("\tJSON output will be saved to {OutFile}",outFile);

                try
                {

                    using var sWrite =
                        new StreamWriter(new FileStream(outFile, FileMode.Create, FileAccess.Write));
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
                catch (Exception e)
                {
                    exported = false;
                    Console.WriteLine();
                    Log.Error(e,
                        "Error exporting to JSON. Please report to saericzimmerman@gmail.com. Error: {Message}",e.Message);
                }
            }

            Console.WriteLine();
        }


        #region ExportRecord

        if (dd.IsNullOrEmpty() == false)
        {
            Console.WriteLine();

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
                b.BaseStream.Seek(offset * _mft.FileRecords.Values.First().AllocatedRecordSize, 0); // offset is the FILE entry, so we need to multiply it by the size of the record, typically 1024, but don't assume that 

                var fileBytes = b.ReadBytes(1024);

                var outFile = $"MFTECmd_FILE_Offset0x{offset:X}.bin";
                var outFull = Path.Combine(dd, outFile);

                File.WriteAllBytes(outFull, fileBytes);

                Log.Information("FILE record at offset {Offset} dumped to {OutFull}",$"0x{offset:X}",outFull);
                Console.WriteLine();
            }
            else
            {
                Log.Warning(
                   "Could not parse {Do} to valid value. Exiting",@do);
                return false;
            }
        }

        #endregion

  

        #region DumpEntry

        if (de.IsNullOrEmpty())
        {
            return exported;
        }
        
        Console.WriteLine();

        FileRecord fr = null;

        var segs = de.Split('-');

        bool entryOk;
        bool seqOk;
        int entry;
        int seq;

        var key = string.Empty;

        // if (segs.Length == 2)
        // {
        //     Log.Warning(
        //         "Could not parse {De} to valid values. Format is Entry#-Sequence# in either decimal or hex format. Exiting",de);
        //     return;
        // }

        if (segs.Length == 1)
        {
            if (de.StartsWith("0x"))
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
                Log.Warning(
                    "More than one FILE record found. Please specify one of the values below and try again!");
                Console.WriteLine();

                foreach (var f in ff)
                {
                    FileRecord record = null;
                    if (_mft.FileRecords.TryGetValue(f, out var fileRecord))
                    {
                        record = fileRecord;
                    }
                    else if (_mft.FreeFileRecords.TryGetValue(f, out var freeFileRecord))
                    {
                        record = freeFileRecord;
                    }

                    if (record != null)
                    {
                        Log.Information("{Entry}-{Seq}",$"0x{record.EntryNumber:X}",$"0x{record.SequenceNumber:X}");    
                    }
                }

                return false;
            }
            else
            {
                Console.WriteLine();
                Log.Warning(
                    "Could not find FILE record with specified Entry #. Use the --csv option and verify");
                Console.WriteLine();

                return false;
            }
        }
        else if (segs.Length == 2)
        {
            if (de.StartsWith("0x"))
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
            
            //try to find correct one

            var ff = _mft.FileRecords.Keys.Where(t => t.Equals($"{entry:X8}-{seq:X8}")).ToList();

            if (ff.Count == 0)
            {
                ff = _mft.FreeFileRecords.Keys.Where(t => t.Equals($"{entry:X8}-{seq:X8}")).ToList();
            }
            
            if (ff.Count == 1)
            {
                key = ff.First();
            }
            else
            {
                Console.WriteLine();
                Log.Warning(
                    "Could not find FILE record with specified Entry #. Use the --csv option and verify");
                Console.WriteLine();

                return false;
            }
            
        }


        if (key.Length == 0)
        {
            if (de.StartsWith("0x"))
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
                Log.Warning(
                    "Could not parse {De} to valid values. Exiting",de);
                return false;
            }

            key = $"{entry:X8}-{seq:X8}";
        }


        if (_mft.FileRecords.TryGetValue(key, out var mftFileRecord))
        {
            fr = mftFileRecord;
        }
        else if (_mft.FreeFileRecords.TryGetValue(key, out var record))
        {
            fr = record;
        }

        if (fr == null)
        {
            Log.Warning(
                "Could not find file record with entry/seq {De}. Exiting",de);
            return false;
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

            Log.Information("Contents for {Name}",name);
            Console.WriteLine();
            Log.Information("Key\t\t\tType\t\tName");
            foreach (var parentMapEntry in dlist)
            {
                if (parentMapEntry.IsDirectory)
                {
                    Log.Information("{Key}\t{Type}\t{FileName}",$"{parentMapEntry.Key,-16}","Directory",parentMapEntry.FileName);
                }
                else
                {
                    Log.Information("{Key}\t{Type}\t\t{FileName}",$"{parentMapEntry.Key,-16}","File",parentMapEntry.FileName);
                }
            }

            Console.WriteLine();
        }
        else
        {
            Log.Information("Dumping details for file record with key {Key}",key);
            Console.WriteLine();
            
            var fa0 = fr.FixupData.FixupActual[0].ToArray();
            var fa1 = fr.FixupData.FixupActual[1].ToArray();
            var das0 = BitConverter.ToString(fa0);
            var das1 = BitConverter.ToString(fa1);
            
            var actual = $"{das0} | {das1}";

            var expected = BitConverter.ToString(BitConverter.GetBytes(fr.FixupData.FixupExpected));
            
            Log.Information("Entry-seq #: {Entry}-{Seq}, Offset: {Offset}, Flags: {Flags}, Log seq #: {Lsn}, Base Record entry-seq: {BaseEntry}-{BaseSeq}"
                ,$"0x{fr.EntryNumber:X}",$"0x{fr.SequenceNumber:X}",$"0x{fr.Offset:X}",fr.EntryFlags,$"0x{fr.LogSequenceNumber:X}",$"0x{fr.MftRecordToBaseRecord.MftEntryNumber:X}",$"0x{fr.MftRecordToBaseRecord.MftSequenceNumber:X}");
            Log.Information("Reference count: {RefCount}, FixUp Data Expected: {Expected:X}, FixUp Data Actual: {Actual} (FixUp OK: {Ok})",$"0x{fr.ReferenceCount:X}",expected,actual,fr.FixupOk);

            void DumpAttributeInfo(Attribute item, string description)
            {
                Log.Information("**** {Type} ****",description);
                
                Log.Information("  Attribute #: {AttrNumber}, Size: {Size}, Content size: {ContentSize}, Name size: {NameSize}, ContentOffset {ContentOffset}. Resident: {Resident}", $"0x{item.AttributeNumber:X}",
                    $"0x{item.AttributeSize:X}", $"0x{item.AttributeContentLength:X}", $"0x{item.NameSize:X}", $"0x{item.ContentOffset:X}", item.IsResident);
                
                if (item.AttributeDataFlag > 0)
                {
                    Log.Information("  Attribute flags: {AttributeFlags}", item.AttributeDataFlag);
                }
                
                if (item.Name.IsNullOrEmpty() == false)
                {
                    Log.Information("  Name: {Name}", item.Name);
                }
            }

            void DumpResidentData(ResidentData residentData)
            {
                Log.Information("  Resident Data");
                Console.WriteLine();
                var asAscii = Encoding.ASCII.GetString(residentData.Data);
                var asUnicode = Encoding.Unicode.GetString(residentData.Data);
                Log.Information("  Data: {Data}",BitConverter.ToString(residentData.Data));
                Console.WriteLine();
                Log.Information("    ASCII:   {Data}",asAscii);
                Log.Information("    UNICODE: {Data}",asUnicode);
            }

            void DumpNonResidentData(NonResidentData nonResidentData)
            {
                Log.Information("  Non-Resident Data");
                Log.Information(
                    "  Starting Virtual Cluster #: {StartingVirtualClusterNumber}, Ending Virtual Cluster #: {EndingVirtualClusterNumber}, Allocated Size: {AllocatedSize}, Actual Size: {ActualSize}, Initialized Size: {InitializedSize} "
                    ,$"0x{nonResidentData.StartingVirtualClusterNumber:X}",$"0x{nonResidentData.EndingVirtualClusterNumber:X}",$"0x{nonResidentData.AllocatedSize:X}", $"0x{nonResidentData.ActualSize:X}",$"0x{nonResidentData.InitializedSize:X}");

                Console.WriteLine();
                Log.Information("  DataRuns Entries (Cluster offset -> # of clusters)");
                            
                foreach (var dataRun in nonResidentData.DataRuns)
                {
                    Log.Information("  {Offset} ->      {Clusters}",$"0x{dataRun.ClusterOffset:X}".PadRight(32),$"0x{dataRun.ClustersInRun:X}");
                }
            }

            void DumpAcl(XAclRecord acl)
            {
                Log.Information("    ACL Revision:   {Revision}",$"0x{acl.AclRevision:X}");
                Log.Information("    ACL Size:       {Size}",$"0x{acl.AclSize:X}");
                Log.Information("    ACL Type:       {Type}",acl.AclType);
                Log.Information("    Sbz1:           {Sbz}",$"0x{acl.Sbz1:X}");
                Log.Information("    Sbz2:           {Sbz}",$"0x{acl.Sbz2:X}");

                Console.WriteLine();
                Log.Information("    ACE Records Count: {AceCount}",acl.AceCount);

                Console.WriteLine();

                var i = 0;
                foreach (var aceRecord in acl.AceRecords)
                {
                    Log.Information("    ------------ Ace record #{I} ------------",i);
                    
                    Log.Information("    ACE Size:  {Size}",$"0x{aceRecord.AceSize:X}");

                    Log.Information("    ACE Type:  {AceType}",aceRecord.AceType);

                    Log.Information("    ACE Flags: {AceFlags}",aceRecord.AceFlags);

                    Log.Information("    Mask:      {Mask}",aceRecord.Mask);

                    Log.Information("    SID:       {Sid}",aceRecord.Sid);
                    Log.Information("    SID Type:  {SidType}",aceRecord.SidType);

                    Log.Information("    SID Type Description: {Desc}",Helpers.GetDescriptionFromEnumValue(aceRecord.SidType));
                    i += 1;
                    Console.WriteLine();
                    
                }
            }
            
            Console.WriteLine();
            
            foreach (var frAttribute in fr.Attributes)
            {
                switch (frAttribute)
                {
                    case StandardInfo item:
                        DumpAttributeInfo(item,"STANDARD INFO");

                        Log.Information("  Flags: {Flags}, Max Version: {MaxVersion}, Flags 2: {Flags2}, Class Id: {ClassId}, Owner Id: {OwnerId}, Security Id: {SecurityId}, Quota charged: {Quota}, Update sequence #: {Usn}"
                            ,item.Flags,$"0x{item.MaxVersion:X}",item.Flags2,$"0x{item.ClassId:X}",$"0x{item.OwnerId:X}",$"0x{item.SecurityId:X}",$"0x{item.QuotaCharged:X}",$"0x{item.UpdateSequenceNumber:X}");
                        Console.WriteLine();
                        
                        Log.Information("  Created On:         {Date}", item.CreatedOn);
                        Log.Information("  Modified On:        {Date}", item.ContentModifiedOn);
                        Log.Information("  Record Modified On: {Date}", item.RecordModifiedOn);
                        Log.Information("  Last Accessed On:   {Date}", item.LastAccessedOn);
                        
                        Console.WriteLine();
                        
                        break;
                    
                    case FileName item:
                        DumpAttributeInfo(item,"FILE NAME");
                        
                        Console.WriteLine();
                        
                        Log.Information("  File name: {Thing}",item.FileInfo.FileName);
                        Log.Information("  Flags: {Flags}, Name Type: {NameType}, Reparse Value: {Reparse}, Physical Size: {Physical}, Logical Size: {Logical}"
                            ,item.FileInfo.Flags,item.FileInfo.NameType,$"0x{item.FileInfo.ReparseValue:X}",$"0x{item.FileInfo.PhysicalSize:X}",$"0x{item.FileInfo.LogicalSize:X}");
                        Log.Information("  Parent Entry-seq #: {Entry}-{Seq}",$"0x{item.FileInfo.ParentMftRecord.MftEntryNumber:X}",$"0x{item.FileInfo.ParentMftRecord.MftSequenceNumber:X}");
                        
                        Console.WriteLine();
                        
                        Log.Information("  Created On:         {Date}", item.FileInfo.CreatedOn);
                        Log.Information("  Modified On:        {Date}", item.FileInfo.ContentModifiedOn);
                        Log.Information("  Record Modified On: {Date}", item.FileInfo.RecordModifiedOn);
                        Log.Information("  Last Accessed On:   {Date}", item.FileInfo.LastAccessedOn);
                        
                        Console.WriteLine();
                        break;
                    
                    case Data item:
                        DumpAttributeInfo(item,"DATA");
                        Console.WriteLine();
                        if (item.ResidentData == null)
                        {
                            DumpNonResidentData(item.NonResidentData);

                        }
                        else
                        {
                            DumpResidentData(item.ResidentData);
                        }
                        
                        Console.WriteLine();
                        break;

                    case SecurityDescriptor item:
                        DumpAttributeInfo(item, "SECURITY DESCRIPTOR");
                        Console.WriteLine();

                        if (item.SecurityInfo == null)
                        {
                            continue;
                        }

                        Log.Information("  Security Information");
                        Log.Information("    Revision: {Item}", $"0x{item.SecurityInfo.Revision:X}");
                        Log.Information("    Control:  {Item}",item.SecurityInfo.Control);
                        
                        Console.WriteLine();
                        
                        Log.Information("    Owner Offset:   {Item}", $"0x{item.SecurityInfo.OwnerOffset:X}");
                        Log.Information("    Owner SID:      {Item}",item.SecurityInfo.OwnerSid);
                        Log.Information("    Owner SID Type: {Item}",item.SecurityInfo.OwnerSidType);
                        
                        Console.WriteLine();
                        
                        Log.Information("    Group Offset:   {Item}", $"0x{item.SecurityInfo.GroupOffset:X}");
                        Log.Information("    Group SID:      {Item}",item.SecurityInfo.GroupSid);
                        Log.Information("    Group SID Type: {Item}",item.SecurityInfo.GroupSidType);

                        if (item.SecurityInfo.Dacl != null)
                        {
                            Console.WriteLine();
                            Log.Information("    Dacl Offset:    {Item}", $"0x{item.SecurityInfo.DaclOffset:X}");
                            DumpAcl(item.SecurityInfo.Dacl);
                        }
                        
                        if (item.SecurityInfo.Sacl != null)
                        {
                            Console.WriteLine();
                            Log.Information("    Sacl Offset:    {Item}", $"0x{item.SecurityInfo.SaclOffset:X}");
                            DumpAcl(item.SecurityInfo.Sacl);
                        }
                        
                        Console.WriteLine();
                        
                        if (item.ResidentData == null)
                        {
                            DumpNonResidentData(item.NonResidentData);
                        }
                        else
                        {
                            DumpResidentData(item.ResidentData);
                        }
               
                        Console.WriteLine();
                        break;

                    case IndexRoot item:
                        DumpAttributeInfo(item,"INDEX ROOT");
                        Console.WriteLine();

                        Log.Information("  Indexed Attribute Type: {IndexedAttributeType} Entry Size: {EntrySize} Number Cluster Blocks: {NumberClusterBlocks} Collation Type: {CollationType} Index entries count: {IndexEntriesCount}",
                            item.IndexedAttributeType,$"0x{item.EntrySize:X}",$"0x{item.NumberClusterBlocks:X}",item.CollationType,$"0x{item.IndexEntries.Count:X}");
                        
                        Log.Information("  FileInfo Records Entries");
                        foreach (var itemIndex in item.IndexEntries)
                        {
                            Log.Information("  File name: {Thing}",itemIndex.FileName);
                            Log.Information("  Flags: {Flags}, Name Type: {NameType}, Reparse Value: {Reparse}, Physical Size: {Physical}, Logical Size: {Logical}",itemIndex.Flags,itemIndex.NameType,$"0x{itemIndex.ReparseValue:X}",$"0x{itemIndex.PhysicalSize:X}",$"0x{itemIndex.LogicalSize:X}");
                            if (itemIndex.MftReference != null)
                            {
                                Log.Information("  Entry-seq #: {Entry}-{Seq}",$"0x{itemIndex.MftReference.MftEntryNumber:X}",$"0x{itemIndex.MftReference.MftSequenceNumber:X}");
                            }

                            if (itemIndex.ParentMftRecord != null)
                            {
                                Log.Information("  Parent Entry-seq #: {Entry}-{Seq}",$"0x{itemIndex.ParentMftRecord.MftEntryNumber:X}",$"0x{itemIndex.ParentMftRecord.MftSequenceNumber:X}");
                            }
                        
                            Console.WriteLine();
                        
                            Log.Information("  Created On:         {Date}", itemIndex.CreatedOn);
                            Log.Information("  Modified On:        {Date}", itemIndex.ContentModifiedOn);
                            Log.Information("  Record Modified On: {Date}", itemIndex.RecordModifiedOn);
                            Log.Information("  Last Accessed On:   {Date}", itemIndex.LastAccessedOn);
                        
                            Console.WriteLine();
                        }
                        
                        Console.WriteLine();
                        break;
                    case IndexAllocation item:
                        DumpAttributeInfo(item,"INDEX ALLOCATION");
                        Console.WriteLine();
                        
                        DumpNonResidentData(item.NonResidentData);
                        
                        Console.WriteLine();
                        break;
                    case Bitmap item:
                        DumpAttributeInfo(item,"BITMAP");
                        Console.WriteLine();
                        
                        if (item.ResidentData == null)
                        {
                            DumpNonResidentData(item.NonResidentData);
                        }
                        else
                        {
                            DumpResidentData(item.ResidentData);
                        }
                        
                        Console.WriteLine();
                        break;
                    
                    case LoggedUtilityStream item:
                        DumpAttributeInfo(item,"LOGGED UTILITY STREAM");
                        Console.WriteLine();
                        
                        DumpResidentData(item.ResidentData);

                        Console.WriteLine();
                        break;
                    
                    case ObjectId_ item:
                        DumpAttributeInfo(item,"OBJECT ID");
                        Console.WriteLine();
                        
                        DateTimeOffset GetDateTimeOffsetFromGuid(Guid guid)
                        {
                            // offset to move from 1/1/0001, which is 0-time for .NET, to gregorian 0-time of 10/15/1582
                            var gregorianCalendarStart = new DateTimeOffset(1582, 10, 15, 0, 0, 0, TimeSpan.Zero);
                            const int versionByte = 7;
                            const int versionByteMask = 0x0f;
                            const int versionByteShift = 4;
                            const byte timestampByte = 0;

                            var bytes = guid.ToByteArray();

                            // reverse the version
                            bytes[versionByte] &= versionByteMask;
                            bytes[versionByte] |= 0x01 >> versionByteShift;

                            var timestampBytes = new byte[8];
                            Array.Copy(bytes, timestampByte, timestampBytes, 0, 8);

                            var timestamp = BitConverter.ToInt64(timestampBytes, 0);
                            var ticks = timestamp + gregorianCalendarStart.Ticks;

                            return new DateTimeOffset(ticks, TimeSpan.Zero);
                        }
                        
                        var tempMac = item.ObjectId.ToString().Split('-').Last();
                        var objectIdMacAddress = Regex.Replace(tempMac, ".{2}", "$0:").TrimEnd(':');
                        var objectIdCreatedOn = GetDateTimeOffsetFromGuid(item.ObjectId);

                        tempMac = item.BirthObjectId.ToString().Split('-').Last();
                        var birthVolumeIdMacAddress = Regex.Replace(tempMac, ".{2}", "$0:").TrimEnd(':');
                        var birthVolumeIdCreatedOn = GetDateTimeOffsetFromGuid(item.BirthObjectId);
                        
                        Log.Information("  Object Id:            {ObjectId}",item.ObjectId);
                        Log.Information("    Object Id MAC:        {MacAddress}",objectIdMacAddress);
                        if (objectIdCreatedOn.Year < 3000)
                        {
                            Log.Information("    Object Id Created On: {CreatedOn}",objectIdCreatedOn);    
                        }
                        
                        Console.WriteLine();
                        Log.Information("  Birth Volume Id:      {BirthVolumeId}",item.BirthVolumeId);
                        if (item.BirthObjectId.ToString() != "00000000-0000-0000-0000-000000000000")
                        {
                            Log.Information("    Birth Volume Id MAC:      {MacAddress}",birthVolumeIdMacAddress);
                            Log.Information("  Birth Volume Id Created On: {CreatedOn}",birthVolumeIdCreatedOn);
                        }
                        
                        Console.WriteLine();
                        Log.Information("  Birth Object Id:      {BirthObjectId}",item.BirthObjectId);
                        Log.Information("  Domain Id:            {Domain}",item.DomainId);
                        
                        Console.WriteLine();
                        break;
                    
                    case ExtendedAttribute item:
                        DumpAttributeInfo(item,"EXTENDED ATTRIBUTE");
                        Console.WriteLine();
                        
                        var asAscii = Encoding.ASCII.GetString(item.Content);
                        var asUnicode = Encoding.Unicode.GetString(item.Content);

                        Log.Information("  Extended Attribute: {Content}",BitConverter.ToString(item.Content));
                        Log.Information("  ASCII: {Ascii}",asAscii);
                        Log.Information("  Unicode: {Unicode}",asUnicode);

                        if (item.SubItems.Count > 0)
                        {
                            Log.Information("  Sub items");
                        }
                        foreach (IEa itemSubItem in item.SubItems)
                        {
                            Log.Information("  Name: {Name}", itemSubItem.InternalName);
                            
                            // need to find working data to get this all done right
                            
                            switch (itemSubItem.InternalName)
                            { 
                                case "$LXUID":
                                case "$LXGID":
                                case "$LXMOD":
                                    Log.Information("  Name: {Name}",(itemSubItem as LxXXX).Name);
                                    
                                    break;
                                case ".LONGNAME":
                                    Log.Information("  .LONGNAME: {Name}",(itemSubItem as LongName).Name);
                                    break;
                                case "LXATTRB":

                                    var lxa = itemSubItem as Lxattrb;
                                    
                                    Log.Information("  Format: {Format}",$"0x{lxa.Format:X}");
                                    Log.Information("  Version: {Version}",$"0x{lxa.Version:X}");
                                    Log.Information("  Mode: {Mode}",$"0x{lxa.Mode:X}");
                                    Log.Information("  Uid/Gid: {U}/{G}",$"0x{lxa.Uid:X}",$"0x{lxa.Gid:X}");
                                    Log.Information("  DeviceId: {DeviceId}",$"0x{lxa.DeviceId:X}");
                                    
                                    //convert to seconds so we can use it later.
                                    //.net has no API for adding nanoseconds that works, so this is what we get
                                    var lastAccessSubSec = (lxa.LastAccessNanoSeconds / 1e+9).ToString(CultureInfo.InvariantCulture);
                                    var modifiedSubsec = (lxa.ModifiedNanoSeconds / 1e+9).ToString(CultureInfo.InvariantCulture);
                                    var inodeChangeSubsec = (lxa.InodeChangedNanoSeconds / 1e+9).ToString(CultureInfo.InvariantCulture);

                                    var subSec = lastAccessSubSec.Length > 2 ? lastAccessSubSec.Substring(2) : "0000000";
                                    
                                    Log.Information("  Last Access Time: {Time}.{Sub}",lxa.LastAccessTime.ToUniversalTime(),subSec);
                                    Log.Information("  Modified Time: {Time}.{Sub}",lxa.ModifiedTime,modifiedSubsec);
                                    Log.Information("  Inode Time: {Time}.{Sub}",lxa.ModifiedTime,inodeChangeSubsec);
                                    
                                    break;
                                case "LXXATTR":
                                    var lxx = itemSubItem as Lxattrr;
                                    foreach (var keyValue in lxx.KeyValues)
                                    {
                                        Log.Information("  Key: {Key} --> {Value}", keyValue.Key, keyValue.Value);
                                    }
                                    
                                    break;
                                case "$KERNEL.PURGE.ESBCACHE":
                                    var kpe = itemSubItem as PurgeEsbCache;
                                    Log.Information("  $KERNEL.PURGE.ESBCACHE | Timestamp: {Timestamp} Timestamp2: {Timestamp2}",kpe.Timestamp,kpe.Timestamp2);
                                    break;
                                case "$CI.CATALOGHINT":
                                    var ch = itemSubItem as CatHint;
                                    Log.Information("  $CI.CATALOGHINT | Hint: {Hint}",ch.Hint);
                                    
                                    break;
                                case "$KERNEL.PURGE.APPFIXCACHE":
                                case "$KERNEL.PURGE.APPXFICACHE":
                                    
                                    var af = itemSubItem as AppFixCache;
                                    
                                    Log.Information("  $KERNEL.PURGE.APPFIXCACHE | Timestamp: {Timestamp} Remaining bytes: {RemainingBytes}",af.Timestamp,BitConverter.ToString(af.RemainingBytes));
                                    break;
                                case ".CLASSINFO":
                                    Log.Information("  .ClassInfo: Not decoded");
                                    break;
                             
                                
                                default:
                                    Log.Information("{Si}",itemSubItem);
                                    throw new Exception($"You should report this to saericzimmerman@gmail.com! Type not supported yet: {itemSubItem.GetType()}");
                            }
                        }
                        
                        Console.WriteLine();
                        break;
                    
                    case ExtendedAttributeInformation item:
                        DumpAttributeInfo(item,"EXTENDED ATTRIBUTE INFORMATION");
                        Console.WriteLine();
                        
                        Log.Information("  Ea Size: {EaSize}, Number Of Extended Attributes With Need Ea Set: {NumberOfExtendedAttrWithNeedEaSet} Size Of Ea Data: {SizeOfEaData} ", $"0x{item.EaSize:X}",$"0x{item.NumberOfExtendedAttrWithNeedEaSet:X}",$"0x{item.SizeOfEaData:X}");
                        
                        Console.WriteLine();
                        break;
                    
                    case ReparsePoint item:
                        DumpAttributeInfo(item,"REPARSE POINT");
                        Console.WriteLine();
                        
                        Log.Information("  Substitute Name: {SubstituteName} Print Name: {PrintName} Tag: {Tag}",item.SubstituteName,item.PrintName,item.Tag);
                        
                        Console.WriteLine();
                        break;
                    
                    case VolumeInformation item:
                        DumpAttributeInfo(item,"VOLUME INFORMATION");
                        Console.WriteLine();
                        
                        Log.Information("  Volume Flags: {Flags} Major Version: {MajorVersion} Minor Version: {MinorVersion} Unknown Bytes: {Bytes} ",item.VolumeFlags,$"0x{item.MajorVersion:X}",$"0x{item.MinorVersion:X}",BitConverter.ToString(item.UnknownBytes));
                        
                        Console.WriteLine();
                        
                        break;
                    
                    case VolumeName item:
                        DumpAttributeInfo(item,"VOLUME NAME");
                        Console.WriteLine();
                        
                        Log.Information("  Volume Name: {Name}",item.VolName);
                        
                        Console.WriteLine();
                        break;
                    
                    case AttributeList item:
                        //BOOOOOORING
                        break;
                    
                    default:
                        Log.Information("{Attrib}",frAttribute);
                        throw new Exception($"You should report this to saericzimmerman@gmail.com! Attribute Type not supported yet: {frAttribute.GetType()}");
                }
                
            }
        }
        

        #endregion

        return exported;
    }

    /// <summary>GetFileType found too little data in the file (it returns null, as when the file can't be read)</summary>
    private static bool _emptyFile;

    /// <summary>The file type from its signature, or null when the file could not be read or is empty (the error is logged)</summary>
    private static FileType? GetFileType(string file)
    {
        const int logFileSig = 0x52545352; //RSTR
        const int logFileChkdskSig = 0x444B4843; //CHKD, after chkdsk
        const int mftSig = 0x454C4946;
        const int sdsSig = 0x32FEC6CB;
        const int bootSig = 0x5346544E;
        const int indxSig = 0x58444e49;

        Log.Debug("Opening {File} and checking header",file);

        var buff = new byte[50];

        try
        {
            try
            {
                using var br = new BinaryReader(new FileStream(file, FileMode.Open, FileAccess.Read));
                buff = br.ReadBytes(50);
                Log.Verbose("Raw bytes: {RawBytes}",BitConverter.ToString(buff));
            }
            catch (Exception)
            {
                var ll = new List<string> { file };

                try
                {
                    var rawFiles = Helper.GetRawFiles(ll);

                    rawFiles.First().FileStream.ReadExactly(buff, 0, 50);
                }
                catch (Exception e)
                {
                    Console.WriteLine();
                    Log.Fatal(e,
                        "Error opening file {File}. Does it exist? Error: {Message} Exiting",file,e.Message);
                    Console.WriteLine();
                    return null;
                }
            }

            if (buff.Length < 20)
            {
                Console.WriteLine();
                Log.Information(
                    "Not enough data found in {File}. Is the file empty? Exiting",file);
                Console.WriteLine();
                _emptyFile = true;
                return null;
            }

            var sig32 = BitConverter.ToInt32(buff, 0);

            //some usn checks
            var majorVer = BitConverter.ToInt16(buff, 4);
            var minorVer = BitConverter.ToInt16(buff, 6);

            Log.Debug("Sig32: {Sig32}",$"0x{sig32:X}");

            switch (sig32)
            {
                case indxSig:
                    Log.Debug("Found $I30 sig");
                    return FileType.I30;
                case logFileSig:
                case logFileChkdskSig:
                    Log.Debug("Found $LogFile sig");
                    return FileType.LogFile;

                case mftSig:
                    Log.Debug("Found $MFT sig");
                    return FileType.Mft;
                case sdsSig:
                    return FileType.Sds;

                case 0x0:
                    //00 for sparse file

                    if (majorVer != 0 || minorVer != 0)
                    {
                        return FileType.Unknown;
                    }

                    Log.Debug("Found $J sig (0 size) and major/minor == 0)");
                    return FileType.UsnJournal;

                default:
                    var isBootSig = BitConverter.ToInt32(buff, 3);
                    if (isBootSig == bootSig)
                    {
                        Log.Debug("Found $Boot sig");
                        return FileType.Boot;
                    }

                    if (majorVer == 2 && minorVer == 0)
                    {
                        Log.Debug("Found $J sig (Major == 2, Minor == 0)");
                        return FileType.UsnJournal;
                    }

                    var zeroOffset = BitConverter.ToUInt64(buff, 8);

                    if (zeroOffset == 0)
                    {
                        Log.Debug("Found $SDS sig (Offset 0x8 as Int64 == 0");
                        return FileType.Sds;
                    }

                    break;
            }

            Log.Debug("Failed to find a signature! Returning unknown");
        }
        catch (UnauthorizedAccessException e)
        {
            Console.WriteLine();
            Log.Fatal(e,
                "Could not access {File}. Rerun the program as an administrator",file);
            Console.WriteLine();
            return null;
        }

        return FileType.Unknown;
    }

    /// <summary>Damaged data that the parsers kept going past; the details are shown with --debug</summary>
    private static void ReportProblems(string file, string what, List<string> problems)
    {
        if (problems.Count == 0)
        {
            return;
        }

        Log.Warning("{File}: {Count:N0} problem(s) with {What} were found and skipped. Use --debug to see them", file, problems.Count, what);
        foreach (var problem in problems)
        {
            Log.Debug("  {Problem}", problem);
        }
    }

    private static void ReportParseErrors(string file, Mft mft)
    {
        ReportProblems(file, "FILE records", mft.ParseErrors);

        var damaged = mft.FileRecords.Values.Concat(mft.FreeFileRecords.Values).Where(t => t.HasParseErrors).ToList();
        if (damaged.Count == 0)
        {
            return;
        }

        Log.Warning("{File}: {Count:N0} FILE record(s) have damaged attributes; the rest of each record was used. See the ParseErrors column, or use --debug", file, damaged.Count);
        foreach (var fr in damaged)
        {
            Log.Debug("  Entry/seq 0x{Entry:X}-0x{Seq:X} at offset 0x{Offset:X}: {Errors}", fr.EntryNumber, fr.SequenceNumber, fr.Offset, string.Join("; ", fr.ParseErrors));
        }
    }

    private static void ProcessRecords(Dictionary<string, FileRecord> records, bool includeShort, bool alltimestamp, string bdl, string drDumpDir, string mftFilePath, bool includeResident, string residentExt, int residentMaxSize)
    {
        
        foreach (var fr in records)
        {
            Log.Verbose(
                "Dumping record with entry: {EntryNumber} at offset {Offset}",$"0x{fr.Value.EntryNumber:X}",$"0x{fr.Value.SequenceNumber:X}");

            if (fr.Value.MftRecordToBaseRecord.MftEntryNumber > 0 &&
                fr.Value.MftRecordToBaseRecord.MftSequenceNumber > 0)
            {
                Log.Debug(
                    "Skipping entry # {EntryNumber}, seq #: {SequenceNumber} since it is an extension record",$"0x{fr.Value.EntryNumber:X}",$"0x{fr.Value.SequenceNumber:X}");
                //will get this record via extension records, which were already handled in MFT.dll code
                continue;
            }
            
            //A useful little thing to find attributes we need to decode
            // foreach (var valueAttribute in fr.Value.Attributes)
            // {
            //     if (valueAttribute is not LoggedUtilityStream && valueAttribute is not ReparsePoint && valueAttribute is not LoggedUtilityStream &&  valueAttribute is not VolumeInformation && valueAttribute is not VolumeName && valueAttribute is not StandardInfo && valueAttribute is not Data && valueAttribute is not FileName && valueAttribute is not IndexRoot && valueAttribute is not IndexAllocation && valueAttribute is not Bitmap && valueAttribute is not ObjectId_  && valueAttribute.GetType().Name != "AttributeList")
            //     {
            //         Log.Information("E/S: {E}-{S}: {A}",fr.Value.EntryNumber,fr.Value.SequenceNumber,valueAttribute.GetType());
            //     }
            // }

            foreach (var attribute in fr.Value.Attributes.Where(t =>
                         t.AttributeType == AttributeType.FileName).OrderBy(t => ((FileName)t).FileInfo.NameType))
            {
                var fn = (FileName)attribute;
                if (includeShort == false &&
                    fn.FileInfo.NameType == NameTypes.Dos)
                {
                    continue;
                }

                var mftr = GetCsvData(fr.Value, fn, null, alltimestamp, mftFilePath, includeResident, residentExt, residentMaxSize);

                if (_cutoff.HasValue && (mftr.Created0x10 >= _cutoff.Value || mftr.LastModified0x10 >= _cutoff.Value) == false)
                {
                    //--cutoff: also skips this record's ADSs, resident data and bodyfile lines
                    continue;
                }

                var ads = fr.Value.GetAlternateDataStreams();

                if (drDumpDir.IsNullOrEmpty() == false)
                {
                    var data = fr.Value.Attributes.Where(t =>
                        t.AttributeType == AttributeType.Data);

                    foreach (var da in data)
                    {
                        if (da.IsResident == false)
                        {
                            continue;
                        }

                        var outNameR = Path.Combine(drDumpDir, $"{fr.Value.EntryNumber}-{fr.Value.SequenceNumber}-{da.AttributeNumber}_{fn.FileInfo.FileName}.bin");
                        
                        Log.Debug("Saving resident data for {Entry}-{Seq} to {File}",fr.Value.EntryNumber,fr.Value.SequenceNumber,outNameR);
                        
                        File.WriteAllBytes(outNameR,((Data)da).ResidentData.Data);
                    }
                }
                

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
                    var f = GetBodyData(mftr, true, bdl);

                    _bodyWriter.WriteRecord(f);
                    _bodyWriter.NextRecord();

                    f = GetBodyData(mftr, false, bdl);

                    _bodyWriter.WriteRecord(f);
                    _bodyWriter.NextRecord();
                }


                foreach (var adsInfo in ads)
                {
                    var adsRecord = GetCsvData(fr.Value, fn, adsInfo, alltimestamp, mftFilePath, includeResident, residentExt, residentMaxSize);
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
                        var f1 = GetBodyData(adsRecord, true, bdl);

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

    private static void PopulateResidentData(MFTRecordOut mftr, FileRecord fr, string residentExt, int residentMaxSize)
    {
        if (mftr.FileSize > (ulong)residentMaxSize)
        {
            return;
        }

        if (!string.IsNullOrEmpty(residentExt))
        {
            var allowedExtensions = new HashSet<string>(
                residentExt.Split(',').Select(e => e.Trim().ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase
            );

            if (!string.IsNullOrEmpty(mftr.Extension) && 
                !allowedExtensions.Contains(mftr.Extension.ToLowerInvariant()))
            {
                return;
            }
        }

        var dataAttrs = fr.Attributes.Where(t => 
            t.AttributeType == AttributeType.Data && t.IsResident).ToList();

        foreach (var attr in dataAttrs)
        {
            var dataAttr = attr as Data;
            if (dataAttr?.ResidentData?.Data != null)
            {
                var data = dataAttr.ResidentData.Data;

                mftr.ResidentDataBase64 = Convert.ToBase64String(data);

                mftr.ResidentDataHex = BitConverter.ToString(data);

                try
                {
                    var text = Encoding.UTF8.GetString(data);
                    if (text.All(c => char.IsControl(c) || char.IsWhiteSpace(c) || 
                        (c >= 32 && c <= 126) || c == '\r' || c == '\n' || c == '\t'))
                    {
                        mftr.ResidentDataASCII = text;
                    }
                }
                catch
                {
                }

                break;
            }
        }
    }
    
    private class CustomHelpAction : SynchronousCommandLineAction
    {
        private readonly HelpAction _defaultHelp;

        public CustomHelpAction(HelpAction action)
        {
            _defaultHelp = action;
        }

        public override int Invoke(ParseResult parseResult)
        {
            var result = _defaultHelp.Invoke(parseResult);

            Log.Warning("{Msg}", string.Join(" ",parseResult.Tokens));

            return result;
        }
    }

    public static MFTRecordOut GetCsvData(FileRecord fr, FileName fn, AdsInfo adsinfo, bool alltimestamp, string mftFilePath, bool includeResident, string residentExt, int residentMaxSize)
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
            FnAttributeId = fn.AttributeNumber,
            SourceFile = mftFilePath,
            ParseErrors = fr.HasParseErrors ? string.Join("; ", fr.ParseErrors) : null
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
                    mftr.ZoneIdContents = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(adsinfo.ResidentData.Data);
                }
                else
                {
                    mftr.ZoneIdContents = "(Zone.Identifier data is non-resident)";
                }
            }
        }

        mftr.ReferenceCount = fr.GetReferenceCount();

        mftr.LogfileSequenceNumber = fr.LogSequenceNumber;

        var oid = (ObjectId_)fr.Attributes.SingleOrDefault(t =>
            t.AttributeType == AttributeType.VolumeVersionObjectId);

        if (oid != null)
        {
            mftr.ObjectIdFileDroid = oid.ObjectId.ToString();
        }

        var lus = (LoggedUtilityStream)fr.Attributes.FirstOrDefault(t =>
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

        var si = (StandardInfo)fr.Attributes.SingleOrDefault(t =>
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

        if (includeResident && adsinfo == null)
        {
            PopulateResidentData(mftr, fr, residentExt, residentMaxSize);
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

    

    private enum FileType
    {
        Mft = 0,
        LogFile = 1,
        UsnJournal = 2,
        Boot = 3,
        Sds = 4,
        I30 = 5,
        Unknown = 99
    }
}

