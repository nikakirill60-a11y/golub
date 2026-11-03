using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Sandbox
{
    /// <summary>
    /// Port of assets/data/training_bot_slot.gd: one bot seat in the Sandbox.
    /// The selected tank and modules are references into the read-only tank
    /// catalogue; a slot never mutates an account.
    /// </summary>
    public sealed class SandboxBotSlot
    {
        public const int RandomSelection = -1;
        public const int MaxSkill = 4;

        public bool RandomTank = true;
        public string TankName = string.Empty;
        public int GunId = RandomSelection;
        public int EngineId = RandomSelection;
        public int TrackId = RandomSelection;

        /// <summary>-1 keeps the ordinary skill roll; 0..4 map to AiSkillLevel.</summary>
        public int Skill = RandomSelection;
        public bool Harmless;
        public bool Stationary;

        /// <summary>Slot multipliers stack with the Sandbox global multipliers.</summary>
        public readonly SandboxMultipliers Multipliers = new SandboxMultipliers();

        /// <summary>True when the seat carries an exact vehicle.</summary>
        public bool IsExact
        {
            get { return !RandomTank && !string.IsNullOrEmpty(TankName); }
        }

        public bool ValidateNumeric()
        {
            var before = ToExport().ToJson(false);
            Skill = SandboxMultiplierMath.Clamp(Skill, RandomSelection, MaxSkill);
            Multipliers.Sanitize();
            if (RandomTank)
            {
                MakeRandom();
            }

            return before != ToExport().ToJson(false);
        }

        /// <summary>Clamps module indices to the catalogue counts of the selected tank.</summary>
        public bool ValidateModules(int guns, int engines, int tracks)
        {
            var gun = GunId;
            var engine = EngineId;
            var track = TrackId;
            GunId = ValidateModuleId(GunId, guns);
            EngineId = ValidateModuleId(EngineId, engines);
            TrackId = ValidateModuleId(TrackId, tracks);
            return gun != GunId || engine != EngineId || track != TrackId;
        }

        private static int ValidateModuleId(int value, int count)
        {
            if (value == RandomSelection)
            {
                return value;
            }

            if (count <= 0)
            {
                return RandomSelection;
            }

            return SandboxMultiplierMath.Clamp(value, 0, count - 1);
        }

        public SandboxBotSlot Copy()
        {
            var copy = new SandboxBotSlot
            {
                RandomTank = RandomTank,
                TankName = TankName,
                GunId = GunId,
                EngineId = EngineId,
                TrackId = TrackId,
                Skill = Skill,
                Harmless = Harmless,
                Stationary = Stationary,
            };
            copy.Multipliers.CopyFrom(Multipliers);
            return copy;
        }

        public void MakeRandom()
        {
            RandomTank = true;
            TankName = string.Empty;
            GunId = RandomSelection;
            EngineId = RandomSelection;
            TrackId = RandomSelection;
            Skill = RandomSelection;
            Harmless = false;
            Stationary = false;
            Multipliers.Reset();
        }

        /// <summary>Selects an exact vehicle with the ordinary module roll.</summary>
        public void SelectTank(string tankName)
        {
            RandomTank = false;
            TankName = tankName ?? string.Empty;
            GunId = RandomSelection;
            EngineId = RandomSelection;
            TrackId = RandomSelection;
        }

        /// <summary>The Godot <c>to_export_dictionary</c> shape, key for key.</summary>
        public JsonNode ToExport()
        {
            var node = JsonNode.NewObject()
                .Set("random_tank", RandomTank)
                .Set("tank_name", TankName ?? string.Empty)
                .Set("gun_id", GunId)
                .Set("engine_id", EngineId)
                .Set("track_id", TrackId)
                .Set("skill", Skill)
                .Set("harmless", Harmless)
                .Set("stationary", Stationary);
            Multipliers.Write(node, string.Empty);
            return node;
        }

        public static SandboxBotSlot FromExport(JsonNode data)
        {
            var slot = new SandboxBotSlot();
            if (data == null || data.Kind != JsonKind.Object)
            {
                return slot;
            }

            slot.RandomTank = SandboxJson.Bool(data, "random_tank", true);
            slot.TankName = SandboxJson.Text(data, "tank_name", string.Empty);
            slot.GunId = SandboxJson.Int(data, "gun_id", slot.GunId);
            slot.EngineId = SandboxJson.Int(data, "engine_id", slot.EngineId);
            slot.TrackId = SandboxJson.Int(data, "track_id", slot.TrackId);
            slot.Skill = SandboxJson.Int(data, "skill", slot.Skill);
            slot.Harmless = SandboxJson.Bool(data, "harmless", slot.Harmless);
            slot.Stationary = SandboxJson.Bool(data, "stationary", slot.Stationary);
            slot.Multipliers.Read(data, string.Empty);
            slot.ValidateNumeric();
            if (slot.RandomTank)
            {
                slot.TankName = string.Empty;
            }

            return slot;
        }
    }
}
