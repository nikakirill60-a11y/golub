using System;
using System.Runtime.InteropServices;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// The language of the messages: <c>--lang</c>, then <c>REMPACK_LANG</c>, then the
    /// user interface language of the system. The CLI runs with invariant globalization
    /// (no ICU on the modder's machine is needed), so it asks Windows directly and reads
    /// the POSIX locale variables elsewhere.
    /// </summary>
    internal static class CliLanguage
    {
        public const string Variable = "REMPACK_LANG";
        private static readonly string[] LocaleVariables = { "LC_ALL", "LC_MESSAGES", "LANG" };

        public static bool IsSupported(string language)
        {
            return language == "en" || language == "ru";
        }

        public static string Detect()
        {
            var forced = Environment.GetEnvironmentVariable(Variable);
            if (IsSupported(forced))
            {
                return forced;
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsLanguage();
            }

            foreach (var name in LocaleVariables)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                {
                    return FromLocale(value);
                }
            }

            return "en";
        }

        /// <summary><c>ru</c> for <c>ru_RU.UTF-8</c> and the like, <c>en</c> for anything else.</summary>
        public static string FromLocale(string locale)
        {
            return !string.IsNullOrEmpty(locale) && locale.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        }

        /// <summary>
        /// Whether the console window lives only as long as this process: Explorer started
        /// the tool for a double click or a drop, and the window would close with the result.
        /// </summary>
        public static bool OwnsConsoleWindow()
        {
            if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                return false;
            }

            try
            {
                var processes = new uint[4];
                return GetConsoleProcessList(processes, (uint)processes.Length) == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string WindowsLanguage()
        {
            try
            {
                // LANG_RUSSIAN is primary language 0x19 of the UI language id.
                return (GetUserDefaultUILanguage() & 0x3FF) == 0x19 ? "ru" : "en";
            }
            catch (Exception)
            {
                return "en";
            }
        }

        [DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
    }
}
