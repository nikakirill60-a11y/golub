using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Save;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Outcome of a research, purchase, sale or install attempt. Godot shows the
    /// popups inline in <c>account.gd</c>; the port keeps the rules here and
    /// leaves popups and sounds to the hangar UI.
    /// </summary>
    public struct TankActionResult
    {
        public bool Ok;
        /// <summary>Localization key of the error, empty when <see cref="Ok"/>.</summary>
        public string ErrorKey;
        /// <summary>Amount actually paid: credits, gold or XP depending on the action.</summary>
        public int Paid;
        /// <summary>Free XP spent on top of the tank XP (Godot's free-XP branch).</summary>
        public int PaidFreeXp;

        public static TankActionResult Success(int paid = 0, int paidFreeXp = 0)
        {
            var result = new TankActionResult();
            result.Ok = true;
            result.ErrorKey = string.Empty;
            result.Paid = paid;
            result.PaidFreeXp = paidFreeXp;
            return result;
        }

        public static TankActionResult Fail(string errorKey)
        {
            var result = new TankActionResult();
            result.Ok = false;
            result.ErrorKey = errorKey;
            return result;
        }
    }

    /// <summary>
    /// Port of the research part of Godot <c>scripts/account.gd</c>:
    /// <c>can_research_tank</c>, <c>tank_researched</c>, <c>try_research_tank</c>,
    /// <c>try_buy_tank</c>, <c>try_sell_tank</c>, <c>try_research_module</c>,
    /// <c>install_module</c>, <c>module_researched</c>, <c>validate_modules</c>
    /// and <c>get_tank_status</c>.
    /// </summary>
    public sealed class TankResearchService
    {
        public const string ErrorNotEnoughExp = "popup:NotEnoughExp";
        public const string ErrorNotEnoughCredits = "popup:NotEnoughCredits";
        public const string ErrorNotEnoughGold = "popup:NotEnoughGold";
        public const string ErrorResearchTankFirst = "popup:research_tank_first";
        public const string ErrorBuyTankFirst = "popup:buy_tank_first";
        public const string ErrorResearchPrevious = "hangar:research_prev_mods";
        public const string ErrorTankAlreadyBought = "hangar:tank_alr_bought";
        public const string ErrorModuleAlreadyResearched = "hangar:mod_alr_researched";
        public const string ErrorModuleAlreadyInstalled = "hangar:mod_alr_installed";

        /// <summary>
        /// The installed turret does not carry this gun. Godot had no turrets,
        /// so neither its CSV nor the client string tables have a row for this
        /// refusal; the port authors one in Tools/port_localization_gaps.py
        /// rather than letting the screen print the raw key.
        /// </summary>
        public const string ErrorGunNeedsTurret = "hangar:mod_needs_turret";
        public const string ErrorInvalidTank = "invalid tank";

        private readonly AccountData _account;
        private readonly TanksDatabase _database;

        public TankResearchService(AccountData account, TanksDatabase database)
        {
            if (account == null)
            {
                throw new ArgumentNullException("account");
            }

            if (database == null)
            {
                throw new ArgumentNullException("database");
            }

            _account = account;
            _database = database;
        }

        /// <summary>Godot <c>get_tank_info_validate(name, true)</c>.</summary>
        public TankInfo Info(string tank)
        {
            return _database.Get(tank);
        }

        /// <summary>
        /// Godot <c>get_tanks_unlocked_by</c> reduced to a yes/no answer. A card
        /// rebuilt from the Blitz client hangs the vehicle edge off a module, so
        /// the preceding tank also has to have reached that module.
        /// </summary>
        private bool IsFreeTreeRoot(string tank)
        {
            var info = Info(tank);
            // The 11.20 mixed-nation AC branch starts at tier IV, so a root is
            // any tree card nothing leads to, not only a tier I one.
            return info != null && info.TreeTank && !info.Premium && info.Level >= 1
                && info.CostXp == 0 && _database.PrecedingTanks(tank).Count == 0;
        }

        public bool CanResearchTank(string tank)
        {
            // A newly added nation must be accessible to existing saves too.
            if (IsFreeTreeRoot(tank)) { return true; }
            var unlockers = _database.PrecedingTanks(tank);
            for (var i = 0; i < unlockers.Count; i++)
            {
                if (!TankResearched(unlockers[i].Name))
                {
                    continue;
                }

                if (VehicleEdgeOpen(unlockers[i], tank))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TankBought(string tank)
        {
            return _account.HasTank(tank);
        }

        /// <summary>Godot <c>tank_researched</c>: premium cards count as researched.</summary>
        public bool TankResearched(string tank)
        {
            var info = Info(tank);
            if (info == null)
            {
                return false;
            }

            return info.Premium || _account.HasTank(tank, true);
        }

        public bool ModuleResearched(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName))
            {
                return false;
            }

            for (var i = 0; i < _account.ResearchedModules.Count; i++)
            {
                if (string.Equals(_account.ResearchedModules[i], moduleName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Godot <c>get_tank_status</c>.</summary>
        public ResearchStatus TankStatus(string tank)
        {
            var info = Info(tank);
            if (info == null)
            {
                return ResearchStatus.NotAvailable;
            }

            if (TankBought(tank))
            {
                return ResearchStatus.InstalledBought;
            }

            if (TankResearched(tank))
            {
                return ResearchStatus.Researched;
            }

            if (CanResearchTank(tank))
            {
                return ResearchStatus.Available;
            }

            return ResearchStatus.NotAvailable;
        }

        /// <summary>Godot <c>is_tank_elite</c>: every follow-up tank and module researched.</summary>
        public bool IsTankElite(string tank)
        {
            var info = Info(tank);
            if (info == null)
            {
                return false;
            }

            // A bought vehicle is elite from the start unless it brings its own
            // module research (11.20 Battle Pass and AC vehicles).
            if (info.Premium && !HasModuleResearch(info))
            {
                return true;
            }

            foreach (var unlock in info.UnlockTanks)
            {
                AdditionalTankResearchLink additional;
                if (info.AdditionalResearchLinks.TryGetValue(unlock.Key, out additional)
                    && additional.PreserveLegacyElite) { continue; }
                if (_database.Has(unlock.Key) && !TankResearched(unlock.Key))
                {
                    return false;
                }
            }

            var targets = Targets(info);
            for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
            {
                var modules = Modules(info, kind);
                for (var i = 0; i < modules.Count; i++)
                {
                    if (info.Turrets.Count > 0)
                    {
                        if (!GraphResearched(targets, kind, modules[i]))
                        {
                            return false;
                        }

                        continue;
                    }

                    if (!ModuleResearched(modules[i].Name))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>True when any module of the card costs XP to research.</summary>
        public static bool HasModuleResearch(TankInfo info)
        {
            if (info == null) { return false; }
            for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
            {
                var modules = Modules(info, kind);
                for (var i = 0; i < modules.Count; i++)
                {
                    if (modules[i].CostXp > 0) { return true; }
                }
            }

            return false;
        }

        public static IReadOnlyList<ModuleInfo> Modules(TankInfo info, ModuleKind kind)
        {
            var modules = new List<ModuleInfo>();
            if (info == null)
            {
                return modules;
            }

            switch (kind)
            {
                case ModuleKind.Gun:
                    modules.AddRange(info.Guns);
                    break;
                case ModuleKind.Engine:
                    modules.AddRange(info.Engines);
                    break;
                case ModuleKind.Tracks:
                    modules.AddRange(info.Tracks);
                    break;
                case ModuleKind.Turret:
                    modules.AddRange(info.Turrets);
                    break;
                case ModuleKind.Radio:
                    modules.AddRange(info.Radios);
                    break;
            }

            return modules;
        }

        /// <summary>
        /// Prefix the Blitz client uses in its <c>unlocks</c> edges. The client
        /// calls the chassis "chassis" where the port calls it tracks.
        /// </summary>
        public static string EdgeKey(ModuleKind kind, string name)
        {
            switch (kind)
            {
                case ModuleKind.Gun:
                    return "gun:" + name;
                case ModuleKind.Engine:
                    return "engine:" + name;
                case ModuleKind.Tracks:
                    return "chassis:" + name;
                case ModuleKind.Turret:
                    return "turret:" + name;
                case ModuleKind.Radio:
                    return "radio:" + name;
                default:
                    return string.Empty;
            }
        }

        /// <summary>Every module the research graph of this card points at.</summary>
        private static HashSet<string> Targets(TankInfo info)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            if (info == null)
            {
                return targets;
            }

            for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
            {
                var modules = Modules(info, kind);
                for (var i = 0; i < modules.Count; i++)
                {
                    foreach (var edge in modules[i].Unlocks)
                    {
                        targets.Add(edge.Key);
                    }
                }
            }

            for (var t = 0; t < info.Turrets.Count; t++)
            {
                var turret = info.Turrets[t];
                for (var g = 0; g < turret.Guns.Count; g++)
                {
                    foreach (var edge in turret.Guns[g].Unlocks)
                    {
                        targets.Add(edge.Key);
                    }
                }
            }

            return targets;
        }

        /// <summary>
        /// A module nothing unlocks is the stock module: the client ships it
        /// with the tank, exactly like Godot treated index 0.
        /// </summary>
        private bool GraphResearched(
            HashSet<string> targets, ModuleKind kind, ModuleInfo module)
        {
            if (module == null)
            {
                return false;
            }

            return !targets.Contains(EdgeKey(kind, module.Name))
                || ModuleResearched(module.Name);
        }

        /// <summary>
        /// Edges whose source is already researched: those are the modules the
        /// player may research next. A gun edge only counts when the turret
        /// that carries the gun is researched too, because the client authors
        /// the edge inside <c>turrets0/&lt;turret&gt;/guns/&lt;gun&gt;</c>.
        /// </summary>
        private Dictionary<string, int> OpenEdges(TankInfo info)
        {
            var open = new Dictionary<string, int>(StringComparer.Ordinal);
            if (info == null)
            {
                return open;
            }

            var targets = Targets(info);
            for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
            {
                var modules = Modules(info, kind);
                for (var i = 0; i < modules.Count; i++)
                {
                    if (!GraphResearched(targets, kind, modules[i]))
                    {
                        continue;
                    }

                    foreach (var edge in modules[i].Unlocks)
                    {
                        open[edge.Key] = edge.Value;
                    }
                }
            }

            for (var t = 0; t < info.Turrets.Count; t++)
            {
                var turret = info.Turrets[t];
                if (!GraphResearched(targets, ModuleKind.Turret, turret))
                {
                    continue;
                }

                for (var g = 0; g < turret.Guns.Count; g++)
                {
                    var slot = turret.Guns[g];
                    var gun = GunNamed(info, slot.Name);
                    if (gun == null || !GraphResearched(targets, ModuleKind.Gun, gun))
                    {
                        continue;
                    }

                    foreach (var edge in slot.Unlocks)
                    {
                        open[edge.Key] = edge.Value;
                    }
                }
            }

            return open;
        }

        private static GunInfo GunNamed(TankInfo info, string name)
        {
            for (var i = 0; info != null && i < info.Guns.Count; i++)
            {
                if (string.Equals(info.Guns[i].Name, name, StringComparison.Ordinal))
                {
                    return info.Guns[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Godot researched the next tank from <c>unlock_tanks</c> of the card.
        /// With the client graph the tank edges hang off the modules, so a
        /// follow-up tank is only reachable once its source module is.
        /// </summary>
        public bool VehicleEdgeOpen(TankInfo info, string tank)
        {
            if (info == null || info.Turrets.Count == 0)
            {
                return true;
            }

            AdditionalTankResearchLink additional;
            if (info.AdditionalResearchLinks.TryGetValue(tank, out additional))
            {
                var targets = Targets(info);
                return GraphResearched(targets, additional.RequiredKind, additional.RequiredModule)
                    && (additional.RequiredTurret == null
                        || GraphResearched(targets, ModuleKind.Turret, additional.RequiredTurret));
            }

            // Card names are lower cased on load, the client spells the edge
            // the way the vehicle file is named ("vehicle:BT-2").
            var wanted = "vehicle:" + tank;
            foreach (var edge in OpenEdges(info))
            {
                if (string.Equals(edge.Key, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public int InstalledModuleId(TankSaveData save, ModuleKind kind)
        {
            if (save == null)
            {
                return 0;
            }

            switch (kind)
            {
                case ModuleKind.Gun:
                    return save.GunId;
                case ModuleKind.Engine:
                    return save.EngineId;
                case ModuleKind.Tracks:
                    return save.TrackId;
                case ModuleKind.Turret:
                    return save.TurretId;
                case ModuleKind.Radio:
                    return save.RadioId;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Godot treats the stock module as researched for every owned tank: it
        /// runs <c>validate_modules</c> for each tank on load, and the module tile
        /// itself forces <c>RESEARCHED</c> for index 0. The port mirrors that here
        /// so a freshly added tank does not show its stock module as locked.
        /// </summary>
        private bool StockOrResearched(IReadOnlyList<ModuleInfo> modules, int id)
        {
            if (id == 0)
            {
                return true;
            }

            if (id < 0 || id >= modules.Count)
            {
                return false;
            }

            return ModuleResearched(modules[id].Name);
        }

        /// <summary>Status of a module tile, matching the Godot tile backgrounds.</summary>
        public ResearchStatus ModuleStatus(string tank, ModuleKind kind, int id)
        {
            var info = Info(tank);
            var modules = Modules(info, kind);
            if (info == null || id < 0 || id >= modules.Count)
            {
                return ResearchStatus.NotAvailable;
            }

            var save = _account.GetTank(tank);
            if (save != null && InstalledModuleId(save, kind) == id)
            {
                return ResearchStatus.InstalledBought;
            }

            // Cards rebuilt from the Blitz client carry the real research
            // graph, so a module is available when an already researched
            // module unlocks it, not when the previous tile happens to be
            // researched.
            if (info.Turrets.Count > 0)
            {
                var targets = Targets(info);
                if (GraphResearched(targets, kind, modules[id]))
                {
                    return ResearchStatus.Researched;
                }

                return OpenEdges(info).ContainsKey(EdgeKey(kind, modules[id].Name))
                    ? ResearchStatus.Available
                    : ResearchStatus.NotAvailable;
            }

            if (StockOrResearched(modules, id))
            {
                return ResearchStatus.Researched;
            }

            // Godot gates a module behind the previous one of the same kind.
            if (StockOrResearched(modules, id - 1))
            {
                return ResearchStatus.Available;
            }

            return ResearchStatus.NotAvailable;
        }

        /// <summary>
        /// What the shop charges for a card. The client prices its premiums,
        /// event vehicles and collectibles in gold through <c>cost_gold</c>
        /// and leaves their silver price at zero, while researchable cards
        /// keep paying silver out of <c>cost</c>.
        /// </summary>
        public static int PurchasePrice(TankInfo info)
        {
            if (info == null)
            {
                return 0;
            }

            if (info.Premium)
            {
                return Math.Max(info.CostGold, 0);
            }

            return Math.Max(info.Cost, 0);
        }

        /// <summary>Godot <c>TankInfoMain.get_sell_value</c>.</summary>
        public static int SellValue(TankInfo info)
        {
            if (info == null)
            {
                return 0;
            }

            if (info.Collector)
            {
                return Math.Max(info.CollectorSellGold, 0);
            }

            var credits = info.Premium ? info.Cost * 400 : info.Cost;
            return (int)(credits * 0.5);
        }

        /// <summary>Godot <c>try_research_tank</c> after the confirmation popup.</summary>
        public TankActionResult ResearchTank(string tank)
        {
            var info = Info(tank);
            if (info == null)
            {
                return TankActionResult.Fail(ErrorInvalidTank);
            }

            if (TankBought(tank))
            {
                return TankActionResult.Fail(ErrorTankAlreadyBought);
            }

            if (TankResearched(tank))
            {
                return TankActionResult.Fail(ErrorTankAlreadyBought);
            }

            if (!CanResearchTank(tank))
            {
                return TankActionResult.Fail(ErrorResearchPrevious);
            }

            var cost = _database.ResearchCost(tank);
            if (cost == 0 && IsFreeTreeRoot(tank))
            {
                _account.AddTank(tank);
                return TankActionResult.Success(0);
            }
            var payer = BestPayerFor(tank, cost);
            if (payer == null || payer.Exp + _account.FreeXp < cost)
            {
                return TankActionResult.Fail(ErrorNotEnoughExp);
            }

            var fromTank = Math.Min(payer.Exp, cost);
            var fromFreeXp = cost - fromTank;
            payer.Exp -= fromTank;
            _account.FreeXp -= fromFreeXp;
            _account.AddTank(tank);
            return TankActionResult.Success(fromTank, fromFreeXp);
        }

        /// <summary>Preceding tank with the most XP, as Godot picks its priority payer.</summary>
        private TankSaveData BestPayerFor(string tank, int cost)
        {
            var unlockers = _database.PrecedingTanks(tank);
            TankSaveData best = null;
            for (var i = 0; i < unlockers.Count; i++)
            {
                var save = _account.GetTank(unlockers[i].Name);
                if (save == null)
                {
                    continue;
                }

                if (best == null || save.Exp > best.Exp)
                {
                    best = save;
                }
            }

            return best;
        }

        /// <summary>Godot <c>try_buy_tank</c> after the confirmation popup.</summary>
        public TankActionResult BuyTank(string tank)
        {
            var info = Info(tank);
            if (info == null)
            {
                return TankActionResult.Fail(ErrorInvalidTank);
            }

            if (TankBought(tank))
            {
                return TankActionResult.Fail(ErrorTankAlreadyBought);
            }

            if (!TankResearched(tank))
            {
                return TankActionResult.Fail(
                    info.Premium ? ErrorBuyTankFirst : ErrorResearchTankFirst);
            }

            // Premiums are priced in gold by the client and carry no silver
            // price at all, so the shop reads cost_gold for them.
            var price = PurchasePrice(info);
            if (info.Premium)
            {
                if (_account.Gold < price)
                {
                    return TankActionResult.Fail(ErrorNotEnoughGold);
                }
            }
            else if (_account.Credits < price)
            {
                return TankActionResult.Fail(ErrorNotEnoughCredits);
            }

            var save = _account.GetTank(tank);
            if (save != null)
            {
                save.Bought = true;
            }
            else
            {
                _account.AddTank(tank, true);
            }

            if (info.Premium)
            {
                _account.Gold -= price;
            }
            else
            {
                _account.Credits = Math.Max(_account.Credits - price, 0);
            }

            return TankActionResult.Success(price);
        }

        /// <summary>Godot <c>sell_tank</c>: keeps the card researched, drops ownership.</summary>
        public TankActionResult SellTank(string tank)
        {
            var info = Info(tank);
            if (info == null)
            {
                return TankActionResult.Fail(ErrorInvalidTank);
            }

            if (!TankResearched(tank))
            {
                return TankActionResult.Fail(
                    info.Premium ? ErrorBuyTankFirst : ErrorResearchTankFirst);
            }

            var save = _account.GetTank(tank);
            if (save == null || !save.Bought)
            {
                return TankActionResult.Fail(ErrorBuyTankFirst);
            }

            var value = SellValue(info);
            save.Bought = false;
            if (info.Collector)
            {
                _account.Gold = AccountData.AddCurrency(_account.Gold, value);
            }
            else
            {
                _account.Credits = Math.Max(AccountData.AddCurrency(_account.Credits, value), 0L);
            }

            var bought = _account.TanksBoughtCount;
            _account.SelectedTankId = Math.Min(
                Math.Max(_account.SelectedTankId, 0), Math.Max(bought - 1, 0));
            return TankActionResult.Success(value);
        }

        /// <summary>Godot <c>try_research_module</c> after the confirmation popup.</summary>
        public TankActionResult ResearchModule(string tank, ModuleKind kind, int id)
        {
            var info = Info(tank);
            if (info == null)
            {
                return TankActionResult.Fail(ErrorInvalidTank);
            }

            if (!_account.HasTank(tank, true))
            {
                return TankActionResult.Fail(
                    info.Premium ? ErrorBuyTankFirst : ErrorResearchTankFirst);
            }

            var modules = Modules(info, kind);
            if (id < 0 || id >= modules.Count)
            {
                return TankActionResult.Fail(ErrorInvalidTank);
            }

            var status = ModuleStatus(tank, kind, id);
            if (status == ResearchStatus.InstalledBought)
            {
                return TankActionResult.Fail(ErrorModuleAlreadyInstalled);
            }

            if (status == ResearchStatus.Researched)
            {
                return TankActionResult.Fail(ErrorModuleAlreadyResearched);
            }

            if (status == ResearchStatus.NotAvailable)
            {
                return TankActionResult.Fail(ErrorResearchPrevious);
            }

            var save = _account.GetTank(tank);
            var cost = modules[id].CostXp;
            if (save == null || save.Exp + _account.FreeXp < cost)
            {
                return TankActionResult.Fail(ErrorNotEnoughExp);
            }

            var fromTank = Math.Min(save.Exp, cost);
            var fromFreeXp = cost - fromTank;
            save.Exp -= fromTank;
            _account.FreeXp -= fromFreeXp;
            MarkModuleResearched(modules[id].Name);
            InstallModule(tank, kind, id);
            return TankActionResult.Success(fromTank, fromFreeXp);
        }

        /// <summary>Godot <c>_research_module</c>.</summary>
        public void MarkModuleResearched(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName) || ModuleResearched(moduleName))
            {
                return;
            }

            _account.ResearchedModules.Add(moduleName);
        }

        /// <summary>Godot <c>install_module</c>: installing a gun clears the ammo rack.</summary>
        public TankActionResult InstallModule(string tank, ModuleKind kind, int id)
        {
            var info = Info(tank);
            if (info == null)
            {
                return TankActionResult.Fail(ErrorInvalidTank);
            }

            if (!_account.HasTank(tank, true))
            {
                return TankActionResult.Fail(
                    info.Premium ? ErrorBuyTankFirst : ErrorResearchTankFirst);
            }

            var modules = Modules(info, kind);
            if (modules.Count < 1)
            {
                return TankActionResult.Fail(ErrorInvalidTank);
            }

            var save = _account.GetTank(tank);
            if (save == null)
            {
                return TankActionResult.Fail(ErrorResearchTankFirst);
            }

            id = Math.Min(Math.Max(id, 0), modules.Count - 1);
            var graph = info.Turrets.Count > 0;
            if (graph)
            {
                if (!GraphResearched(Targets(info), kind, modules[id]))
                {
                    return TankActionResult.Fail(ErrorResearchPrevious);
                }
            }
            else if (!ModuleResearched(modules[id].Name) && id != 0)
            {
                return TankActionResult.Fail(ErrorResearchPrevious);
            }

            switch (kind)
            {
                case ModuleKind.Gun:
                    // A gun only fits the turrets the client lists it in.
                    if (graph)
                    {
                        var mounted = info.Turret(save.TurretId);
                        if (mounted != null && !mounted.CanMount(modules[id].Name))
                        {
                            return TankActionResult.Fail(ErrorGunNeedsTurret);
                        }
                    }

                    save.GunId = id;
                    ClearRack(save);
                    break;
                case ModuleKind.Engine:
                    save.EngineId = id;
                    break;
                case ModuleKind.Radio:
                    save.RadioId = id;
                    break;
                case ModuleKind.Turret:
                    save.TurretId = id;
                    // Blitz drops a gun the new turret cannot carry and mounts
                    // the best gun of that turret instead.
                    var replacement = info.GunIdFor(id, save.GunId);
                    if (replacement != save.GunId)
                    {
                        save.GunId = replacement;
                        ClearRack(save);
                    }

                    break;
                default:
                    save.TrackId = id;
                    break;
            }

            return TankActionResult.Success();
        }

        /// <summary>
        /// Godot rebuilds shells_amounts from the new gun, so the rack is
        /// emptied; the save keeps its fixed slot count.
        /// </summary>
        private static void ClearRack(TankSaveData save)
        {
            for (var i = 0; i < save.ShellsAmounts.Count; i++)
            {
                save.ShellsAmounts[i] = 0;
            }
        }

        /// <summary>
        /// Godot <c>SaveTank.sort_modules</c>: the module list with repeated
        /// names dropped. Every module id indexes into this list, so a raw
        /// index into the card's array would be clamped back down on load.
        /// </summary>
        public static List<ModuleInfo> UniqueModules(IReadOnlyList<ModuleInfo> modules)
        {
            var unique = new List<ModuleInfo>();
            if (modules == null)
            {
                return unique;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < modules.Count; i++)
            {
                var module = modules[i];
                if (module == null || !names.Add(module.Name))
                {
                    continue;
                }

                unique.Add(module);
            }

            return unique;
        }

        /// <summary>
        /// Godot <c>ui_account_toolbox.gd::_top_module_id</c>: the last unique
        /// module, or -1 when the card carries none of that kind.
        /// </summary>
        public static int TopModuleId(IReadOnlyList<ModuleInfo> modules)
        {
            return UniqueModules(modules).Count - 1;
        }

        /// <summary>
        /// Godot <c>Account.cleanup()</c>: drop repeated garage entries and
        /// cards the database no longer carries, normalise every save and mark
        /// the stock and installed modules as researched. Logging in runs this
        /// in the source; the account toolbox runs it after adding tanks.
        /// Returns how many tanks are left in the garage.
        /// </summary>
        public int CleanupAccount()
        {
            var kept = new List<TankSaveData>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < _account.Tanks.Count; i++)
            {
                var save = _account.Tanks[i];
                if (save == null || names.Contains(save.Name))
                {
                    continue;
                }

                // get_tank_by_name() answers with the base tank for a card the
                // database does not carry, and the source drops both cases.
                var info = _database.Get(save.Name);
                if (info == null ||
                    string.Equals(info.Name, TankSaveData.DefaultName, StringComparison.Ordinal))
                {
                    continue;
                }

                names.Add(save.Name);
                kept.Add(save);
                save.Cleanup();
            }

            if (kept.Count != _account.Tanks.Count)
            {
                _account.Tanks.Clear();
                _account.Tanks.AddRange(kept);
            }

            for (var i = 0; i < kept.Count; i++)
            {
                ValidateModules(kept[i].Name);
            }

            return kept.Count;
        }

        /// <summary>Godot <c>validate_modules</c>: the stock and installed modules count as researched.</summary>
        public void ValidateModules(string tank)
        {
            var info = Info(tank);
            var save = _account.GetTank(tank);
            if (info == null || save == null)
            {
                return;
            }

            var graph = info.Turrets.Count > 0;
            if (graph)
            {
                // The client keeps the gun list inside the turret, so the
                // turret is clamped first and the gun follows whatever the
                // installed turret can actually carry.
                save.TurretId = Math.Min(
                    Math.Max(save.TurretId, 0), info.Turrets.Count - 1);
                var gunId = info.GunIdFor(save.TurretId, save.GunId);
                if (gunId != save.GunId)
                {
                    save.GunId = gunId;
                    ClearRack(save);
                }
            }

            for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
            {
                var modules = Modules(info, kind);
                if (modules.Count < 1)
                {
                    continue;
                }

                var installed = InstalledModuleId(save, kind);
                if (installed < 0 || installed >= modules.Count)
                {
                    installed = 0;
                }

                // On a graph card the stock module is the one no edge points
                // at, and marking index 0 researched would hand out a module
                // the player still has to unlock.
                if (!graph)
                {
                    MarkModuleResearched(modules[0].Name);
                }

                MarkModuleResearched(modules[installed].Name);
            }
        }
    }
}
