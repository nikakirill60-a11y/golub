namespace BlitzLegacy.Core.Tanks.Ai
{
    /// <summary>ai_controller.gd::SkillLevel.</summary>
    public enum AiSkillLevel
    {
        Idle = 0,
        WhiteArmy = 1,
        GreenArmy = 2,
        BlueArmy = 3,
        PurpleArmy = 4,
    }

    /// <summary>ai_controller.gd::PlayStyle.</summary>
    public enum AiPlayStyle
    {
        Generic = 0,
        Sniper = 1,
        Heavy = 2,
        Light = 3,
    }

    /// <summary>ai_controller.gd::JumpReason, for the gravity mode's jet jump.</summary>
    public enum AiJumpReason
    {
        None = 0,
        Escape = 1,
        Chase = 2,
        Peek = 3,
    }

    /// <summary>
    /// The five states try_switching_to_relevant_state chooses between. The
    /// source names them by script class; the port needs a value it can score.
    /// Duel, Withdraw and Chase are the port's engagement layer on top: they
    /// are entered from an odds judgement, never from the source's scoring.
    /// Escort and Rush belong to the mode layer: a Big Boss healer or graviton
    /// keeping to its boss, a bomber's or an Uprising rammer's charge.
    /// </summary>
    public enum AiStateKind
    {
        Rollout = 0,
        Careful = 1,
        Brawl = 2,
        Distant = 3,
        Single = 4,
        Duel = 5,
        Withdraw = 6,
        Chase = 7,
        Escort = 8,
        Rush = 9,
    }
}
