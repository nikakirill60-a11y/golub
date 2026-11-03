namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// A new research branch attached to an existing card without rewriting
    /// its module graph or invalidating the elite status of an old save.
    /// References are resolved only after the entire database is loaded.
    /// </summary>
    public sealed class AdditionalTankResearchLink
    {
        public readonly ModuleKind RequiredKind;
        public readonly ModuleInfo RequiredModule;
        public readonly TurretInfo RequiredTurret;
        public readonly bool PreserveLegacyElite;

        public AdditionalTankResearchLink(ModuleKind kind, ModuleInfo module,
            TurretInfo turret, bool preserveLegacyElite)
        {
            RequiredKind = kind;
            RequiredModule = module;
            RequiredTurret = turret;
            PreserveLegacyElite = preserveLegacyElite;
        }
    }
}
