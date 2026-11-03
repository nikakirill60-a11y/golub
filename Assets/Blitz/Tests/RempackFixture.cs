using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using BlitzLegacy.Core.Mods.Rempack;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Tests
{
    /// <summary>Small Remaster Packages built in memory for the package tests.</summary>
    internal sealed class RempackFixture
    {
        /// <summary>What the tests pretend the game loads; the real list grows with the loaders.</summary>
        public static readonly RempackFeatureSet Features = new RempackFeatureSet("tanks@1", "hangars@1", "maps@1", "overrides@1");

        public readonly JsonNode Manifest;
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        private RempackFixture(JsonNode manifest)
        {
            Manifest = manifest;
        }

        public static RempackValidationOptions Options()
        {
            return new RempackValidationOptions { Features = Features, GameVersion = new SemanticVersion(2, 0, 0) };
        }

        /// <summary>A valid package adding one tank.</summary>
        public static RempackFixture Tank(string id = "author.test_mod", string version = "1.0.0", string tank = "t1")
        {
            var manifest = JsonNode.Parse("{\"format\": \"rempack\", \"formatVersion\": 1}");
            manifest.Set("id", id);
            manifest.Set("version", version);
            manifest.Set("name", JsonNode.Parse("{\"en\": \"Test mod\", \"ru\": \"\u0422\u0435\u0441\u0442\u043e\u0432\u044b\u0439 \u043c\u043e\u0434\"}"));
            manifest.Set("authors", JsonNode.Parse("[\"Tester\"]"));
            manifest.Set("game", JsonNode.Parse("{\"features\": [\"tanks@1\"]}"));
            manifest.Set("content", JsonNode.Parse("{\"tanks\": [\"" + tank + "\"]}"));
            var fixture = new RempackFixture(manifest);
            fixture.Text("tanks/" + tank + "/tank.json", "{\"name\": \"" + tank + "\"}");
            return fixture;
        }

        public RempackFixture Text(string path, string text)
        {
            Files[path] = Encoding.UTF8.GetBytes(text);
            return this;
        }

        public RempackFixture Bytes(string path, byte[] data)
        {
            Files[path] = data;
            return this;
        }

        /// <summary>Sets a manifest field to raw JSON.</summary>
        public RempackFixture Set(string key, string json)
        {
            Manifest.Set(key, JsonNode.Parse(json));
            return this;
        }

        public RempackFixture Depends(string id, string range)
        {
            return Reference("dependencies", id, range);
        }

        public RempackFixture Conflicts(string id, string range)
        {
            return Reference("conflicts", id, range);
        }

        /// <summary>Declares stock replacements (and the feature they need).</summary>
        public RempackFixture Overrides(params string[] references)
        {
            var list = JsonNode.NewArray();
            foreach (var reference in references)
            {
                list.Add(JsonNode.Str(reference));
            }

            Manifest.Set("overrides", list);
            Manifest["game"]["features"].Add(JsonNode.Str(RempackFormat.OverridesFeature + "@1"));
            return this;
        }

        public byte[] Build()
        {
            return RempackBuilder.Pack(Manifest, Files);
        }

        /// <summary>
        /// The package with its file list edited after hashing and files added that the
        /// list does not name: what a hand-edited or half-repacked archive looks like.
        /// </summary>
        public byte[] Tampered(Action<JsonNode> editFiles, IDictionary<string, byte[]> unlisted = null)
        {
            Build();
            if (editFiles != null)
            {
                editFiles(Manifest["files"]);
            }

            var writer = new RempackZipWriter();
            foreach (var pair in Files)
            {
                writer.Add(pair.Key, pair.Value);
            }

            if (unlisted != null)
            {
                foreach (var pair in unlisted)
                {
                    writer.Add(pair.Key, pair.Value);
                }
            }

            writer.Add(RempackFormat.ManifestFile, Encoding.UTF8.GetBytes(Manifest.ToJson(true)));
            using (var output = new MemoryStream())
            {
                writer.Write(output);
                return output.ToArray();
            }
        }

        /// <summary>Writes the package into <paramref name="folder"/>, as <c>&lt;id&gt;.rempack</c> unless named.</summary>
        public string Write(string folder, string fileName = null)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, fileName ?? Manifest["id"].AsString() + RempackFormat.Extension);
            File.WriteAllBytes(path, Build());
            return path;
        }

        public RempackReport Validate()
        {
            return Validate(Build(), Options());
        }

        public RempackReport Validate(RempackValidationOptions options)
        {
            return Validate(Build(), options);
        }

        public static RempackReport Validate(byte[] package, RempackValidationOptions options)
        {
            var report = new RempackReport();
            using (var opened = RempackPackage.Open(new MemoryStream(package), false, report))
            {
                if (opened != null)
                {
                    RempackValidator.Validate(opened, options, report);
                }
            }

            return report;
        }

        /// <summary>The header of a PNG of the given size: all the validator reads.</summary>
        public static byte[] Png(int width, int height)
        {
            var data = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
            data.AddRange(BigEndian(width));
            data.AddRange(BigEndian(height));
            data.AddRange(new byte[] { 8, 6, 0, 0, 0, 0, 0, 0, 0 });
            data.AddRange(new byte[] { 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82 });
            return data.ToArray();
        }

        private RempackFixture Reference(string key, string id, string range)
        {
            if (!Manifest.Has(key))
            {
                Manifest.Set(key, JsonNode.NewArray());
            }

            var item = JsonNode.NewObject();
            item.Set("id", id);
            item.Set("version", range);
            Manifest[key].Add(item);
            return this;
        }

        private static byte[] BigEndian(int value)
        {
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }
    }

    /// <summary>
    /// Writes ZIP files byte by byte with whatever headers a test asks for, so the reader
    /// can be shown the broken and hostile archives no normal tool produces.
    /// </summary>
    internal sealed class RawZip
    {
        private readonly MemoryStream _data = new MemoryStream();
        private readonly MemoryStream _central = new MemoryStream();
        private int _count;

        public ushort Disk;

        public int Count
        {
            get { return _count; }
        }

        /// <summary>One entry. Sizes and CRC default to the truth about <paramref name="data"/>.</summary>
        public RawZip Add(string name, byte[] data, ushort method = 0, ushort flags = 0x0800, ushort madeBy = 20,
            uint external = 0, long? size = null, uint? crc = null, long? localOffset = null)
        {
            data = data ?? new byte[0];
            var payload = method == 8 ? Deflate(data) : data;
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var realCrc = crc ?? RempackZipReader.Crc32Of(data, 0, data.Length);
            var declared = size ?? data.Length;
            var offset = _data.Position;
            Put32(_data, 0x04034b50);
            Put16(_data, 20);
            Put16(_data, flags);
            Put16(_data, method);
            Put16(_data, 0);
            Put16(_data, 0x0021);
            Put32(_data, realCrc);
            Put32(_data, (uint)payload.Length);
            Put32(_data, (uint)declared);
            Put16(_data, (ushort)nameBytes.Length);
            Put16(_data, 0);
            _data.Write(nameBytes, 0, nameBytes.Length);
            _data.Write(payload, 0, payload.Length);

            Put32(_central, 0x02014b50);
            Put16(_central, madeBy);
            Put16(_central, 20);
            Put16(_central, flags);
            Put16(_central, method);
            Put16(_central, 0);
            Put16(_central, 0x0021);
            Put32(_central, realCrc);
            Put32(_central, (uint)payload.Length);
            Put32(_central, (uint)declared);
            Put16(_central, (ushort)nameBytes.Length);
            Put16(_central, 0);
            Put16(_central, 0);
            Put16(_central, 0);
            Put16(_central, 0);
            Put32(_central, external);
            Put32(_central, (uint)(localOffset ?? offset));
            _central.Write(nameBytes, 0, nameBytes.Length);
            _count++;
            return this;
        }

        /// <summary>
        /// A stored entry whose data holds a complete second entry, and a directory record
        /// for that inner entry: two records sharing bytes.
        /// </summary>
        public RawZip AddNested(string outer, string inner, byte[] data)
        {
            var innerName = Encoding.UTF8.GetBytes(inner);
            var header = new MemoryStream();
            Put32(header, 0x04034b50);
            Put16(header, 20);
            Put16(header, 0x0800);
            Put16(header, 0);
            Put16(header, 0);
            Put16(header, 0x0021);
            Put32(header, RempackZipReader.Crc32Of(data, 0, data.Length));
            Put32(header, (uint)data.Length);
            Put32(header, (uint)data.Length);
            Put16(header, (ushort)innerName.Length);
            Put16(header, 0);
            header.Write(innerName, 0, innerName.Length);
            header.Write(data, 0, data.Length);
            var outerData = header.ToArray();
            var outerOffset = _data.Position;
            Add(outer, outerData);
            var innerOffset = outerOffset + 30 + Encoding.UTF8.GetByteCount(outer);
            AddRecordOnly(inner, data, innerOffset);
            return this;
        }

        public byte[] ToArray()
        {
            var output = new MemoryStream();
            var data = _data.ToArray();
            output.Write(data, 0, data.Length);
            var central = _central.ToArray();
            output.Write(central, 0, central.Length);
            Put32(output, 0x06054b50);
            Put16(output, Disk);
            Put16(output, Disk);
            Put16(output, (ushort)_count);
            Put16(output, (ushort)_count);
            Put32(output, (uint)central.Length);
            Put32(output, (uint)data.Length);
            Put16(output, 0);
            return output.ToArray();
        }

        private void AddRecordOnly(string name, byte[] data, long localOffset)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            Put32(_central, 0x02014b50);
            Put16(_central, 20);
            Put16(_central, 20);
            Put16(_central, 0x0800);
            Put16(_central, 0);
            Put16(_central, 0);
            Put16(_central, 0x0021);
            Put32(_central, RempackZipReader.Crc32Of(data, 0, data.Length));
            Put32(_central, (uint)data.Length);
            Put32(_central, (uint)data.Length);
            Put16(_central, (ushort)nameBytes.Length);
            Put16(_central, 0);
            Put16(_central, 0);
            Put16(_central, 0);
            Put16(_central, 0);
            Put32(_central, 0);
            Put32(_central, (uint)localOffset);
            _central.Write(nameBytes, 0, nameBytes.Length);
            _count++;
        }

        private static byte[] Deflate(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    deflate.Write(data, 0, data.Length);
                }

                return output.ToArray();
            }
        }

        private static void Put16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
        }

        private static void Put32(Stream stream, uint value)
        {
            Put16(stream, (ushort)value);
            Put16(stream, (ushort)(value >> 16));
        }
    }
}
