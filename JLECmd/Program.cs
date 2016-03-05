﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using ExtensionBlocks;
using Fclp;
using Fclp.Internals.Extensions;
using JumpList.Automatic;
using JumpList.Custom;
using Lnk;
using Lnk.ExtraData;
using Lnk.ShellItems;
using Microsoft.Win32;
using NLog;
using NLog.Config;
using NLog.Targets;
using ServiceStack;
using ServiceStack.Text;
using CsvWriter = CsvHelper.CsvWriter;

namespace JLECmd
{
    internal class Program
    {
        private const string SSLicenseFile = @"D:\SSLic.txt";
        private static Logger _logger;

        private static readonly string _preciseTimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff K";

        private static FluentCommandLineParser<ApplicationArguments> _fluentCommandLineParser;

        private static List<string> _failedFiles;

        private static readonly Dictionary<string, string> _macList = new Dictionary<string, string>();

        private static List<AutomaticDestination> _processedAutoFiles;
        private static List<CustomDestination> _processedCustomFiles;

        private static bool CheckForDotnet46()
        {
            using (
                var ndpKey =
                    RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                        .OpenSubKey("SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\\"))
            {
                var releaseKey = Convert.ToInt32(ndpKey?.GetValue("Release"));

                return releaseKey >= 393295;
            }
        }


        private static void Main(string[] args)
        {
            Licensing.RegisterLicenseFromFileIfExists(SSLicenseFile);

            //LoadMACs();

            SetupNLog();

            _logger = LogManager.GetCurrentClassLogger();

            if (!CheckForDotnet46())
            {
                _logger.Warn(".net 4.6 not detected. Please install .net 4.6 and try again.");
                return;
            }

            _fluentCommandLineParser = new FluentCommandLineParser<ApplicationArguments>
            {
                IsCaseSensitive = false
            };

            _fluentCommandLineParser.Setup(arg => arg.File)
                .As('f')
                .WithDescription("File to process. Either this or -d is required");

            _fluentCommandLineParser.Setup(arg => arg.Directory)
                .As('d')
                .WithDescription("Directory to recursively process. Either this or -f is required");

            _fluentCommandLineParser.Setup(arg => arg.AllFiles)
                .As("all")
                .WithDescription(
                    "When true, process all files in directory vs. only files matching *.automaticDestinations-ms or *.customDestinations-ms\r\n")
                .SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.CsvDirectory)
                .As("csv")
                .WithDescription(
                    "Directory to save CSV (tab separated) formatted results to. Be sure to include the full path in double quotes");


//            _fluentCommandLineParser.Setup(arg => arg.xHtmlDirectory)
//                .As("html")
//                .WithDescription(
//                    "Directory to save xhtml formatted results to. Be sure to include the full path in double quotes");

            _fluentCommandLineParser.Setup(arg => arg.JsonDirectory)
                .As("json")
                .WithDescription(
                    "Directory to save json representation to. Use --pretty for a more human readable layout");

            _fluentCommandLineParser.Setup(arg => arg.JsonPretty)
                .As("pretty")
                .WithDescription(
                    "When exporting to json, use a more human readable layout").SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.Quiet)
                .As('q')
                .WithDescription(
                    "When true, only show the filename being processed vs all output. Useful to speed up exporting to json and/or csv\r\n")
                .SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.IncludeLnkDetail).As("ld")
                .WithDescription(
                    "When true, include more information about auto files (for full auto details, dump lnk files using --dumpTo and process with LECmd")
                .SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.LnkDumpDirectory).As("dumpTo")
                .WithDescription(
                    "The directory to use when exporting embedded lnk files")
                .SetDefault(string.Empty);

            _fluentCommandLineParser.Setup(arg => arg.DateTimeFormat)
                .As("dt")
                .WithDescription(
                    "The custom date/time format to use when displaying time stamps. Default is: yyyy-MM-dd HH:mm:ss K")
                .SetDefault("yyyy-MM-dd HH:mm:ss K");

            _fluentCommandLineParser.Setup(arg => arg.PreciseTimestamps)
                .As("mp")
                .WithDescription(
                    "When true, display higher precision for time stamps. Default is false").SetDefault(false);


            var header =
                $"JLECmd version {Assembly.GetExecutingAssembly().GetName().Version}" +
                "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
                "\r\nhttps://github.com/EricZimmerman/JLECmd";

            var footer = @"Examples: JLECmd.exe -f ""C:\Temp\f01b4d95cf55d32a.customDestinations-ms""" + "\r\n\t " +
                         @" JLECmd.exe -f ""C:\Temp\f01b4d95cf55d32a.customDestinations-ms"" --json ""D:\jsonOutput"" --jsonpretty" +
                         "\r\n\t " +
                         @" JLECmd.exe -d ""C:\CustomDestinations"" --csv ""c:\temp\jumplist_out.tsv"" --html c:\temp -q" +
                         "\r\n\t " +
                         @" JLECmd.exe -d ""C:\Temp"" --all" + "\r\n\t" +
                         "\r\n\t" +
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

            if (UsefulExtension.IsNullOrEmpty(_fluentCommandLineParser.Object.File) &&
                UsefulExtension.IsNullOrEmpty(_fluentCommandLineParser.Object.Directory))
            {
                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                _logger.Warn("Either -f or -d is required. Exiting");
                return;
            }

            if (UsefulExtension.IsNullOrEmpty(_fluentCommandLineParser.Object.File) == false &&
                !File.Exists(_fluentCommandLineParser.Object.File))
            {
                _logger.Warn($"File '{_fluentCommandLineParser.Object.File}' not found. Exiting");
                return;
            }

            if (UsefulExtension.IsNullOrEmpty(_fluentCommandLineParser.Object.Directory) == false &&
                !Directory.Exists(_fluentCommandLineParser.Object.Directory))
            {
                _logger.Warn($"Directory '{_fluentCommandLineParser.Object.Directory}' not found. Exiting");
                return;
            }

            _logger.Info(header);
            _logger.Info("");
            _logger.Info($"Command line: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}\r\n");

            if (_fluentCommandLineParser.Object.PreciseTimestamps)
            {
                _fluentCommandLineParser.Object.DateTimeFormat = _preciseTimeFormat;
            }

            _processedAutoFiles = new List<AutomaticDestination>();
            _processedCustomFiles = new List<CustomDestination>();

            if (_fluentCommandLineParser.Object.File?.Length > 0)
            {
                if (IsAutomaticDestinationFile(_fluentCommandLineParser.Object.File))
                {
                    try
                    {
                        AutomaticDestination adjl = null;
                        adjl = ProcessAutoFile(_fluentCommandLineParser.Object.File);
                        if (adjl != null)
                        {
                            _processedAutoFiles.Add(adjl);
                        }
                    }
                    catch (UnauthorizedAccessException ua)
                    {
                        _logger.Error(
                            $"Unable to access '{_fluentCommandLineParser.Object.File}'. Are you running as an administrator? Error: {ua.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(
                            $"Error getting jump lists. Error: {ex.Message}");
                        return;
                    }
                }
                else
                {
                    try
                    {
                        CustomDestination cdjl = null;
                        cdjl = ProcessCustomFile(_fluentCommandLineParser.Object.File);
                        if (cdjl != null)
                        {
                            _processedCustomFiles.Add(cdjl);
                        }
                    }
                    catch (UnauthorizedAccessException ua)
                    {
                        _logger.Error(
                            $"Unable to access '{_fluentCommandLineParser.Object.File}'. Are you running as an administrator? Error: {ua.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(
                            $"Error getting jump lists. Error: {ex.Message}");
                        return;
                    }
                }
            }
            else
            {
                _logger.Info($"Looking for jump list files in '{_fluentCommandLineParser.Object.Directory}'");
                _logger.Info("");

                var jumpFiles = new List<string>();

                _failedFiles = new List<string>();

                try
                {
                    var mask = "*.*Destinations-ms";
                    if (_fluentCommandLineParser.Object.AllFiles)
                    {
                        mask = "*";
                    }

                    jumpFiles.AddRange(Directory.GetFiles(_fluentCommandLineParser.Object.Directory, mask,
                        SearchOption.AllDirectories));
                }
                catch (UnauthorizedAccessException ua)
                {
                    _logger.Error(
                        $"Unable to access '{_fluentCommandLineParser.Object.Directory}'. Error message: {ua.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Error(
                        $"Error getting jump list files in '{_fluentCommandLineParser.Object.Directory}'. Error: {ex.Message}");
                    return;
                }

                _logger.Info($"Found {jumpFiles.Count:N0} files");
                _logger.Info("");

                var sw = new Stopwatch();
                sw.Start();

                foreach (var file in jumpFiles)
                {
                    if (IsAutomaticDestinationFile(file))
                    {
                        AutomaticDestination adjl = null;
                        adjl = ProcessAutoFile(file);
                        if (adjl != null)
                        {
                            _processedAutoFiles.Add(adjl);
                        }
                    }
                    else
                    {
                        CustomDestination cdjl = null;
                        cdjl = ProcessCustomFile(file);
                        if (cdjl != null)
                        {
                            _processedCustomFiles.Add(cdjl);
                        }
                    }
                }

                sw.Stop();

                if (_fluentCommandLineParser.Object.Quiet)
                {
                    _logger.Info("");
                }

                _logger.Info(
                    $"Processed {jumpFiles.Count - _failedFiles.Count:N0} out of {jumpFiles.Count:N0} files in {sw.Elapsed.TotalSeconds:N4} seconds");
                if (_failedFiles.Count > 0)
                {
                    _logger.Info("");
                    _logger.Warn("Failed files");
                    foreach (var failedFile in _failedFiles)
                    {
                        _logger.Info($"  {failedFile}");
                    }
                }
            }


            //export lnks if requested
            if (_fluentCommandLineParser.Object.LnkDumpDirectory.Length > 0)
            {
                _logger.Info("");
                _logger.Warn(
                    $"Dumping lnk files to '{_fluentCommandLineParser.Object.LnkDumpDirectory}'");

                if (Directory.Exists(_fluentCommandLineParser.Object.LnkDumpDirectory) == false)
                {
                    Directory.CreateDirectory(_fluentCommandLineParser.Object.LnkDumpDirectory);
                }

                foreach (var processedCustomFile in _processedCustomFiles)
                {
                    foreach (var entry in processedCustomFile.Entries)
                    {
                        if (entry.LnkFiles.Count == 0)
                        {
                            continue;
                        }

                        var outDir = Path.Combine(_fluentCommandLineParser.Object.LnkDumpDirectory,
                            Path.GetFileName(processedCustomFile.SourceFile));

                        if (Directory.Exists(outDir) == false)
                        {
                            Directory.CreateDirectory(outDir);
                        }

                        entry.DumpAllLnkFiles(outDir, processedCustomFile.AppId.AppId);
                    }
                }

                foreach (var automaticDestination in _processedAutoFiles)
                {
                    if (automaticDestination.DestListCount == 0)
                    {
                        continue;
                    }
                    var outDir = Path.Combine(_fluentCommandLineParser.Object.LnkDumpDirectory,
                        Path.GetFileName(automaticDestination.SourceFile));

                    if (Directory.Exists(outDir) == false)
                    {
                        Directory.CreateDirectory(outDir);
                    }

                    automaticDestination.DumpAllLnkFiles(outDir);
                }
            }


            if (_processedAutoFiles.Count > 0)
            {
                ExportAuto();
            }

            if (_processedCustomFiles.Count > 0)
            {
                ExportCustom();
            }
        }

        private static void ExportCustom()
        {
            _logger.Info("");

            try
            {
                CsvWriter csvCustom = null;
                StreamWriter swCustom = null;

                if (_fluentCommandLineParser.Object.CsvDirectory?.Length > 0)
                {
                    var outName = $"{DateTimeOffset.Now.ToString("yyyyMMddHHmmss")}_CustomDestinations.tsv";
                    var outFile = Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, outName);


                    _logger.Warn(
                        $"CustomDestinations CSV (tab separated) output will be saved to '{outFile}'");

                    try
                    {
                        swCustom = new StreamWriter(outFile);
                        csvCustom = new CsvWriter(swCustom);
                        csvCustom.Configuration.Delimiter = $"{'\t'}";
                        csvCustom.WriteHeader(typeof(CustomCsvOut));
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(
                            $"Unable to write to '{_fluentCommandLineParser.Object.CsvDirectory}'. Custom CSV export canceled. Error: {ex.Message}");
                    }
                }

                if (_fluentCommandLineParser.Object.JsonDirectory?.Length > 0)
                {
                    _logger.Warn($"Saving Custom json output to '{_fluentCommandLineParser.Object.JsonDirectory}'");
                }


                XmlTextWriter xml = null;

                if (_fluentCommandLineParser.Object.xHtmlDirectory?.Length > 0)
                {
                    var outDir = Path.Combine(_fluentCommandLineParser.Object.xHtmlDirectory,
                        $"{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")}_JLECmd_Output_for_{_fluentCommandLineParser.Object.xHtmlDirectory.Replace(@":\", "_").Replace(@"\", "_")}");

                    if (Directory.Exists(outDir) == false)
                    {
                        Directory.CreateDirectory(outDir);
                    }

                    //                        File.WriteAllText(Path.Combine(outDir, "normalize.css"), Resources.normalize);
                    //                        File.WriteAllText(Path.Combine(outDir, "style.css"), Resources.style);

                    var outFile = Path.Combine(_fluentCommandLineParser.Object.xHtmlDirectory, outDir, "index.xhtml");

                    _logger.Warn($"Saving HTML output to '{outFile}'");

                    xml = new XmlTextWriter(outFile, Encoding.UTF8)
                    {
                        Formatting = Formatting.Indented,
                        Indentation = 4
                    };

                    xml.WriteStartDocument();

                    xml.WriteProcessingInstruction("xml-stylesheet", "href=\"normalize.css\"");
                    xml.WriteProcessingInstruction("xml-stylesheet", "href=\"style.css\"");

                    xml.WriteStartElement("document");
                }

                foreach (var processedFile in _processedCustomFiles)
                {
                    if (_fluentCommandLineParser.Object.JsonDirectory?.Length > 0)
                    {
                        SaveJsonCustom(processedFile, _fluentCommandLineParser.Object.JsonPretty,
                            _fluentCommandLineParser.Object.JsonDirectory);
                    }

                    var o = GetCustomCsvFormat(processedFile);

                    try
                    {
                        csvCustom?.WriteRecords(o);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(
                            $"Error writing record for '{processedFile.SourceFile}' to '{_fluentCommandLineParser.Object.CsvDirectory}'. Error: {ex.Message}");
                    }
                }

                //

                //
                //                        //XHTML
                //                        xml?.WriteStartElement("Container");
                //                        xml?.WriteElementString("SourceFile", o.SourceFile);
                //                        xml?.WriteElementString("SourceCreated", o.SourceCreated.ToString());
                //                        xml?.WriteElementString("SourceModified", o.SourceModified.ToString());
                //                        xml?.WriteElementString("SourceAccessed", o.SourceAccessed.ToString());
                //                        xml?.WriteElementString("TargetCreated", o.TargetCreated.ToString());
                //                        xml?.WriteElementString("TargetModified", o.TargetModified.ToString());
                //                        xml?.WriteElementString("TargetAccessed", o.TargetModified.ToString());
                //                        xml?.WriteElementString("FileSize", o.FileSize.ToString());
                //                        xml?.WriteElementString("RelativePath", o.RelativePath);
                //                        xml?.WriteElementString("WorkingDirectory", o.WorkingDirectory);
                //                        xml?.WriteElementString("FileAttributes", o.FileAttributes);
                //                        xml?.WriteElementString("HeaderFlags", o.HeaderFlags);
                //                        xml?.WriteElementString("DriveType", o.DriveType);
                //                        xml?.WriteElementString("DriveSerialNumber", o.DriveSerialNumber);
                //                        xml?.WriteElementString("DriveLabel", o.DriveLabel);
                //                        xml?.WriteElementString("LocalPath", o.LocalPath);
                //                        xml?.WriteElementString("CommonPath", o.CommonPath);
                //
                //                        xml?.WriteElementString("TargetIDAbsolutePath", o.TargetIDAbsolutePath);
                //
                //                        xml?.WriteElementString("TargetMFTEntryNumber", $"{o.TargetMFTEntryNumber}");
                //                        xml?.WriteElementString("TargetMFTSequenceNumber", $"{o.TargetMFTSequenceNumber}");
                //
                //                        xml?.WriteElementString("MachineID", o.MachineID);
                //                        xml?.WriteElementString("MachineMACAddress", o.MachineMACAddress);
                //                        xml?.WriteElementString("MACVendor", o.MACVendor);
                //                        xml?.WriteElementString("TrackerCreatedOn", o.TrackerCreatedOn.ToString());
                //
                //                        xml?.WriteElementString("ExtraBlocksPresent", o.ExtraBlocksPresent);
                //
                //                        xml?.WriteEndElement();
                //
                //                        if (_fluentCommandLineParser.Object.XmlDirectory?.Length > 0)
                //                        {
                //                            SaveXML(o, _fluentCommandLineParser.Object.XmlDirectory);
                //                        }
                //                    }


                //Close CSV stuff
                swCustom?.Flush();
                swCustom?.Close();

                //Close XML
                xml?.WriteEndElement();
                xml?.WriteEndDocument();
                xml?.Flush();
            }
            catch (Exception ex)
            {
                _logger.Error(
                    $"Error exporting Custom Destinations data! Error: {ex.Message}");
            }
        }

        private static void ExportAuto()
        {
            _logger.Info("");

            try
            {
                CsvWriter csvAuto = null;
                StreamWriter swAuto = null;

                if (_fluentCommandLineParser.Object.CsvDirectory?.Length > 0)
                {
                    var outName = $"{DateTimeOffset.Now.ToString("yyyyMMddHHmmss")}_AutomaticDestinations.tsv";
                    var outFile = Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, outName);


                    _logger.Warn(
                        $"AutomaticDestinations CSV (tab separated) output will be saved to '{outFile}'");

                    try
                    {
                        swAuto = new StreamWriter(outFile);
                        csvAuto = new CsvWriter(swAuto);
                        csvAuto.Configuration.Delimiter = $"{'\t'}";
                        csvAuto.WriteHeader(typeof(AutoCsvOut));
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(
                            $"Unable to write to '{_fluentCommandLineParser.Object.CsvDirectory}'. Automatic CSV export canceled. Error: {ex.Message}");
                    }
                }

                if (_fluentCommandLineParser.Object.JsonDirectory?.Length > 0)
                {
                    _logger.Warn($"Saving Automatic json output to '{_fluentCommandLineParser.Object.JsonDirectory}'");
                }


                XmlTextWriter xml = null;

                if (_fluentCommandLineParser.Object.xHtmlDirectory?.Length > 0)
                {
                    var outDir = Path.Combine(_fluentCommandLineParser.Object.xHtmlDirectory,
                        $"{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")}_JLECmd_Output_for_{_fluentCommandLineParser.Object.xHtmlDirectory.Replace(@":\", "_").Replace(@"\", "_")}");

                    if (Directory.Exists(outDir) == false)
                    {
                        Directory.CreateDirectory(outDir);
                    }

                    //                        File.WriteAllText(Path.Combine(outDir, "normalize.css"), Resources.normalize);
                    //                        File.WriteAllText(Path.Combine(outDir, "style.css"), Resources.style);

                    var outFile = Path.Combine(_fluentCommandLineParser.Object.xHtmlDirectory, outDir, "index.xhtml");

                    _logger.Warn($"Saving HTML output to '{outFile}'");

                    xml = new XmlTextWriter(outFile, Encoding.UTF8)
                    {
                        Formatting = Formatting.Indented,
                        Indentation = 4
                    };

                    xml.WriteStartDocument();

                    xml.WriteProcessingInstruction("xml-stylesheet", "href=\"normalize.css\"");
                    xml.WriteProcessingInstruction("xml-stylesheet", "href=\"style.css\"");

                    xml.WriteStartElement("document");
                }

                foreach (var processedFile in _processedAutoFiles)
                {
                    if (_fluentCommandLineParser.Object.JsonDirectory?.Length > 0)
                    {
                        SaveJsonAuto(processedFile, _fluentCommandLineParser.Object.JsonPretty,
                            _fluentCommandLineParser.Object.JsonDirectory);
                    }

                    var o = GetAutoCsvFormat(processedFile);

                    try
                    {
                        csvAuto?.WriteRecords(o);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(
                            $"Error writing record for '{processedFile.SourceFile}' to '{_fluentCommandLineParser.Object.CsvDirectory}'. Error: {ex.Message}");
                    }
                }

                //

                //
                //                        //XHTML
                //                        xml?.WriteStartElement("Container");
                //                        xml?.WriteElementString("SourceFile", o.SourceFile);
                //                        xml?.WriteElementString("SourceCreated", o.SourceCreated.ToString());
                //                        xml?.WriteElementString("SourceModified", o.SourceModified.ToString());
                //                        xml?.WriteElementString("SourceAccessed", o.SourceAccessed.ToString());
                //                        xml?.WriteElementString("TargetCreated", o.TargetCreated.ToString());
                //                        xml?.WriteElementString("TargetModified", o.TargetModified.ToString());
                //                        xml?.WriteElementString("TargetAccessed", o.TargetModified.ToString());
                //                        xml?.WriteElementString("FileSize", o.FileSize.ToString());
                //                        xml?.WriteElementString("RelativePath", o.RelativePath);
                //                        xml?.WriteElementString("WorkingDirectory", o.WorkingDirectory);
                //                        xml?.WriteElementString("FileAttributes", o.FileAttributes);
                //                        xml?.WriteElementString("HeaderFlags", o.HeaderFlags);
                //                        xml?.WriteElementString("DriveType", o.DriveType);
                //                        xml?.WriteElementString("DriveSerialNumber", o.DriveSerialNumber);
                //                        xml?.WriteElementString("DriveLabel", o.DriveLabel);
                //                        xml?.WriteElementString("LocalPath", o.LocalPath);
                //                        xml?.WriteElementString("CommonPath", o.CommonPath);
                //
                //                        xml?.WriteElementString("TargetIDAbsolutePath", o.TargetIDAbsolutePath);
                //
                //                        xml?.WriteElementString("TargetMFTEntryNumber", $"{o.TargetMFTEntryNumber}");
                //                        xml?.WriteElementString("TargetMFTSequenceNumber", $"{o.TargetMFTSequenceNumber}");
                //
                //                        xml?.WriteElementString("MachineID", o.MachineID);
                //                        xml?.WriteElementString("MachineMACAddress", o.MachineMACAddress);
                //                        xml?.WriteElementString("MACVendor", o.MACVendor);
                //                        xml?.WriteElementString("TrackerCreatedOn", o.TrackerCreatedOn.ToString());
                //
                //                        xml?.WriteElementString("ExtraBlocksPresent", o.ExtraBlocksPresent);
                //
                //                        xml?.WriteEndElement();
                //
                //                        if (_fluentCommandLineParser.Object.XmlDirectory?.Length > 0)
                //                        {
                //                            SaveXML(o, _fluentCommandLineParser.Object.XmlDirectory);
                //                        }
                //                    }


                //Close CSV stuff
                swAuto?.Flush();
                swAuto?.Close();

                //Close XML
                xml?.WriteEndElement();
                xml?.WriteEndDocument();
                xml?.Flush();
            }
            catch (Exception ex)
            {
                _logger.Error(
                    $"Error exporting Automatic Destinations data! Error: {ex.Message}");
            }
        }


        private static bool IsAutomaticDestinationFile(string file)
        {
            const ulong signature = 0xe11ab1a1e011cfd0;

            var sig = BitConverter.ToUInt64(File.ReadAllBytes(file), 0);

            return signature == sig;
        }

        private static List<CustomCsvOut> GetCustomCsvFormat(CustomDestination cust)
        {
            var csList = new List<CustomCsvOut>();

            var fs = new FileInfo(cust.SourceFile);
            var ct = DateTimeOffset.FromFileTime(fs.CreationTime.ToFileTime()).ToUniversalTime();
            var mt = DateTimeOffset.FromFileTime(fs.LastWriteTime.ToFileTime()).ToUniversalTime();
            var at = DateTimeOffset.FromFileTime(fs.LastAccessTime.ToFileTime()).ToUniversalTime();

            foreach (var entry in cust.Entries)
            {
                foreach (var lnk in entry.LnkFiles)
                {
                    var csOut = new CustomCsvOut
                    {
                        SourceFile = cust.SourceFile,
                        SourceCreated = ct.ToString(_fluentCommandLineParser.Object.DateTimeFormat),
                        SourceModified = mt.ToString(_fluentCommandLineParser.Object.DateTimeFormat),
                        SourceAccessed = at.ToString(_fluentCommandLineParser.Object.DateTimeFormat),
                        AppId = cust.AppId.AppId,
                        AppIdDescription = cust.AppId.Description,
                        EntryName = entry.Name,
                        TargetCreated =
                            lnk.Header.TargetCreationDate.Year == 1601
                                ? string.Empty
                                : lnk.Header.TargetCreationDate.ToString(_fluentCommandLineParser.Object.DateTimeFormat),
                        TargetModified =
                            lnk.Header.TargetModificationDate.Year == 1601
                                ? string.Empty
                                : lnk.Header.TargetModificationDate.ToString(
                                    _fluentCommandLineParser.Object.DateTimeFormat),
                        TargetAccessed =
                            lnk.Header.TargetLastAccessedDate.Year == 1601
                                ? string.Empty
                                : lnk.Header.TargetLastAccessedDate.ToString(
                                    _fluentCommandLineParser.Object.DateTimeFormat),
                        CommonPath = lnk.CommonPath,
                        DriveLabel = lnk.VolumeInfo?.VolumeLabel,
                        DriveSerialNumber = lnk.VolumeInfo?.DriveSerialNumber,
                        DriveType =
                            lnk.VolumeInfo == null ? "(None)" : GetDescriptionFromEnumValue(lnk.VolumeInfo.DriveType),
                        FileAttributes = lnk.Header.FileAttributes.ToString(),
                        FileSize = lnk.Header.FileSize,
                        HeaderFlags = lnk.Header.DataFlags.ToString(),
                        LocalPath = lnk.LocalPath,
                        RelativePath = lnk.RelativePath
                    };

                    if (lnk.TargetIDs?.Count > 0)
                    {
                        csOut.TargetIDAbsolutePath = GetAbsolutePathFromTargetIDs(lnk.TargetIDs);
                    }

                    csOut.WorkingDirectory = lnk.WorkingDirectory;

                    var ebPresent = string.Empty;

                    if (lnk.ExtraBlocks.Count > 0)
                    {
                        var names = new List<string>();

                        foreach (var extraDataBase in lnk.ExtraBlocks)
                        {
                            names.Add(extraDataBase.GetType().Name);
                        }

                        ebPresent = string.Join(", ", names);
                    }

                    csOut.ExtraBlocksPresent = ebPresent;

                    var tnb = lnk.ExtraBlocks.SingleOrDefault(t => t.GetType().Name.ToUpper() == "TRACKERDATABASEBLOCK");

                    if (tnb != null)
                    {
                        var tnbBlock = tnb as TrackerDataBaseBlock;

                        csOut.TrackerCreatedOn =
                            tnbBlock?.CreationTime.ToString(_fluentCommandLineParser.Object.DateTimeFormat);

                        csOut.MachineID = tnbBlock?.MachineId;
                        csOut.MachineMACAddress = tnbBlock?.MacAddress;
                    }

                    if (lnk.TargetIDs?.Count > 0)
                    {
                        var si = lnk.TargetIDs.Last();

                        if (si.ExtensionBlocks?.Count > 0)
                        {
                            var eb = si.ExtensionBlocks?.Last();
                            if (eb is Beef0004)
                            {
                                var eb4 = eb as Beef0004;
                                if (eb4.MFTInformation.MFTEntryNumber != null)
                                {
                                    csOut.TargetMFTEntryNumber =
                                        $"0x{eb4.MFTInformation.MFTEntryNumber.Value.ToString("X")}";
                                }

                                if (eb4.MFTInformation.MFTSequenceNumber != null)
                                {
                                    csOut.TargetMFTSequenceNumber =
                                        $"0x{eb4.MFTInformation.MFTSequenceNumber.Value.ToString("X")}";
                                }
                            }
                        }
                    }

                    csList.Add(csOut);
                }
            }


            return csList;
        }

        private static List<AutoCsvOut> GetAutoCsvFormat(AutomaticDestination auto)
        {
            var csList = new List<AutoCsvOut>();

            var fs = new FileInfo(auto.SourceFile);
            var ct = DateTimeOffset.FromFileTime(fs.CreationTime.ToFileTime()).ToUniversalTime();
            var mt = DateTimeOffset.FromFileTime(fs.LastWriteTime.ToFileTime()).ToUniversalTime();
            var at = DateTimeOffset.FromFileTime(fs.LastAccessTime.ToFileTime()).ToUniversalTime();

            foreach (var destListEntry in auto.DestListEntries)
            {
                var csOut = new AutoCsvOut
                {
                    SourceFile = auto.SourceFile,
                    SourceCreated = ct.ToString(_fluentCommandLineParser.Object.DateTimeFormat),
                    SourceModified = mt.ToString(_fluentCommandLineParser.Object.DateTimeFormat),
                    SourceAccessed = at.ToString(_fluentCommandLineParser.Object.DateTimeFormat),
                    AppId = auto.AppId.AppId,
                    AppIdDescription = auto.AppId.Description,
                    DestListVersion = auto.DestListVersion.ToString(),
                    LastUsedEntryNumber = auto.LastUsedEntryNumber.ToString(),
                    EntryNumber = destListEntry.EntryNumber.ToString(),
                    CreationTime =
                        destListEntry.CreatedOn.Year == 1582
                            ? string.Empty
                            : destListEntry.CreatedOn.ToString(_fluentCommandLineParser.Object.DateTimeFormat),
                    LastModified = destListEntry.LastModified.ToString(_fluentCommandLineParser.Object.DateTimeFormat),
                    Hostname = destListEntry.Hostname,
                    MacAddress =
                        destListEntry.MacAddress == "00:00:00:00:00:00" ? string.Empty : destListEntry.MacAddress,
                    Path = destListEntry.Path,
                    PinStatus = destListEntry.Pinned.ToString(),
                    FileBirthDroid =
                        destListEntry.FileBirthDroid.ToString() == "00000000-0000-0000-0000-000000000000"
                            ? string.Empty
                            : destListEntry.FileBirthDroid.ToString(),
                    FileDroid =
                        destListEntry.FileDroid.ToString() == "00000000-0000-0000-0000-000000000000"
                            ? string.Empty
                            : destListEntry.FileDroid.ToString(),
                    VolumeBirthDroid =
                        destListEntry.VolumeBirthDroid.ToString() == "00000000-0000-0000-0000-000000000000"
                            ? string.Empty
                            : destListEntry.VolumeBirthDroid.ToString(),
                    VolumeDroid =
                        destListEntry.VolumeDroid.ToString() == "00000000-0000-0000-0000-000000000000"
                            ? string.Empty
                            : destListEntry.VolumeDroid.ToString(),
                    TargetCreated =
                        destListEntry.Lnk.Header.TargetCreationDate.Year == 1601
                            ? string.Empty
                            : destListEntry.Lnk.Header.TargetCreationDate.ToString(
                                _fluentCommandLineParser.Object.DateTimeFormat),
                    TargetModified =
                        destListEntry.Lnk.Header.TargetModificationDate.Year == 1601
                            ? string.Empty
                            : destListEntry.Lnk.Header.TargetModificationDate.ToString(
                                _fluentCommandLineParser.Object.DateTimeFormat),
                    TargetAccessed =
                        destListEntry.Lnk.Header.TargetLastAccessedDate.Year == 1601
                            ? string.Empty
                            : destListEntry.Lnk.Header.TargetLastAccessedDate.ToString(
                                _fluentCommandLineParser.Object.DateTimeFormat),
                    CommonPath = destListEntry.Lnk.CommonPath,
                    DriveLabel = destListEntry.Lnk.VolumeInfo?.VolumeLabel,
                    DriveSerialNumber = destListEntry.Lnk.VolumeInfo?.DriveSerialNumber,
                    DriveType =
                        destListEntry.Lnk.VolumeInfo == null
                            ? "(None)"
                            : GetDescriptionFromEnumValue(destListEntry.Lnk.VolumeInfo.DriveType),
                    FileAttributes = destListEntry.Lnk.Header.FileAttributes.ToString(),
                    FileSize = destListEntry.Lnk.Header.FileSize,
                    HeaderFlags = destListEntry.Lnk.Header.DataFlags.ToString(),
                    LocalPath = destListEntry.Lnk.LocalPath,
                    RelativePath = destListEntry.Lnk.RelativePath
                };

                if (destListEntry.Lnk.TargetIDs?.Count > 0)
                {
                    csOut.TargetIDAbsolutePath = GetAbsolutePathFromTargetIDs(destListEntry.Lnk.TargetIDs);
                }

                csOut.WorkingDirectory = destListEntry.Lnk.WorkingDirectory;

                var ebPresent = string.Empty;

                if (destListEntry.Lnk.ExtraBlocks.Count > 0)
                {
                    var names = new List<string>();

                    foreach (var extraDataBase in destListEntry.Lnk.ExtraBlocks)
                    {
                        names.Add(extraDataBase.GetType().Name);
                    }

                    ebPresent = string.Join(", ", names);
                }

                csOut.ExtraBlocksPresent = ebPresent;

                var tnb =
                    destListEntry.Lnk.ExtraBlocks.SingleOrDefault(
                        t => t.GetType().Name.ToUpper() == "TRACKERDATABASEBLOCK");

                if (tnb != null)
                {
                    var tnbBlock = tnb as TrackerDataBaseBlock;

                    csOut.TrackerCreatedOn =
                        tnbBlock?.CreationTime.ToString(_fluentCommandLineParser.Object.DateTimeFormat);

                    csOut.MachineID = tnbBlock?.MachineId;
                    csOut.MachineMACAddress = tnbBlock?.MacAddress;
                }

                if (destListEntry.Lnk.TargetIDs?.Count > 0)
                {
                    var si = destListEntry.Lnk.TargetIDs.Last();

                    if (si.ExtensionBlocks?.Count > 0)
                    {
                        var eb = si.ExtensionBlocks?.Last();
                        if (eb is Beef0004)
                        {
                            var eb4 = eb as Beef0004;
                            if (eb4.MFTInformation.MFTEntryNumber != null)
                            {
                                csOut.TargetMFTEntryNumber =
                                    $"0x{eb4.MFTInformation.MFTEntryNumber.Value.ToString("X")}";
                            }

                            if (eb4.MFTInformation.MFTSequenceNumber != null)
                            {
                                csOut.TargetMFTSequenceNumber =
                                    $"0x{eb4.MFTInformation.MFTSequenceNumber.Value.ToString("X")}";
                            }
                        }
                    }
                }

                csList.Add(csOut);
            }


            return csList;
        }

        private static void DumpToJsonAuto(AutomaticDestination auto, bool pretty, string outFile)
        {
            if (pretty)
            {
                File.WriteAllText(outFile, auto.Dump());
            }
            else
            {
                File.WriteAllText(outFile, auto.ToJson());
            }
        }

        private static void DumpToJsonCustom(CustomDestination cust, bool pretty, string outFile)
        {
            if (pretty)
            {
                File.WriteAllText(outFile, cust.Dump());
            }
            else
            {
                File.WriteAllText(outFile, cust.ToJson());
            }
        }

        private static void SaveJsonCustom(CustomDestination cust, bool pretty, string outDir)
        {
            try
            {
                if (Directory.Exists(outDir) == false)
                {
                    Directory.CreateDirectory(outDir);
                }

                var outName =
                    $"{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")}_{Path.GetFileName(cust.SourceFile)}.json";
                var outFile = Path.Combine(outDir, outName);

                DumpToJsonCustom(cust, pretty, outFile);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error exporting json for '{cust.SourceFile}'. Error: {ex.Message}");
            }
        }

        private static void SaveJsonAuto(AutomaticDestination auto, bool pretty, string outDir)
        {
            try
            {
                if (Directory.Exists(outDir) == false)
                {
                    Directory.CreateDirectory(outDir);
                }

                var outName =
                    $"{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")}_{Path.GetFileName(auto.SourceFile)}.json";
                var outFile = Path.Combine(outDir, outName);

                DumpToJsonAuto(auto, pretty, outFile);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error exporting json for '{auto.SourceFile}'. Error: {ex.Message}");
            }
        }

        private static string GetDescriptionFromEnumValue(Enum value)
        {
            var attribute = value.GetType()
                .GetField(value.ToString())
                .GetCustomAttributes(typeof(DescriptionAttribute), false)
                .SingleOrDefault() as DescriptionAttribute;
            return attribute == null ? value.ToString() : attribute.Description;
        }

        private static string GetAbsolutePathFromTargetIDs(List<ShellBag> ids)
        {
            var absPath = string.Empty;

            foreach (var shellBag in ids)
            {
                absPath += shellBag.Value + @"\";
            }

            absPath = absPath.Substring(0, absPath.Length - 1);

            return absPath;
        }

        private static AutomaticDestination ProcessAutoFile(string jlFile)
        {
            if (_fluentCommandLineParser.Object.Quiet == false)
            {
                _logger.Warn($"Processing '{jlFile}'");
                _logger.Info("");
            }

            var sw = new Stopwatch();
            sw.Start();

            try
            {
                var autoDest = JumpList.JumpList.LoadAutoJumplist(jlFile);

                if (_fluentCommandLineParser.Object.Quiet == false)
                {
                    _logger.Error($"Source file: {autoDest.SourceFile}");

                    _logger.Info("");

                    _logger.Warn("--- AppId information ---");
                    _logger.Info($"  AppID: {autoDest.AppId.AppId}");
                    _logger.Info($"  Description: {autoDest.AppId.Description}");
                    _logger.Info("");

                    _logger.Warn("--- DestList information ---");
                    _logger.Info($"  Expected DestList entries:  {autoDest.DestListCount:N0}");
                    _logger.Info($"  Actual DestList entriesL {autoDest.DestListCount.ToString("N0")}");
                    _logger.Info($"  DestList version: {autoDest.DestListVersion}");


                    _logger.Info("");

                    _logger.Warn("--- DestList entries ---");
                    foreach (var autoDestList in autoDest.DestListEntries)
                    {
                        _logger.Info($"Entry #: {autoDestList.EntryNumber}");
                        _logger.Info($"  Path: {autoDestList.Path}");
                        _logger.Info($"Pinned: {autoDestList.Pinned}");
                        _logger.Info(
                            $"  Created on: {autoDestList.CreatedOn.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
                        _logger.Info(
                            $"  Last modified: {autoDestList.LastModified.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
                        _logger.Info($"  Hostname: {autoDestList.Hostname}");
                        _logger.Info(
                            $"  Mac Address: {(autoDestList.MacAddress == "00:00:00:00:00:00" ? string.Empty : autoDestList.MacAddress)}");

                        var tc = autoDestList.Lnk.Header.TargetCreationDate.Year == 1601
                            ? ""
                            : autoDestList.Lnk.Header.TargetCreationDate.ToString(
                                _fluentCommandLineParser.Object.DateTimeFormat);
                        var tm = autoDestList.Lnk.Header.TargetModificationDate.Year == 1601
                            ? ""
                            : autoDestList.Lnk.Header.TargetModificationDate.ToString(
                                _fluentCommandLineParser.Object.DateTimeFormat);
                        var ta = autoDestList.Lnk.Header.TargetLastAccessedDate.Year == 1601
                            ? ""
                            : autoDestList.Lnk.Header.TargetLastAccessedDate.ToString(
                                _fluentCommandLineParser.Object.DateTimeFormat);

                        _logger.Warn("--- Lnk information ---");
                        _logger.Info($"  Lnk target created: {tc}");
                        _logger.Info($"  Lnk target modified: {tm}");
                        _logger.Info($"  Lnk target accessed: {ta}");


                        if (_fluentCommandLineParser.Object.IncludeLnkDetail)
                        {
                            if ((autoDestList.Lnk.Header.DataFlags & Header.DataFlag.HasName) == Header.DataFlag.HasName)
                            {
                                _logger.Info($"  Name: {autoDestList.Lnk.Name}");
                            }

                            if ((autoDestList.Lnk.Header.DataFlags & Header.DataFlag.HasRelativePath) ==
                                Header.DataFlag.HasRelativePath)
                            {
                                _logger.Info($"  Relative Path: {autoDestList.Lnk.RelativePath}");
                            }

                            if ((autoDestList.Lnk.Header.DataFlags & Header.DataFlag.HasWorkingDir) ==
                                Header.DataFlag.HasWorkingDir)
                            {
                                _logger.Info($"  Working Directory: {autoDestList.Lnk.WorkingDirectory}");
                            }

                            if ((autoDestList.Lnk.Header.DataFlags & Header.DataFlag.HasArguments) ==
                                Header.DataFlag.HasArguments)
                            {
                                _logger.Info($"  Arguments: {autoDestList.Lnk.Arguments}");
                            }

                            if ((autoDestList.Lnk.Header.DataFlags & Header.DataFlag.HasLinkInfo) ==
                                Header.DataFlag.HasLinkInfo)
                            {
                                _logger.Info("");
                                _logger.Error("--- Link information ---");
                                _logger.Info($"Flags: {autoDestList.Lnk.LocationFlags}");

                                if (autoDestList.Lnk.VolumeInfo != null)
                                {
                                    _logger.Info("");
                                    _logger.Warn(">>Volume information");
                                    _logger.Info(
                                        $"  Drive type: {GetDescriptionFromEnumValue(autoDestList.Lnk.VolumeInfo.DriveType)}");
                                    _logger.Info($"  Serial number: {autoDestList.Lnk.VolumeInfo.DriveSerialNumber}");

                                    var label = autoDestList.Lnk.VolumeInfo.VolumeLabel.Length > 0
                                        ? autoDestList.Lnk.VolumeInfo.VolumeLabel
                                        : "(No label)";

                                    _logger.Info($"  Label: {label}");
                                }

                                if (autoDestList.Lnk.NetworkShareInfo != null)
                                {
                                    _logger.Info("");
                                    _logger.Warn("  Network share information");

                                    if (autoDestList.Lnk.NetworkShareInfo.DeviceName.Length > 0)
                                    {
                                        _logger.Info($"    Device name: {autoDestList.Lnk.NetworkShareInfo.DeviceName}");
                                    }

                                    _logger.Info($"    Share name: {autoDestList.Lnk.NetworkShareInfo.NetworkShareName}");

                                    _logger.Info(
                                        $"    Provider type: {autoDestList.Lnk.NetworkShareInfo.NetworkProviderType}");
                                    _logger.Info($"    Share flags: {autoDestList.Lnk.NetworkShareInfo.ShareFlags}");
                                    _logger.Info("");
                                }

                                if (autoDestList.Lnk.LocalPath?.Length > 0)
                                {
                                    _logger.Info($"  Local path: {autoDestList.Lnk.LocalPath}");
                                }

                                if (autoDestList.Lnk.CommonPath.Length > 0)
                                {
                                    _logger.Info($"  Common path: {autoDestList.Lnk.CommonPath}");
                                }
                            }
                        }

                        if (autoDestList.Lnk.TargetIDs.Count > 0)
                        {
                            _logger.Info("");

                            var absPath = string.Empty;

                            foreach (var shellBag in autoDestList.Lnk.TargetIDs)
                            {
                                absPath += shellBag.Value + @"\";
                            }

                            _logger.Info($"  Absolute path: {GetAbsolutePathFromTargetIDs(autoDestList.Lnk.TargetIDs)}");
                            _logger.Info("");
                        }

                        _logger.Info("");
                    }
                }

                sw.Stop();

                if (_fluentCommandLineParser.Object.Quiet == false)
                {
                    _logger.Info("");
                }

                _logger.Info(
                    $"---------- Processed '{autoDest.SourceFile}' in {sw.Elapsed.TotalSeconds:N8} seconds ----------");

                if (_fluentCommandLineParser.Object.Quiet == false)
                {
                    _logger.Info("\r\n");
                }

                return autoDest;
            }

            catch (Exception ex)
            {
                _failedFiles.Add($"{jlFile} ==> ({ex.Message})");
                _logger.Fatal($"Error opening '{jlFile}'. Message: {ex.Message}");
                _logger.Info("");
            }

            return null;
        }

        private static CustomDestination ProcessCustomFile(string jlFile)
        {
            if (_fluentCommandLineParser.Object.Quiet == false)
            {
                _logger.Warn($"Processing '{jlFile}'");
                _logger.Info("");
            }

            var sw = new Stopwatch();
            sw.Start();

            try
            {
                var customDest = JumpList.JumpList.LoadCustomJumplist(jlFile);

                if (_fluentCommandLineParser.Object.Quiet == false)
                {
                    _logger.Error($"Source file: {customDest.SourceFile}");

                    _logger.Info("");

                    _logger.Warn("--- AppId information ---");
                    _logger.Warn($"AppID: {customDest.AppId.AppId}, Description: {customDest.AppId.Description}");
                    _logger.Warn("--- DestList information ---");
                    _logger.Info($"  Entries:  {customDest.Entries.Count:N0}");
                    _logger.Info("");

                    var entryNum = 0;
                    foreach (var entry in customDest.Entries)
                    {
                        _logger.Warn($"  Entry #: {entryNum}, lnk count: {entry.LnkFiles.Count:N0} Rank: {entry.Rank}");

                        if (entry.Name.Length > 0)
                        {
                            _logger.Info($"   Name: {entry.Name}");
                        }

                        _logger.Info("");

                        var lnkCounter = 0;

                        foreach (var lnkFile in entry.LnkFiles)
                        {
                            var tc = lnkFile.Header.TargetCreationDate.Year == 1601
                                ? ""
                                : lnkFile.Header.TargetCreationDate.ToString(
                                    _fluentCommandLineParser.Object.DateTimeFormat);
                            var tm = lnkFile.Header.TargetModificationDate.Year == 1601
                                ? ""
                                : lnkFile.Header.TargetModificationDate.ToString(
                                    _fluentCommandLineParser.Object.DateTimeFormat);
                            var ta = lnkFile.Header.TargetLastAccessedDate.Year == 1601
                                ? ""
                                : lnkFile.Header.TargetLastAccessedDate.ToString(
                                    _fluentCommandLineParser.Object.DateTimeFormat);


                            _logger.Warn($"--- Lnk #{lnkCounter:N0} information ---");
                            _logger.Info($"  Lnk target created: {tc}");
                            _logger.Info($"  Lnk target modified: {tm}");
                            _logger.Info($"  Lnk target accessed: {ta}");

                            if (_fluentCommandLineParser.Object.IncludeLnkDetail)
                            {
                                if ((lnkFile.Header.DataFlags & Header.DataFlag.HasName) == Header.DataFlag.HasName)
                                {
                                    _logger.Info($"  Name: {lnkFile.Name}");
                                }

                                if ((lnkFile.Header.DataFlags & Header.DataFlag.HasRelativePath) ==
                                    Header.DataFlag.HasRelativePath)
                                {
                                    _logger.Info($"  Relative Path: {lnkFile.RelativePath}");
                                }

                                if ((lnkFile.Header.DataFlags & Header.DataFlag.HasWorkingDir) ==
                                    Header.DataFlag.HasWorkingDir)
                                {
                                    _logger.Info($"  Working Directory: {lnkFile.WorkingDirectory}");
                                }

                                if ((lnkFile.Header.DataFlags & Header.DataFlag.HasArguments) ==
                                    Header.DataFlag.HasArguments)
                                {
                                    _logger.Info($"  Arguments: {lnkFile.Arguments}");
                                }

                                if ((lnkFile.Header.DataFlags & Header.DataFlag.HasLinkInfo) ==
                                    Header.DataFlag.HasLinkInfo)
                                {
                                    _logger.Info("");
                                    _logger.Error("--- Link information ---");
                                    _logger.Info($"Flags: {lnkFile.LocationFlags}");

                                    if (lnkFile.VolumeInfo != null)
                                    {
                                        _logger.Info("");
                                        _logger.Warn(">>Volume information");
                                        _logger.Info(
                                            $"  Drive type: {GetDescriptionFromEnumValue(lnkFile.VolumeInfo.DriveType)}");
                                        _logger.Info($"  Serial number: {lnkFile.VolumeInfo.DriveSerialNumber}");

                                        var label = lnkFile.VolumeInfo.VolumeLabel.Length > 0
                                            ? lnkFile.VolumeInfo.VolumeLabel
                                            : "(No label)";

                                        _logger.Info($"  Label: {label}");
                                    }

                                    if (lnkFile.NetworkShareInfo != null)
                                    {
                                        _logger.Info("");
                                        _logger.Warn("  Network share information");

                                        if (lnkFile.NetworkShareInfo.DeviceName.Length > 0)
                                        {
                                            _logger.Info($"    Device name: {lnkFile.NetworkShareInfo.DeviceName}");
                                        }

                                        _logger.Info($"    Share name: {lnkFile.NetworkShareInfo.NetworkShareName}");

                                        _logger.Info(
                                            $"    Provider type: {lnkFile.NetworkShareInfo.NetworkProviderType}");
                                        _logger.Info($"    Share flags: {lnkFile.NetworkShareInfo.ShareFlags}");
                                        _logger.Info("");
                                    }

                                    if (lnkFile.LocalPath?.Length > 0)
                                    {
                                        _logger.Info($"  Local path: {lnkFile.LocalPath}");
                                    }

                                    if (lnkFile.CommonPath.Length > 0)
                                    {
                                        _logger.Info($"  Common path: {lnkFile.CommonPath}");
                                    }
                                }
                            }

                            if (lnkFile.TargetIDs.Count > 0)
                            {
                                _logger.Info("");

                                var absPath = string.Empty;

                                foreach (var shellBag in lnkFile.TargetIDs)
                                {
                                    absPath += shellBag.Value + @"\";
                                }

                                _logger.Info($"  Absolute path: {GetAbsolutePathFromTargetIDs(lnkFile.TargetIDs)}");
                                _logger.Info("");
                            }


                            lnkCounter += 1;
                        }
                        _logger.Info("");
                        entryNum += 1;
                    }
                }


//                _logger.Warn("--- DestList entries ---");
//                foreach (var autoDestList in customDest.DestList)
//                {
//                    _logger.Info($"Entry #: {autoDestList.EntryNumber}");
//                    _logger.Info($"  Path: {autoDestList.Path}");
//                    _logger.Info($"  Created on: {autoDestList.CreatedOn}");
//                    _logger.Info($"  Last modified: {autoDestList.LastModified}");
//                    _logger.Info($"  Hostname: {autoDestList.Hostname}");
//                    _logger.Info($"  Mac Address: {autoDestList.MacAddress}");
//                    _logger.Info($"  Lnk target created: {autoDestList.Lnk.Header.TargetCreationDate}");
//                    _logger.Info($"  Lnk target modified: {autoDestList.Lnk.Header.TargetModificationDate}");
//                    _logger.Info($"  Lnk target accessed: {autoDestList.Lnk.Header.TargetLastAccessedDate}");
//
//                    _logger.Error("  (More info will be included here)");
//
//                    _logger.Info("");
//                }


                //                if (DestList != null)
                //                {
                //                    DestListCount = DestList.Header.NumberOfEntries;
                //                    PinnedDestListCount = DestList.Header.NumberOfPinnedEntries;
                //                    DestListVersion = DestList.Header.Version;
                //
                //                    foreach (var entry in DestList.Entries)
                //                    {
                //
                //                        var dirItem =
                //                                        _oleContainer.Directory.SingleOrDefault(
                //                                            t => t.DirectoryName.ToLowerInvariant() == entry.EntryNumber.ToString("X").ToLowerInvariant());
                //
                //                        if (dirItem != null)
                //                        {
                //                            var p = _oleContainer.GetPayloadForDirectory(dirItem);
                //
                //                            var dlnk = new LnkFile(p, $"{sourceFile}__Directory name {entry.EntryNumber}");
                //
                //                            var dl = new AutoDestList(entry, dlnk);
                //
                //                            DestList.Add(dl);
                //                        }
                //
                //
                //
                //                    }
                //                }


                //                if (_fluentCommandLineParser.Object.Quiet == false)
                //                {
                //                    _logger.Error($"Source file: {autoDest.SourceFile}");
                //                    _logger.Info($"  Source created:  {autoDest.SourceCreated}");
                //                    _logger.Info($"  Source modified: {autoDest.SourceModified}");
                //                    _logger.Info($"  Source accessed: {autoDest.SourceAccessed}");
                //                    _logger.Info("");
                //
                //                    _logger.Warn("--- Header ---");
                //
                //                    var tc = autoDest.Header.TargetCreationDate.Year == 1601 ? "" : autoDest.Header.TargetCreationDate.ToString();
                //                    var tm = autoDest.Header.TargetModificationDate.Year == 1601 ? "" : autoDest.Header.TargetModificationDate.ToString();
                //                    var ta = autoDest.Header.TargetLastAccessedDate.Year == 1601 ? "" : autoDest.Header.TargetLastAccessedDate.ToString();
                //
                //                    _logger.Info($"  Target created:  {tc}");
                //                    _logger.Info($"  Target modified: {tm}");
                //                    _logger.Info($"  Target accessed: {ta}");
                //                    _logger.Info("");
                //                    _logger.Info($"  File size: {autoDest.Header.FileSize:N0}");
                //                    _logger.Info($"  Flags: {autoDest.Header.DataFlags}");
                //                    _logger.Info($"  File attributes: {autoDest.Header.FileAttributes}");
                //
                //                    if (autoDest.Header.HotKey.Length > 0)
                //                    {
                //                        _logger.Info($"  Hot key: {autoDest.Header.HotKey}");
                //                    }
                //
                //                    _logger.Info($"  Icon index: {autoDest.Header.IconIndex}");
                //                    _logger.Info(
                //                        $"  Show window: {autoDest.Header.ShowWindow} ({GetDescriptionFromEnumValue(autoDest.Header.ShowWindow)})");
                //
                //                    _logger.Info("");
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasName) == Header.DataFlag.HasName)
                //                    {
                //                        _logger.Info($"Name: {autoDest.Name}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasRelativePath) == Header.DataFlag.HasRelativePath)
                //                    {
                //                        _logger.Info($"Relative Path: {autoDest.RelativePath}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasWorkingDir) == Header.DataFlag.HasWorkingDir)
                //                    {
                //                        _logger.Info($"Working Directory: {autoDest.WorkingDirectory}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasArguments) == Header.DataFlag.HasArguments)
                //                    {
                //                        _logger.Info($"Arguments: {autoDest.Arguments}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasIconLocation) == Header.DataFlag.HasIconLocation)
                //                    {
                //                        _logger.Info($"Icon Location: {autoDest.IconLocation}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasLinkInfo) == Header.DataFlag.HasLinkInfo)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Error("--- Link information ---");
                //                        _logger.Info($"Flags: {autoDest.LocationFlags}");
                //
                //                        if (autoDest.VolumeInfo != null)
                //                        {
                //                            _logger.Info("");
                //                            _logger.Warn(">>Volume information");
                //                            _logger.Info($"  Drive type: {GetDescriptionFromEnumValue(autoDest.VolumeInfo.DriveType)}");
                //                            _logger.Info($"  Serial number: {autoDest.VolumeInfo.DriveSerialNumber}");
                //
                //                            var label = autoDest.VolumeInfo.VolumeLabel.Length > 0
                //                                ? autoDest.VolumeInfo.VolumeLabel
                //                                : "(No label)";
                //
                //                            _logger.Info($"  Label: {label}");
                //                        }
                //
                //                        if (autoDest.NetworkShareInfo != null)
                //                        {
                //                            _logger.Info("");
                //                            _logger.Warn("  Network share information");
                //
                //                            if (autoDest.NetworkShareInfo.DeviceName.Length > 0)
                //                            {
                //                                _logger.Info($"    Device name: {autoDest.NetworkShareInfo.DeviceName}");
                //                            }
                //
                //                            _logger.Info($"    Share name: {autoDest.NetworkShareInfo.NetworkShareName}");
                //
                //                            _logger.Info($"    Provider type: {autoDest.NetworkShareInfo.NetworkProviderType}");
                //                            _logger.Info($"    Share flags: {autoDest.NetworkShareInfo.ShareFlags}");
                //                            _logger.Info("");
                //                        }
                //
                //                        if (autoDest.LocalPath?.Length > 0)
                //                        {
                //                            _logger.Info($"  Local path: {autoDest.LocalPath}");
                //                        }
                //
                //                        if (autoDest.CommonPath.Length > 0)
                //                        {
                //                            _logger.Info($"  Common path: {autoDest.CommonPath}");
                //                        }
                //                    }
                //
                //                    if (_fluentCommandLineParser.Object.NoTargetIDList)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Warn($"(Target ID information suppressed. Lnk TargetID count: {autoDest.TargetIDs.Count:N0})");
                //                    }
                //
                //                    if (autoDest.TargetIDs.Count > 0 && !_fluentCommandLineParser.Object.NoTargetIDList)
                //                    {
                //                        _logger.Info("");
                //
                //                        var absPath = string.Empty;
                //
                //                        foreach (var shellBag in autoDest.TargetIDs)
                //                        {
                //                            absPath += shellBag.Value + @"\";
                //                        }
                //
                //                        _logger.Error("--- Target ID information (Format: Type ==> Value) ---");
                //                        _logger.Info("");
                //                        _logger.Info($"  Absolute path: {GetAbsolutePathFromTargetIDs(autoDest.TargetIDs)}");
                //                        _logger.Info("");
                //
                //                        foreach (var shellBag in autoDest.TargetIDs)
                //                        {
                //                            //HACK
                //                            //This is a total hack until i can refactor some shellbag code to clean things up
                //
                //                            var val = shellBag.Value.IsNullOrEmpty() ? "(None)" : shellBag.Value;
                //
                //                            _logger.Info($"  -{shellBag.FriendlyName} ==> {val}");
                //
                //                            switch (shellBag.GetType().Name.ToUpper())
                //                            {
                //                                case "SHELLBAG0X32":
                //                                    var b32 = shellBag as ShellBag0X32;
                //
                //                                    _logger.Info($"    Short name: {b32.ShortName}");
                //                                    _logger.Info($"    Modified: {b32.LastModificationTime}");
                //
                //                                    var extensionNumber32 = 0;
                //                                    if (b32.ExtensionBlocks.Count > 0)
                //                                    {
                //                                        _logger.Info($"    Extension block count: {b32.ExtensionBlocks.Count:N0}");
                //                                        _logger.Info("");
                //                                        foreach (var extensionBlock in b32.ExtensionBlocks)
                //                                        {
                //                                            _logger.Info(
                //                                                $"    --------- Block {extensionNumber32:N0} ({extensionBlock.GetType().Name}) ---------");
                //                                            if (extensionBlock is Beef0004)
                //                                            {
                //                                                var b4 = extensionBlock as Beef0004;
                //
                //                                                _logger.Info($"    Long name: {b4.LongName}");
                //                                                if (b4.LocalisedName.Length > 0)
                //                                                {
                //                                                    _logger.Info($"    Localized name: {b4.LocalisedName}");
                //                                                }
                //
                //                                                _logger.Info($"    Created: {b4.CreatedOnTime}");
                //                                                _logger.Info($"    Last access: {b4.LastAccessTime}");
                //                                                if (b4.MFTInformation.MFTEntryNumber > 0)
                //                                                {
                //                                                    _logger.Info(
                //                                                        $"    MFT entry/sequence #: {b4.MFTInformation.MFTEntryNumber}/{b4.MFTInformation.MFTSequenceNumber} (0x{b4.MFTInformation.MFTEntryNumber:X}/0x{b4.MFTInformation.MFTSequenceNumber:X})");
                //                                                }
                //                                            }
                //                                            else if (extensionBlock is Beef0025)
                //                                            {
                //                                                var b25 = extensionBlock as Beef0025;
                //                                                _logger.Info(
                //                                                    $"    Filetime 1: {b25.FileTime1}, Filetime 2: {b25.FileTime2}");
                //                                            }
                //                                            else if (extensionBlock is Beef0003)
                //                                            {
                //                                                var b3 = extensionBlock as Beef0003;
                //                                                _logger.Info($"    GUID: {b3.GUID1} ({b3.GUID1Folder})");
                //                                            }
                //                                            else
                //                                            {
                //                                                _logger.Info($"    {extensionBlock}");
                //                                            }
                //
                //                                            extensionNumber32 += 1;
                //                                        }
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X31":
                //
                //                                    var b3x = shellBag as ShellBag0X31;
                //
                //                                    _logger.Info($"    Short name: {b3x.ShortName}");
                //                                    _logger.Info($"    Modified: {b3x.LastModificationTime}");
                //
                //                                    var extensionNumber = 0;
                //                                    if (b3x.ExtensionBlocks.Count > 0)
                //                                    {
                //                                        _logger.Info($"    Extension block count: {b3x.ExtensionBlocks.Count:N0}");
                //                                        _logger.Info("");
                //                                        foreach (var extensionBlock in b3x.ExtensionBlocks)
                //                                        {
                //                                            _logger.Info(
                //                                                $"    --------- Block {extensionNumber:N0} ({extensionBlock.GetType().Name}) ---------");
                //                                            if (extensionBlock is Beef0004)
                //                                            {
                //                                                var b4 = extensionBlock as Beef0004;
                //
                //                                                _logger.Info($"    Long name: {b4.LongName}");
                //                                                if (b4.LocalisedName.Length > 0)
                //                                                {
                //                                                    _logger.Info($"    Localized name: {b4.LocalisedName}");
                //                                                }
                //
                //                                                _logger.Info($"    Created: {b4.CreatedOnTime}");
                //                                                _logger.Info($"    Last access: {b4.LastAccessTime}");
                //                                                if (b4.MFTInformation.MFTEntryNumber > 0)
                //                                                {
                //                                                    _logger.Info(
                //                                                        $"    MFT entry/sequence #: {b4.MFTInformation.MFTEntryNumber}/{b4.MFTInformation.MFTSequenceNumber} (0x{b4.MFTInformation.MFTEntryNumber:X}/0x{b4.MFTInformation.MFTSequenceNumber:X})");
                //                                                }
                //                                            }
                //                                            else if (extensionBlock is Beef0025)
                //                                            {
                //                                                var b25 = extensionBlock as Beef0025;
                //                                                _logger.Info(
                //                                                    $"    Filetime 1: {b25.FileTime1}, Filetime 2: {b25.FileTime2}");
                //                                            }
                //                                            else if (extensionBlock is Beef0003)
                //                                            {
                //                                                var b3 = extensionBlock as Beef0003;
                //                                                _logger.Info($"    GUID: {b3.GUID1} ({b3.GUID1Folder})");
                //                                            }
                //                                            else
                //                                            {
                //                                                _logger.Info($"    {extensionBlock}");
                //                                            }
                //
                //                                            extensionNumber += 1;
                //                                        }
                //                                    }
                //                                    break;
                //
                //                                case "SHELLBAG0X00":
                //                                    var b00 = shellBag as ShellBag0X00;
                //
                //                                    if (b00.PropertyStore.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Warn("  >> Property store (Format: GUID\\ID Description ==> Value)");
                //                                        var propCount = 0;
                //
                //                                        foreach (var prop in b00.PropertyStore.Sheets)
                //                                        {
                //                                            foreach (var propertyName in prop.PropertyNames)
                //                                            {
                //                                                propCount += 1;
                //
                //                                                var prefix = $"{prop.GUID}\\{propertyName.Key}".PadRight(43);
                //
                //                                                var suffix =
                //                                                    $"{Utils.GetDescriptionFromGuidAndKey(prop.GUID, int.Parse(propertyName.Key))}"
                //                                                        .PadRight(35);
                //
                //                                                _logger.Info($"     {prefix} {suffix} ==> {propertyName.Value}");
                //                                            }
                //                                        }
                //
                //                                        if (propCount == 0)
                //                                        {
                //                                            _logger.Warn("     (Property store is empty)");
                //                                        }
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X01":
                //                                    var baaaa1f = shellBag as ShellBag0X01;
                //                                    if (baaaa1f.DriveLetter.Length > 0)
                //                                    {
                //                                        _logger.Info($"  Drive letter: {baaaa1f.DriveLetter}");
                //                                    }
                //                                    break;
                //                                case "SHELLBAG0X1F":
                //
                //                                    var b1f = shellBag as ShellBag0X1F;
                //
                //                                    if (b1f.PropertyStore.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Warn("  >> Property store (Format: GUID\\ID Description ==> Value)");
                //                                        var propCount = 0;
                //
                //                                        foreach (var prop in b1f.PropertyStore.Sheets)
                //                                        {
                //                                            foreach (var propertyName in prop.PropertyNames)
                //                                            {
                //                                                propCount += 1;
                //
                //                                                var prefix = $"{prop.GUID}\\{propertyName.Key}".PadRight(43);
                //
                //                                                var suffix =
                //                                                    $"{Utils.GetDescriptionFromGuidAndKey(prop.GUID, int.Parse(propertyName.Key))}"
                //                                                        .PadRight(35);
                //
                //                                                _logger.Info($"     {prefix} {suffix} ==> {propertyName.Value}");
                //                                            }
                //                                        }
                //
                //                                        if (propCount == 0)
                //                                        {
                //                                            _logger.Warn("     (Property store is empty)");
                //                                        }
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X2E":
                //                                    break;
                //                                case "SHELLBAG0X2F":
                //                                    var b2f = shellBag as ShellBag0X2F;
                //
                //                                    break;
                //                                case "SHELLBAG0X40":
                //                                    break;
                //                                case "SHELLBAG0X61":
                //
                //                                    break;
                //                                case "SHELLBAG0X71":
                //                                    var b71 = shellBag as ShellBag0X71;
                //                                    if (b71.PropertyStore?.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Fatal(
                //                                            "Property stores found! Please email auto file to saericzimmerman@gmail.com so support can be added!!");
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X74":
                //                                    var b74 = shellBag as ShellBag0X74;
                //
                //                                    _logger.Info($"    Modified: {b74.LastModificationTime}");
                //
                //                                    var extensionNumber74 = 0;
                //                                    if (b74.ExtensionBlocks.Count > 0)
                //                                    {
                //                                        _logger.Info($"    Extension block count: {b74.ExtensionBlocks.Count:N0}");
                //                                        _logger.Info("");
                //                                        foreach (var extensionBlock in b74.ExtensionBlocks)
                //                                        {
                //                                            _logger.Info(
                //                                                $"    --------- Block {extensionNumber74:N0} ({extensionBlock.GetType().Name}) ---------");
                //                                            if (extensionBlock is Beef0004)
                //                                            {
                //                                                var b4 = extensionBlock as Beef0004;
                //
                //                                                _logger.Info($"    Long name: {b4.LongName}");
                //                                                if (b4.LocalisedName.Length > 0)
                //                                                {
                //                                                    _logger.Info($"    Localized name: {b4.LocalisedName}");
                //                                                }
                //
                //                                                _logger.Info($"    Created: {b4.CreatedOnTime}");
                //                                                _logger.Info($"    Last access: {b4.LastAccessTime}");
                //                                                if (b4.MFTInformation.MFTEntryNumber > 0)
                //                                                {
                //                                                    _logger.Info(
                //                                                        $"    MFT entry/sequence #: {b4.MFTInformation.MFTEntryNumber}/{b4.MFTInformation.MFTSequenceNumber} (0x{b4.MFTInformation.MFTEntryNumber:X}/0x{b4.MFTInformation.MFTSequenceNumber:X})");
                //                                                }
                //                                            }
                //                                            else if (extensionBlock is Beef0025)
                //                                            {
                //                                                var b25 = extensionBlock as Beef0025;
                //                                                _logger.Info(
                //                                                    $"    Filetime 1: {b25.FileTime1}, Filetime 2: {b25.FileTime2}");
                //                                            }
                //                                            else if (extensionBlock is Beef0003)
                //                                            {
                //                                                var b3 = extensionBlock as Beef0003;
                //                                                _logger.Info($"    GUID: {b3.GUID1} ({b3.GUID1Folder})");
                //                                            }
                //                                            else
                //                                            {
                //                                                _logger.Info($"    {extensionBlock}");
                //                                            }
                //
                //                                            extensionNumber74 += 1;
                //                                        }
                //                                    }
                //                                    break;
                //                                case "SHELLBAG0XC3":
                //                                    break;
                //                                case "SHELLBAGZIPCONTENTS":
                //                                    break;
                //                                default:
                //                                    _logger.Fatal(
                //                                        $">> UNMAPPED Type! Please email auto file to saericzimmerman@gmail.com so support can be added!");
                //                                    _logger.Fatal($">>{shellBag}");
                //                                    break;
                //                            }
                //
                //                            _logger.Info("");
                //                        }
                //                        _logger.Error("--- End Target ID information ---");
                //                    }
                //
                //                    if (_fluentCommandLineParser.Object.NoExtraBlocks)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Warn(
                //                            $"(Extra blocks information suppressed. Lnk Extra block count: {autoDest.ExtraBlocks.Count:N0})");
                //                    }
                //
                //                    if (autoDest.ExtraBlocks.Count > 0 && !_fluentCommandLineParser.Object.NoExtraBlocks)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Error("--- Extra blocks information ---");
                //                        _logger.Info("");
                //
                //                        foreach (var extraDataBase in autoDest.ExtraBlocks)
                //                        {
                //                            switch (extraDataBase.GetType().Name)
                //                            {
                //                                case "ConsoleDataBlock":
                //                                    var cdb = extraDataBase as ConsoleDataBlock;
                //                                    _logger.Warn(">> Console data block");
                //                                    _logger.Info($"   Fill Attributes: {cdb.FillAttributes}");
                //                                    _logger.Info($"   Popup Attributes: {cdb.PopupFillAttributes}");
                //                                    _logger.Info(
                //                                        $"   Buffer Size (Width x Height): {cdb.ScreenWidthBufferSize} x {cdb.ScreenHeightBufferSize}");
                //                                    _logger.Info(
                //                                        $"   Window Size (Width x Height): {cdb.WindowWidth} x {cdb.WindowHeight}");
                //                                    _logger.Info($"   Origin (X/Y): {cdb.WindowOriginX}/{cdb.WindowOriginY}");
                //                                    _logger.Info($"   Font Size: {cdb.FontSize}");
                //                                    _logger.Info($"   Is Bold: {cdb.IsBold}");
                //                                    _logger.Info($"   Face Name: {cdb.FaceName}");
                //                                    _logger.Info($"   Cursor Size: {cdb.CursorSize}");
                //                                    _logger.Info($"   Is Full Screen: {cdb.IsFullScreen}");
                //                                    _logger.Info($"   Is Quick Edit: {cdb.IsQuickEdit}");
                //                                    _logger.Info($"   Is Insert Mode: {cdb.IsInsertMode}");
                //                                    _logger.Info($"   Is Auto Positioned: {cdb.IsAutoPositioned}");
                //                                    _logger.Info($"   History Buffer Size: {cdb.HistoryBufferSize}");
                //                                    _logger.Info($"   History Buffer Count: {cdb.HistoryBufferCount}");
                //                                    _logger.Info($"   History Duplicates Allowed: {cdb.HistoryDuplicatesAllowed}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "ConsoleFEDataBlock":
                //                                    var cfedb = extraDataBase as ConsoleFeDataBlock;
                //                                    _logger.Warn(">> Console FE data block");
                //                                    _logger.Info($"   Code page: {cfedb.CodePage}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "DarwinDataBlock":
                //                                    var ddb = extraDataBase as DarwinDataBlock;
                //                                    _logger.Warn(">> Darwin data block");
                //                                    _logger.Info($"   Application ID: {ddb.ApplicationIdentifierUnicode}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "EnvironmentVariableDataBlock":
                //                                    var evdb = extraDataBase as EnvironmentVariableDataBlock;
                //                                    _logger.Warn(">> Environment variable data block");
                //                                    _logger.Info($"   Environment variables: {evdb.EnvironmentVariablesUnicode}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "IconEnvironmentDataBlock":
                //                                    var iedb = extraDataBase as IconEnvironmentDataBlock;
                //                                    _logger.Warn(">> Icon environment data block");
                //                                    _logger.Info($"   Icon path: {iedb.IconPathUni}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "KnownFolderDataBlock":
                //                                    var kfdb = extraDataBase as KnownFolderDataBlock;
                //                                    _logger.Warn(">> Known folder data block");
                //                                    _logger.Info(
                //                                        $"   Known folder GUID: {kfdb.KnownFolderId} ==> {kfdb.KnownFolderName}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "PropertyStoreDataBlock":
                //                                    var psdb = extraDataBase as PropertyStoreDataBlock;
                //
                //                                    if (psdb.PropertyStore.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Warn(
                //                                            ">> Property store data block (Format: GUID\\ID Description ==> Value)");
                //                                        var propCount = 0;
                //
                //                                        foreach (var prop in psdb.PropertyStore.Sheets)
                //                                        {
                //                                            foreach (var propertyName in prop.PropertyNames)
                //                                            {
                //                                                propCount += 1;
                //
                //                                                var prefix = $"{prop.GUID}\\{propertyName.Key}".PadRight(43);
                //                                                var suffix =
                //                                                    $"{Utils.GetDescriptionFromGuidAndKey(prop.GUID, int.Parse(propertyName.Key))}"
                //                                                        .PadRight(35);
                //
                //                                                _logger.Info($"   {prefix} {suffix} ==> {propertyName.Value}");
                //                                            }
                //                                        }
                //
                //                                        if (propCount == 0)
                //                                        {
                //                                            _logger.Warn("   (Property store is empty)");
                //                                        }
                //                                    }
                //                                    _logger.Info("");
                //                                    break;
                //                                case "ShimDataBlock":
                //                                    var sdb = extraDataBase as ShimDataBlock;
                //                                    _logger.Warn(">> Shimcache data block");
                //                                    _logger.Info($"   LayerName: {sdb.LayerName}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "SpecialFolderDataBlock":
                //                                    var sfdb = extraDataBase as SpecialFolderDataBlock;
                //                                    _logger.Warn(">> Special folder data block");
                //                                    _logger.Info($"   Special Folder ID: {sfdb.SpecialFolderId}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "TrackerDataBaseBlock":
                //                                    var tdb = extraDataBase as TrackerDataBaseBlock;
                //                                    _logger.Warn(">> Tracker database block");
                //                                    _logger.Info($"   Machine ID: {tdb.MachineId}");
                //                                    _logger.Info($"   MAC Address: {tdb.MacAddress}");
                //                                    _logger.Info($"   MAC Vendor: {GetVendorFromMac(tdb.MacAddress)}");
                //                                    _logger.Info($"   Creation: {tdb.CreationTime}");
                //                                    _logger.Info("");
                //                                    _logger.Info($"   Volume Droid: {tdb.VolumeDroid}");
                //                                    _logger.Info($"   Volume Droid Birth: {tdb.VolumeDroidBirth}");
                //                                    _logger.Info($"   File Droid: {tdb.FileDroid}");
                //                                    _logger.Info($"   File Droid birth: {tdb.FileDroidBirth}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "VistaAndAboveIDListDataBlock":
                //                                    var vdb = extraDataBase as VistaAndAboveIdListDataBlock;
                //                                    _logger.Warn(">> Vista and above ID List data block");
                //
                //                                    foreach (var shellBag in vdb.TargetIDs)
                //                                    {
                //                                        var val = shellBag.Value.IsNullOrEmpty() ? "(None)" : shellBag.Value;
                //                                        _logger.Info($"   {shellBag.FriendlyName} ==> {val}");
                //                                    }
                //
                //                                    _logger.Info("");
                //                                    break;
                //                            }
                //                        }
                //                    }
                //                }
                //
                sw.Stop();

                if (_fluentCommandLineParser.Object.Quiet == false)
                {
                    _logger.Info("");
                }

                _logger.Info(
                    $"---------- Processed '{customDest.SourceFile}' in {sw.Elapsed.TotalSeconds:N8} seconds ----------");

                if (_fluentCommandLineParser.Object.Quiet == false)
                {
                    _logger.Info("\r\n");
                }

                return customDest;
            }

            catch (Exception ex)
            {
                _failedFiles.Add($"{jlFile} ==> ({ex.Message})");
                _logger.Fatal($"Error opening '{jlFile}'. Message: {ex.Message}");
                _logger.Info("");
            }

            return null;
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

    public sealed class AutoCsvOut
    {
        //jump list info
        public string SourceFile { get; set; }
        public string SourceCreated { get; set; }
        public string SourceModified { get; set; }
        public string SourceAccessed { get; set; }
        public string AppId { get; set; }
        public string AppIdDescription { get; set; }

        public string DestListVersion { get; set; }
        public string LastUsedEntryNumber { get; set; }

        //destlist entry
        public string EntryNumber { get; set; }
        public string CreationTime { get; set; }
        public string LastModified { get; set; }
        public string Hostname { get; set; }
        public string MacAddress { get; set; }
        public string Path { get; set; }
        public string PinStatus { get; set; }
        public string FileBirthDroid { get; set; }
        public string FileDroid { get; set; }
        public string VolumeBirthDroid { get; set; }
        public string VolumeDroid { get; set; }


        //lnk file info
        public string TargetCreated { get; set; }
        public string TargetModified { get; set; }
        public string TargetAccessed { get; set; }
        public int FileSize { get; set; }
        public string RelativePath { get; set; }
        public string WorkingDirectory { get; set; }
        public string FileAttributes { get; set; }
        public string HeaderFlags { get; set; }
        public string DriveType { get; set; }
        public string DriveSerialNumber { get; set; }
        public string DriveLabel { get; set; }
        public string LocalPath { get; set; }
        public string CommonPath { get; set; }
        public string TargetIDAbsolutePath { get; set; }
        public string TargetMFTEntryNumber { get; set; }
        public string TargetMFTSequenceNumber { get; set; }
        public string MachineID { get; set; }
        public string MachineMACAddress { get; set; }

        public string TrackerCreatedOn { get; set; }
        public string ExtraBlocksPresent { get; set; }
    }

    public sealed class CustomCsvOut
    {
        //
        public string SourceFile { get; set; }
        public string SourceCreated { get; set; }
        public string SourceModified { get; set; }
        public string SourceAccessed { get; set; }
        public string AppId { get; set; }
        public string AppIdDescription { get; set; }

        public string EntryName { get; set; }

        //lnk file info

        public string TargetCreated { get; set; }
        public string TargetModified { get; set; }
        public string TargetAccessed { get; set; }
        public int FileSize { get; set; }
        public string RelativePath { get; set; }
        public string WorkingDirectory { get; set; }
        public string FileAttributes { get; set; }
        public string HeaderFlags { get; set; }
        public string DriveType { get; set; }
        public string DriveSerialNumber { get; set; }
        public string DriveLabel { get; set; }
        public string LocalPath { get; set; }
        public string CommonPath { get; set; }
        public string TargetIDAbsolutePath { get; set; }
        public string TargetMFTEntryNumber { get; set; }
        public string TargetMFTSequenceNumber { get; set; }
        public string MachineID { get; set; }
        public string MachineMACAddress { get; set; }

        public string TrackerCreatedOn { get; set; }
        public string ExtraBlocksPresent { get; set; }
    }

    internal class ApplicationArguments
    {
        public string File { get; set; }
        public string Directory { get; set; }

        public string JsonDirectory { get; set; }
        public bool JsonPretty { get; set; }
        public bool AllFiles { get; set; }

        public string LnkDumpDirectory { get; set; }

        public bool IncludeLnkDetail { get; set; }

        public string CsvDirectory { get; set; }
        public string xHtmlDirectory { get; set; }

        public bool Quiet { get; set; }

        //  public bool LocalTime { get; set; }

        public string DateTimeFormat { get; set; }

        public bool PreciseTimestamps { get; set; }
    }
}