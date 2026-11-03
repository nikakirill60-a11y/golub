using System;
using System.IO;
using BlitzLegacy.Core;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// One run of the tool: its options, words and writers. Lines for people go to
    /// standard output and complaints about the command line to standard error; with
    /// <c>--json</c> standard output carries one JSON document and nothing else.
    /// </summary>
    internal sealed class CliContext
    {
        private readonly TextWriter _output;
        private readonly TextWriter _error;
        private readonly bool _color;

        public CliContext(CliOptions options, CliTexts texts, TextWriter output, TextWriter error, bool color)
        {
            Options = options;
            Texts = texts;
            _output = output;
            _error = error;
            _color = color && !options.Json;
            Validation = new RempackValidationOptions();
            TempFolder = Path.GetTempPath();
        }

        public CliOptions Options { get; private set; }
        public CliTexts Texts { get; private set; }

        /// <summary>What the game would accept; the tests swap in the features of later stages.</summary>
        public RempackValidationOptions Validation { get; set; }

        /// <summary>Where <c>validate</c> packs a folder for its check.</summary>
        public string TempFolder { get; set; }

        public bool Json
        {
            get { return Options.Json; }
        }

        public bool Verbose
        {
            get { return Options.Verbose; }
        }

        /// <summary>A localized line for people; nothing in JSON mode.</summary>
        public void Say(string key, params string[] args)
        {
            Line(Texts.Get(key, args));
        }

        public void Say(ConsoleColor color, string key, params string[] args)
        {
            Line(color, Texts.Get(key, args));
        }

        /// <summary>Writes text line by line, so a multi-line row ends lines the way the system does.</summary>
        public void Line(string text)
        {
            if (Json)
            {
                return;
            }

            foreach (var line in Lines(text))
            {
                _output.WriteLine(line);
            }
        }

        public void Line(ConsoleColor color, string text)
        {
            if (Json)
            {
                return;
            }

            Write(color, text);
            _output.WriteLine();
        }

        /// <summary>Part of a line, coloured when the output is a terminal that wants colour.</summary>
        public void Write(ConsoleColor color, string text)
        {
            if (Json)
            {
                return;
            }

            if (!_color)
            {
                _output.Write(text);
                return;
            }

            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            _output.Write(text);
            _output.Flush();
            Console.ForegroundColor = previous;
        }

        public void Write(string text)
        {
            if (!Json)
            {
                _output.Write(text);
            }
        }

        /// <summary>The base of every JSON document: which tool, which version, which command.</summary>
        public JsonNode Document(string command)
        {
            var document = JsonNode.NewObject();
            document.Set("tool", RempackFormat.Name);
            document.Set("version", BlitzVersion.Current);
            document.Set("command", command ?? string.Empty);
            document.Set("language", Texts.Language);
            return document;
        }

        public void Emit(JsonNode document)
        {
            if (Json)
            {
                _output.WriteLine(document.ToJson(true));
            }
        }

        /// <summary>A command line the tool cannot run: says why and where the help is.</summary>
        public int UsageError(string key, string argument)
        {
            var message = Texts.Get(key, argument ?? string.Empty);
            if (Json)
            {
                Emit(Refusal("usage", key, message));
            }
            else
            {
                _error.WriteLine(message);
                _error.WriteLine(Texts.Get(CliKeys.SeeHelp));
            }

            return Cli.UsageExit;
        }

        /// <summary>A file the tool could not read or write.</summary>
        public int Failure(string key, params string[] args)
        {
            var message = Texts.Get(key, args);
            if (Json)
            {
                Emit(Refusal("failed", key, message));
            }
            else
            {
                foreach (var line in Lines(message))
                {
                    _error.WriteLine(line);
                }
            }

            return Cli.FailedExit;
        }

        private JsonNode Refusal(string status, string key, string message)
        {
            var document = Document(Options.Command);
            document.Set("ok", false);
            document.Set("status", status);
            var error = JsonNode.NewObject();
            error.Set("code", key);
            error.Set("message", message);
            document.Set("error", error);
            return document;
        }

        private static string[] Lines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        }
    }
}
