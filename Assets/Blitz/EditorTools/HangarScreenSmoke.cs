using System;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Shop;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Time;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Hangar.Halloween;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Lighting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Real Play Mode validation of the first 3D hangar vertical slice.</summary>
    [InitializeOnLoad]
    public static class HangarScreenSmoke
    {
        private const string ActiveKey = "Blitz.HangarSmoke.Active";
        private const string ExitCodeKey = "Blitz.HangarSmoke.ExitCode";
        private const string DeadlineKey = "Blitz.HangarSmoke.Deadline";
        private static bool _hooked;
        private static AccountData _smokeAccount;

        /// <summary>
        /// The wall clock of the run: a week after the Halloween 2026 hangar,
        /// so the 4.8 checks see the hangar they were written for on any date;
        /// <see cref="RequireEventHangar"/> moves it through the event.
        /// </summary>
        private static FixedClock _smokeClock;

        static HangarScreenSmoke()
        {
            if (SessionState.GetBool(ActiveKey, false))
            {
                Hook();
            }
        }

        public static void Run()
        {
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(ExitCodeKey, -1);
            SessionState.SetString(
                DeadlineKey, DateTime.UtcNow.AddMinutes(4).ToString("O"));
            Hook();
            try
            {
                EditorSceneManager.OpenScene(
                    SceneGen.BootstrapScenePath, OpenSceneMode.Single);
                EditorApplication.isPlaying = true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Quit(1);
            }
        }

        private static void Hook()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        private static void Unhook()
        {
            if (!_hooked)
            {
                return;
            }
            _hooked = false;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= OnEditorUpdate;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                return;
            }
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                EditorApplication.delayCall += Prepare;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                var exitCode = SessionState.GetInt(ExitCodeKey, -1);
                if (exitCode >= 0)
                {
                    Quit(exitCode);
                }
            }
        }

        private static void Prepare()
        {
            try
            {
                var bootstrap = FindSceneObject<BlitzBootstrap>();
                Require(bootstrap != null, "BlitzBootstrap was not created");
                var login = FindSceneObject<LoginScreen>();
                Require(login != null, "LoginScreen was not created before transition");
                _smokeClock = new FixedClock(
                    LocalUnixSeconds(HangarSchedule.Halloween2026LastDay.AddDays(7).AddHours(12)));
                bootstrap.UseClock(_smokeClock);

                var account = new AccountData
                {
                    Name = "HangarSmoke",
                    Credits = 250000,
                    Gold = 250,
                    FreeXp = 0,
                    SelectedTankName = "ms-1",
                };
                account.AddTank("ms-1", true);

                _smokeAccount = account;
                UnityEngine.Object.Destroy(login.gameObject);
                HangarScreen.Create(bootstrap, account);
                EditorApplication.delayCall += Validate;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Finish(1);
            }
        }

        private static void Validate()
        {
            try
            {
                var bootstrap = FindSceneObject<BlitzBootstrap>();
                var screen = FindSceneObject<HangarScreen>();
                Require(bootstrap != null, "BlitzBootstrap disappeared");
                Require(screen != null, "HangarScreen was not created");
                Require(FindSceneObject<LoginScreen>() == null,
                    "LoginScreen survived the hangar transition");

                var world = screen.World;
                Require(world != null, "HangarWorld is missing");
                Require(world.HangarModel != null, "Hangar model is missing");
                Require(world.TankModel != null, "MS-1 model is missing");

                var hangarRenderers = world.HangarModel.GetComponentsInChildren<MeshRenderer>(true);
                Require(world.ClientScene != null && world.ClientScene.Definition.id == "hangar",
                    "Default account must use the original ordinary 4.8 hangar");
                Require(hangarRenderers.Length == world.ClientScene.Definition.batches.Length,
                    "Source hangar batch count changed");
                Require(world.SourceLightCount == 1, "Ordinary hangar needs its source dynamic light");
                Require(world.SmokeEmitterCount == 4, "Ordinary hangar needs four original smoke emitters");
                Require(world.ClientScene.ParticleLayerCount == 66, "Ordinary hangar lost source particle layers");
                Require(world.Lighting == null, "Godot hangar bake must not be loaded by the 4.8 world");
                Require(world.HangarCamera != null && world.HangarCamera.enabled, "Hangar camera missing");
                Require(Mathf.Approximately(world.HangarCamera.fieldOfView, HangarOrbitController.SourceFov),
                    "Hangar camera FOV changed");
                var fog = Shader.GetGlobalVector(SourceEnvironment.FogRangeId);
                Require(Mathf.Abs(fog.x - 12f) < .001f && Mathf.Abs(fog.y - 60f) < .001f,
                    "Hangar must use the 4.8 fog range, not Godot fog");
                var tankRenderers =
                    world.TankModel.GetComponentsInChildren<MeshRenderer>(true);
                Require(hangarRenderers.Length == world.BakedInstanceCount,
                    "Every baked hangar instance needs its own renderer");
                // MS1.obj carries an object record per part so the battle tank can
                // traverse its turret, which is why Unity imports it as one
                // renderer per object instead of a single merged mesh. The
                // hangar shows the same tree, so the geometry is checked by
                // walking it: body and tracks still have to add up to the
                // source's visible triangle counts.
                Require(tankRenderers.Length == 38,
                    "MS-1 should import one renderer per source object, got " +
                    tankRenderers.Length);

                var bodyTriangles = 0L;
                var trackTriangles = 0L;
                var trackParts = 0;
                for (var i = 0; i < tankRenderers.Length; i++)
                {
                    var part = tankRenderers[i];
                    var mesh = GetMesh(part);
                    Require(mesh != null && mesh.subMeshCount == 1,
                        "MS-1 part " + part.name + " is not a single-material mesh");
                    if (part.name == "tracks_crash")
                    {
                        Require(!part.gameObject.activeSelf, "Broken tracks must be hidden on a live tank");
                        continue;
                    }
                    var isTrack = IsTrackPart(part.name);
                    if (isTrack)
                    {
                        trackParts++;
                        trackTriangles += TriangleCount(mesh, 0);
                    }
                    else
                    {
                        bodyTriangles += TriangleCount(mesh, 0);
                    }
                }

                Require(trackParts == 2, "MS-1 is missing one of its two tracks");
                Require(bodyTriangles == 4546L && trackTriangles == 432L,
                    "MS-1 visible geometry does not match the source OBJ, got " +
                    bodyTriangles + " body and " + trackTriangles + " track triangles");
                // The client pack names the modules turret_NN / gun_NN and
                // ships every researchable pair in one OBJ, so the stand has
                // to carry the mounted turret and at least one gun mesh.
                Require(FindByName<Transform>(world.TankModel, "turret_01") != null &&
                        FindByName<Transform>(world.TankModel, "gun_02") != null,
                    "MS-1 lost the turret and gun objects the battle tank aims");

                var sourceMaterials = new HashSet<string>(StringComparer.Ordinal);
                foreach (var renderer in hangarRenderers)
                {
                    Require(renderer.sharedMaterial != null &&
                            renderer.sharedMaterial.shader.name == ClientHangarScene.SurfaceShader,
                        "4.8 hangar surface lost its source material");
                    Require(renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off,
                        "Source lightmapped geometry must not be lit twice");
                    var mesh = GetMesh(renderer);
                    Require(mesh != null && mesh.uv2.Length == mesh.vertexCount,
                        "Hangar lost the second source UV channel");
                    sourceMaterials.Add(renderer.sharedMaterial.name);
                }
                Require(sourceMaterials.Count == 28, "Ordinary hangar lost source material bindings");

                // One material per part now, picked by object name the way
                // HangarWorld assigns them.
                Material trackMaterial = null;
                for (var i = 0; i < tankRenderers.Length; i++)
                {
                    var part = tankRenderers[i];
                    // The inactive destruction mesh is not assigned a live tank material.
                    if (!part.gameObject.activeSelf && part.name == "tracks_crash") continue;
                    Require(part.sharedMaterials.Length == 1,
                        "MS-1 part " + part.name + " has more than one material");
                    var isTrack = IsTrackPart(part.name);
                    RequireMaterial(part.sharedMaterial, isTrack ? "MS1Tracks" : "MS1Body");
                    if (isTrack)
                    {
                        trackMaterial = part.sharedMaterial;
                    }
                }

                Require(trackMaterial != null &&
                        trackMaterial.IsKeywordEnabled("_ALPHATEST_ON"),
                    "MS-1 track alpha clipping is not enabled");

                Require(screen.DragSurface != null, "Hangar drag surface is missing");
                var dragGraphic = screen.DragSurface.GetComponent<Graphic>();
                Require(dragGraphic != null && dragGraphic.raycastTarget,
                    "Hangar drag surface cannot receive pointer input");
                // Device screenshots cannot prove that the camera turned once
                // the smoke animates, so the whole pointer path is exercised
                // here: PointerEventData -> HangarDragSurface -> orbit.
                var pointer = new PointerEventData(EventSystem.current)
                {
                    pointerId = 0,
                    position = new Vector2(400f, 300f),
                    delta = new Vector2(100f, 0f),
                };
                var yaw = world.Orbit.Yaw;
                screen.DragSurface.OnBeginDrag(pointer);
                Require(world.Orbit.IsDragging,
                    "Hangar drag surface did not start an orbit drag");
                screen.DragSurface.OnDrag(pointer);
                var rightYaw = world.Orbit.Yaw - yaw;
                Require(Mathf.Abs(rightYaw) > 0.5f,
                    "Hangar drag did not move the orbit camera");

                var secondFinger = new PointerEventData(EventSystem.current)
                {
                    pointerId = 1,
                    position = pointer.position,
                    delta = new Vector2(400f, 0f),
                };
                screen.DragSurface.OnDrag(secondFinger);
                Require(Mathf.Approximately(world.Orbit.Yaw - yaw, rightYaw),
                    "A second pointer must not steer the hangar camera");

                pointer.delta = new Vector2(-100f, 0f);
                screen.DragSurface.OnDrag(pointer);
                var leftYaw = world.Orbit.Yaw - yaw - rightYaw;
                Require(Mathf.Abs(rightYaw + leftYaw) < 0.001f,
                    "Opposite drags must rotate the hangar camera symmetrically");
                screen.DragSurface.OnEndDrag(pointer);
                Require(!world.Orbit.IsDragging,
                    "Hangar drag surface did not end the orbit drag");

                var sourceEffects = world.GetComponentsInChildren<ParticleSystem>(true);
                Require(sourceEffects.Length == 66, "Ordinary 4.8 hangar lost effect layers");
                foreach (var effect in sourceEffects)
                {
                    Require(effect.GetComponent<ParticleSystemRenderer>().sharedMaterial.shader.name == ClientHangarScene.ParticleShader,
                        "Hangar effects must use original source sprites and blending");
                    Require(effect.main.simulationSpace == ParticleSystemSimulationSpace.World,
                        "Hangar effects lost source world-space simulation");
                }

                Require(screen.Toolbox != null,
                    "Debug account toolbox was not built");
                screen.Toolbox.CreditsField.text = "987654";
                screen.Toolbox.GoldField.text = "7654";
                screen.Toolbox.FreeXpField.text = "54321";
                screen.Toolbox.PremHoursField.text = "12";
                screen.Toolbox.SetCredits();
                screen.Toolbox.SetGold();
                screen.Toolbox.SetFreeXp();
                screen.Toolbox.AddPremHours();
                // ui_hangar.update() formats the header with
                // Util.format_number, so the readouts are grouped.
                Require(screen.CurrencyAmount("credits_amount") == "987 654"
                        && screen.CurrencyAmount("gold_amount") == "7 654"
                        && screen.CurrencyAmount("freexp_amount") == "54 321",
                    "Debug resource setters did not update the real hangar header");
                Require(screen.PremiumText.Contains("12"),
                    "Debug premium hours did not update the account header");

                screen.Toolbox.UnlockAllTanks();
                screen.Toolbox.FullUpgradeAllTanks();
                // tanks.json carries 522 cards; the World of Ducks WarDuck is
                // hidden and stays out of the hangar.
                Require(screen.CarouselCardCount == 521,
                    "Unlock all must add the complete hybrid 4.8/11.20 playable roster");
                // Only the cards over the view exist; building the whole
                // roster up front stalled every filter, pick and purchase.
                Require(screen.CarouselCardsBuilt > 0 &&
                        screen.CarouselCardsBuilt <= MaxCarouselCardsBuilt,
                    "The carousel built " + screen.CarouselCardsBuilt + " of its 521 cards up front");

                // Blitz 4.8 stores every carousel silhouette as an authored
                // BigTankIcons frame. Validate the first one against the source
                // descriptor, then validate every card against the exact Godot
                // carousel_tank.tscn geometry. This catches both wrong artwork
                // and the cumulative drift caused by one malformed card.
                Canvas.ForceUpdateCanvases();
                // The carousel opens on the strongest vehicles, so MS-1 is no
                // longer the leftmost card: look it up by its own frame.
                var ms1Card = FindCarouselCard(screen, "tank_icon_ms1");
                var cardIcon = ms1Card == null ? null : ms1Card.Find("icon").GetComponent<Image>();
                Require(cardIcon != null && cardIcon.sprite != null,
                    "Carousel card has no MS-1 icon");
                Require(Mathf.Approximately(cardIcon.sprite.rect.width, 218f) &&
                        Mathf.Approximately(cardIcon.sprite.rect.height, 193f),
                    "MS-1 icon does not use its Blitz 4.8 BigTankIcons frame");
                var iconSize = cardIcon.rectTransform.rect.size;
                // The modern carousel sizes its cards from ui_carousel_size, so
                // the Godot KEEP_CENTERED geometry only applies to classic.
                Require(screen.IsModern || (Mathf.Abs(iconSize.x - 132.98f) < 0.05f &&
                        Mathf.Abs(iconSize.y - 117.73f) < 0.05f),
                    "MS-1 icon is not drawn at its KEEP_CENTERED size, got " + iconSize);

                const float carouselCardWidth = 196f;
                const float carouselCardHeight = 130f;
                const float carouselSeparation = 4f;
                var carouselNations = new HashSet<string>();
                var carouselLevels = new List<int>();
                var carouselGoldGlyphs = new List<bool>();
                var collectorGlyphs = 0;
                for (var cardIndex = 0; cardIndex < screen.CarouselCardCount; cardIndex++)
                {
                    // Scrolled on one card at a time: the strip binds each
                    // as it comes in, on a card that scrolled out.
                    var card = screen.ShowCarouselCard(cardIndex);
                    // The modern card keeps the original nation plate as a faded
                    // child inside its rounded frame.
                    var plate = screen.IsModern ? card.Find("nation") : card;
                    var cardBackground = plate == null ? null : plate.GetComponent<Image>();
                    // Card geometry is the Godot carousel's; the modern one is
                    // sized by ui_carousel_size and laid out by its own strip.
                    var classicGeometry = !screen.IsModern;
                    Require(!classicGeometry || (Mathf.Abs(card.rect.width - carouselCardWidth) < 0.01f &&
                            Mathf.Abs(card.rect.height - carouselCardHeight) < 0.01f),
                        "Carousel card " + cardIndex + " is not 196x130");
                    Require(!classicGeometry || (Mathf.Abs(card.anchoredPosition.x -
                                      (cardIndex * (carouselCardWidth + carouselSeparation))) < 0.05f &&
                            Mathf.Abs(card.anchoredPosition.y) < 0.05f),
                        "Carousel card " + cardIndex + " is not aligned to the 200 px grid");
                    Require(cardBackground != null && cardBackground.sprite != null &&
                            cardBackground.sprite.name.StartsWith("tank_nation_"),
                        "Carousel card " + cardIndex + " has no nation flag plate");
                    carouselNations.Add(cardBackground.sprite.name);

                    var lvlTransform = card.Find("lvl");
                    var lvlImage = lvlTransform == null
                        ? null : lvlTransform.GetComponent<Image>();
                    Require(lvlImage != null && lvlImage.sprite != null &&
                            lvlImage.sprite.name.StartsWith("tank_level_"),
                        "Carousel card " + cardIndex + " has no level glyph");
                    var lvlName = lvlImage.sprite.name.Substring("tank_level_".Length);
                    var goldGlyph = lvlName.EndsWith("_prem");
                    if (goldGlyph)
                    {
                        lvlName = lvlName.Substring(0, lvlName.Length - "_prem".Length);
                    }

                    int cardLevel;
                    Require(int.TryParse(lvlName, out cardLevel) &&
                            cardLevel >= 1 && cardLevel <= 10,
                        "Carousel card " + cardIndex + " has an unreadable level glyph");
                    carouselLevels.Add(cardLevel);
                    carouselGoldGlyphs.Add(goldGlyph);

                    var classTransform = card.Find("class");
                    var classImage = classTransform == null
                        ? null : classTransform.GetComponent<Image>();
                    Require(classImage != null && classImage.sprite != null &&
                            classImage.sprite.name.StartsWith("tank_class_") &&
                            classImage.sprite.name.EndsWith("_prem") == goldGlyph,
                        "Carousel card " + cardIndex +
                        " mixes the plain and the gold class glyph");
                    Require(lvlImage.color == classImage.color,
                        "Carousel card " + cardIndex +
                        " tints its level and class glyphs differently");
                    if (lvlImage.color != Color.white)
                    {
                        Require(lvlImage.color == HangarUiKit.TankNameCollector &&
                                !goldGlyph,
                            "Carousel card " + cardIndex +
                            " uses a tint the source never applies");
                        collectorGlyphs++;
                    }

                    var cardImageTransform = card.Find("icon");
                    var cardImage = cardImageTransform == null
                        ? null : cardImageTransform.GetComponent<Image>();
                    Require(cardImage != null && cardImage.sprite != null,
                        "Carousel card " + cardIndex + " has no exact tank icon");
                    var expectedIcon = HangarScreen.KeepCenteredRect(
                        new Rect(0.43f, 4.5f, 195.2f, 122f),
                        cardImage.sprite.rect.size,
                        0.61f);
                    Require(!classicGeometry || (Mathf.Abs(cardImage.rectTransform.anchoredPosition.x - expectedIcon.x) < 0.05f &&
                            Mathf.Abs(cardImage.rectTransform.anchoredPosition.y + expectedIcon.y) < 0.05f &&
                            Mathf.Abs(cardImage.rectTransform.rect.width - expectedIcon.width) < 0.05f &&
                            Mathf.Abs(cardImage.rectTransform.rect.height - expectedIcon.height) < 0.05f),
                        "Carousel card " + cardIndex + " icon broke KEEP_CENTERED alignment");

                    var cardNameTransform = card.Find("name");
                    var cardName = cardNameTransform == null
                        ? null : cardNameTransform.GetComponent<Text>();
                    Require(cardName != null && !string.IsNullOrWhiteSpace(cardName.text),
                        "Carousel card " + cardIndex + " has no localized short name");
                    Require(!classicGeometry || (Mathf.Abs(cardName.rectTransform.anchoredPosition.x - 8f) < 0.01f &&
                            Mathf.Abs(cardName.rectTransform.anchoredPosition.y + 100f) < 0.01f &&
                            Mathf.Abs(cardName.rectTransform.rect.width - 110f) < 0.01f &&
                            Mathf.Abs(cardName.rectTransform.rect.height - 25f) < 0.01f),
                        "Carousel card " + cardIndex + " name is not aligned like the source card");
                }
                Require(screen.CarouselCardsBuilt <= MaxCarouselCardsBuilt,
                    "Scrolling the carousel built " + screen.CarouselCardsBuilt +
                    " cards instead of reusing the ones that left the view");
                // Seven playable nations plus TankInfoMain.nation_flags[0], the
                // "other" plate worn by the crossover vehicles (Vindicator,
                // Van Helsing, Edelweiss, ...).
                // The carousel opens on tier 10 and walks down, and the gold
                // vehicles sit inside their own tier instead of behind the
                // whole tech tree.
                for (var i = 1; i < carouselLevels.Count; i++)
                {
                    Require(carouselLevels[i] <= carouselLevels[i - 1],
                        "Carousel card " + i + " breaks the tier 10 to 1 order");
                }

                var firstGoldCard = carouselGoldGlyphs.IndexOf(true);
                var lastPlainCard = carouselGoldGlyphs.LastIndexOf(false);
                Require(firstGoldCard >= 0 && lastPlainCard > firstGoldCard,
                    "Premium cards are parked behind the tech tree ones");
                Require(collectorGlyphs > 0,
                    "No carousel card wears the collector tint");

                Require(carouselNations.Count == 9,
                    "The complete carousel does not show all nine nation flags, got " +
                    carouselNations.Count);

                PressCarouselCard(screen, "tank_icon_t62a");
                Require(screen.World.TankModel != null
                        && screen.World.TankModel.name == "T62A",
                    "Selecting the T-62A card did not display T-62A");
                // The unchanged current UI routes battle/statistics/settings/
                // mods to real screens. Count only the remaining placeholders;
                // do not add a dummy button to satisfy an obsolete smoke value.
                // Both styles keep only crew and news; the filter toggles
                // and the drawer's logout are real now.
                Require(screen.PlaceholderButtonCount == 2,
                    "Unexpected remaining placeholder-wired hangar button count: " + screen.PlaceholderButtonCount);
                screen.ShowCarouselCard(0);
                Require(screen.IsModern || (screen.CarouselRoot.pivot == new Vector2(0f, 1f) &&
                        Mathf.Approximately(screen.CarouselRoot.anchoredPosition.x, 0f)),
                    "Carousel content is not flush against the left edge");

                var buttons = screen.GetComponentsInChildren<Button>(true);
                var texts = screen.GetComponentsInChildren<Text>(true);
                var sprites = 0;
                var images = screen.GetComponentsInChildren<Image>(true);
                for (var i = 0; i < images.Length; i++)
                {
                    if (images[i].sprite != null)
                    {
                        sprites++;
                    }
                }
                Require(buttons.Length >= 34, "Too few hangar buttons were built");
                Require(sprites >= 70, "Too few source atlas sprites were built");

                // The Godot theme clamps button icons to icon_max_width. Drawing
                // the atlas region at its native size is the regression that
                // made every hangar icon oversized.
                // Hidden sub-screens also own a btn_menu (back arrow). Select
                // the actual burger button rather than hierarchy traversal order.
                Button menuButton = null;
                foreach (var candidate in buttons)
                {
                    if (candidate.name != "btn_menu") continue;
                    var icon = candidate.transform.Find("icon");
                    var image = icon == null ? null : icon.GetComponent<Image>();
                    if (image != null && image.sprite != null && image.sprite.name == "hangar_menu")
                    { menuButton = candidate; break; }
                }
                Require(menuButton != null, "btn_menu is missing");
                var menuIcon = FindByName<Image>(menuButton.gameObject, "icon");
                // Modern draws the same hangar_menu glyph inside a smaller
                // rounded button, so only classic pins the Godot clamp.
                Require(menuIcon != null && (screen.IsModern ||
                        (Mathf.Abs(menuIcon.rectTransform.sizeDelta.x - 50f) < 0.5f &&
                        Mathf.Abs(menuIcon.rectTransform.sizeDelta.y - 35.2f) < 0.5f)),
                    "btn_menu icon is not clamped to the source icon_max_width: " +
                    (menuIcon == null ? "missing" : menuIcon.rectTransform.sizeDelta.ToString() +
                    " sprite=" + (menuIcon.sprite == null ? "missing" : menuIcon.sprite.rect.ToString())));

                // screens/main/menu: the drawer slides in from the left edge
                // instead of being shown and hidden, so it has to be present
                // and parked one full width off-screen before btn_menu is hit.
                var drawer = screen.GetComponentInChildren<HangarMenuPanel>(true);
                Require(drawer != null, "The hangar burger drawer is missing");
                Require(drawer.gameObject.activeInHierarchy,
                    "The drawer must stay in the tree while it is closed");
                Require(!drawer.IsOpen &&
                        Mathf.Abs(drawer.PositionX - HangarMenuPanel.ClosedX) < 0.01f,
                    "The drawer does not start parked off-screen");

                menuButton.onClick.Invoke();
                Require(drawer.IsOpen, "btn_menu did not open the drawer");
                drawer.Advance(HangarMenuPanel.SlideSeconds * 0.5f);
                Require(drawer.PositionX < HangarMenuPanel.OpenX &&
                        drawer.PositionX > HangarMenuPanel.ClosedX,
                    "The drawer jumped open instead of sliding, got " +
                    drawer.PositionX);
                drawer.Advance(HangarMenuPanel.SlideSeconds);
                Require(Mathf.Abs(drawer.PositionX - HangarMenuPanel.OpenX) < 0.01f,
                    "The drawer did not finish on the show_menu end key");

                var logoutButton = FindByName<Button>(drawer.gameObject, "btn_logout");
                var settingsButton = FindByName<Button>(drawer.gameObject, "btn_settings");
                Require(logoutButton != null && settingsButton != null,
                    "The drawer is missing btn_logout or btn_settings");
                Require(FindByName<Image>(logoutButton.gameObject, "icon") != null &&
                        FindByName<Image>(settingsButton.gameObject, "icon") != null,
                    "The drawer buttons lost their sidebar icons");
                var logoutRect = (RectTransform)logoutButton.transform;
                Require(Mathf.Abs(logoutRect.rect.width - HangarMenuPanel.Width) < 0.5f &&
                        Mathf.Abs(logoutRect.rect.height - HangarMenuPanel.ButtonHeight) < 0.5f,
                    "btn_logout is not the source size, got " + logoutRect.rect.size);

                RequireSettingsScreen(screen, settingsButton);

                // OS.is_debug_build(): the account toolbox ships in a debug
                // build only, and the editor always counts as one.
                Require(HangarAccountToolbox.Enabled && drawer.Toolbox != null,
                    "The debug account toolbox is missing from the drawer");
                Require(drawer.Toolbox.CreditsField != null &&
                        drawer.Toolbox.GoldField != null &&
                        drawer.Toolbox.FreeXpField != null &&
                        drawer.Toolbox.PremHoursField != null,
                    "The account toolbox lost one of its TextEdit rows");
                var toolboxButtons =
                    drawer.Toolbox.Root.GetComponentsInChildren<Button>(true);
                Require(toolboxButtons.Length == 8,
                    "The account toolbox should carry 8 buttons, got " +
                    toolboxButtons.Length);

                menuButton.onClick.Invoke();
                drawer.Advance(HangarMenuPanel.SlideSeconds);
                Require(!drawer.IsOpen &&
                        Mathf.Abs(drawer.PositionX - HangarMenuPanel.ClosedX) < 0.01f,
                    "btn_menu did not slide the drawer back out of view");

                var newsButton = FindByName<Button>(screen.gameObject, "btn_notifs");
                var techTreeButton = FindByName<Button>(screen.gameObject, "btn_techTree");
                Require(newsButton != null && techTreeButton != null,
                    "News and tech tree buttons are missing");
                var treeNations = new[]
                {
                    Nation.Ussr, Nation.Germany, Nation.Usa, Nation.Gb,
                    Nation.Japan, Nation.France, Nation.China, Nation.European,
                };
                // Researchable cards plus the premiums the client sells for
                // gold from their own slot inside the tree.
                var treeCounts = new[] { 50, 67, 57, 42, 13, 33, 10, 15 };
                for (var nationIndex = 0; nationIndex < treeNations.Length; nationIndex++)
                {
                    screen.TechTree.Update(treeNations[nationIndex]);
                    Require(screen.TechTree.CardCount == treeCounts[nationIndex],
                        "Tech tree lost cards for " + treeNations[nationIndex]);
                    Require(screen.TechTree.SlotCount >= screen.TechTree.CardCount,
                        "Tech tree overlaps branches for " + treeNations[nationIndex]);
                }
                // The modern left rail lists the tech tree first.
                Require(screen.IsModern || newsButton.transform.position.y > techTreeButton.transform.position.y,
                    "btn_notifs must sit above btn_techTree, as in the source screen");
                for (var i = 0; i < texts.Length; i++)
                {
                    Require(texts[i].font != null,
                        "Hangar text has no source font: " + texts[i].name);
                }

                var crew = FindByName<Button>(screen.gameObject, "btn_crew");
                var placeholder = FindByName<RectTransform>(
                    screen.gameObject, "PlaceholderPanel");
                Require(crew != null && placeholder != null,
                    "Hangar placeholder controls are missing");
                crew.onClick.Invoke();
                Require(placeholder.gameObject.activeSelf,
                    "Placeholder panel did not open from a hangar button");

                var armorButton = FindByName<Button>(screen.gameObject, "btn_armor_viewer");
                Require(armorButton != null, "btn_armor_viewer is missing");
                armorButton.onClick.Invoke();
                Require(screen.CurrentScreen == HangarScreen.HangarScreenId.ArmorViewer &&
                        screen.ArmorViewer != null && screen.ArmorViewer.Root.gameObject.activeSelf,
                    "Armor viewer did not open from the hangar button");
                var armorViewer = screen.ArmorViewer.Viewer;
                Require(armorViewer != null && armorViewer.PlateCount > 0,
                    "Armor viewer did not build the selected tank plates");
                armorViewer.SetMode(HangarArmorViewer.ViewMode.Penetration);
                var armorColliders = world.TankModel.GetComponentsInChildren<MeshCollider>(true);
                Require(armorColliders.Length > 0,
                    "Armor viewer has no clickable plate colliders");
                var hitPoint = world.HangarCamera.WorldToScreenPoint(
                    armorColliders[0].bounds.center);
                HangarArmorViewer.HitInfo armorHit;
                Require(armorViewer.TryShoot(hitPoint, out armorHit),
                    "Armor viewer penetration test did not hit a plate");
                Require(armorHit.NominalArmor > 0 && armorHit.EffectiveArmor >= 0f,
                    "Armor viewer returned invalid penetration numbers");
                screen.ArmorViewer.Close();
                Require(screen.CurrentScreen == HangarScreen.HangarScreenId.Main,
                    "Armor viewer did not return to the hangar");

                var shopButton = FindByName<Button>(screen.gameObject, "btn_shop");
                Require(shopButton != null && screen.Shop != null, "Shop button or screen is missing");
                bootstrap.NetworkClock.SetSynced(1750032000d);
                shopButton.onClick.Invoke();
                Require(screen.CurrentScreen == HangarScreen.HangarScreenId.Shop &&
                        screen.Shop.Root.gameObject.activeSelf, "Shop did not open from its button");
                Require(screen.Shop.Availability == ShopAvailability.Online &&
                        screen.Shop.CardCount(ShopScreen.TabPremium) == 8 &&
                        screen.Shop.CardCount(ShopScreen.TabContainers) == 3,
                    "Shop lost premium or container cards");
                Require(screen.Shop.CardCount(ShopScreen.TabTanks) > 5, "Weekly shop has no tank rotation");
                Require(!shopButton.gameObject.activeInHierarchy, "Shop shortcut remains over its own screen");
                Require(!screen.IsModern || screen.CarouselState == CarouselMode.Hidden,
                    "Modern carousel overlaps the shop");
                var shopGold = _smokeAccount.Gold;
                screen.Shop.PressBuy(ShopScreen.TabPremium, 0);
                Require(screen.Shop.ActivePopup != null && _smokeAccount.Gold == shopGold,
                    "Shop failed to confirm before charging");
                screen.Shop.ActivePopup.Close();
                screen.Shop.ShowTab(ShopScreen.TabContainers);
                bootstrap.NetworkClock.SetOffline();
                screen.Shop.Refresh(true);
                Require(screen.Shop.Availability == ShopAvailability.Offline && screen.Shop.RetryVisible,
                    "Shop does not block purchases offline");
                screen.Shop.HomePressed();
                Require(screen.CurrentScreen == HangarScreen.HangarScreenId.Main &&
                        !screen.Shop.Root.gameObject.activeSelf && shopButton.gameObject.activeInHierarchy,
                    "Closing shop did not restore the hangar HUD");
                bootstrap.NetworkClock.SetSynced(1750032000d);

                Require(EventSystem.current != null &&
                        EventSystem.current.GetComponent<InputSystemUIInputModule>() != null,
                    "Input System EventSystem is missing");

                var listeners = Resources.FindObjectsOfTypeAll<AudioListener>();
                var enabledListeners = 0;
                for (var i = 0; i < listeners.Length; i++)
                {
                    if (listeners[i] != null && listeners[i].enabled &&
                        listeners[i].gameObject.scene.IsValid())
                    {
                        enabledListeners++;
                    }
                }
                Require(enabledListeners == 1,
                    "Expected exactly one persistent AudioListener");

                var music = 0;
                var audioSources = bootstrap.Audio.GetComponentsInChildren<AudioSource>(true);
                for (var i = 0; i < audioSources.Length; i++)
                {
                    if (audioSources[i].loop)
                    {
                        music++;
                    }
                }
                Require(music == 0, "Login music is still looping in the hangar");

                // screens/main/filter_handle/filter/tank_equipment: the strip
                // has to mirror the save. Setting HE to zero used to leave the
                // old number on screen, and a bought consumable drew nothing
                // because the slot only looked its id up in equipment_list.
                TankSaveData stripSave = null;
                for (var i = 0; i < _smokeAccount.Tanks.Count; i++)
                {
                    if (_smokeAccount.Tanks[i].Name == _smokeAccount.SelectedTankName)
                    {
                        stripSave = _smokeAccount.Tanks[i];
                    }
                }
                Require(stripSave != null, "The selected tank has no save");
                stripSave.ShellsAmounts[0] = 1234;
                stripSave.Consumables.Clear();
                stripSave.Consumables.Add("repair_kit");
                // Re-selecting the current card runs update_tank_equipment.
                PressCarouselCard(screen, "tank_icon_t62a");

                var stripRoot = FindByName<RectTransform>(
                    screen.gameObject, "tank_equipment");
                Require(stripRoot != null, "tank_equipment strip is missing");
                var firstShell = FindByName<BattleShellSelector.ShellIcon>(
                    stripRoot.gameObject, "shell1");
                Require(firstShell != null,
                    "The hangar strip is missing its shell icons");
                Require(firstShell.AmountLabel.text ==
                        HangarUiKit.FormatNumber(1234),
                    "The strip counter does not print Util.format_number, got " +
                    firstShell.AmountLabel.text);
                Require(firstShell.BackgroundImage.sprite != null,
                    "The hangar shell slots lost the BulletButton frame");
                Require(firstShell.IconImage.sprite != null &&
                        Mathf.Approximately(
                            firstShell.IconImage.rectTransform.sizeDelta.x,
                            firstShell.IconImage.sprite.rect.width),
                    "The strip shell artwork is not drawn at its authored size");

                var consumableSlot = FindByName<RectTransform>(
                    stripRoot.gameObject, "cons_icon1");
                Require(consumableSlot != null, "cons_icon1 is missing");
                var consumableIcon =
                    FindByName<Image>(consumableSlot.gameObject, "icon");
                Require(consumableIcon != null && consumableIcon.enabled &&
                        consumableIcon.sprite != null,
                    "An installed consumable draws no artwork in the strip");
                Require(consumableSlot.GetComponent<Image>() != null &&
                        consumableSlot.GetComponent<Image>().sprite != null,
                    "The filled consumable slot lost its frame");

                // Zeroing a rack in the save has to reach the panel too.
                stripSave.ShellsAmounts[0] = 0;
                PressCarouselCard(screen, "tank_icon_t62a");
                Require(firstShell.AmountLabel.text == "0",
                    "Emptying a rack did not update the strip counter, got " +
                    firstShell.AmountLabel.text);

                RequireCarouselFilter(screen, carouselLevels, menuButton, armorButton);
                var rebuilt = RequireFilterOutlivesHangar(bootstrap, screen, carouselLevels);

                var summary =
                    "[Blitz] hangar smoke ok: bakedInstances=" +
                    world.BakedInstanceCount +
                    " hangarRenderers=" + hangarRenderers.Length +
                    " tankRenderers=" + tankRenderers.Length +
                    " buttons=" + buttons.Length +
                    " sprites=" + sprites +
                    " lights=" + world.SourceLightCount +
                    " smoke=" + world.SmokeEmitterCount;
                RequireMusicPanel(bootstrap, rebuilt, music => RequireEventHangar(rebuilt, summary + " music=" + music));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Finish(1);
            }
        }

        /// <summary>
        /// Most carousel cards ever built at once: the ones over the view and
        /// a card past either edge. The whole roster is 521 cards.
        /// </summary>
        private const int MaxCarouselCardsBuilt = 24;

        /// <summary>
        /// Scrolls the carousel on card by card to the one that draws
        /// <paramref name="iconName"/> and returns it, or null. Only the cards
        /// over the view exist, so every card has to be brought on to be read.
        /// </summary>
        private static RectTransform FindCarouselCard(HangarScreen screen, string iconName)
        {
            for (var i = 0; i < screen.CarouselCardCount; i++)
            {
                var card = screen.ShowCarouselCard(i);
                var icon = card.Find("icon");
                var image = icon == null ? null : icon.GetComponent<Image>();
                if (image != null && image.gameObject.activeSelf && image.sprite != null &&
                    image.sprite.name == iconName)
                {
                    return card;
                }
            }

            return null;
        }

        /// <summary>Clicks the carousel card that draws <paramref name="iconName"/>, as a player picks it.</summary>
        private static void PressCarouselCard(HangarScreen screen, string iconName)
        {
            var card = FindCarouselCard(screen, iconName);
            Require(card != null, "The complete carousel has no " + iconName + " card");
            var button = card.Find("button");
            Require(button != null && button.GetComponent<Button>() != null,
                "The " + iconName + " carousel card has no button");
            button.GetComponent<Button>().onClick.Invoke();
        }

        /// <summary>
        /// The hangar is built anew after every battle and every style switch.
        /// The filter belongs to the session, so the new hangar has to open
        /// with it: the carousel narrowed and the toggle outlined.
        /// </summary>
        private static HangarScreen RequireFilterOutlivesHangar(
            BlitzBootstrap bootstrap, HangarScreen screen, List<int> carouselLevels)
        {
            var tierSeven = 0;
            for (var i = 0; i < carouselLevels.Count; i++)
            {
                if (carouselLevels[i] == 7) { tierSeven++; }
            }

            Require(screen.Filter == bootstrap.CarouselFilter,
                "The hangar keeps a carousel filter of its own instead of the session's");
            screen.Filter.ToggleLevel(7);
            // BlitzBootstrap's way back from a battle: the old hangar goes,
            // a new one is built for the same account.
            screen.Discard();
            var rebuilt = HangarScreen.Create(bootstrap, _smokeAccount);
            Require(rebuilt.Filter.HasLevel(7) && rebuilt.CarouselCardCount == tierSeven,
                "The rebuilt hangar dropped the filter: " + rebuilt.CarouselCardCount +
                " cards, expected " + tierSeven);
            if (rebuilt.IsModern)
            {
                var panel = rebuilt.CarouselBar.parent.Find("filter");
                var toggle = panel == null ? null : FindByName<Button>(panel.gameObject, "lvl7");
                var outline = toggle == null ? null : toggle.transform.Find("selected");
                Require(outline != null && outline.gameObject.activeSelf,
                    "The rebuilt Modern filter does not outline lvl7");
            }
            else
            {
                Require(rebuilt.FilterDrawer.IsSelected("lvl7"),
                    "The rebuilt classic filter does not outline lvl7");
            }

            bootstrap.CarouselFilter.Clear();
            return rebuilt;
        }

        private static Mesh GetMesh(MeshRenderer renderer)
        {
            var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>The two objects HangarWorld shades with MS1Tracks.</summary>
        private static bool IsTrackPart(string name)
        {
            return name != null &&
                (name.StartsWith("trackL", StringComparison.Ordinal) ||
                 name.StartsWith("trackR", StringComparison.Ordinal) || name == "tracks_crash");
        }

        private static long TriangleCount(Mesh mesh, int subMesh)
        {
            return mesh != null ? mesh.GetIndexCount(subMesh) / 3L : 0L;
        }

        /// <summary>
        /// The carousel filter in either style. Classic: btn_filter lifts the
        /// footer band by show_filter's 264 px, a tier toggle narrows the
        /// carousel, btn_menu and a sub-screen shut it, reset_filter brings
        /// every card back. Modern: the pop-up fades in, the chrome fades out
        /// around the tank panel and the carousel, and both come back.
        /// </summary>
        private static void RequireCarouselFilter(
            HangarScreen screen, List<int> carouselLevels, Button menuButton, Button armorButton)
        {
            var tierSeven = 0;
            for (var i = 0; i < carouselLevels.Count; i++)
            {
                if (carouselLevels[i] == 7) { tierSeven++; }
            }

            Require(tierSeven > 0, "The complete carousel has no tier VII card");
            var allCards = screen.CarouselCardCount;
            var filterButton = FindByName<Button>(screen.gameObject, "btn_filter");
            Require(filterButton != null, "btn_filter is missing");
            Require(!screen.IsFilterOpen && !screen.Filter.IsActive,
                "The hangar opened with the filter up or set");

            if (screen.IsModern)
            {
                RequireModernFilter(screen, filterButton, armorButton, allCards, tierSeven);
                return;
            }

            var drawer = screen.FilterDrawer;
            Require(drawer != null && drawer.ToggleCount == 27 &&
                    drawer.FindToggle("filter_regulars") != null && drawer.FindToggle("filter_mods") != null &&
                    drawer.FindToggle("nation9") != null,
                "The classic filter does not carry its 27 toggles: 10 tiers, 4 statuses, 4 classes, 9 nations");
            Require(Mathf.Abs(drawer.PositionY - HangarFilterDrawer.ClosedY) < 0.01f && !drawer.IsVisible,
                "The filter band does not start on the screen edge");
            Require(filterButton.transform.IsChildOf(drawer.Band) &&
                    screen.CarouselRoot.IsChildOf(drawer.Band) &&
                    FindByName<RectTransform>(screen.gameObject, "tank_equipment").IsChildOf(drawer.Band),
                "btn_filter, the carousel and the loadout strip must ride on the filter band");

            var caption = FindByName<Text>(filterButton.gameObject, "filter_onoff");
            var icon = FindByName<Image>(filterButton.gameObject, "filter_icon");
            Require(caption != null && icon != null && icon.sprite != null &&
                    icon.sprite.name == "hangar_filter_icon",
                "btn_filter lost its caption or its icon");
            var sceneCaption = caption.text;

            filterButton.onClick.Invoke();
            Require(screen.IsFilterOpen && GodotButtonState.Of(filterButton).Latched,
                "btn_filter did not open the filter");
            // tr("ON") if !on else tr("OFF"): the scene's OFF stays up.
            Require(caption.text == sceneCaption && icon.sprite.name == "hangar_filter_icon_on",
                "btn_filter does not draw the open filter like the source");
            drawer.Advance(HangarFilterDrawer.SlideSeconds * 0.5f);
            Require(drawer.PositionY < HangarFilterDrawer.ClosedY && drawer.PositionY > HangarFilterDrawer.OpenY,
                "The filter jumped up instead of sliding");
            drawer.Advance(HangarFilterDrawer.SlideSeconds);
            Require(Mathf.Abs(drawer.PositionY - HangarFilterDrawer.OpenY) < 0.01f,
                "show_filter did not lift the band to y = -399");

            var tierToggle = drawer.FindToggle("lvl7");
            tierToggle.onClick.Invoke();
            Require(screen.Filter.HasLevel(7) && drawer.IsSelected("lvl7"),
                "lvl7 did not latch with its outline");
            Require(screen.CarouselCardCount == tierSeven,
                "The tier VII filter left " + screen.CarouselCardCount + " cards, expected " + tierSeven);

            // btn_menu unpresses btn_filter first.
            menuButton.onClick.Invoke();
            Require(!screen.IsFilterOpen && !GodotButtonState.Of(filterButton).Latched &&
                    caption.text != sceneCaption && icon.sprite.name == "hangar_filter_icon",
                "btn_menu left the filter up");
            menuButton.onClick.Invoke();
            Require(!screen.IsMenuOpen, "btn_menu did not close the drawer again");
            drawer.Advance(HangarFilterDrawer.SlideSeconds);
            Require(Mathf.Abs(drawer.PositionY - HangarFilterDrawer.ClosedY) < 0.01f,
                "show_filter did not play backwards");
            Require(screen.CarouselCardCount == tierSeven,
                "Shutting the filter dropped the filter itself");

            filterButton.onClick.Invoke();
            drawer.Advance(HangarFilterDrawer.SlideSeconds);
            drawer.ResetButton.onClick.Invoke();
            Require(!screen.Filter.IsActive && !drawer.IsSelected("lvl7") &&
                    screen.CarouselCardCount == allCards,
                "reset_filter did not bring every card back");

            // Leaving screens/main shuts the filter at once.
            armorButton.onClick.Invoke();
            Require(!screen.IsFilterOpen && !drawer.IsVisible &&
                    !GodotButtonState.Of(filterButton).Latched,
                "A sub-screen left the filter up behind it");
            screen.ArmorViewer.Close();
            Require(screen.CurrentScreen == HangarScreen.HangarScreenId.Main && !screen.IsFilterOpen,
                "The hangar came back with the filter up");
        }

        private static void RequireModernFilter(
            HangarScreen screen, Button filterButton, Button armorButton, int allCards, int tierSeven)
        {
            var chrome = screen.ChromeFade;
            var panelFade = screen.FilterPanelFade;
            var panel = screen.CarouselBar.parent.Find("filter");
            Require(chrome != null && panelFade != null && panel != null,
                "The Modern filter or its fades are missing");
            Require(!panel.gameObject.activeSelf && chrome.IsShown && chrome.Alpha == 1f,
                "The Modern filter does not start shut");

            var tankPanel = FindByName<RectTransform>(screen.gameObject, "tank_panel");
            var rail = FindByName<Button>(screen.gameObject, "btn_techTree");
            var battle = FindByName<Button>(screen.gameObject, "btn_battle");
            var currencies = FindByName<RectTransform>(screen.gameObject, "currencies");
            var strip = FindByName<RectTransform>(screen.gameObject, "tank_equipment");
            Require(tankPanel != null && !chrome.Contains(tankPanel),
                "The tank panel must stay up under the filter");
            Require(chrome.Contains(rail) && chrome.Contains(battle) &&
                    chrome.Contains(currencies) && chrome.Contains(strip) &&
                    chrome.Contains(BurgerButton(screen)),
                "The open filter leaves part of the hangar chrome up");

            filterButton.onClick.Invoke();
            Require(screen.IsFilterOpen && panel.gameObject.activeSelf &&
                    GodotButtonState.Of(filterButton).Latched,
                "btn_filter did not open the Modern filter");
            Require(!chrome.IsShown && chrome.IsFading && !battle.GetComponent<CanvasGroup>().blocksRaycasts,
                "The hangar chrome does not fade out under the filter");
            chrome.Advance(chrome.Seconds * 0.5f);
            Require(chrome.Alpha > 0f && chrome.Alpha < 1f, "The chrome vanished instead of fading");
            chrome.Advance(chrome.Seconds);
            panelFade.Advance(panelFade.Seconds);
            Require(chrome.Alpha == 0f && panelFade.Alpha == 1f,
                "The filter fades did not finish");
            var tankGroup = tankPanel.GetComponent<CanvasGroup>();
            Require((tankGroup == null || tankGroup.alpha == 1f) && screen.CarouselBarDrawn,
                "The tank panel or the carousel faded with the chrome");

            var tierToggle = FindByName<Button>(panel.gameObject, "lvl7");
            var reset = FindByName<Button>(panel.gameObject, "reset_filter");
            Require(tierToggle != null && reset != null &&
                    FindByName<Button>(panel.gameObject, "filter_regulars") != null &&
                    FindByName<Button>(panel.gameObject, "filter_collectors") != null &&
                    FindByName<Button>(panel.gameObject, "filter_mods") != null &&
                    FindByName<Button>(panel.gameObject, "td") != null &&
                    FindByName<Button>(panel.gameObject, "nation9") != null,
                "The Modern filter lost a toggle");
            tierToggle.onClick.Invoke();
            var outline = tierToggle.transform.Find("selected");
            Require(screen.Filter.HasLevel(7) && outline != null && outline.gameObject.activeSelf,
                "lvl7 did not latch with its outline");
            Require(screen.CarouselCardCount == tierSeven,
                "The tier VII filter left " + screen.CarouselCardCount + " cards, expected " + tierSeven);
            reset.onClick.Invoke();
            Require(!screen.Filter.IsActive && !outline.gameObject.activeSelf &&
                    screen.CarouselCardCount == allCards,
                "reset_filter did not bring every card back");

            filterButton.onClick.Invoke();
            Require(!screen.IsFilterOpen && chrome.IsShown && battle.GetComponent<CanvasGroup>().blocksRaycasts,
                "Shutting the filter did not bring the chrome back");
            chrome.Advance(chrome.Seconds);
            panelFade.Advance(panelFade.Seconds);
            Require(chrome.Alpha == 1f && !panel.gameObject.activeSelf,
                "The filter fades did not finish going back");

            // A sub-screen shuts the filter and never keeps the chrome hidden.
            filterButton.onClick.Invoke();
            armorButton.onClick.Invoke();
            Require(!screen.IsFilterOpen && chrome.IsShown && !chrome.IsFading,
                "A sub-screen left the filter up or the chrome hidden");
            screen.ArmorViewer.Close();
            Require(screen.CurrentScreen == HangarScreen.HangarScreenId.Main && !screen.IsFilterOpen &&
                    chrome.Alpha == 1f,
                "The hangar came back with the filter up");
        }

        private static Button BurgerButton(HangarScreen screen)
        {
            foreach (var candidate in screen.GetComponentsInChildren<Button>(true))
            {
                if (candidate.name != "btn_menu") { continue; }
                var icon = candidate.transform.Find("icon");
                var image = icon == null ? null : icon.GetComponent<Image>();
                if (image != null && image.sprite != null && image.sprite.name == "hangar_menu")
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void RequireMaterial(Material material, string expectedName)
        {
            Require(material != null, expectedName + " material is missing");
            Require(string.Equals(material.name, expectedName, StringComparison.Ordinal),
                "Unexpected runtime material: " + material.name);
            Require(material.mainTexture != null,
                expectedName + " does not reference a source texture");
        }

        private static T FindSceneObject<T>() where T : Component
        {
            var matches = Resources.FindObjectsOfTypeAll<T>();
            for (var i = 0; i < matches.Length; i++)
            {
                if (matches[i] != null && matches[i].gameObject.scene.IsValid())
                {
                    return matches[i];
                }
            }
            return null;
        }

        private static T FindByName<T>(GameObject root, string name) where T : Component
        {
            var matches = root.GetComponentsInChildren<T>(true);
            for (var i = 0; i < matches.Length; i++)
            {
                if (string.Equals(matches[i].gameObject.name, name, StringComparison.Ordinal))
                {
                    return matches[i];
                }
            }
            return null;
        }

        /// <summary>
        /// btn_settings of the hangar drawer opens the ported ui_settings.tscn
        /// over the hangar, and its btn_menu hands the hangar back.
        /// </summary>
        private static void RequireSettingsScreen(HangarScreen screen, Button open)
        {
            open.onClick.Invoke();

            var settings = screen.SettingsUi;
            Require(settings != null, "btn_settings did not open the settings screen");
            var sourceRows = BlitzLegacy.Core.Serialization.JsonNode.Parse(
                BlitzResources.LoadText(BlitzResources.SettingsManifestData))["screen"]["rows"];
            Require(settings.Rows.Count == sourceRows.Count,
                "The settings screen must expose every current source row: " + settings.Rows.Count);
            var expectedSliders = 0; var expectedCheckboxes = 0; var expectedOptions = 0;
            foreach (var sourceRow in sourceRows.Items)
            {
                if (sourceRow["kind"].AsString() == "slider") { expectedSliders++; }
                // The music player row of the hangar is a checkbox as well.
                if (sourceRow["kind"].AsString() == "boolean" ||
                    sourceRow["kind"].AsString() == "hangar_music") { expectedCheckboxes++; }
                expectedOptions += sourceRow["options"].Count;
            }
            Require(settings.TabButtons.Count == 5,
                "The settings screen should show MAIN, GRAPHICS, MATCHMAKER, GAMEPLAY "
                + "and, off a phone, CONTROLS");
            Require(settings.ControlRows.Count == 20,
                "The controls tab should list every rebindable action, got "
                + settings.ControlRows.Count);

            var sliders = 0;
            var checkboxes = 0;
            var options = 0;
            for (var i = 0; i < settings.Rows.Count; i++)
            {
                var row = settings.Rows[i];
                Require(row.Label != null && row.Label.text.Length > 0,
                    "A settings row has no caption: " + row.Source.Setting);
                if (row.Slider != null) { sliders++; }
                if (row.Checkbox != null) { checkboxes++; }
                options += row.Options.Count;
            }

            Require(sliders == expectedSliders, "Missing source slider rows: " + sliders);
            Require(checkboxes == expectedCheckboxes, "Missing source boolean rows: " + checkboxes);
            Require(options == expectedOptions, "Missing source option buttons: " + options);

            var picked = RequireHangarPicker(screen, settings);

            settings.SelectTab(1);
            Require(settings.CurrentTab == 1, "The tab strip did not switch pages");

            settings.Close();
            Require(screen.SettingsUi == null,
                "btn_menu did not close the settings screen");
            Debug.Log("[Blitz] settings screen ok: rows=" + settings.Rows.Count
                + " sliders=" + sliders + " booleans=" + checkboxes
                + " options=" + options + " hangars=" + picked);
        }

        /// <summary>
        /// The hangar row names the hangar behind the screen; its list greys the
        /// premium hangar without premium, and a pick puts the hangar up at once.
        /// The smoke walks every hangar the account may use and then hands the
        /// choice back to the schedule for the event walk that follows.
        /// </summary>
        private static string RequireHangarPicker(HangarScreen screen, SettingsScreen settings)
        {
            SettingsScreen.Row row = null;
            for (var i = 0; i < settings.Rows.Count; i++)
            {
                if (settings.Rows[i].Source.Setting == "hangar") { row = settings.Rows[i]; }
            }

            Require(row != null && row.Picker != null, "The settings screen has no hangar row");
            var world = screen.World;
            var start = world.Kind;
            var bootstrap = FindSceneObject<BlitzBootstrap>();
            var lang = LocalizationTable.Parse(BlitzResources.LoadText(BlitzResources.LanguageData));
            lang.Language = bootstrap.Settings.Language;
            Require(settings.CurrentHangar == start,
                "The hangar row judges " + settings.CurrentHangar + " while " + start + " is up");
            var picked = new List<string>();
            foreach (var info in HangarCatalog.All)
            {
                row.Picker.onClick.Invoke();
                var picker = settings.HangarPicker;
                Require(picker != null, "The hangar button did not open the list");
                Require(picker.Entries.Count == HangarCatalog.All.Count,
                    "The hangar list shows " + picker.Entries.Count + " entries");
                HangarPickerPopup.Entry entry = null;
                for (var i = 0; i < picker.Entries.Count; i++)
                {
                    if (picker.Entries[i].Info == info) { entry = picker.Entries[i]; }
                }

                var available = !info.RequiresPremium || HasPremium();
                Require(entry != null && entry.Available == available && entry.Button.interactable == available,
                    info.Id + " is offered as available=" + (entry != null && entry.Available)
                    + " with premium=" + HasPremium());
                if (!available)
                {
                    picker.Cancel.onClick.Invoke();
                    Require(settings.HangarPicker == null, "Cancel did not close the hangar list");
                    continue;
                }

                entry.Button.onClick.Invoke();
                Require(settings.HangarPicker == null, "Picking " + info.Id + " did not close the list");
                Require(world.Kind == info.Kind,
                    "Picking " + info.Id + " left " + world.Kind + " up behind the settings");
                Require(info.Ported == (world.ClientScene == null),
                    info.Id + " came up on the wrong scene runtime");
                Require(row.Value.text == lang.Get(info.NameKey),
                    "The hangar row reads '" + row.Value.text + "' after picking " + info.Id);
                picked.Add(info.Id);
            }

            settings.SelectTab(0);
            bootstrap.Settings.SetString("hangar", string.Empty);
            screen.RefreshHangarKind();
            Require(world.Kind == start, "Clearing the choice did not bring " + start + " back");
            return string.Join(",", picked.ToArray());
        }

        /// <summary>
        /// The Halloween 2026 hangar replaces both 4.8 hangars for every
        /// account from 29 October to 13 November, local days inclusive, and
        /// hands the stand back to the premium rule on 14 November. The open
        /// hangar polls the schedule once a second, so the smoke walks the
        /// pinned clock across both ends of the window, with and without
        /// premium, and waits for the stand to follow without a rebuild.
        /// </summary>
        private static void RequireEventHangar(HangarScreen screen, string summary)
        {
            var world = screen.World;
            var tank = world.TankModel;
            var camera = world.HangarCamera;
            Require(world.Kind == (HasPremium() ? HangarKind.Premium : HangarKind.Regular),
                "Outside the event the hangar has to follow the premium state, got " + world.Kind);
            var startKind = world.Kind;
            var before = RenderState.Capture();
            var depthOption = camera.GetUniversalAdditionalCameraData().requiresDepthOption;
            var firstSecond = LocalUnixSeconds(HangarSchedule.Halloween2026FirstDay);
            var afterLastDay = LocalUnixSeconds(HangarSchedule.Halloween2026LastDay.AddDays(1));
            var premiumDuringEvent = false;
            var holdUntil = 0d;
            Func<bool> held = () => EditorApplication.timeSinceStartup >= holdUntil;
            Action hold = () => holdUntil = EditorApplication.timeSinceStartup + 1.5d;

            _smokeClock.NowUnixSeconds = firstSecond;
            Sequence(
                (() => world.Kind == HangarKind.Halloween2026, 60d,
                    "The open hangar did not switch to Halloween 2026 at local midnight of 29 October",
                    () =>
                    {
                        RequireHalloweenStand(world, tank, camera);
                        // Everyone gets the event hangar: flip the premium
                        // state and the stand must not move.
                        premiumDuringEvent = HasPremium();
                        _smokeAccount.PremAccountExpiration = premiumDuringEvent ? 0d : afterLastDay + 86400d;
                        hold();
                    }),
                (held, 10d, string.Empty,
                    () =>
                    {
                        Require(world.Kind == HangarKind.Halloween2026 && world.PortedScene != null,
                            "Changing the premium state took the Halloween hangar away");
                        _smokeAccount.PremAccountExpiration = 0d;
                        _smokeClock.NowUnixSeconds = afterLastDay - 1d;
                        hold();
                    }),
                (held, 10d, string.Empty,
                    () =>
                    {
                        Require(world.Kind == HangarKind.Halloween2026,
                            "The Halloween hangar went before 13 November was over");
                        _smokeClock.NowUnixSeconds = afterLastDay;
                    }),
                (() => world.Kind == HangarKind.Regular, 30d,
                    "The ordinary hangar did not come back at local midnight of 14 November",
                    () =>
                    {
                        RequireClientStand(world, tank, camera, "hangar", depthOption);
                        _smokeAccount.PremAccountExpiration = afterLastDay + 86400d;
                    }),
                (() => world.Kind == HangarKind.Premium, 30d,
                    "Premium did not bring the premium hangar back after the event",
                    () =>
                    {
                        RequireClientStand(world, tank, camera, "hangar_prem_2018", depthOption);
                        if (startKind == HangarKind.Premium)
                            before.Require("after the Halloween hangar", world);
                        Require(UnityEngine.Object.FindObjectsByType<HalloweenHangarScene>(
                                FindObjectsInactive.Include).Length == 0,
                            "The Halloween hangar outlived its switch");
                        RequireModernEventHangar(screen, summary + " event=" + HangarKind.Halloween2026 +
                            " premiumDuringEvent=" + premiumDuringEvent);
                    }));
        }

        /// <summary>
        /// The Modern menus draw no premium label, and the stand still has to
        /// follow the schedule poll there: the hangar is rebuilt in the Modern
        /// style and the clock walks it into the event and out again.
        /// </summary>
        private static void RequireModernEventHangar(HangarScreen screen, string summary)
        {
            var bootstrap = FindSceneObject<BlitzBootstrap>();
            MenuStyle.ToolOverride = SettingsData.UiStyleModern;
            screen.Discard();
            var modern = HangarScreen.Create(bootstrap, _smokeAccount);
            Require(modern.IsModern, "The style override did not build the Modern hangar");
            var outside = HasPremium() ? HangarKind.Premium : HangarKind.Regular;
            Require(modern.World.Kind == outside,
                "The Modern hangar opened on " + modern.World.Kind + " outside the event");
            RequireModernMusicPanel(bootstrap, modern);
            var afterEvent = _smokeClock.NowUnixSeconds;
            var holdUntil = 0d;
            Func<bool> held = () => EditorApplication.timeSinceStartup >= holdUntil;
            Action hold = () => holdUntil = EditorApplication.timeSinceStartup + 1.5d;
            _smokeClock.NowUnixSeconds = LocalUnixSeconds(HangarSchedule.Halloween2026FirstDay.AddDays(3).AddHours(12));
            Sequence(
                (() => modern.World.Kind == HangarKind.Halloween2026, 30d,
                    "The Modern hangar did not switch to Halloween 2026 inside the event",
                    () =>
                    {
                        Require(modern.World.PortedScene != null && modern.World.ClientScene == null,
                            "The Modern hangar switched without the Halloween scene");
                        // A style switch builds the hangar again on the
                        // event stand; the old one must not take the
                        // lighting and the fog with it.
                        modern.Discard();
                        modern = HangarScreen.Create(bootstrap, _smokeAccount);
                        hold();
                    }),
                (held, 10d, string.Empty,
                    () =>
                    {
                        var world = modern.World;
                        Require(world.Kind == HangarKind.Halloween2026,
                            "The rebuilt hangar opened on " + world.Kind + " inside the event");
                        RequireHalloweenStand(world, world.TankModel, world.HangarCamera);
                        Require(Shader.GetGlobalVector(SourceEnvironment.FogRangeId).y > 0f,
                            "The rebuilt Halloween hangar lost its fog range");
                        _smokeClock.NowUnixSeconds = afterEvent;
                    }),
                (() => modern.World.Kind == outside, 30d,
                    "The Modern hangar kept the Halloween hangar after the event",
                    () =>
                    {
                        Require(modern.World.PortedScene == null,
                            "The Modern hangar kept the Halloween scene after the event");
                        MenuStyle.ToolOverride = null;
                        Debug.Log(summary + " modernEvent=ok");
                        RequireLogout(bootstrap, modern);
                        Debug.Log("[Blitz] hangar smoke logout=ok");
                        Finish(0);
                    }));
        }

        /// <summary>
        /// The hangar music player of the main view. The 4.8 hangars ship it
        /// switched off, so the smoke switches it on for the hangar on screen
        /// and walks the panel: up in the main view only and clear of every
        /// other control, down under the classic filter band, the armor viewer
        /// and the settings while the music goes on, folded to its header, and
        /// stepping and pausing the playlist with its own buttons. The switch
        /// and the player's state go back as they were for the walks that
        /// follow, and <paramref name="then"/> gets the summary.
        /// </summary>
        private static void RequireMusicPanel(BlitzBootstrap bootstrap, HangarScreen screen, Action<string> then)
        {
            var settings = bootstrap.Settings;
            var kind = screen.World.Kind;
            var player = bootstrap.Music;
            var panel = screen.MusicPanel;
            Require(player != null && panel != null, "The hangar has no music player panel");
            Require(player.Owner == screen, "The music player does not follow the hangar on screen");
            Require(player.Count > 0, "The shipped hangar music playlist is empty");
            Require(!HangarCatalog.Get(kind).MusicPlayerByDefault,
                kind + " must not put the music player up out of the box");
            var lang = LocalizationTable.Parse(BlitzResources.LoadText(BlitzResources.LanguageData));
            lang.Language = settings.Language;
            var wasOn = settings.HangarMusicPlayer(kind);
            var savedState = settings.HangarMusic.ToJson();
            settings.HangarMusic.Placed = false;

            settings.SetHangarMusicPlayer(kind, false);
            panel.Refresh();
            Require(!panel.Visible && !player.Engaged && player.Status == HangarMusicPlayer.PlayerStatus.Off,
                "The music player is up in " + kind + " with its switch off");

            settings.SetHangarMusicPlayer(kind, true);
            if (player.Paused) { player.TogglePause(); }
            player.SetShuffle(false);
            player.SetRepeatOne(false);
            player.SetCollapsed(false);
            panel.Refresh();
            Require(panel.Visible && player.Engaged && player.Status != HangarMusicPlayer.PlayerStatus.Off,
                "Switched on, the music player stays off in " + kind);
            Require(Mathf.Abs(panel.Height - HangarMusicPanel.ExpandedHeight) < 0.5f,
                "The music panel opens " + panel.Height + " high");
            Require(ShowsText(panel.TitleText, player.Track.Title),
                "The music panel reads '" + panel.TitleText + "' for " + player.Track.Title);
            RequireClear(screen, panel);

            var filter = FindByName<Button>(screen.gameObject, "btn_filter");
            filter.onClick.Invoke();
            panel.Refresh();
            Require(screen.IsFilterOpen && panel.Visible == screen.IsModern && player.Engaged,
                "The open filter leaves the music panel visible=" + panel.Visible);
            filter.onClick.Invoke();
            panel.Refresh();
            Require(!screen.IsFilterOpen && panel.Visible, "The music panel did not come back with the filter shut");

            FindByName<Button>(screen.gameObject, "btn_armor_viewer").onClick.Invoke();
            panel.Refresh();
            Require(screen.CurrentScreen == HangarScreen.HangarScreenId.ArmorViewer && !panel.Visible && player.Engaged,
                "The armor viewer must hide the music panel and keep the music");
            screen.ArmorViewer.Close();
            panel.Refresh();
            Require(panel.Visible, "The music panel did not come back after the armor viewer");

            FindByName<Button>(screen.gameObject, "btn_settings").onClick.Invoke();
            panel.Refresh();
            Require(screen.SettingsUi != null && !panel.Visible && player.Engaged,
                "The settings must hide the music panel and keep the music");
            screen.SettingsUi.Close();
            panel.Refresh();
            Require(panel.Visible, "The music panel did not come back after the settings");
            RequireMusicPanelDrag(settings, player, panel);
            RequireMusicPanelViews(screen, player, panel);

            var collapse = FindByName<Button>(panel.gameObject, "CollapseButton");
            collapse.onClick.Invoke();
            Require(player.Collapsed && Mathf.Abs(panel.Height - HangarMusicPanel.HeaderHeight) < 0.5f,
                "The fold button left the music panel " + panel.Height + " high");
            Require(ShowsText(panel.TitleText, player.Track.Title), "The folded music panel does not name the track");
            collapse.onClick.Invoke();
            Require(!player.Collapsed && Mathf.Abs(panel.Height - HangarMusicPanel.ExpandedHeight) < 0.5f,
                "The fold button did not open the music panel again");

            var first = player.Track.Id;
            Sequence(
                (() => player.Status == HangarMusicPlayer.PlayerStatus.Playing ||
                       player.Status == HangarMusicPlayer.PlayerStatus.Unavailable, 60d,
                    "The music player never got past reading " + first,
                    () =>
                    {
                        // Without an audio device the track may not read; the
                        // buttons have to step the playlist either way.
                        var status = player.Status;
                        var start = player.Index;
                        FindByName<Button>(panel.gameObject, "NextButton").onClick.Invoke();
                        Require(player.Index == (start + 1) % player.Count &&
                                ShowsText(panel.TitleText, player.Track.Title),
                            "NextButton went from track " + start + " to " + player.Index);
                        FindByName<Button>(panel.gameObject, "PreviousButton").onClick.Invoke();
                        Require(player.Index == start && player.Track.Id == first,
                            "PreviousButton did not come back to " + first);
                        var play = FindByName<Button>(panel.gameObject, "PlayButton");
                        play.onClick.Invoke();
                        Require(player.Paused && player.Status == HangarMusicPlayer.PlayerStatus.Paused &&
                                panel.SubtitleText == lang.Get("ui:hangar_music/paused"),
                            "PlayButton did not pause the music");
                        play.onClick.Invoke();
                        Require(!player.Paused, "PlayButton did not resume the music");

                        var restored = HangarMusicSettings.FromJson(savedState);
                        settings.HangarMusic = restored;
                        player.SetShuffle(restored.Shuffle);
                        player.SetRepeatOne(restored.RepeatOne);
                        settings.SetHangarMusicPlayer(kind, wasOn);
                        bootstrap.Persist();
                        panel.Refresh();
                        Require(panel.Visible == wasOn, "The music panel did not follow its switch back");
                        then("tracks=" + player.Count + ",status=" + status);
                    }));
        }

        /// <summary>
        /// Modern stacks the music panel under the tank panel, clear of the
        /// other controls, and keeps it up under the filter.
        /// </summary>
        private static void RequireModernMusicPanel(BlitzBootstrap bootstrap, HangarScreen screen)
        {
            var settings = bootstrap.Settings;
            var kind = screen.World.Kind;
            var wasOn = settings.HangarMusicPlayer(kind);
            var wasPlaced = settings.HangarMusic.Placed;
            var wasCollapsed = settings.HangarMusic.Collapsed;
            var wasView = settings.HangarMusic.View;
            var panel = screen.MusicPanel;
            Require(panel != null && bootstrap.Music.Owner == screen, "The Modern hangar has no music panel of its own");
            settings.SetHangarMusicPlayer(kind, true);
            settings.HangarMusic.Placed = false;
            settings.HangarMusic.Collapsed = false;
            settings.HangarMusic.View = HangarMusicView.Player;
            panel.Refresh();
            Require(panel.Visible, "The Modern music panel is not up with its switch on");
            var tankPanel = FindByName<RectTransform>(screen.gameObject, "tank_panel");
            OnDeviceCanvases(panel, (root, size) =>
            {
                panel.Refresh();
                var area = CanvasRect(root, panel.Frame);
                var tank = CanvasRect(root, tankPanel);
                Require(area.yMax <= tank.yMin + 0.5f && Mathf.Abs(area.xMax - tank.xMax) < 0.5f,
                    "The Modern music panel is not stacked under the tank panel on the " + size + " canvas: " +
                    area + " under " + tank);
            });
            RequireClear(screen, panel);

            // The playlist rises over the tank panel to keep clear of the battle row.
            settings.HangarMusic.View = HangarMusicView.Playlist;
            RequireClear(screen, panel);
            settings.HangarMusic.View = HangarMusicView.Player;
            var filter = FindByName<Button>(screen.gameObject, "btn_filter");
            filter.onClick.Invoke();
            panel.Refresh();
            Require(screen.IsFilterOpen && panel.Visible, "The Modern filter took the music panel down");
            filter.onClick.Invoke();
            Require(!screen.IsFilterOpen, "btn_filter did not shut the Modern filter");
            settings.SetHangarMusicPlayer(kind, wasOn);
            settings.HangarMusic.Placed = wasPlaced;
            settings.HangarMusic.Collapsed = wasCollapsed;
            settings.HangarMusic.View = wasView;
            panel.Refresh();
            Require(panel.Visible == wasOn, "The Modern music panel did not follow its switch");
        }

        /// <summary>
        /// The header drags the music panel: the player saves where it was let
        /// go, however far the drag goes the panel keeps a margin inside the
        /// screen, folding keeps its top edge, a panel unfolded at the bottom
        /// rises to fit without the saved place moving, and a place a screen
        /// has no room for is shown at its edge and kept for a larger one.
        /// The panel goes back where the hangar puts it after.
        /// </summary>
        private static void RequireMusicPanelDrag(SettingsData settings, HangarMusicPlayer player, HangarMusicPanel panel)
        {
            panel.Refresh();
            var handle = panel.GetComponentInChildren<HangarMusicDragHandle>();
            Require(handle != null, "The music panel has no header to drag it by");
            Require(!player.Placed && !settings.HangarMusic.ToJson().Has("right"),
                "The music panel counts as dragged before it was");
            var room = (RectTransform)panel.transform;
            var size = room.rect.size;
            var margin = HangarMusicPlacement.Margin;
            var homeRight = panel.Right;
            var homeTop = panel.Top;

            DragMusicPanel(handle, room, new Vector2(-100f, 50f));
            Require(Near(panel.Right, homeRight + 100f) && Near(panel.Top, homeTop - 50f),
                "A drag 100 left and 50 up took the music panel from " + homeRight + "," + homeTop +
                " to " + panel.Right + "," + panel.Top);
            var saved = settings.HangarMusic.ToJson();
            Require(player.Placed && Near(player.PlacedRight, panel.Right) && Near(player.PlacedTop, panel.Top) &&
                    Near(saved["right"].AsFloat(), panel.Right) && Near(saved["top"].AsFloat(), panel.Top),
                "The player did not save where the music panel was let go");

            DragMusicPanel(handle, room, new Vector2(5000f, 5000f));
            Require(Near(panel.Right, margin) && Near(panel.Top, margin),
                "Dragged past the top right corner, the music panel stands at " + panel.Right + "," + panel.Top);
            DragMusicPanel(handle, room, new Vector2(-5000f, -5000f));
            var low = size.y - margin - HangarMusicPanel.ExpandedHeight;
            Require(Near(panel.Right, size.x - margin - HangarMusicPanel.Width) && Near(panel.Top, low),
                "Dragged past the bottom left corner, the music panel stands at " + panel.Right + "," + panel.Top);

            player.SetCollapsed(true);
            panel.Refresh();
            Require(Near(panel.Height, HangarMusicPanel.HeaderHeight) && Near(panel.Top, low),
                "Folding moved the music panel's top edge from " + low + " to " + panel.Top);
            DragMusicPanel(handle, room, new Vector2(0f, -5000f));
            var bottom = size.y - margin - HangarMusicPanel.HeaderHeight;
            Require(Near(panel.Top, bottom) && Near(player.PlacedTop, bottom),
                "The folded music panel did not go down to " + bottom + ": " + panel.Top);
            player.SetCollapsed(false);
            panel.Refresh();
            Require(Near(panel.Top, low) && Near(player.PlacedTop, bottom),
                "Unfolded at the bottom, the music panel stands at " + panel.Top + " and saved " + player.PlacedTop);
            player.SetCollapsed(true);
            panel.Refresh();
            Require(Near(panel.Top, bottom), "Folded again, the music panel did not go back down to " + bottom);
            player.SetCollapsed(false);

            const float farRight = 1000f;
            const float farTop = 600f;
            player.Place(farRight, farTop);
            OnDeviceCanvases(panel, (root, device) =>
            {
                panel.Refresh();
                var right = Mathf.Min(farRight, device.x - margin - HangarMusicPanel.Width);
                var top = Mathf.Min(farTop, device.y - margin - HangarMusicPanel.ExpandedHeight);
                Require(Near(panel.Right, right) && Near(panel.Top, top) &&
                        Near(player.PlacedRight, farRight) && Near(player.PlacedTop, farTop),
                    "On the " + device + " canvas the music panel saved at " + farRight + "," + farTop +
                    " stands at " + panel.Right + "," + panel.Top);
            });
            panel.Refresh();
            Require(Near(panel.Right, Mathf.Min(farRight, size.x - margin - HangarMusicPanel.Width)) &&
                    Near(panel.Top, Mathf.Min(farTop, low)) &&
                    Near(player.PlacedRight, farRight) && Near(player.PlacedTop, farTop),
                "Back on the " + size + " canvas the music panel saved at " + farRight + "," + farTop +
                " stands at " + panel.Right + "," + panel.Top);

            settings.HangarMusic.Placed = false;
            panel.Refresh();
            Require(Near(panel.Right, homeRight) && Near(panel.Top, homeTop),
                "The music panel did not go back to where the hangar puts it");
        }

        /// <summary>
        /// The header's view buttons: the playlist opens on the track that is
        /// on and plays the row pressed, resuming a paused track, a folded
        /// panel unfolds into the view asked for, the equalizer switches,
        /// takes a band from a press on its fader and resets, and a second
        /// press on a view's button goes back to the player. Where the hangar
        /// puts the panel, the tall views cover none of its controls either.
        /// </summary>
        private static void RequireMusicPanelViews(HangarScreen screen, HangarMusicPlayer player, HangarMusicPanel panel)
        {
            var wasOn = player.EqualizerOn;
            var gains = new int[HangarMusicEqualizer.BandCount];
            for (var band = 0; band < gains.Length; band++)
            {
                gains[band] = player.EqualizerGain(band);
            }

            player.SetView(HangarMusicView.Player);
            panel.Refresh();
            var playlist = FindByName<Button>(panel.gameObject, "PlaylistButton");
            var equalizer = FindByName<Button>(panel.gameObject, "EqualizerButton");
            Require(playlist != null && equalizer != null, "The music panel has no view buttons");

            playlist.onClick.Invoke();
            var current = player.Index;
            Require(player.View == HangarMusicView.Playlist && Near(panel.Height, HangarMusicPanel.TallHeight),
                "PlaylistButton left the music panel on the " + player.View + ", " + panel.Height + " high");
            Require(panel.RowLit(current), "The playlist did not open on track " + current);
            Require(ShowsText(panel.TitleText, player.Track.Title),
                "The playlist's bar reads '" + panel.TitleText + "' for " + player.Track.Title);
            var glass = FindByName<Image>(panel.gameObject, "Panel");
            Require(glass != null && glass.color.a >= 1f,
                "The playlist's glass lets what it rises over through: alpha " + (glass != null ? glass.color.a : 0f));
            RequireClear(screen, panel);

            var pick = current + 1 < player.Count ? current + 1 : current - 1;
            var row = panel.Row(pick);
            Require(row != null, "The playlist does not show track " + pick + " next to " + current);
            row.onClick.Invoke();
            Require(player.Index == pick && panel.RowLit(pick) && !panel.RowLit(current),
                "A press on row " + pick + " left the player on track " + player.Index);
            if (!player.Paused)
            {
                player.TogglePause();
            }

            row.onClick.Invoke();
            Require(player.Index == pick && !player.Paused, "A press on the paused track's row did not resume it");

            FindByName<Button>(panel.gameObject, "CollapseButton").onClick.Invoke();
            Require(player.Collapsed && Near(panel.Height, HangarMusicPanel.HeaderHeight),
                "Folded from the playlist, the music panel stands " + panel.Height + " high");
            equalizer.onClick.Invoke();
            Require(!player.Collapsed && player.View == HangarMusicView.Equalizer &&
                    Near(panel.Height, HangarMusicPanel.TallHeight),
                "EqualizerButton did not unfold the folded music panel into the equalizer");
            RequireClear(screen, panel);

            var power = FindByName<Button>(panel.gameObject, "PowerButton");
            if (player.EqualizerOn)
            {
                power.onClick.Invoke();
            }

            Require(!player.EqualizerOn, "PowerButton did not switch the equalizer off");
            power.onClick.Invoke();
            Require(player.EqualizerOn, "PowerButton did not switch the equalizer on");
            power.onClick.Invoke();
            PressFader(FindByName<HangarMusicSlider>(panel.gameObject, "Band9"), 1f);
            Require(player.EqualizerOn && player.EqualizerGain(9) == HangarMusicEqualizer.MaxGain,
                "A press at the top of the 16 kHz fader left it at " + player.EqualizerGain(9) +
                " dB with the equalizer on=" + player.EqualizerOn);
            PressFader(FindByName<HangarMusicSlider>(panel.gameObject, "Band0"), 0f);
            Require(player.EqualizerGain(0) == HangarMusicEqualizer.MinGain,
                "A press at the foot of the 31 Hz fader left it at " + player.EqualizerGain(0) + " dB");
            FindByName<Button>(panel.gameObject, "ResetButton").onClick.Invoke();
            for (var band = 0; band < gains.Length; band++)
            {
                Require(player.EqualizerGain(band) == 0, "ResetButton left band " + band + " at " + player.EqualizerGain(band));
            }

            Require(player.EqualizerOn, "ResetButton switched the equalizer off");

            playlist.onClick.Invoke();
            Require(player.View == HangarMusicView.Playlist && Near(panel.Height, HangarMusicPanel.TallHeight),
                "PlaylistButton did not go from the equalizer to the playlist");
            playlist.onClick.Invoke();
            Require(player.View == HangarMusicView.Player && Near(panel.Height, HangarMusicPanel.ExpandedHeight),
                "A second press on PlaylistButton did not go back to the player");

            for (var band = 0; band < gains.Length; band++)
            {
                player.SetEqualizerGain(band, gains[band]);
            }

            player.SetEqualizer(wasOn);
        }

        /// <summary>Presses a fader <paramref name="at"/> of the way up it, as a finger would, and lets go.</summary>
        private static void PressFader(HangarMusicSlider fader, float at)
        {
            Require(fader != null, "The equalizer lost a fader");
            var rt = (RectTransform)fader.transform;
            var rect = rt.rect;
            var local = new Vector2(rect.center.x, Mathf.Lerp(rect.yMin, rect.yMax, at));
            var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            pointer.position = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(local));
            fader.OnPointerDown(pointer);
            fader.OnPointerUp(pointer);
            Require(!fader.Dragging, "The fader still follows the pointer after it let go");
        }

        /// <summary>Drags the music panel's header by <paramref name="by"/> canvas units, y up, as a finger would.</summary>
        private static void DragMusicPanel(HangarMusicDragHandle handle, RectTransform room, Vector2 by)
        {
            var header = (RectTransform)handle.transform;
            Vector2 from = room.InverseTransformPoint(header.TransformPoint(header.rect.center));
            var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            pointer.position = RectTransformUtility.WorldToScreenPoint(null, room.TransformPoint(from));
            handle.OnBeginDrag(pointer);
            pointer.position = RectTransformUtility.WorldToScreenPoint(null, room.TransformPoint(from + (by * 0.5f)));
            handle.OnDrag(pointer);
            pointer.position = RectTransformUtility.WorldToScreenPoint(null, room.TransformPoint(from + by));
            handle.OnDrag(pointer);
            handle.OnEndDrag(pointer);
            Require(!handle.Dragging, "The music panel's header still drags after the pointer let go");
        }

        private static bool Near(float value, float expected)
        {
            return Mathf.Abs(value - expected) < 0.5f;
        }

        /// <summary>
        /// The canvases the hangar meets, in canvas units: a 2392x1080 phone,
        /// a 16:9 screen and a 4:3 tablet. The batch editor's own screen is an
        /// unscaled 640x480 that no device has, so the layout is checked on
        /// these instead.
        /// </summary>
        private static readonly Vector2[] DeviceCanvases =
        {
            new Vector2(UiLayout.ReferenceHeight * 2392f / 1080f, UiLayout.ReferenceHeight),
            new Vector2(UiLayout.ReferenceWidth, UiLayout.ReferenceWidth * 9f / 16f),
            new Vector2(UiLayout.ReferenceWidth, UiLayout.ReferenceWidth * 3f / 4f),
        };

        /// <summary>
        /// On every device canvas the music panel lies on the screen and no
        /// live control outside it lies under it, or the panel's glass would
        /// take the presses meant for that control.
        /// </summary>
        private static void RequireClear(HangarScreen screen, HangarMusicPanel panel)
        {
            OnDeviceCanvases(panel, (root, size) =>
            {
                panel.Refresh();
                var area = CanvasRect(root, panel.Frame);
                var canvas = CanvasRect(root, root);
                Require(area.xMin >= canvas.xMin - 0.5f && area.xMax <= canvas.xMax + 0.5f &&
                        area.yMin >= canvas.yMin - 0.5f && area.yMax <= canvas.yMax + 0.5f,
                    "The music panel " + area + " leaves the " + size + " canvas");
                foreach (var selectable in screen.GetComponentsInChildren<Selectable>(false))
                {
                    if (!selectable.isActiveAndEnabled || !selectable.IsInteractable() ||
                        selectable.transform.IsChildOf(panel.transform))
                    {
                        continue;
                    }

                    var rect = CanvasRect(root, (RectTransform)selectable.transform);
                    Require(!rect.Overlaps(area),
                        selectable.name + " " + rect + " lies under the music panel " + area + " on the " + size + " canvas");
                }
            });
        }

        /// <summary>
        /// Lays the hangar's canvas out at each of <see cref="DeviceCanvases"/>
        /// in turn for <paramref name="check"/>, then hands it back to the screen.
        /// </summary>
        private static void OnDeviceCanvases(Component inside, Action<RectTransform, Vector2> check)
        {
            var canvas = inside.GetComponentInParent<Canvas>().rootCanvas;
            var root = (RectTransform)canvas.transform;
            var mode = canvas.renderMode;
            var size = root.sizeDelta;
            try
            {
                // A world-space canvas takes any size; an overlay one is the screen's.
                canvas.renderMode = RenderMode.WorldSpace;
                foreach (var device in DeviceCanvases)
                {
                    root.sizeDelta = device;
                    Canvas.ForceUpdateCanvases();
                    check(root, device);
                }
            }
            finally
            {
                root.sizeDelta = size;
                canvas.renderMode = mode;
                Canvas.ForceUpdateCanvases();
            }
        }

        /// <summary>The rect a UI element covers in the canvas's own units, y up.</summary>
        private static Rect CanvasRect(RectTransform root, RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var a = root.InverseTransformPoint(corners[0]);
            var b = root.InverseTransformPoint(corners[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        /// <summary>Whether a one-line label shows <paramref name="value"/>, whole or cut with an ellipsis.</summary>
        private static bool ShowsText(string shown, string value)
        {
            if (string.Equals(shown, value, StringComparison.Ordinal))
            {
                return true;
            }

            return shown.Length > 1 && shown.EndsWith("\u2026", StringComparison.Ordinal) &&
                   value.StartsWith(shown.Substring(0, shown.Length - 1), StringComparison.Ordinal);
        }

        /// <summary>
        /// btn_logout asks first. Cancel keeps the hangar; OK ends the
        /// session: the hangar goes, the login screen opens, and the boot
        /// stops skipping it until the next sign-in.
        /// </summary>
        private static void RequireLogout(BlitzBootstrap bootstrap, HangarScreen screen)
        {
            var logout = FindByName<Button>(screen.gameObject, "btn_logout");
            Require(logout != null, "The hangar lost btn_logout");
            var loggedOut = bootstrap.Accounts.LoggedOut;
            logout.onClick.Invoke();
            var popup = screen.LogoutPopup;
            Require(popup != null && popup.Ok != null && popup.Cancel != null,
                "btn_logout did not ask to confirm with OK and Cancel");
            Require(!string.IsNullOrEmpty(popup.Body.text) && popup.Body.text != "popup:logout",
                "The logout confirmation is not translated: " + popup.Body.text);
            popup.Cancel.onClick.Invoke();
            Require(screen.LogoutPopup == null && screen.gameObject.activeSelf &&
                    bootstrap.Accounts.LoggedOut == loggedOut && FindSceneObject<LoginScreen>() == null,
                "Cancel on the logout confirmation logged out anyway");

            logout.onClick.Invoke();
            Require(screen.LogoutPopup != null, "btn_logout did not ask again");
            screen.LogoutPopup.Ok.onClick.Invoke();
            var login = FindSceneObject<LoginScreen>();
            try
            {
                Require(bootstrap.Accounts.LoggedOut, "OK on the logout confirmation did not log out");
                Require(!screen.gameObject.activeSelf, "The hangar stayed up after logout");
                Require(login != null && login.gameObject.activeInHierarchy,
                    "Logout did not open the login screen");
                Require(!BlitzBootstrap.SkipsLogin(new SettingsData { SkipLogin = true }, bootstrap.Accounts),
                    "SKIP LOGIN SCREEN still skips the login after logout");
            }
            finally
            {
                // The smoke shares the editor's save: put the flag back.
                bootstrap.Accounts.LoggedOut = loggedOut;
                bootstrap.Persist();
                if (login != null) UnityEngine.Object.Destroy(login.gameObject);
            }
        }

        private static bool HasPremium()
        {
            return _smokeAccount.HasPremium(_smokeClock.NowUnixSeconds);
        }

        private static void RequireHalloweenStand(HangarWorld world, GameObject tank, Camera camera)
        {
            var scene = world.PortedScene;
            Require(scene != null && world.ClientScene == null && world.HangarModel == scene.gameObject &&
                    scene.gameObject.activeInHierarchy,
                "The Halloween 2026 hangar is not the environment on show");
            Require(world.TankModel == tank && world.HangarCamera == camera,
                "Switching the hangar rebuilt the vehicle or its camera");
            Require(scene.RendererCount == 420 && scene.StateSwitcherCount == 5 &&
                    scene.AnimationCount == 38 && scene.LightCount == 2 && scene.ParticleSystemCount > 0,
                "The Halloween hangar lost scene content: renderers=" + scene.RendererCount +
                " states=" + scene.StateSwitcherCount + " animations=" + scene.AnimationCount +
                " lights=" + scene.LightCount + " particle systems=" + scene.ParticleSystemCount);
            Require(scene.Audio != null && scene.Audio.EmitterCount == 16,
                "The Halloween hangar lost its sound emitters");
            var shaders = new HashSet<string>(HalloweenHangarScene.ShaderNames, StringComparer.Ordinal);
            foreach (var renderer in scene.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    // A particle renderer keeps an empty trail slot.
                    if (material == null && renderer is ParticleSystemRenderer) continue;
                    Require(material != null && shaders.Contains(material.shader.name),
                        "Halloween renderer " + renderer.name + " is not drawn by a ported client shader");
                }
            }
            Require(tank.transform.localPosition == world.StandPosition &&
                    Quaternion.Angle(tank.transform.localRotation, world.StandRotation) < 0.01f &&
                    world.Orbit.transform.localPosition ==
                        world.StandPosition + Vector3.up * HangarWorld.OrbitHeight,
                "The vehicle or the orbit did not move onto the Halloween stand");
            Require(RenderSettings.defaultReflectionMode == UnityEngine.Rendering.DefaultReflectionMode.Custom &&
                    RenderSettings.customReflectionTexture is Cubemap &&
                    RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Trilight &&
                    RenderSettings.sun == scene.Sun,
                "The Halloween hangar does not light the vehicle");
            var fogColour = Shader.GetGlobalVector(SourceEnvironment.FogColourId);
            Require(Mathf.Abs(fogColour.x - scene.BackgroundColour.r) < 1e-4f &&
                    Mathf.Abs(fogColour.y - scene.BackgroundColour.g) < 1e-4f &&
                    Mathf.Abs(fogColour.z - scene.BackgroundColour.b) < 1e-4f &&
                    camera.backgroundColor == scene.BackgroundColour,
                "The Halloween hangar does not own the fog and the clear colour");
            Require(camera.GetUniversalAdditionalCameraData().requiresDepthTexture,
                "Soft particles of the Halloween hangar need the depth texture");
            Require(Mathf.Approximately(camera.fieldOfView, HangarOrbitController.SourceFov),
                "The Halloween hangar changed the hangar camera FOV");
        }

        private static void RequireClientStand(
            HangarWorld world, GameObject tank, Camera camera, string id, CameraOverrideOption depthOption)
        {
            Require(world.ClientScene != null && world.ClientScene.Definition.id == id &&
                    world.PortedScene == null && world.HangarModel == world.ClientScene.gameObject,
                "The 4.8 " + id + " is not the environment on show");
            Require(world.TankModel == tank && world.HangarCamera == camera,
                "Switching the hangar rebuilt the vehicle or its camera");
            Require(tank.transform.localPosition == Vector3.zero &&
                    Quaternion.Angle(tank.transform.localRotation, Quaternion.Euler(0f, HangarWorld.ModelYaw, 0f)) < 0.01f &&
                    world.Orbit.transform.localPosition == Vector3.up * HangarWorld.OrbitHeight,
                "The vehicle did not move back onto the 4.8 stand");
            Require(camera.GetUniversalAdditionalCameraData().requiresDepthOption == depthOption,
                "The 4.8 hangar kept the depth texture the Halloween one asked for");
            if (id == "hangar")
            {
                var fog = Shader.GetGlobalVector(SourceEnvironment.FogRangeId);
                Require(Mathf.Abs(fog.x - 12f) < .001f && Mathf.Abs(fog.y - 60f) < .001f,
                    "The ordinary hangar did not get its 4.8 fog range back");
            }
        }

        /// <summary>The render settings a hangar switch must give back.</summary>
        private sealed class RenderState
        {
            private UnityEngine.Rendering.DefaultReflectionMode _reflectionMode;
            private Texture _reflection;
            private float _reflectionIntensity;
            private UnityEngine.Rendering.AmbientMode _ambientMode;
            private Color _ambient;
            private Light _sun;

            public static RenderState Capture()
            {
                return new RenderState
                {
                    _reflectionMode = RenderSettings.defaultReflectionMode,
                    _reflection = RenderSettings.customReflectionTexture,
                    _reflectionIntensity = RenderSettings.reflectionIntensity,
                    _ambientMode = RenderSettings.ambientMode,
                    _ambient = RenderSettings.ambientLight,
                    _sun = RenderSettings.sun,
                };
            }

            /// <summary>
            /// The 4.8 hangars never set the reflection, so it has to come
            /// back as it was; the ambient is theirs and has to match. A sun
            /// that lived in the replaced 4.8 hangar went with it, so the
            /// rebuilt one may only leave no sun or one of its own lights.
            /// </summary>
            public void Require(string when, HangarWorld world)
            {
                var problems = new List<string>();
                if (RenderSettings.defaultReflectionMode != _reflectionMode)
                    problems.Add("reflection mode " + _reflectionMode + " -> " + RenderSettings.defaultReflectionMode);
                if (RenderSettings.customReflectionTexture != _reflection)
                    problems.Add("reflection " + Name(_reflection) + " -> " + Name(RenderSettings.customReflectionTexture));
                if (!Mathf.Approximately(RenderSettings.reflectionIntensity, _reflectionIntensity))
                    problems.Add("reflection intensity " + _reflectionIntensity + " -> " + RenderSettings.reflectionIntensity);
                if (RenderSettings.ambientMode != _ambientMode)
                    problems.Add("ambient mode " + _ambientMode + " -> " + RenderSettings.ambientMode);
                if (RenderSettings.ambientLight != _ambient)
                    problems.Add("ambient " + _ambient + " -> " + RenderSettings.ambientLight);
                var sun = RenderSettings.sun;
                var sunGone = !ReferenceEquals(_sun, null) && _sun == null;
                var sunBack = sun == _sun ||
                    (sunGone && (sun == null || sun.transform.IsChildOf(world.HangarModel.transform)));
                if (!sunBack)
                    problems.Add("sun " + Name(_sun) + (sunGone ? " (destroyed)" : string.Empty) + " -> " + Name(sun));
                HangarScreenSmoke.Require(problems.Count == 0,
                    "The render settings did not come back " + when + ": " + string.Join("; ", problems));
            }

            private static string Name(UnityEngine.Object obj)
            {
                if (ReferenceEquals(obj, null)) return "none";
                return obj == null ? "a destroyed object" : obj.name;
            }
        }

        /// <summary>
        /// Runs <paramref name="steps"/> in order on editor updates, at most
        /// one a frame: each waits for its condition, up to its timeout in
        /// seconds, then acts.
        /// </summary>
        private static void Sequence(
            params (Func<bool> Until, double Timeout, string Failure, Action Then)[] steps)
        {
            var index = 0;
            var deadline = EditorApplication.timeSinceStartup + steps[0].Timeout;
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                try
                {
                    if (index >= steps.Length)
                    {
                        EditorApplication.update -= poll;
                        return;
                    }
                    var step = steps[index];
                    if (!step.Until())
                    {
                        Require(EditorApplication.timeSinceStartup <= deadline,
                            step.Failure.Length > 0 ? step.Failure : "Smoke step " + index + " timed out");
                        return;
                    }
                    index++;
                    if (index < steps.Length)
                        deadline = EditorApplication.timeSinceStartup + steps[index].Timeout;
                    step.Then();
                }
                catch (Exception exception)
                {
                    EditorApplication.update -= poll;
                    index = steps.Length;
                    Debug.LogException(exception);
                    Finish(1);
                }
            };
            EditorApplication.update += poll;
        }

        /// <summary>Unix seconds of a wall-clock time on the machine's own calendar.</summary>
        private static double LocalUnixSeconds(DateTime local)
        {
            return HangarSchedule.UnixSeconds(local, null);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void OnEditorUpdate()
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                return;
            }
            var exitCode = SessionState.GetInt(ExitCodeKey, -1);
            if (exitCode >= 0 && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Quit(exitCode);
                return;
            }
            DateTime deadline;
            if (DateTime.TryParse(SessionState.GetString(DeadlineKey, string.Empty), out deadline) &&
                DateTime.UtcNow > deadline.ToUniversalTime())
            {
                Debug.LogError("[Blitz] hangar smoke timed out");
                Finish(1);
            }
        }

        private static void Finish(int exitCode)
        {
            MenuStyle.ToolOverride = null;
            SessionState.SetInt(ExitCodeKey, exitCode);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
            }
            else
            {
                Quit(exitCode);
            }
        }

        private static void Quit(int exitCode)
        {
            SessionState.SetBool(ActiveKey, false);
            Unhook();
            EditorApplication.delayCall += () => EditorApplication.Exit(exitCode);
        }
    }
}
