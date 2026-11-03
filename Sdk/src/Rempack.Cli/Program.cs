using System;
using System.IO;
using System.Text;

namespace BlitzLegacy.Sdk
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                // Russian texts and package paths print the same on every console.
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (IOException)
            {
                // No console to configure (output to a file or a pipe): UTF-8 is used anyway.
            }

            var cli = new Cli(Console.Out, Console.Error, true);
            var exit = cli.Run(args);
            if (CliLanguage.OwnsConsoleWindow())
            {
                // A folder dropped on rempack.exe opens a window that would close with the report.
                Console.WriteLine();
                Console.WriteLine(cli.Texts.Get(CliKeys.PressEnter));
                Console.ReadLine();
            }

            return exit;
        }
    }
}
