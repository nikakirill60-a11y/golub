using System;
using System.Collections.Generic;
using System.IO;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// The command line: a command, its paths and options anywhere in between.
    /// <c>--name=value</c> and <c>--name value</c> both work, <c>--</c> ends the options.
    /// A lone existing path without a command is what a drop on the executable gives:
    /// a folder is packed, a file inspected.
    /// </summary>
    internal sealed class CliOptions
    {
        public const string Validate = "validate";
        public const string Pack = "pack";
        public const string Inspect = "inspect";
        public const string Help = "help";

        private static readonly string[] Commands = { Validate, Pack, Inspect, Help };

        public string Command;
        public readonly List<string> Arguments = new List<string>();
        public string Output;
        public string Language;
        public bool Json;
        public bool Strict;
        public bool Verbose;
        public bool Files;
        public bool NoColor;
        public bool ShowHelp;
        public bool ShowVersion;

        /// <summary>The command came from a dropped path, not from the command line.</summary>
        public bool Implicit;

        /// <summary>The <c>sdk_*</c> key of a usage error; null when the line parsed.</summary>
        public string Error;
        public string ErrorArgument;

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            var positional = new List<string>();
            var literal = false;
            args = args ?? new string[0];
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i] ?? string.Empty;
                if (!literal && arg == "/?")
                {
                    options.ShowHelp = true;
                    continue;
                }

                if (literal || arg.Length < 2 || arg[0] != '-')
                {
                    positional.Add(arg);
                    continue;
                }

                if (arg == "--")
                {
                    literal = true;
                    continue;
                }

                var name = arg;
                string value = null;
                var equals = arg.IndexOf('=');
                if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
                {
                    name = arg.Substring(0, equals);
                    value = arg.Substring(equals + 1);
                }

                if (name == "-o" || name == "--output" || name == "--lang")
                {
                    if (value == null)
                    {
                        if (i + 1 >= args.Length)
                        {
                            return options.Fail(CliKeys.OptionValue, name);
                        }

                        value = args[++i] ?? string.Empty;
                    }

                    if (name == "--lang")
                    {
                        if (!CliLanguage.IsSupported(value))
                        {
                            return options.Fail(CliKeys.BadLanguage, value);
                        }

                        options.Language = value;
                    }
                    else
                    {
                        if (value.Length == 0)
                        {
                            return options.Fail(CliKeys.OptionValue, name);
                        }

                        options.Output = value;
                    }

                    continue;
                }

                if (value != null || !options.Flag(name))
                {
                    return options.Fail(CliKeys.UnknownOption, arg);
                }
            }

            if (positional.Count > 0)
            {
                if (Array.IndexOf(Commands, positional[0]) >= 0)
                {
                    options.Command = positional[0];
                    positional.RemoveAt(0);
                }
                else if (positional.Count == 1 && (Directory.Exists(positional[0]) || File.Exists(positional[0])))
                {
                    options.Command = Directory.Exists(positional[0]) ? Pack : Inspect;
                    options.Implicit = true;
                }
                else
                {
                    return options.Fail(CliKeys.UnknownCommand, positional[0]);
                }
            }

            options.Arguments.AddRange(positional);
            return options;
        }

        private bool Flag(string name)
        {
            switch (name)
            {
                case "--json":
                    Json = true;
                    return true;
                case "--strict":
                    Strict = true;
                    return true;
                case "-v":
                case "--verbose":
                    Verbose = true;
                    return true;
                case "--files":
                    Files = true;
                    return true;
                case "--no-color":
                case "--no-colour":
                    NoColor = true;
                    return true;
                case "-h":
                case "--help":
                    ShowHelp = true;
                    return true;
                case "--version":
                    ShowVersion = true;
                    return true;
                default:
                    return false;
            }
        }

        private CliOptions Fail(string key, string argument)
        {
            Error = key;
            ErrorArgument = argument ?? string.Empty;
            return this;
        }
    }
}
