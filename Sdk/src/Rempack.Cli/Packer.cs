using System.IO;
using BlitzLegacy.Core.Mods.Rempack;

namespace BlitzLegacy.Sdk
{
    /// <summary>What a written package holds, for the lines after packing.</summary>
    internal sealed class PackageFacts
    {
        public int Files;
        public long Size;
        public long Unpacked;
    }

    /// <summary>
    /// Packs a folder and checks the result. The check reads the written bytes back with
    /// the game's own reader and validator, so whatever passes here installs in the game.
    /// </summary>
    internal static class Packer
    {
        /// <summary>
        /// Packs <paramref name="source"/> into <paramref name="output"/>. A file that cannot
        /// be packed goes to <paramref name="report"/> and false is returned; an I/O error
        /// of the output itself is thrown.
        /// </summary>
        public static bool Build(SourceFolder source, Stream output, RempackReport report)
        {
            try
            {
                RempackBuilder.Pack(source.Manifest, source.Files, output);
                output.Flush();
                return true;
            }
            catch (RempackException error)
            {
                report.Add(error.Issue);
                return false;
            }
        }

        /// <summary>Checks the package in <paramref name="stream"/>; null when it does not even open.</summary>
        public static PackageFacts Check(Stream stream, RempackValidationOptions options, RempackReport report)
        {
            stream.Position = 0;
            using (var package = RempackPackage.Open(stream, true, report))
            {
                if (package == null)
                {
                    return null;
                }

                RempackValidator.Validate(package, options, report);
                return Facts(package);
            }
        }

        public static PackageFacts Facts(RempackPackage package)
        {
            return new PackageFacts
            {
                Files = package.Zip.Entries.Count,
                Size = package.Size,
                Unpacked = package.Zip.UnpackedSize,
            };
        }
    }
}
