namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// Geometry of the Blitz 4.8.0 damage panels,
    /// <c>UI/Screens/Battle/HUDLayer.yaml</c>:
    /// <c>BottomHUD/VerticalContainer/DamageCrewContainer</c> and
    /// <c>DamageTankPartsContainer</c>, plus the <c>EnemyDamagedModules</c> row
    /// of <c>VehicleUIObject</c>.
    ///
    /// Both containers are laid out by the client at runtime: the row is a
    /// centred <c>LinearLayout</c> of one icon per module, and the plate behind
    /// it is the sprite at its authored size (<c>PercentOfContent</c>). The
    /// sizes below are therefore the authored frame sizes of the art the
    /// porter cut out of the pack.
    /// </summary>
    public static class DamagePanelTuning
    {
        /// <summary>Authored frame of <c>DamagedModulesDamageTankIcon</c>.</summary>
        public const float ModuleIconWidth = 76f;

        /// <inheritdoc cref="ModuleIconWidth"/>
        public const float ModuleIconHeight = 52f;

        /// <summary>Authored frame of <c>DamagedModulesDamageCrew</c>.</summary>
        public const float CrewIconWidth = 50f;

        /// <inheritdoc cref="CrewIconWidth"/>
        public const float CrewIconHeight = 48f;

        /// <summary><c>DamagedModulesDamageTankBg</c>.</summary>
        public const float TankBgWidth = 656f;

        /// <inheritdoc cref="TankBgWidth"/>
        public const float TankBgHeight = 70f;

        /// <summary><c>DamagedModulesDamageCrewBg</c>.</summary>
        public const float CrewBgWidth = 568f;

        /// <inheritdoc cref="CrewBgWidth"/>
        public const float CrewBgHeight = 46f;

        /// <summary>The <c>LinearLayout</c> of both rows packs its icons.</summary>
        public const float RowSpacing = 0f;

        /// <summary>
        /// The client draws the panels at the HUD scale of the bottom block;
        /// the port's battle canvas is the 1040x550 Godot one, so the 4.8.0
        /// pixels come in at this factor.
        /// </summary>
        public const float PanelScale = 0.5f;

        /// <summary>Bottom margin of <c>DamageTankPartsContainer</c>.</summary>
        public const float TankRowBottom = 6f;

        /// <summary>
        /// <c>DamageCrewContainer</c> sits directly above the module row in the
        /// same bottom-anchored column.
        /// </summary>
        public const float CrewRowGap = 2f;

        /// <summary>
        /// The client slides both plates out of the bottom edge instead of
        /// popping them over the health bar. This is the length of that slide,
        /// short enough to read as an instant reaction to the hit.
        /// </summary>
        public const float SlideDuration = 0.14f;

        /// <summary>
        /// Air the bottom readouts keep above the damage stack once it is out,
        /// so the health plate never touches the module icons.
        /// </summary>
        public const float StackPadding = 4f;

        /// <summary>Authored frame of <c>BattleScreenHUD/ModuleDamaged</c>.</summary>
        public const float EnemyIconWidth = 52f;

        /// <inheritdoc cref="EnemyIconWidth"/>
        public const float EnemyIconHeight = 44f;

        /// <summary><c>EnemyDamagedModules</c> LinearLayout spacing.</summary>
        public const float EnemyIconSpacing = 5f;

        /// <summary>The marker row is drawn at the plate's own scale.</summary>
        public const float EnemyIconScale = 0.5f;

        /// <summary>
        /// Height of the row above the plate: <c>VehicleUIObject</c> stacks the
        /// icons over the marker with a five pixel gap.
        /// </summary>
        public const float EnemyRowOffsetY = 100f;
    }
}
