using System;
using System.Collections.Generic;
using System.IO;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>One card of a Godot mod with the files that belong to it.</summary>
    public sealed class GodotModTank : ModVehicleAssets
    {
    }

    /// <summary>
    /// A mod folder as the Godot build laid it out: a tanks_mod directory with
    /// one .tres card per vehicle and an assets/tanks directory holding the
    /// .glb hull plus its icon, body and track textures.
    ///
    /// The archives are repacked by hand and travel with a wrapper folder more
    /// often than not, so the scan walks the whole tree instead of trusting a
    /// fixed depth, and it pairs files by the card's own name rather than by
    /// the directory they sit in.
    /// </summary>
    public sealed class GodotModPackage
    {
        public string Id = string.Empty;
        public string DisplayName = string.Empty;
        public string RootPath = string.Empty;
        public readonly List<GodotModTank> Tanks = new List<GodotModTank>();
        public readonly List<string> Warnings = new List<string>();

        public bool IsEmpty
        {
            get { return Tanks.Count == 0; }
        }

        public static GodotModPackage Scan(string root)
        {
            var package = new GodotModPackage { RootPath = root };
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                package.Warnings.Add("mods_error_empty");
                return package;
            }

            package.Id = Slug(new DirectoryInfo(root).Name);
            package.DisplayName = new DirectoryInfo(root).Name;

            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            var cards = new List<string>();
            var assets = new List<string>();
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                var lower = file.ToLowerInvariant();

                // .godot/imported holds the engine's own binary cache, which is
                // useless outside Godot and would only confuse the pairing.
                if (lower.Replace('\\', '/').Contains("/.godot/"))
                {
                    continue;
                }

                if (lower.EndsWith(".tres", StringComparison.Ordinal))
                {
                    cards.Add(file);
                }
                else if (lower.EndsWith(".glb", StringComparison.Ordinal)
                    || lower.EndsWith(".gltf", StringComparison.Ordinal)
                    || lower.EndsWith(".png", StringComparison.Ordinal))
                {
                    assets.Add(file);
                }
            }

            for (var i = 0; i < cards.Count; i++)
            {
                package.Tanks.Add(Pair(Path.GetFileNameWithoutExtension(cards[i]), cards[i], assets));
            }

            // A model nobody's card claimed is still a vehicle: it gets a
            // placeholder card later (see ModLibrary.Cards) instead of the
            // whole mod being refused.
            for (var i = 0; i < assets.Count; i++)
            {
                var extension = Path.GetExtension(assets[i]).ToLowerInvariant();
                if ((extension != ".glb" && extension != ".gltf") || Claimed(package.Tanks, assets[i]))
                {
                    continue;
                }

                package.Tanks.Add(Pair(Path.GetFileNameWithoutExtension(assets[i]), null, assets));
            }

            if (package.Tanks.Count == 0)
            {
                package.Warnings.Add("mods_error_no_cards");
            }

            return package;
        }

        private static bool Claimed(List<GodotModTank> tanks, string model)
        {
            for (var i = 0; i < tanks.Count; i++)
            {
                if (string.Equals(tanks[i].ModelPath, model, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static GodotModTank Pair(string id, string cardPath, List<string> assets)
        {
            var tank = new GodotModTank { Id = id, CardPath = cardPath ?? string.Empty };
            for (var i = 0; i < assets.Count; i++)
            {
                var asset = assets[i];
                var name = Path.GetFileNameWithoutExtension(asset);
                var extension = Path.GetExtension(asset).ToLowerInvariant();
                if (!name.StartsWith(id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var suffix = name.Substring(id.Length).ToLowerInvariant();
                if (extension == ".glb" || extension == ".gltf")
                {
                    if (suffix.Length == 0 || string.IsNullOrEmpty(tank.ModelPath))
                    {
                        tank.ModelPath = asset;
                    }

                    continue;
                }

                if (suffix == "_icon")
                {
                    tank.IconPath = asset;
                }
                else if (suffix == "_track" || suffix == "_tracks")
                {
                    tank.TrackTexturePath = asset;
                }
                else if (suffix.Length == 0)
                {
                    tank.BodyTexturePath = asset;
                }
            }

            return tank;
        }

        /// <summary>A folder name reduced to a stable mod id.</summary>
        public static string Slug(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "mod";
            }

            var text = name.ToLowerInvariant().ToCharArray();
            for (var i = 0; i < text.Length; i++)
            {
                if (!char.IsLetterOrDigit(text[i]))
                {
                    text[i] = '_';
                }
            }

            var slug = new string(text).Trim('_');
            return slug.Length > 0 ? slug : "mod";
        }

        /// <summary>
        /// The directory a scan should really start from. Repacked archives
        /// usually nest the mod one or two folders deep, so an extraction that
        /// produced a single folder and nothing else is stepped into.
        /// </summary>
        public static string Unwrap(string root)
        {
            var current = root;
            for (var depth = 0; depth < 4; depth++)
            {
                if (!Directory.Exists(current))
                {
                    return current;
                }

                var folders = Directory.GetDirectories(current);
                var files = Directory.GetFiles(current);
                if (folders.Length != 1 || files.Length != 0)
                {
                    return current;
                }

                current = folders[0];
            }

            return current;
        }
    }
}
