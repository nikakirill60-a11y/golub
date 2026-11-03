using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// The <c>rempack</c> tool: parses the command line and runs a command. Exit codes:
    /// 0 the package is accepted (or help was asked for), 1 it is refused, 2 the command
    /// line is wrong, 3 a file could not be written.
    /// </summary>
    internal sealed class Cli
    {
        public const int AcceptedExit = 0;
        public const int RefusedExit = 1;
        public const int UsageExit = 2;
        public const int FailedExit = 3;

        private readonly TextWriter _output;
        private readonly TextWriter _error;
        private readonly bool _console;

        /// <param name="console">The writers are the terminal, so colour may be used.</param>
        public Cli(TextWriter output, TextWriter error, bool console)
        {
            _output = output;
            _error = error;
            _console = console;
            Validation = new RempackValidationOptions();
            TempFolder = Path.GetTempPath();
            Texts = new CliTexts("en");
        }

        /// <summary>What the game accepts; by default the game this SDK was built with.</summary>
        public RempackValidationOptions Validation { get; set; }

        public string TempFolder { get; set; }

        /// <summary>The words of the last run, in its language.</summary>
        public CliTexts Texts { get; private set; }

        public int Run(string[] args)
        {
            args = args ?? new string[0];
            var options = CliOptions.Parse(args);
            if (options.Error != null && WantsJson(args))
            {
                // The parse stopped at the bad argument; a script that asked for JSON still gets it.
                options.Json = true;
            }

            Texts = new CliTexts(options.Language ?? CliLanguage.Detect());
            var context = new CliContext(options, Texts, _output, _error, _console && WantsColor(options));
            context.Validation = Validation;
            context.TempFolder = TempFolder;
            if (options.Error != null)
            {
                return context.UsageError(options.Error, options.ErrorArgument);
            }

            if (options.ShowVersion)
            {
                return Version(context);
            }

            var command = options.Command;
            if (command == null)
            {
                Help(null);
                return options.ShowHelp ? AcceptedExit : UsageExit;
            }

            if (command == CliOptions.Help)
            {
                var topic = options.Arguments.Count > 0 ? options.Arguments[0] : null;
                if (options.Arguments.Count > 1)
                {
                    return context.UsageError(CliKeys.ExtraArgument, options.Arguments[1]);
                }

                if (topic != null && topic != CliOptions.Validate && topic != CliOptions.Pack && topic != CliOptions.Inspect)
                {
                    return context.UsageError(CliKeys.UnknownCommand, topic);
                }

                Help(topic);
                return AcceptedExit;
            }

            if (options.ShowHelp)
            {
                Help(command);
                return AcceptedExit;
            }

            if (options.Arguments.Count == 0)
            {
                return context.UsageError(CliKeys.MissingPath, string.Empty);
            }

            if (options.Arguments.Count > 1)
            {
                return context.UsageError(CliKeys.ExtraArgument, options.Arguments[1]);
            }

            var path = options.Arguments[0];
            switch (command)
            {
                case CliOptions.Validate:
                    return ValidateCommand.Run(context, path);
                case CliOptions.Pack:
                    return PackCommand.Run(context, path);
                default:
                    return InspectCommand.Run(context, path);
            }
        }

        /// <summary>The help is for people, so it is plain text even with <c>--json</c>.</summary>
        private void Help(string command)
        {
            string text;
            switch (command)
            {
                case CliOptions.Validate:
                    text = Texts.Get(CliKeys.UsageValidate);
                    break;
                case CliOptions.Pack:
                    text = Texts.Get(CliKeys.UsagePack);
                    break;
                case CliOptions.Inspect:
                    text = Texts.Get(CliKeys.UsageInspect);
                    break;
                default:
                    text = Texts.Get(CliKeys.Usage, BlitzVersion.Current);
                    break;
            }

            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                _output.WriteLine(line);
            }
        }

        private int Version(CliContext context)
        {
            var features = new List<string>(Validation.Features.Names);
            if (context.Json)
            {
                var document = context.Document("version");
                document.Set("ok", true);
                document.Set("formatVersion", RempackFormat.FormatVersion);
                document.Set("gameVersion", Validation.GameVersion != null ? Validation.GameVersion.ToString() : string.Empty);
                document.Set("features", PackCommand.Strings(features));
                context.Emit(document);
            }
            else
            {
                context.Say(CliKeys.Version, BlitzVersion.Current,
                    RempackFormat.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    features.Count > 0 ? string.Join(", ", features) : Texts.Get(CliKeys.NoFeatures));
            }

            return AcceptedExit;
        }

        private static bool WantsJson(string[] args)
        {
            foreach (var arg in args)
            {
                if (arg == "--")
                {
                    return false;
                }

                if (arg == "--json")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Colour unless asked not to (<c>--no-color</c>, <c>NO_COLOR</c>) or the output is a file or a pipe.</summary>
        private static bool WantsColor(CliOptions options)
        {
            return !options.NoColor && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("TERM") != "dumb";
        }
    }
}
