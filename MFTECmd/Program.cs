using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using Fclp;
using Fclp.Internals.Extensions;
using MFT;
using MFT.Other;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace MFTECmd
{
    class Program
    {
        private static Logger _logger;

        private static string _preciseTimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff K";

        private static FluentCommandLineParser<ApplicationArguments> _fluentCommandLineParser;
        private static Mft _mft;


        static void Main(string[] args)
        {
            SetupNLog();

           _logger = LogManager.GetCurrentClassLogger();

            _fluentCommandLineParser = new FluentCommandLineParser<ApplicationArguments>
            {
                IsCaseSensitive = false
            };

            _fluentCommandLineParser.Setup(arg => arg.File)
                .As('f')
                .WithDescription("File to process. Either this or -d is required");

//            _fluentCommandLineParser.Setup(arg => arg.Directory)
//                .As('d')
//                .WithDescription("Directory to recursively process. Either this or -f is required");

            _fluentCommandLineParser.Setup(arg => arg.CsvDirectory)
                .As("csv")
                .WithDescription(
                    "Directory to save CSV ormatted results to. Be sure to include the full path in double quotes. Required");

// 
//            _fluentCommandLineParser.Setup(arg => arg.JsonDirectory)
//                .As("json")
//                .WithDescription(
//                    "Directory to save json representation to. Use --pretty for a more human readable layout");

//            _fluentCommandLineParser.Setup(arg => arg.JsonPretty)
//                .As("pretty")
//                .WithDescription(
//                    "When exporting to json, use a more human readable layout\r\n").SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.Quiet)
                .As('q')
                .WithDescription(
                    "Only show the filename being processed vs all output. Useful to speed up exporting to json and/or csv\r\n")
                .SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.DateTimeFormat)
    .As("dt")
    .WithDescription(
        "The custom date/time format to use when displaying time stamps. Default is: yyyy-MM-dd HH:mm:ss K").SetDefault("yyyy-MM-dd HH:mm:ss K");

            _fluentCommandLineParser.Setup(arg => arg.PreciseTimestamps)
   .As("mp")
   .WithDescription(
       "Display higher precision for time stamps. Default is false").SetDefault(false);


            var header =
                $"MFTECmd version {Assembly.GetExecutingAssembly().GetName().Version}" +
                "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
                "\r\nhttps://github.com/EricZimmerman/MFTECmd";
                

            var footer = @"Examples: MFTECmd.exe -f ""C:\Temp\SomeMFT""" + "\r\n\t " +
                         @" MFTECmd.exe -f ""C:\Temp\SomeMFT"" --csv ""c:\temp\out"" -q" + "\r\n\t " +
                         "\r\n\t"+
                         "  Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes\r\n";

            _fluentCommandLineParser.SetupHelp("?", "help")
                .WithHeader(header)
                .Callback(text => _logger.Info(text + "\r\n" + footer));

            var result = _fluentCommandLineParser.Parse(args);

            if (result.HelpCalled)
            {
                return;
            }

            if (result.HasErrors)
            {
                _logger.Error("");
                _logger.Error(result.ErrorText);

                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                return;
            }

            if (_fluentCommandLineParser.Object.File.IsNullOrEmpty() )
            {
                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                _logger.Warn("-f is required. Exiting");
                return;
            }

            if (_fluentCommandLineParser.Object.File.IsNullOrEmpty() == false &&
                !File.Exists(_fluentCommandLineParser.Object.File))
            {
                _logger.Warn($"File '{_fluentCommandLineParser.Object.File}' not found. Exiting");
                return;
            }

            if (_fluentCommandLineParser.Object.CsvDirectory.IsNullOrEmpty() )
            {
                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                _logger.Warn("--csv is required. Exiting");
                return;
            }

            _logger.Info(header);
            _logger.Info("");
            _logger.Info($"Command line: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}\r\n");

            if (_fluentCommandLineParser.Object.PreciseTimestamps)
            {
                _fluentCommandLineParser.Object.DateTimeFormat = _preciseTimeFormat;
            }

            if (IsAdministrator() == false)
            {
                _logger.Fatal($"Warning: Administrator privileges not found!\r\n");
            }

            var sw = new Stopwatch();
            sw.Start();

            _mft = MftFile.Load(_fluentCommandLineParser.Object.File);
            _mft.BuildFileSystem();

     //do work here

            sw.Stop();


            if (_fluentCommandLineParser.Object.Quiet)
            {
                _logger.Info("");
            }

            _logger.Info(
                $"\r\nProcessed '{_fluentCommandLineParser.Object.File}' in {sw.Elapsed.TotalSeconds:N4} seconds");

            if (Directory.Exists(_fluentCommandLineParser.Object.CsvDirectory) == false)
            {
                _logger.Warn($"Path to '{_fluentCommandLineParser.Object.CsvDirectory}' doesn't exist. Creating...");
                Directory.CreateDirectory(_fluentCommandLineParser.Object.CsvDirectory);
            }

            var outName = $"{DateTimeOffset.Now.ToString("yyyyMMddHHmmss")}_MFTECmd_Output.csv";
            var outFile = Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, outName);


            _logger.Warn($"\r\nCSV (tab separated) output will be saved to '{outFile}'");

            try
            {
                using (var sw1 = new StreamWriter(outFile,false,Encoding.UTF8))
                {
                   

                    var csv = new CsvWriter(sw1);
       
                    
                    //automap here

                    csv.WriteHeader<MFTRecordOut>();
                    csv.NextRecord();

                                var co1 = new MFTRecordOut();
            co1.FileName = _mft.RootDirectory.Name.IsNullOrEmpty() ? "." : _mft.RootDirectory.Name;

                    var pp = string.Empty;
            
            pp = _mft.RootDirectory.ParentPath;

            if (pp == ".")
            {
                pp = ".\\";
            }


            co1.ParentPath = pp;
            co1.InUse = _mft.RootDirectory.IsDeleted == false;

                    var fr = _mft.GetFileRecord(_mft.RootDirectory.Key);
                    co1.EntryNumber = fr.EntryNumber;
                    co1.SequenceNumber = fr.SequenceNumber;
              
            csv.WriteRecord(co1);
            csv.NextRecord();


                    GetCsvData(_mft.RootDirectory, csv);

                    sw1.Flush();
                }



            }
            catch (Exception ex)
            {
                _logger.Error(
                    $"Error exporting data. Error: {ex.Message}");
            }

        }

        private static void GetCsvData(DirectoryItem di, CsvWriter csv)
        {
//            var co1 = new MFTRecordOut();
//            co1.FileName = di.Name.IsNullOrEmpty() ? "." : di.Name;

            var pp = string.Empty;
            
//            pp = di.ParentPath;
//
//            if (pp == ".")
//            {
//                pp = ".\\";
//            }
//
//
//            co1.ParentPath = pp;
//            co1.InUse = di.IsDeleted == false;
//            
//              
//            csv.WriteRecord(co1);
//            csv.NextRecord();


            foreach (var directoryItem in di.SubItems)
            {
                var co = new MFTRecordOut();
                co.FileName = directoryItem.Value.Name;

                 pp = directoryItem.Value.ParentPath;

                if (pp == ".")
                {
                    pp = ".\\";
                }

                co.ParentPath = pp;
                co.InUse = directoryItem.Value.IsDeleted == false;

                Console.WriteLine(directoryItem.Key);
                var fr = _mft.GetFileRecord(directoryItem.Key);

                if (fr != null)
                {
                    co.EntryNumber = fr.EntryNumber;
                    co.SequenceNumber = fr.SequenceNumber;

                }

                //412	62527


                if (co.EntryNumber == 558)
                {
                    Debug.WriteLine(1);
                }

              
                csv.WriteRecord(co);
                csv.NextRecord();

               GetCsvData(directoryItem.Value,csv);
            }


          //  return l;
        }

        public static bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void SetupNLog()
        {
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
    }

    internal class ApplicationArguments
    {
        public string File { get; set; }
        public string Directory { get; set; }

       // public string JsonDirectory { get; set; }
        public bool JsonPretty { get; set; }
        public string CsvDirectory { get; set; }

        public string DateTimeFormat { get; set; }

        public bool PreciseTimestamps { get; set; }

        public bool Quiet { get; set; }

    }
}
