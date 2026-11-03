using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>
    /// One destructible model group exported from a client DAVA map scene.
    /// Mesh vertices are already in Unity world coordinates; the group names
    /// link the visible and wreck SwitchComponent states in the imported OBJ.
    /// </summary>
    public sealed class ExternalMapDestructible
    {
        public string Name { get; set; }
        public string IntactGroup { get; set; }
        public string WreckGroup { get; set; }
        public int Health { get; set; }
        public int CollisionType { get; set; }
        public int MaterialKind { get; set; }
        public int FallType { get; set; }
        public int CollisionTypeCrashed { get; set; }
        public int HealthFromClient { get; set; }
    }

    /// <summary>
    /// One client-scene tree (CollisionType 8, a FallType other than bush):
    /// the node that draws it and its foot in Unity world space, the pivot
    /// it topples around when a tank drives into it.
    /// </summary>
    public sealed class ExternalMapTree
    {
        public string Name { get; set; }
        public string Group { get; set; }
        public float[] Origin { get; set; }
        public int FallType { get; set; }
    }

    /// <summary>One TankSpawner marker, still in source coordinates.</summary>
    public sealed class MapSpawnPoint
    {
        public string Name { get; set; }

        /// <summary>Godot BaseTank.Team: 0 is team A, 1 is team B.</summary>
        public int Team { get; set; }

        public float[] Basis { get; set; }

        public float[] Origin { get; set; }

        /// <summary>
        /// The numbered spawn area the marker belongs to, from one. The
        /// minimap draws one numbered sprite per area of a team; zero when the
        /// manifest predates the minimap port.
        /// </summary>
        public int Respawn { get; set; }
    }

    /// <summary>
    /// The ground rectangle the minimap image shows, in source coordinates.
    /// </summary>
    public sealed class MapMinimapArea
    {
        /// <summary>x, y, width, height of the painted part of the image, in pixels from its top-left corner.</summary>
        public int[] Frame { get; set; }

        /// <summary>Source x and z under the frame's top-left corner.</summary>
        public float[] Min { get; set; }

        /// <summary>Source x and z under the frame's bottom-right corner.</summary>
        public float[] Max { get; set; }

        /// <summary>
        /// Width and height, in the frame's pixels, the frame is drawn at;
        /// null draws it at its own size. The 11.20 maps ship a retina
        /// MiniMapSmall@2x whose frame is larger than the 4.8.0 MiniMapSmall.txt
        /// one the battle HUD is laid out for (505 against 326 px), so their
        /// manifests draw the whole frame at the 4.8.0 size.
        /// </summary>
        public int[] Size { get; set; }

        /// <summary>The drawn width: <see cref="Size"/>, else the frame's own.</summary>
        public float Width { get { return Size != null ? Size[0] : Frame[2]; } }

        /// <summary>The drawn height: <see cref="Size"/>, else the frame's own.</summary>
        public float Height { get { return Size != null ? Size[1] : Frame[3]; } }
    }

    /// <summary>AIMarker.Type, in the source's declaration order.</summary>
    public enum MapAiMarkerType
    {
        PriorityPrimary = 0,
        PrioritySecondary = 1,
        PriorityTertiary = 2,
        Snipe = 3,
        Hideout = 4,
    }

    /// <summary>
    /// One AIMarker of the map's ai_markers node, still in source coordinates.
    /// maps/components/ai_marker.gd exports exactly these three fields and
    /// ai_controller.gd reads all of them when it picks where to drive.
    /// </summary>
    public sealed class MapAiMarker
    {
        public string Name { get; set; }

        public MapAiMarkerType Type { get; set; }

        /// <summary>The radius a bot roams inside, in metres.</summary>
        public float Radius { get; set; }

        /// <summary>The 0..5 weight the tactical scoring adds to a marker.</summary>
        public int Priority { get; set; }

        public float[] Origin { get; set; }
    }

    /// <summary>The HTerrain node's geometry and shader parameters.</summary>
    public sealed class MapTerrainInfo
    {
        public string Resource { get; set; }

        public string ColorMapResource { get; set; }

        public string TileMaskResource { get; set; }

        public string TileTextureResource { get; set; }

        /// <summary>The hterrain normal map a Godot scene lights its vertices by.</summary>
        public string NormalMapResource { get; set; }

        /// <summary>Present when the landscape is drawn by landscape.gdshader.</summary>
        public MapGodotLandscape Godot { get; set; }

        /// <summary>The HTerrain normal-map file used to light the surface.</summary>
        public string NormalSource { get; set; }

        /// <summary>
        /// The one source-space normal encoded by the map's constant normal map.
        /// </summary>
        public float[] SurfaceNormal { get; set; }

        public int Side { get; set; }

        public float Step { get; set; }

        public float Extent { get; set; }

        /// <summary>
        /// Source-space position of the grid's first sample. A Godot map
        /// starts its landscape at the origin and leaves this zero; a client
        /// DAVA landscape is centred on its own bounding box instead.
        /// </summary>
        public float[] Origin { get; set; }

        public float MinHeight { get; set; }

        public float MaxHeight { get; set; }

        public bool CollisionEnabled { get; set; }

        /// <summary>Godot cast_shadow: the source landscape casts none.</summary>
        public int CastShadow { get; set; }

        public float TileTexScale { get; set; }

        public float[] TileColour0 { get; set; }

        public float[] TileColour1 { get; set; }

        public float[] TileColour2 { get; set; }

        public float[] TileColour3 { get; set; }

        public float Brightness { get; set; }

        public float Contrast { get; set; }

        public float DetailStrength { get; set; }

        public float DetailContrast { get; set; }

        public float MaskSharpness { get; set; }

        public string Sha256 { get; set; }

        public int Bytes { get; set; }

        /// <summary>
        /// The client's own tile mask parameters (TileMaskSpecular), present
        /// only for a map drawn with the client formulas; null otherwise.
        /// </summary>
        public MapClientLandscape Client { get; set; }
    }

    /// <summary>TileMaskSpecular's material values of a client landscape.</summary>
    /// <summary>
    /// landscape.gdshader's parameters on a map ported with its Godot render
    /// scene (Tools/port_godot_map_scene.py); the tile colours are linear.
    /// </summary>
    public sealed class MapGodotLandscape
    {
        public int Resolution { get; set; }
        public float[] MapScale { get; set; }
        public float TileTexScale { get; set; }
        public float[][] TileColours { get; set; }
        public float DetailStrength { get; set; }
        public float DetailContrast { get; set; }
        public float Brightness { get; set; }
        public float Contrast { get; set; }
        public float MaskSharpness { get; set; }
    }

    public sealed class MapClientLandscape
    {
        /// <summary>textureTiling: how often the tile detail repeats.</summary>
        public float[] Tiling { get; set; }

        public float Glossiness { get; set; }

        public float Specularity { get; set; }

        public float MetalFresnelReflectance { get; set; }

        /// <summary>
        /// Whether the tile mask adds its specular term. The 4.8 HIGH effect
        /// (TileMaskSpecular) does; 11.20's non-PBR tilemask-fp has none.
        /// </summary>
        public bool Specular { get; set; } = true;

        /// <summary>
        /// LANDSCAPE_SEPARATE_LIGHTMAP_CHANNEL: the colour map's alpha is the
        /// baked light the colour is multiplied by, not a gloss mask.
        /// </summary>
        public bool SeparateLightmapChannel { get; set; }
    }

    /// <summary>The map's single DirectionalLight3D.</summary>
    public sealed class MapLightInfo
    {
        public float[] Basis { get; set; }

        public float[] Origin { get; set; }

        public float Energy { get; set; }

        /// <summary>Authored light colour, white when the source has none.</summary>
        public float[] Color { get; set; }

        /// <summary>
        /// Whether the sun casts realtime shadows. False where the client
        /// never renders shadow maps on the map (maps.yaml grants Sea of
        /// Tranquility no shadowMapsAvailable).
        /// </summary>
        public bool Shadows { get; set; } = true;
    }

    /// <summary>
    /// The fog and ambient a client .sc2 authors on its global material. Godot
    /// maps carry the same values in their lighting manifest instead, so this
    /// block is only present for maps ported straight from the client.
    /// </summary>
    public sealed class MapEnvironmentInfo
    {
        public float[] FogColor { get; set; }

        public float FogStart { get; set; }

        public float FogEnd { get; set; }

        /// <summary>How much fog the client allows at its far distance.</summary>
        public float FogLimit { get; set; }

        /// <summary>
        /// FOG_LINEAR of the client's global material. Off, the client fades
        /// by <c>1 - exp(-FogDensity * distance)</c> instead of between
        /// FogStart and FogEnd.
        /// </summary>
        public bool FogLinear { get; set; }

        public float FogDensity { get; set; }

        /// <summary>
        /// The client's shadowColor: what a shadow volume multiplies the
        /// frame by. Null for maps the port shades with its own lighting.
        /// </summary>
        public float[] ShadowColor { get; set; }

        /// <summary>
        /// FOG_ATMOSPHERE on the client's global material. With it the haze
        /// stops being one flat colour: it reads as the sky colour away from
        /// the sun and the sun colour towards it, which is what keeps the
        /// client's horizon blue-grey rather than near-white.
        /// </summary>
        public bool FogAtmosphere { get; set; }

        public float[] FogSkyColor { get; set; }

        public float[] FogSunColor { get; set; }

        /// <summary>How far the atmosphere colour takes over from FogColor.</summary>
        public float FogAtmosphereDistance { get; set; }

        /// <summary>The exponent that tightens the halo around the sun.</summary>
        public float FogScattering { get; set; }

        /// <summary>
        /// FOG_HALFSPACE of a client-shaded map: haze pooled below a height.
        /// Null when the scene does not enable it (or the port does not
        /// shade the map with the client formulas).
        /// </summary>
        public MapHalfspaceFog FogHalfspace { get; set; }

        public float[] Ambient { get; set; }

        /// <summary>
        /// The ambient level this map wants, when it does not want the shared
        /// one. A client scene only authors an ambient hue, so the runtime
        /// normalises it; zero means "keep the default normalisation".
        /// </summary>
        public float AmbientLuminance { get; set; }

        /// <summary>The direction the map's sunlight travels, already in Unity space.</summary>
        public float[] SunDirection { get; set; }
    }

    /// <summary>vp-fog-math.slh's half-space fog parameters.</summary>
    public sealed class MapHalfspaceFog
    {
        public float Density { get; set; }

        public float Falloff { get; set; }

        public float Height { get; set; }

        /// <summary>The most the half-space term adds before the fogLimit clamp.</summary>
        public float Limit { get; set; }

        /// <summary>FOG_HALFSPACE_LINEAR: Lengyel's unified fog instead of the exponential falloff.</summary>
        public bool Linear { get; set; }
    }

    /// <summary>One texture assigned to a source sky cube face.</summary>
    public sealed class MapSkyFace
    {
        public string Name { get; set; }

        public string Slot { get; set; }

        public string Resource { get; set; }

        public string Source { get; set; }

        /// <summary>Composed source transform of the PlaneMesh.</summary>
        public float[] Basis { get; set; }

        public float[] Origin { get; set; }
    }

    /// <summary>
    /// A map as Tools/port_godot_map.py exported it. Every number is still the
    /// one Godot authored: the porter deliberately does not convert
    /// coordinates, so the mirror lives in <see cref="GodotSpace"/> alone and
    /// this table can be diffed against the source scene.
    /// </summary>
    public sealed class MapDefinition
    {
        public string Id { get; set; }

        /// <summary>Godot map.gd builds "map:" + id + "/name".</summary>
        public string NameKey { get; set; }

        public string Scene { get; set; }

        /// <summary>Optional Unity model exported directly from a client .sc2.</summary>
        public string ExternalScene { get; set; }

        /// <summary>
        /// True for a map whose manifest keeps the client's handedness
        /// (`orientation: dava`); the maps ported first are mirrored
        /// north-south.
        /// </summary>
        public bool ClientOrientation { get; set; }

        /// <summary>
        /// True when ExternalScene is the binary client scene with its DAVA
        /// materials (`sceneFormat: client`) rather than an imported OBJ.
        /// </summary>
        public bool ClientScene { get; set; }

        /// <summary>
        /// True when ExternalScene is the Godot render scene in the binary map
        /// format (`sceneFormat: godot`): it only draws, while the gameplay
        /// object table keeps the colliders, trees and destructibles.
        /// </summary>
        public bool GodotScene { get; set; }

        /// <summary>Damageable client-scene props and their authored state groups.</summary>
        public List<ExternalMapDestructible> ExternalDestructibles { get; private set; }

        /// <summary>Client-scene trees that fall under a tank.</summary>
        public List<ExternalMapTree> ExternalTrees { get; private set; }

        /// <summary>Tank tiers for which the map is eligible in random battle.</summary>
        public List<int> RandomTiers { get; private set; }

        public string Minimap { get; set; }

        /// <summary>Where <see cref="Minimap"/> lies on the ground; null when unknown.</summary>
        public MapMinimapArea MinimapArea { get; set; }

        public string LoadingScreen { get; set; }

        /// <summary>
        /// The Audio resource the loading screen plays, null for the shared
        /// intro theme.
        /// </summary>
        public string IntroMusic { get; set; }

        public float[] SkyBasis { get; set; }

        public float[] SkyOrigin { get; set; }

        public List<MapSkyFace> SkyFaces { get; private set; }

        /// <summary>map_border_min / map_border_max markers.</summary>
        public float[] BorderMin { get; set; }

        public float[] BorderMax { get; set; }

        public MapTerrainInfo Terrain { get; set; }

        public MapLightInfo Light { get; set; }

        /// <summary>Authored fog and ambient, null for the Godot maps.</summary>
        public MapEnvironmentInfo Environment { get; set; }

        public List<MapSpawnPoint> TeamA { get; private set; }

        public List<MapSpawnPoint> TeamB { get; private set; }

        /// <summary>The map's AIMarker nodes, in scene order.</summary>
        public List<MapAiMarker> AiMarkers { get; private set; }

        public MapDefinition()
        {
            TeamA = new List<MapSpawnPoint>();
            TeamB = new List<MapSpawnPoint>();
            SkyFaces = new List<MapSkyFace>();
            AiMarkers = new List<MapAiMarker>();
            RandomTiers = new List<int>();
            ExternalDestructibles = new List<ExternalMapDestructible>();
            ExternalTrees = new List<ExternalMapTree>();
        }

        /// <summary>Godot _battle.gd derives the play area from the markers.</summary>
        public float SizeX
        {
            get { return BorderMax[0] - BorderMin[0]; }
        }

        public float SizeZ
        {
            get { return BorderMax[2] - BorderMin[2]; }
        }

        public static MapDefinition Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException("map definition is empty");
            }

            var root = JsonNode.Parse(json);
            var map = new MapDefinition
            {
                Id = root["id"].AsString(),
                NameKey = root["nameKey"].AsString(),
                Scene = root["scene"].AsString(),
                Minimap = root["minimap"].AsString(),
                LoadingScreen = root["loadingScreen"].AsString(),
                IntroMusic = root.Has("introMusic") ? root["introMusic"].AsString() : null,
                SkyBasis = Floats(root["sky"]["basis"], 9),
                SkyOrigin = Floats(root["sky"]["origin"], 3),
                BorderMin = Floats(root["border"]["min"], 3),
                BorderMax = Floats(root["border"]["max"], 3),
            };

            foreach (var face in root["sky"]["faces"].Items)
            {
                map.SkyFaces.Add(new MapSkyFace
                {
                    Name = face["name"].AsString(),
                    Slot = face["slot"].AsString(),
                    Resource = face["resource"].AsString(),
                    Source = face["source"].AsString(),
                    Basis = Floats(face["basis"], 9),
                    Origin = Floats(face["origin"], 3),
                });
            }
            // The Godot maps fake their sky with five authored planes. A map
            // taken straight from the client draws its own sky dome inside the
            // scene, so an empty list is the other legal shape here.
            if (map.SkyFaces.Count != 0 && map.SkyFaces.Count != 5)
            {
                throw new ArgumentException(
                    "the map sky must contain five source faces or none at all");
            }

            var terrain = root["terrain"];
            map.Terrain = new MapTerrainInfo
            {
                Resource = terrain["resource"].AsString(),
                ColorMapResource = terrain["colorMap"].AsString(),
                TileMaskResource = terrain["tileMask"].AsString(),
                TileTextureResource = terrain["tileTexture"].AsString(),
                NormalSource = terrain["normalSource"].AsString(),
                SurfaceNormal = Floats(terrain["surfaceNormal"], 3),
                Side = terrain["side"].AsInt(),
                Step = terrain["step"].AsFloat(),
                Extent = terrain["extent"].AsFloat(),
                MinHeight = terrain["minHeight"].AsFloat(),
                MaxHeight = terrain["maxHeight"].AsFloat(),
                CollisionEnabled = terrain["collisionEnabled"].AsBool(),
                CastShadow = terrain["castShadow"].AsInt(),
                TileTexScale = terrain["tileTexScale"].AsFloat(),
                TileColour0 = Floats(terrain["tileColour0"], 4),
                TileColour1 = Floats(terrain["tileColour1"], 4),
                TileColour2 = Floats(terrain["tileColour2"], 4),
                TileColour3 = Floats(terrain["tileColour3"], 4),
                Brightness = terrain["brightness"].AsFloat(),
                Contrast = terrain["contrast"].AsFloat(),
                DetailStrength = terrain["detailStrength"].AsFloat(),
                DetailContrast = terrain["detailContrast"].AsFloat(),
                MaskSharpness = terrain["maskSharpness"].AsFloat(),
                Sha256 = terrain["sha256"].AsString(),
                Bytes = terrain["bytes"].AsInt(),
            };

            var light = root["directionalLight"];
            map.Light = new MapLightInfo
            {
                Basis = Floats(light["basis"], 9),
                Origin = Floats(light["origin"], 3),
                Energy = light["energy"].AsFloat(),
                Color = light.Has("color")
                    ? Floats(light["color"], 3)
                    : new[] { 1f, 1f, 1f },
                Shadows = !light.Has("shadows") || light["shadows"].AsBool(),
            };

            if (root.Has("environment"))
            {
                var environment = root["environment"];
                var fog = environment["fog"];
                map.Environment = new MapEnvironmentInfo
                {
                    FogColor = Floats(fog["color"], 3),
                    FogStart = fog["start"].AsFloat(),
                    FogEnd = fog["end"].AsFloat(),
                    FogLimit = fog["limit"].AsFloat(),
                    FogLinear = !fog.Has("linear") || fog["linear"].AsBool(),
                    FogDensity = fog.Has("density") ? fog["density"].AsFloat() : 0f,
                    ShadowColor = environment.Has("shadowColor")
                        ? Floats(environment["shadowColor"], 4) : null,
                    FogAtmosphere = fog.Has("atmosphere") && fog["atmosphere"].AsBool(),
                    FogSkyColor = fog.Has("skyColor")
                        ? Floats(fog["skyColor"], 3) : new[] { 1f, 1f, 1f },
                    FogSunColor = fog.Has("sunColor")
                        ? Floats(fog["sunColor"], 3) : new[] { 1f, 1f, 1f },
                    FogAtmosphereDistance = fog.Has("atmosphereDistance")
                        ? fog["atmosphereDistance"].AsFloat() : 0f,
                    FogScattering = fog.Has("scattering")
                        ? fog["scattering"].AsFloat() : 1f,
                    FogHalfspace = fog.Has("halfspace")
                        ? new MapHalfspaceFog
                        {
                            Density = fog["halfspace"]["density"].AsFloat(),
                            Falloff = fog["halfspace"]["falloff"].AsFloat(),
                            Height = fog["halfspace"]["height"].AsFloat(),
                            Limit = fog["halfspace"]["limit"].AsFloat(),
                            Linear = fog["halfspace"].Has("linear") &&
                                fog["halfspace"]["linear"].AsBool(),
                        }
                        : null,
                    Ambient = Floats(environment["ambient"], 3),
                    AmbientLuminance = environment.Has("ambientLuminance")
                        ? environment["ambientLuminance"].AsFloat() : 0f,
                    SunDirection = environment.Has("sunDirection")
                        ? Floats(environment["sunDirection"], 3) : new[] { 0f, -1f, 0f },
                };
            }

            map.Terrain.Origin = terrain.Has("origin")
                ? Floats(terrain["origin"], 3)
                : new float[3];

            if (root.Has("externalScene"))
            {
                map.ExternalScene = root["externalScene"].AsString();
            }
            map.ClientOrientation = root.Has("orientation") &&
                root["orientation"].AsString() == "dava";
            map.ClientScene = root.Has("sceneFormat") &&
                root["sceneFormat"].AsString() == "client";
            map.GodotScene = root.Has("sceneFormat") &&
                root["sceneFormat"].AsString() == "godot";
            if (terrain.Has("normalMap"))
            {
                map.Terrain.NormalMapResource = terrain["normalMap"].AsString();
            }
            if (terrain.Has("godot"))
            {
                var godot = terrain["godot"];
                var colours = new List<float[]>();
                foreach (var colour in godot["tileColours"].Items)
                {
                    colours.Add(Floats(colour, 4));
                }
                map.Terrain.Godot = new MapGodotLandscape
                {
                    Resolution = godot["resolution"].AsInt(),
                    MapScale = Floats(godot["mapScale"], 3),
                    TileTexScale = godot["tileTexScale"].AsFloat(),
                    TileColours = colours.ToArray(),
                    DetailStrength = godot["detailStrength"].AsFloat(),
                    DetailContrast = godot["detailContrast"].AsFloat(),
                    Brightness = godot["brightness"].AsFloat(),
                    Contrast = godot["contrast"].AsFloat(),
                    MaskSharpness = godot["maskSharpness"].AsFloat(),
                };
            }
            if (terrain.Has("client"))
            {
                var client = terrain["client"];
                map.Terrain.Client = new MapClientLandscape
                {
                    Tiling = Floats(client["tiling"], 2),
                    Glossiness = client["glossiness"].AsFloat(),
                    Specularity = client["specularity"].AsFloat(),
                    MetalFresnelReflectance = client["metalFresnelReflectance"].AsFloat(),
                    Specular = !client.Has("specular") || client["specular"].AsBool(),
                    SeparateLightmapChannel = client.Has("separateLightmapChannel") &&
                        client["separateLightmapChannel"].AsBool(),
                };
            }
            if (root.Has("randomTiers"))
            {
                foreach (var tier in root["randomTiers"].Items)
                {
                    map.RandomTiers.Add(tier.AsInt());
                }
            }

            var objectData = root["objects"];
            var destructibleData = objectData != null ? objectData["destructibles"] : null;
            if (destructibleData != null)
            {
                foreach (var item in destructibleData.Items)
                {
                    var wreckGroup = item["wreckGroup"];
                    map.ExternalDestructibles.Add(new ExternalMapDestructible
                    {
                        Name = item["name"].AsString(),
                        IntactGroup = item["intactGroup"].AsString(),
                        WreckGroup = wreckGroup != null && wreckGroup.Kind == JsonKind.String
                            ? wreckGroup.AsString() : null,
                        Health = item["health"].AsInt(),
                        CollisionType = item["collisionType"].AsInt(),
                        MaterialKind = item["materialKind"].AsInt(),
                        FallType = item["fallType"].AsInt(),
                        CollisionTypeCrashed = item["collisionTypeCrashed"].AsInt(),
                        HealthFromClient = item["healthFromClient"].AsInt(),
                    });
                }
            }

            var treeData = objectData != null ? objectData["trees"] : null;
            if (treeData != null)
            {
                foreach (var item in treeData.Items)
                {
                    map.ExternalTrees.Add(new ExternalMapTree
                    {
                        Name = item["name"].AsString(),
                        Group = item["group"].AsString(),
                        Origin = Floats(item["origin"], 3),
                        FallType = item["fallType"].AsInt(),
                    });
                }
            }

            if (root.Has("minimapArea"))
            {
                var area = root["minimapArea"];
                var frame = Floats(area["frame"], 4);
                map.MinimapArea = new MapMinimapArea
                {
                    Frame = new[] { (int)frame[0], (int)frame[1], (int)frame[2], (int)frame[3] },
                    Min = Floats(area["min"], 2),
                    Max = Floats(area["max"], 2),
                };
                if (area.Has("size"))
                {
                    var size = Floats(area["size"], 2);
                    map.MinimapArea.Size = new[] { (int)size[0], (int)size[1] };
                }
            }

            ReadSpawns(root["spawns"]["teamA"], map.TeamA);
            ReadSpawns(root["spawns"]["teamB"], map.TeamB);
            ReadAiMarkers(root["aiMarkers"], map.AiMarkers);
            return map;
        }

        private static void ReadSpawns(JsonNode node, List<MapSpawnPoint> into)
        {
            if (node == null)
            {
                return;
            }

            foreach (var item in node.Items)
            {
                into.Add(new MapSpawnPoint
                {
                    Name = item["name"].AsString(),
                    Team = item["team"].AsInt(),
                    Basis = Floats(item["basis"], 9),
                    Origin = Floats(item["origin"], 3),
                    Respawn = item.Has("respawn") ? item["respawn"].AsInt() : 0,
                });
            }
        }

        /// <summary>
        /// The markers are optional: a manifest exported before the AI port
        /// carries no ai_markers at all, and such a map still has to load.
        /// </summary>
        private static void ReadAiMarkers(JsonNode node, List<MapAiMarker> into)
        {
            if (node == null)
            {
                return;
            }

            foreach (var item in node.Items)
            {
                into.Add(new MapAiMarker
                {
                    Name = item["name"].AsString(),
                    Type = (MapAiMarkerType)item["type"].AsInt(),
                    Radius = item["radius"].AsFloat(),
                    Priority = item["priority"].AsInt(),
                    Origin = Floats(item["origin"], 3),
                });
            }
        }

        private static float[] Floats(JsonNode node, int expected)
        {
            if (node == null || node.Count != expected)
            {
                throw new ArgumentException("expected " + expected + " components");
            }

            var values = new float[expected];
            for (var i = 0; i < expected; i++)
            {
                values[i] = node[i].AsFloat();
            }

            return values;
        }
    }
}
