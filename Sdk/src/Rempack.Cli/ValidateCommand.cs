using System;
using System.IO;
using BlitzLegacy.Core.Mods.Rempack;

namespace BlitzLegacy.Sdk
{
    /// <summary>
    /// <c>rempack validate &lt;folder|file&gt;</c>: checks a package exactly as the game does
    /// when it installs one. A folder is packed into a scratch file first, so its check
    /// covers the container and the hashes too, and nothing is written beside it.
    /// </summary>
    internal static class ValidateCommand
    {
        public static int Run(CliContext context, string path)
        {
            if (Directory.Exists(path))
            {
                return Folder(context, path);
            }

            if (!File.Exists(path))
            {
                return context.UsageError(CliKeys.NotFound, path);
            }

            context.Say(CliKeys.Checking, path);
            var full = Path.GetFullPath(path);
            var report = new RempackReport();
            using (var package = RempackPackage.Open(full, report))
            {
                if (package != null)
                {
                    RempackValidator.Validate(package, context.Validation, report);
                }
            }

            return Finish(context, full, report, null);
        }

        private static int Folder(CliContext context, string path)
        {
            context.Say(CliKeys.Checking, path);
            var source = SourceFolder.Scan(path);
            var report = new RempackReport();
            report.AddRange(source.Report);
            if (source.CanPack)
            {
                string temp = null;
                try
                {
                    using (var stream = TempFiles.Create(context.TempFolder, "rempack-", true, out temp))
                    {
                        if (Packer.Build(source, stream, report))
                        {
                            Packer.Check(stream, context.Validation, report);
                        }
                    }
                }
                catch (Exception error) when (SourceFolder.IsFileError(error))
                {
                    return context.Failure(CliKeys.WriteFailed, temp ?? context.TempFolder, error.Message);
                }
                finally
                {
                    TempFiles.Delete(temp);
                }
            }

            PackCommand.Listed(context, source);
            return Finish(context, source.Root, report, source);
        }

        private static int Finish(CliContext context, string path, RempackReport report, SourceFolder source)
        {
            ReportOutput.Print(context, report);
            var exit = ReportOutput.Verdict(context, report);
            if (context.Json)
            {
                var document = context.Document(CliOptions.Validate);
                document.Set("path", path);
                document.Set("folder", source != null);
                if (source != null)
                {
                    document.Set("skipped", PackCommand.Strings(source.Skipped));
                    document.Set("links", PackCommand.Strings(source.Links));
                }

                ReportOutput.Describe(document, context, report);
                context.Emit(document);
            }

            return exit;
        }
    }
}
