using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Tests;

namespace BlitzLegacy.Sdk.Tests
{
    /// <summary>A mod folder on disk, <c>my_mod</c> inside a scratch folder that is removed afterwards.</summary>
    internal sealed class ModFolder : IDisposable
    {
        public ModFolder()
        {
            Base = Path.Combine(Path.GetTempPath(), "rempack_sdk_" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(Base, "my_mod");
            Temp = Path.Combine(Base, "temp");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Temp);
        }

        /// <summary>The scratch folder: the default package lands here, beside <see cref="Root"/>.</summary>
        public string Base { get; private set; }

        public string Root { get; private set; }

        /// <summary>Where the tool's own temporary files go during a test.</summary>
        public string Temp { get; private set; }

        /// <summary>A folder holding the fixture's manifest (without <c>files</c>) and files.</summary>
        public static ModFolder Of(RempackFixture fixture)
        {
            var folder = new ModFolder();
            folder.Write(fixture);
            return folder;
        }

        public ModFolder Write(RempackFixture fixture)
        {
            Text(RempackFormat.ManifestFile, fixture.Manifest.ToJson(true));
            foreach (var pair in fixture.Files)
            {
                Bytes(pair.Key, pair.Value);
            }

            return this;
        }

        public ModFolder Text(string relative, string text)
        {
            return Bytes(relative, new UTF8Encoding(false).GetBytes(text));
        }

        public ModFolder Bytes(string relative, byte[] data)
        {
            var path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
            return this;
        }

        public string PathOf(string relative)
        {
            return Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Where <c>pack</c> writes the fixture's package by default.</summary>
        public string DefaultPackage
        {
            get { return Path.Combine(Base, "author.test_mod-1.0.0.rempack"); }
        }

        /// <summary>Every file below <paramref name="folder"/>, relative, sorted.</summary>
        public static List<string> Listing(string folder)
        {
            var files = new List<string>();
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                files.Add(Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/'));
            }

            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>Entry names of a package file.</summary>
        public static List<string> Entries(string package)
        {
            var names = new List<string>();
            var report = new RempackReport();
            using (var opened = RempackPackage.Open(package, report))
            {
                foreach (var entry in opened.Zip.Entries)
                {
                    names.Add(entry.Path);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Base, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>One run of the tool with its output captured.</summary>
    internal sealed class CliRun
    {
        public int Exit;
        public string Output;
        public string Error;

        public JsonNode Json
        {
            get { return JsonNode.Parse(Output); }
        }

        /// <summary>
        /// Runs the tool with the features the tests pretend the game loads, in English
        /// unless the arguments choose a language.
        /// </summary>
        public static CliRun Of(ModFolder folder, params string[] args)
        {
            var list = new List<string>(args);
            if (!list.Contains("--lang"))
            {
                list.InsertRange(0, new[] { "--lang", "en" });
            }

            var output = new StringWriter();
            var error = new StringWriter();
            var cli = new Cli(output, error, false) { Validation = RempackFixture.Options() };
            if (folder != null)
            {
                cli.TempFolder = folder.Temp;
            }

            var exit = cli.Run(list.ToArray());
            return new CliRun { Exit = exit, Output = output.ToString(), Error = error.ToString() };
        }

        /// <summary>The codes of the issues in a <c>--json</c> run.</summary>
        public List<string> Codes()
        {
            var codes = new List<string>();
            foreach (var issue in Json["issues"].Items)
            {
                codes.Add(issue["code"].AsString());
            }

            return codes;
        }

        /// <summary>The paths of the issues with <paramref name="code"/> in a <c>--json</c> run.</summary>
        public List<string> Paths(string code)
        {
            var paths = new List<string>();
            foreach (var issue in Json["issues"].Items)
            {
                if (issue["code"].AsString() == code)
                {
                    paths.Add(issue["path"].AsString());
                }
            }

            return paths;
        }

        public override string ToString()
        {
            return "exit " + Exit + "\n" + Output + Error;
        }
    }
}
