using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>Validated data-only portable mods. Source code is never loaded.</summary>
    public sealed class PortableModPackage
    {
        public string Id;
        public string DisplayName;
        public int Version;
        public readonly List<ModVehicleAssets> Tanks = new List<ModVehicleAssets>();
        private const long Budget = 512L * 1024 * 1024;
        private const int MetadataLimit = 1024 * 1024;

        public static bool Present(string root) { return File.Exists(Path.Combine(root, "manifest.json")); }

        private static void Check(bool ok) { if (!ok) { throw new ModArchiveException("mods_error_portable_invalid"); } }

        public static string Relative(string value)
        {
            Check(!string.IsNullOrEmpty(value) && !value.Contains("\\") && !value.Contains(":") && !value.Contains("\0") && !Path.IsPathRooted(value));
            foreach (var part in value.Split('/')) { Check(part.Length > 0 && part != "." && part != ".."); }
            return value;
        }

        private static string FileAt(string root, string relative)
        {
            var path = Path.Combine(root, Relative(relative));
            Check(File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
            var parent = Path.GetDirectoryName(path);
            while (!string.Equals(parent, root, StringComparison.Ordinal))
            {
                Check(parent != null && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0);
                parent = Path.GetDirectoryName(parent);
            }
            return path;
        }

        private static JsonNode ReadJson(string path)
        {
            Check(new FileInfo(path).Length <= MetadataLimit);
            return ParseBounded(File.ReadAllText(path));
        }

        private static JsonNode ParseBounded(string text)
        {
            var depth = 0; var quoted = false; var escape = false;
            foreach (var c in text)
            {
                if (quoted)
                {
                    if (escape) { escape = false; }
                    else if (c == '\\') { escape = true; }
                    else if (c == '"') { quoted = false; }
                }
                else if (c == '"') { quoted = true; }
                else if (c == '{' || c == '[') { depth++; Check(depth <= 64); }
                else if (c == '}' || c == ']') { depth--; Check(depth >= 0); }
            }
            Check(depth == 0 && !quoted);
            return JsonNode.Parse(text);
        }

        public static PortableModPackage Read(string root)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            Check((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0);
            var manifest = ReadJson(FileAt(root, "manifest.json"));
            if (manifest["schemaVersion"].AsInt() != 1) { throw new ModArchiveException("mods_error_portable_version"); }
            Check(manifest["format"].AsString() == "blitz-portable-mod");
            Guid guid;
            Check(Guid.TryParse(manifest["guid"].AsString(), out guid));
            var package = new PortableModPackage { Id = "pw_" + guid.ToString("N"), DisplayName = manifest["displayName"].AsString(), Version = manifest["version"].AsInt() };
            Check(package.Version > 0 && package.DisplayName.Length > 0 && package.DisplayName.Length <= 256);
            var hashes = manifest["files"];
            Check(hashes.Kind == JsonKind.Object && hashes.Count <= 4096);
            long total = 0;
            var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in hashes.Fields)
            {
                Check(verified.Add(Relative(pair.Key)));
                var path = FileAt(root, pair.Key);
                total += new FileInfo(path).Length;
                Check(total <= Budget);
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                {
                    var digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    Check(string.Equals(digest, pair.Value.AsString(), StringComparison.Ordinal));
                }
            }
            foreach (var path in SafeFiles(root))
            {
                var relative = path.Substring(root.Length + 1).Replace('\\', '/');
                Check(relative == "manifest.json" || verified.Contains(relative));
            }
            var vehicles = manifest["vehicles"];
            Check(vehicles.Kind == JsonKind.Array && vehicles.Count > 0 && vehicles.Count <= 64);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long decodedPixels = 0;
            foreach (var vehicle in vehicles.Items)
            {
                Guid vg; Check(Guid.TryParse(vehicle["guid"].AsString(), out vg));
                var id = package.Id + "_" + vg.ToString("N");
                Check(vehicle["id"].AsString() == id && ids.Add(id));
                var prefix = Relative(vehicle["path"].AsString()) + "/";
                Check(prefix == "vehicles/" + vg.ToString("D") + "/");
                string Required(string suffix)
                {
                    Check(verified.Contains(prefix + suffix));
                    return FileAt(root, prefix + suffix);
                }
                var cardJson = ReadJson(Required("tank.json"));
                var card = TankInfo.FromJson(cardJson);
                card.TurretRotationSpeed = cardJson["turret_speed_radians"].AsFloat();
                Check(Finite(card.TurretRotationSpeed, true));
                Check(card.Name == id && card.TotalHealth > 0 && card.TotalHealth <= 1000000 && card.Level >= 1 && card.Level <= 10);
                Check(card.Guns.Count == 1 && card.Engines.Count > 0 && card.Tracks.Count > 0);
                var gun = card.Guns[0];
                Check(Finite(gun.Reload, true) && Finite(card.HullWeight, true) && Finite(card.SpeedForward, true)
                    && Finite(card.SpeedBackward, false) && Finite(card.Engines[0].Power, true) && gun.MaxAmmo > 0
                    && gun.Shells.Count > 0 && gun.Shells.Count <= 16 && card.TurretHealth == 0);
                foreach (var shell in gun.Shells)
                {
                    Check(Finite(shell.Damage, true) && Finite(shell.Penetration, false) && Finite(shell.Speed, true)
                        && shell.Type >= ShellType.Ap && shell.Type <= ShellType.Hesh && shell.Caliber > 0);
                }
                // A magazine gun (BURST or AUTOLOAD) needs a sane drum: the
                // battle clock indexes the burst list and the autoload
                // timers, so an empty or negative drum would hang or crash
                // the first shot instead of failing the install.
                Check(gun.FireMode >= 0 && gun.FireMode <= 2);
                if (gun.FireMode != 0)
                {
                    Check(gun.Burst != null && gun.Burst.Bursts.Count > 0);
                    foreach (var burstShells in gun.Burst.Bursts)
                    {
                        Check(burstShells > 0);
                    }

                    Check(Finite(gun.Burst.IntraClip, false) && Finite(gun.Burst.IntraBurst, false));
                }

                if (gun.AutoReload)
                {
                    foreach (var seconds in gun.AutoReloadTimes)
                    {
                        Check(Finite(seconds, true));
                    }
                }
                var tank = new ModVehicleAssets { Id = id, PortableCard = card, ModelPath = Required("model.glb"),
                    DefaultName = card.DisplayName, RussianName = cardJson["localized_names"]["ru"].AsString(), NodesPath = Required("nodes.json"), ArmorPath = Required("armor.json"),
                    BodyTexturePath = Required("body.png"), TrackTexturePath = Required("tracks.png"), IconPath = Required("icon.png") };
                ValidateGlb(tank.ModelPath);
                var model = GlbFile.Load(tank.ModelPath);
                var visibleCount = 0; var physicsCount = 0;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in model.Nodes)
                {
                    Check(names.Add(node.Name));
                    if (node.HasGeometry && ModTankRig.IsVisible(node.Name)) { visibleCount++; }
                    if (node.HasGeometry && ModTankRig.IsPhysicsHull(node.Name)) { physicsCount++; }
                    foreach (var primitive in node.Primitives)
                    {
                        Check(primitive.Indices.Length % 3 == 0 && primitive.Normals.Length == primitive.Positions.Length);
                        foreach (var value in primitive.Positions) { Check(!float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) < 10000); }
                        foreach (var value in primitive.Normals) { Check(!float.IsNaN(value) && !float.IsInfinity(value)); }
                        foreach (var index in primitive.Indices) { Check(index >= 0 && index < primitive.VertexCount); }
                    }
                }
                Check(visibleCount > 0 && physicsCount > 0);
                var armor = ReadJson(tank.ArmorPath)["plates"];
                var actual = ModTankRig.ArmorNodes(model);
                Check(armor.Count > 0 && armor.Count <= 512 && armor.Count == actual.Count);
                for (var i = 0; i < actual.Count; i++)
                {
                    Check(armor[i]["name"].AsString() == actual[i].Name && armor[i]["submesh"].AsInt(-1) == i
                        && Finite(armor[i]["armor"].AsFloat(-1f), false));
                    var triangles = 0; foreach (var primitive in actual[i].Primitives) { triangles += primitive.TriangleCount; }
                    Check(armor[i]["triangles"].AsInt(-1) == triangles);
                    var mount = armor[i]["mount"].AsString();
                    Check(mount == "hull" || (mount == "turret_01" || mount == "gun_01"));
                }
                var nodes = ReadJson(tank.NodesPath);
                foreach (var role in new[] { "turret", "gun", "muzzle" })
                {
                    var name = nodes["rig"][role].AsString(); Check(name.Length > 0 && nodes["nodes"].Has(name));
                }
                Check(nodes["nodes"].Count <= 4096);
                foreach (var node in nodes["nodes"].Fields)
                {
                    foreach (var key in new[] { "world", "local" })
                    {
                        Check(node.Value[key].Count == 3);
                        foreach (var number in node.Value[key].Items)
                        { var value = number.AsFloat(float.NaN); Check(!float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) < 10000); }
                    }
                }
                foreach (var path in new[] { tank.BodyTexturePath, tank.TrackTexturePath, tank.IconPath })
                { decodedPixels += ValidatePng(path); Check(decodedPixels <= 64L * 1024 * 1024); }
                ModLibrary.MarkAsMod(card);
                package.Tanks.Add(tank);
            }
            return package;
        }

        private static bool Finite(float value, bool positive)
        { return !float.IsNaN(value) && !float.IsInfinity(value) && (positive ? value > 0 : value >= 0); }

        private static IEnumerable<string> SafeFiles(string root)
        {
            var pending = new Stack<string>(); pending.Push(root); var count = 0;
            while (pending.Count > 0)
            {
                foreach (var entry in Directory.GetFileSystemEntries(pending.Pop()))
                {
                    count++; Check(count <= 8192);
                    var attributes = File.GetAttributes(entry); Check((attributes & FileAttributes.ReparsePoint) == 0);
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); }
                    else { yield return entry; }
                }
            }
        }

        private static long ValidatePng(string path)
        {
            Check(new FileInfo(path).Length <= 32 * 1024 * 1024);
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                var bytes = reader.ReadBytes(24);
                Check(bytes.Length == 24 && BitConverter.ToUInt32(bytes, 0) == 0x474e5089);
                uint Be(int at) { return ((uint)bytes[at] << 24) | ((uint)bytes[at + 1] << 16) | ((uint)bytes[at + 2] << 8) | bytes[at + 3]; }
                Check(Be(16) > 0 && Be(16) <= 4096 && Be(20) > 0 && Be(20) <= 4096);
                Check(Be(8) == 13 && Be(12) == 0x49484452);
                return (long)Be(16) * Be(20);
            }
        }

        /// <summary>Bound allocations and accessor ranges before invoking the shared GLB reader.</summary>
        private static void ValidateGlb(string path)
        {
            Check(new FileInfo(path).Length <= 64 * 1024 * 1024);
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                Check(reader.ReadUInt32() == 0x46546c67 && reader.ReadUInt32() == 2 && reader.ReadUInt32() == stream.Length);
                var jsonLength = reader.ReadUInt32(); Check(jsonLength <= MetadataLimit && jsonLength < stream.Length - 20);
                Check(reader.ReadUInt32() == 0x4e4f534a);
                var root = ParseBounded(System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)jsonLength)));
                var binLength = reader.ReadUInt32(); Check(reader.ReadUInt32() == 0x004e4942 && binLength <= stream.Length - stream.Position);
                Check(root["nodes"].Count <= 4096 && root["accessors"].Count <= 8192);
                foreach (var node in root["nodes"].Items)
                {
                    Check(!node.Has("children") && !node.Has("matrix") && !node.Has("rotation") && !node.Has("scale") && !node.Has("translation"));
                }
                long vertices = 0;
                foreach (var accessor in root["accessors"].Items)
                {
                    var count = accessor["count"].AsInt(-1); Check(count > 0 && count <= 1500000);
                    var type = accessor["type"].AsString();
                    var width = type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : 0;
                    Check(width != 0 && !accessor.Has("sparse"));
                    var component = accessor["componentType"].AsInt(); Check(component == 5125 || component == 5126);
                    var vi = accessor["bufferView"].AsInt(-1); Check(vi >= 0 && vi < root["bufferViews"].Count);
                    var view = root["bufferViews"][vi]; var offset = view["byteOffset"].AsLong() + accessor["byteOffset"].AsLong();
                    var stride = view["byteStride"].AsInt(width * 4);
                    Check(offset >= 0 && stride >= width * 4 && offset + (long)(count - 1) * stride + width * 4 <= binLength);
                    if (type == "VEC3" && component == 5126) { vertices += count; }
                }
                Check(vertices <= 500000);
            }
        }

        public static void Extract(string archivePath, string root)
        {
            Check(new FileInfo(archivePath).Length <= 256 * 1024 * 1024);
            Directory.CreateDirectory(root);
            using (var stream = File.OpenRead(archivePath))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                Check(zip.Entries.Count <= 4096); long total = 0;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    var name = Relative(entry.FullName);
                    Check(paths.Add(name));
                    var kind = (entry.ExternalAttributes >> 16) & 0xf000;
                    Check(kind == 0 || kind == 0x8000);
                    total += entry.Length; Check(entry.Length >= 0 && total <= Budget);
                    var path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open())
                    using (var output = File.Create(path))
                    {
                        var buffer = new byte[65536]; long written = 0; int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        { written += n; Check(written <= entry.Length); output.Write(buffer, 0, n); }
                        Check(written == entry.Length);
                    }
                }
            }
        }
    }
}
