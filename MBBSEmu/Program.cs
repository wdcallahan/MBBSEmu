using MBBSEmu.Database.Repositories.Account;
using MBBSEmu.Database.Repositories.AccountKey;
using MBBSEmu.DependencyInjection;
using MBBSEmu.HostProcess;
using MBBSEmu.IO;
using MBBSEmu.Module;
using MBBSEmu.Reports;
using MBBSEmu.Resources;
using MBBSEmu.Server;
using MBBSEmu.Server.Socket;
using MBBSEmu.Session.Enums;
using MBBSEmu.Session.LocalConsole;
using Microsoft.Extensions.Configuration;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;

namespace MBBSEmu
{
    public class Program
    {
        public const string DefaultEmuSettingsFilename = "appsettings.json";

        private ILogger _logger;

        /// <summary>
        ///     Module Identifier specified by the -M Command Line Argument
        /// </summary>
        private string _moduleIdentifier;

        /// <summary>
        ///     Module Path specified by the -P Command Line Argument
        /// </summary>
        private string _modulePath;

        /// <summary>
        ///     Specified if -APIREPORT Command Line Argument was passed
        /// </summary>
        private bool _doApiReport;

        /// <summary>
        ///     Specified if -C Command Line Argument want passed
        /// </summary>
        private bool _isModuleConfigFile;

        /// <summary>
        ///     Custom modules json file specified by the -C Command Line Argument
        /// </summary>
        private string _moduleConfigFileName;

        /// <summary>
        ///     Custom appsettings.json File specified by the -S Command Line Argument
        /// </summary>
        private string _settingsFileName;

        /// <summary>
        ///     Specified if -DBRESET Command Line Argument was Passed
        /// </summary>
        private bool _doResetDatabase;

        /// <summary>
        ///     New Sysop Password specified by the -DBRESET Command Line Argument
        /// </summary>
        private string _newSysopPassword;

        /// <summary>
        ///     Specified if the -CONSOLE Command Line Argument was passed
        /// </summary>
        private bool _isConsoleSession;

        private readonly List<IStoppable> _runningServices = new List<IStoppable>();
        private int _cancellationRequests = 0;

        private ServiceResolver _serviceResolver;

        static void Main(string[] args)
        {
            new Program().Run(args);
        }

        private void Run(string[] args) {
            try
            {
                if (args.Length == 0)
                    args = new[] { "-?" };

                for (var i = 0; i < args.Length; i++)
                {
                    switch (args[i].ToUpper())
                    {
                        case "-DBRESET":
                            {
                                _doResetDatabase = true;
                                if (i + 1 < args.Length && args[i + 1][0] != '-')
                                {
                                    _newSysopPassword = args[i + 1];
                                    i++;
                                }

                                break;
                            }
                        case "-APIREPORT":
                            _doApiReport = true;
                            break;
                        case "-M":
                            _moduleIdentifier = args[i + 1];
                            i++;
                            break;
                        case "-P":
                            _modulePath = args[i + 1];
                            i++;
                            break;
                        case "-?":
                            Console.WriteLine(new ResourceManager().GetString("MBBSEmu.Assets.commandLineHelp.txt"));
                            Console.WriteLine($"Version: {new ResourceManager().GetString("MBBSEmu.Assets.version.txt")}");
                            return;
                        case "-CONFIG":
                        case "-C":
                            {
                                _isModuleConfigFile = true;
                                //Is there a following argument that doesn't start with '-'
                                //If so, it's the config file name
                                if (i + 1 < args.Length && args[i + 1][0] != '-')
                                {
                                    _moduleConfigFileName = args[i + 1];

                                    if (!File.Exists(_moduleConfigFileName))
                                    {
                                        Console.Write($"Specified Module Configuration File not found: {_moduleConfigFileName}");
                                        return;
                                    }
                                    i++;
                                }
                                else
                                {
                                    Console.WriteLine("Please specify a Module Configuration File when using the -C command line option");
                                }

                                break;
                            }
                        case "-S":
                            {
                                //Is there a following argument that doesn't start with '-'
                                //If so, it's the config file name
                                if (i + 1 < args.Length && args[i + 1][0] != '-')
                                {
                                    _settingsFileName =  args[i + 1];

                                    if (!File.Exists(_settingsFileName))
                                    {

                                        Console.WriteLine($"Specified MBBSEmu settings not found: {_settingsFileName}");
                                        return;
                                    }
                                    i++;
                                }
                                else
                                {
                                    Console.WriteLine("Please specify an MBBSEmu configuration file when using the -S command line option");
                                }

                                break;
                            }
                        case "-CONSOLE":
                        {
                            _isConsoleSession = true;
                            break;
                        }
                        default:
                            Console.WriteLine($"Unknown Command Line Argument: {args[i]}");
                            return;
                    }
                }

                _serviceResolver = new ServiceResolver(_settingsFileName ?? DefaultEmuSettingsFilename);

                _logger = _serviceResolver.GetService<ILogger>();
                var config = _serviceResolver.GetService<IConfiguration>();
                var fileUtility = _serviceResolver.GetService<IFileUtility>();

                //Database Reset
                if (_doResetDatabase)
                    DatabaseReset();

                //Setup Generic Database
                if (!File.Exists($"BBSGEN.DAT"))
                {
                    _logger.Warn($"Unable to find MajorBBS/WG Generic User Database, creating new copy of BBSGEN.VIR to BBSGEN.DAT");

                    var resourceManager = _serviceResolver.GetService<IResourceManager>();

                    File.WriteAllBytes($"BBSGEN.DAT", resourceManager.GetResource("MBBSEmu.Assets.BBSGEN.VIR").ToArray());
                }

                //Setup Modules
                var modules = new List<MbbsModule>();
                if (!string.IsNullOrEmpty(_moduleIdentifier))
                {
                    //Load Command Line
                    modules.Add(new MbbsModule(fileUtility, _logger, _moduleIdentifier, _modulePath));
                }
                else if (_isModuleConfigFile)
                {
                    //Load Config File
                    var moduleConfiguration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
                        .AddJsonFile(_moduleConfigFileName, optional: false, reloadOnChange: true).Build();

                    foreach (var m in moduleConfiguration.GetSection("Modules").GetChildren())
                    {
                        _logger.Info($"Loading {m["Identifier"]}");
                        modules.Add(new MbbsModule(fileUtility, _logger, m["Identifier"], m["Path"]));
                    }
                }
                else
                {
                    _logger.Warn($"You must specify a module to load either via Command Line or Config File");
                    _logger.Warn($"View help documentation using -? for more information");
                    return;
                }

                //API Report
                if (_doApiReport)
                {
                    foreach (var m in modules)
                    {
                        var apiReport = new ApiReport(_logger, m);
                        apiReport.GenerateReport();
                    }
                    return;
                }

                //Database Sanity Checks
                var databaseFile = _serviceResolver.GetService<IConfiguration>()["Database.File"];
                if (string.IsNullOrEmpty(databaseFile))
                {
                    _logger.Fatal($"Please set a valid database filename (eg: mbbsemu.db) in the appsettings.json file before running MBBSEmu");
                    return;
                }
                if (!File.Exists($"{databaseFile}"))
                {
                    _logger.Warn($"SQLite Database File {databaseFile} missing, performing Database Reset to perform initial configuration");
                    DatabaseReset();
                }

                //Setup and Run Host
                var host = _serviceResolver.GetService<IMbbsHost>();
                foreach (var m in modules)
                    host.AddModule(m);

                host.Start();

                _runningServices.Add(host);

                //Setup and Run Telnet Server
                if (bool.TryParse(config["Telnet.Enabled"], out var telnetEnabled) && telnetEnabled)
                {
                    if (string.IsNullOrEmpty("Telnet.Port"))
                    {
                        _logger.Error("You must specify a port via Telnet.Port in appconfig.json if you're going to enable Telnet");
                        return;
                    }

                    var telnetService = _serviceResolver.GetService<ISocketServer>();
                    telnetService.Start(EnumSessionType.Telnet, int.Parse(config["Telnet.Port"]));

                    _logger.Info($"Telnet listening on port {config["Telnet.Port"]}");

                    _runningServices.Add(telnetService);
                }
                else
                {
                    _logger.Info("Telnet Server Disabled (via appsettings.json)");
                }

                //Setup and Run Rlogin Server
                if (bool.TryParse(config["Rlogin.Enabled"], out var rloginEnabled) && rloginEnabled)
                {
                    if (string.IsNullOrEmpty("Rlogin.Port"))
                    {
                        _logger.Error("You must specify a port via Rlogin.Port in appconfig.json if you're going to enable Rlogin");
                        return;
                    }

                    if (string.IsNullOrEmpty("Rlogin.RemoteIP"))
                    {
                        _logger.Error("For security reasons, you must specify an authorized Remote IP via Rlogin.Port if you're going to enable Rlogin");
                        return;
                    }

                    var rloginService = _serviceResolver.GetService<ISocketServer>();
                    rloginService.Start(EnumSessionType.Rlogin, int.Parse(config["Rlogin.Port"]));

                    _logger.Info($"Rlogin listening on port {config["Rlogin.Port"]}");

                    _runningServices.Add(rloginService);

                    if (bool.Parse(config["Rlogin.PortPerModule"]))
                    {
                        var rloginPort = int.Parse(config["Rlogin.Port"]) + 1;
                        foreach (var m in modules)
                        {
                            _logger.Info($"Rlogin {m.ModuleIdentifier} listening on port {rloginPort}");
                            rloginService = _serviceResolver.GetService<ISocketServer>();
                            rloginService.Start(EnumSessionType.Rlogin, rloginPort++, m.ModuleIdentifier);
                            _runningServices.Add(rloginService);
                        }
                    }
                }
                else
                {
                    _logger.Info("Rlogin Server Disabled (via appsettings.json)");
                }

                _logger.Info($"Started MBBSEmu Build #{new ResourceManager().GetString("MBBSEmu.Assets.version.txt")}");

                Console.CancelKeyPress += CancelKeyPressHandler;

                if(_isConsoleSession)
                    _ = new LocalConsoleSession(_logger, "CONSOLE", host);
            }
            catch (Exception e)
            {
                Console.WriteLine("Critical Exception has occurred:");
                Console.WriteLine(e);
                Environment.Exit(0);
            }
        }

        private void CancelKeyPressHandler(object sender, ConsoleCancelEventArgs args)
        {
            // so args.Cancel is a bit strange. Cancel means to cancel the Ctrl-C processing, so
            // setting it to true keeps the app alive. We want this at first to allow the shutdown
            // routines to process naturally. If we get a 2nd (or more) Ctrl-C, then we set
            // args.Cancel to false which means the app will die a horrible death, and prevents the
            // app from being unkillable by normal means.
            args.Cancel = _cancellationRequests <= 0;

            _cancellationRequests++;

            _logger.Warn("BBS Shutting down");

            foreach (var runningService in _runningServices)
            {
                runningService.Stop();
            }
        }

        /// <summary>
        ///     Performs a Database Reset
        ///
        ///     Deletes the Accounts Table and sets up a new SYSOP and GUEST user
        /// </summary>
        private void DatabaseReset()
        {
            _logger.Info("Resetting Database...");
            var acct = _serviceResolver.GetService<IAccountRepository>();
            if (acct.TableExists())
                acct.DropTable();
            acct.CreateTable();

            if (string.IsNullOrEmpty(_newSysopPassword))
            {
                var bPasswordMatch = false;
                while (!bPasswordMatch)
                {
                    Console.Write("Enter New Sysop Password: ");
                    var password1 = Console.ReadLine();
                    Console.Write("Re-Enter New Sysop Password: ");
                    var password2 = Console.ReadLine();
                    if (password1 == password2)
                    {
                        bPasswordMatch = true;
                        _newSysopPassword = password1;
                    }
                    else
                    {
                        Console.WriteLine("Password mismatch, please tray again.");
                    }
                }
            }

            var sysopUserId = acct.InsertAccount("sysop", _newSysopPassword, "sysop@mbbsemu.com");
            var guestUserId = acct.InsertAccount("guest", "guest", "guest@mbbsemu.com");

            var keys = _serviceResolver.GetService<IAccountKeyRepository>();

            if (keys.TableExists())
                keys.DropTable();

            keys.CreateTable();

            //Keys for SYSOP
            keys.InsertAccountKey(sysopUserId, "DEMO");
            keys.InsertAccountKey(sysopUserId, "NORMAL");
            keys.InsertAccountKey(sysopUserId, "SUPER");
            keys.InsertAccountKey(sysopUserId, "SYSOP");

            //Keys for GUEST
            keys.InsertAccountKey(guestUserId, "DEMO");
            keys.InsertAccountKey(guestUserId, "NORMAL");

            _logger.Info("Database Reset!");
        }
    }
}
