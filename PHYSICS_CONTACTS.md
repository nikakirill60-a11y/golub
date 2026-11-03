# Crash states 11.20 and tank contact physics

## Original assets and scope

`Tools/port_blitz_1120_crash.py` imports all 15 vehicles in `blitz_1120_cards.json`: BZT-70; the European/Kranvagn progression (including the existing Vickers MkF entry); AMX 13 M24, ERAC 105 Proto, Project Louis and Projet Murat. `legacy1120_crash_manifest.json` records SHA-256 hashes of source XML, SC2/SCG and crash textures from `../wotblitz11.20.0files`.

Every inspected SC2 contains authored `chassis_track_crash_L/R` meshes, already exported as `tracks_crash`. These are now combined with the original common 11.20 crash albedo and that vehicle's authored `HP_Fire_01/02` positions. Death stops drive/track animation; resurrection restores the live materials/track meshes and clears contact/fall state.

**Source limitations:** the inspected Blitz scenes do not contain separate deformed hull/turret crash meshes. PC-style XML `crash/lod0` references do not establish that such additional Blitz SC2 files exist. The installed original hull/turret/gun geometry is retained, not replaced with invented rubble. The common crash base-colour/normal/rough-metal textures are also extracted for provenance, but the current Unity tank shader uses the original legacy crash albedo, not a pixel-identical recreation of the 11.20 PBR shader. Existing original 4.8 explosion/fire effects remain in use; this change does not claim to replace those with 11.20 death FX or add turret ejection.

Re-import (uses the existing decoder environment and compatible unpacked scenes):

```sh
PYTHONPATH="$PWD/.venv-texture/lib/python3.14/site-packages" python3 Tools/port_blitz_1120_crash.py
```

## Suspension and contact solver

- The wheel budget resets each fixed step, not every render frame or 20 ms window; the full 14-tank roster receives support at 50/60/100 Hz.
- Wheel spring loads use the complete installed Rigidbody mass; linear compression and damped point velocity replace quadratic compression/frame-scaled forces. PhysX integrates force over the fixed step once.
- Engine horsepower is converted to watts, with finite low gear, 82% drivetrain efficiency and a 1.25 gameplay response multiplier. Terrain-contact fraction limits longitudinal/lateral grip. Steering approaches its target angular speed instead of instantly replacing all angular velocity; pitch/roll remain physical.
- A low centre of mass lies inside the measured hull. Solver iterations are 12/6; angular velocity and depenetration speed are bounded.
- A flat tank-only contact band prevents sloped frontal armour from acting as a powered ramp. It excludes terrain, armour/projectile queries and all non-tank physics layers. The original convex hull still owns map contact. Other tanks do not supply wheel traction or suspension thrust.
- Horizontal ram damage excludes the vertical component; landing and sustained crushing are separate environmental damage paths.

This is explicitly gameplay/physics tuning, **not recovered original Blitz server simulation**. Vehicle-card speed, power, installed mass and chassis HP drive the behaviour.

## Sustained crushing

Actual upward/downward support contact plus separated hull heights is required; proximity or side contact is insufficient. Each live tank in a stack loses **5 HP per simulated second**, irrespective of collider/contact count and at 50/60/100 Hz. A fractional accumulator emits integer health damage (equivalent to 1 HP/200 ms). The user's parenthetical 1 HP/20 ms would instead be 50 HP/s; the requested principal 5 HP/s is used.

Both live participants receive this rate, including friendly stacks. A dead tank stays a physical support/contact source and damages the live tank beneath it, but cannot itself lose HP below zero. Separation/contact expiry and respawn clear the accumulator. Environmental damage intentionally bypasses ram perks, weapon shields and the bot harmless toggle so they do not silently alter the requested rate.

## Falls and track damage

A fall records airborne duration, apex descent and peak downward speed. Landing can occur at the first supporting wheel ray, before hull collision, or at an upward-facing physical contact; the airborne state is consumed once. Short terrain gaps below 0.15 s are ignored.

### Arcade tuning — 2026-10-03 (supersedes the September fall curve)

Equivalent fall height is `min(measuredDescent, v²/(2g)) * clamp01(upNormal)²`. Both a real descent and downward speed are required; steep/sloped support reduces the vertical impact instead of amplifying it. Wall contacts (`upNormal < 0.45`) are not landings.

All tanks share two independent thresholds, with a small hull-height cushion `clamp(hullHeight * 0.1, 0, 0.25)` metres:

- Up to **3 m + cushion**: no hull or track damage, even if solver velocity spikes.
- Between **3 and 6 m + cushion**: only partial track HP loss; a single ordinary 5 m landing cannot disable healthy tracks, even on the heaviest/weakest chassis.
- Above **6 m + cushion**: progressive hull damage; a vertical 10 m drop costs at most about 9% of maximum hull HP across the roster, rather than instantly destroying the tank.
- Large cliffs remain dangerous: tracks can break around 9–12 m, and a vertical drop around 20–25 m can be lethal. Existing damage to hull/tracks still matters; the policy does not grant invulnerability to an already damaged tank.

Chassis strength is now bounded to `clamp(chassisHP * 150 / installedMass, 0.85, 1.15)` so mass/module tuning is a modest modifier, not a lethal multiplier. Track damage fraction is `clamp01(trackExcess / (7 * strength))`; hull damage fraction is `clamp01(0.004 * (min(hullExcess, 20) / strength)²)`. Fractions are clamped before integer HP conversion. Non-finite inputs, non-positive speed/descent/mass/HP and side contacts return no damage. This policy is shared by stock/upgraded modules and future/modded vehicles, with no KpfPz-specific exception.

The normal module path damages/disables tracks and retains existing repair behaviour. Landing consumes the airborne state once; respawn clears it. Ramming, sustained crush damage, vehicle cards and gravity are unchanged.

These constants are a documented gameplay policy, not a claim of an exact 4.8 server fall formula. Respawn resets tracking after repositioning and zeroing velocity.

## Repeatable verification

Always set `SDL_HIDAPI_JOYSTICK_DISABLE_UDEV=1` for this host's Unity editor.

```sh
export SDL_HIDAPI_JOYSTICK_DISABLE_UDEV=1
Tools/unity.sh test
Tools/unity.sh contact-physics-smoke
Tools/unity.sh modules480-smoke
Tools/unity.sh uprising-smoke
Tools/unity.sh wreck1120-render
```

Contact smoke uses real Play Mode/PhysX: stable suspension, fast MS-1→Maus and Maus→MS-1 rams, live/live stack, wreck/live stack, KpfPz 70 drops from 2 m and 5 m, and a measured MS-1 drop from 16 m. Set `BLITZ_CONTACT_HZ=50`, `60` or `100` to exercise each fixed timestep. EditMode checks cover timestep-independent rates, duplicate/expired/side contacts, fall strength, contact masks/centre of mass and destruction/restoration of all 15 newer vehicles. The 11.20 graphics-device renderer captures ordinary and ammo-bay deaths for BZT-70, Kranvagn and Projet Murat across six timestamps each (36 frames).

### Verified result — 2026-09-30

- EditMode: **795/795 passed**, 0 failed (completed 13:19:28 UTC).
- Final real PhysX smoke: MS-1→Maus maximum rise 0.00065 m, Maus→MS-1 0.00572 m, neither became a stack. Live/live stack lost exactly 5/5 HP over the measured second; wreck/live lost 5 HP. A 10 m free fall recorded 14.17 m/s, 127 hull damage, disabled tracks and one landing; runtime errors=0.
- Final Modules480 and Uprising Play Mode regressions passed, runtime errors=0.
- 36 new-vehicle death frames captured; ordinary/ammo-death contact sheets inspected. All 49 source hashes verified; all 15 vehicles have both fire markers.
- Linux debug and Android debug builds succeeded after final texture import normalization, errors=0. Build reports: 1,805,505,859 and 3,490,709,898 bytes respectively. Both compiled artifacts contain the new contact-physics component. The original AA=8 and project settings are retained; automatic editor migrations are excluded from Git.
- No ADB device was connected. Verification is editor/PhysX plus successful desktop and Android builds, not an on-device playtest.

Outputs: `Build/Linux-debug/BlitzLegacy`, `Build/Android/BlitzLegacy-debug.apk`. Logs and rendered frames remain under `Logs/` and are not committed.

## Drive-response follow-up

The first physics pass checked both forward and reverse speed caps before permitting any engine force. Downhill rollback above the reverse cap consequently disabled thrust even when the player requested forward motion. Normal rolling resistance alone then took several seconds to remove the rollback.

`TankSuspension.DriveAcceleration` now checks only the speed limit in the **requested** direction. Opposing motion activates a dedicated brake, targeting a 0.8 s stop with gravity compensation, bounded by actual terrain support/available grip (0.85g maximum); reversal is not an instantaneous velocity assignment. No terrain support means no braking/thrust, so a tank cannot obtain powered traction from another tank or in mid-air. Hit-stagger protection remains intact.

Ordinary powered response gains a modest 1.25 multiplier; card forward/reverse speed limits, installed mass, suspension springs, centre of mass, steering, contact band, crush rate and fall damage are unchanged. This is intentionally responsive game handling, not a claim of exact real-world engine efficiency.

New unit cases cover overspeed reversal in both directions, rollback on a 20° slope at 50/60/100 Hz, forward speed/acceleration for MS-1/Maus/T-54, blocked/airborne behaviour and requested-direction limits. The existing real PhysX smoke also seats Maus on an actual 20° ramp, injects backward velocity of 6 m/s, requests forward, then repeats the opposite direction from 9 m/s forward; both must cross into the requested direction within 2 s while retaining contact/fall regressions.

Follow-up verification: **804/804 EditMode tests passed**, completed 2026-09-30 13:42:21 UTC. Real Maus slope reversal crossed into forward motion at 1.916 s after a -6 m/s rollback and into reverse motion at 1.066 s after +9 m/s forward overspeed. Contact/crush/fall regressions and Uprising Play Mode passed, runtime errors=0. These are measured on the test ramp, not a promise of identical acceleration for every chassis, incline and damaged engine.

Follow-up Linux and Android debug builds succeeded (errors=0, pipeline exit=0); original AA=8/project settings preserved. Outputs at the same Build paths were refreshed. No ADB device was connected.

## Ride height fix (tracks sinking into terrain)

The first contact pass calibrated each wheel spring for a static sag of 22% of the ray length but kept the ray ending 0.05 m below the visible track bottom. At rest the ray therefore ended ~0.15-0.4 m *above* where it met the ground: every tank sat that much low, the rendered track was buried in the landscape (visible only while airborne) and many hull shells touched the ground.

The spring rate and travel are kept (they are tuned for slope reversals and contact handling). Instead:

- The ray is lengthened. `RideDrop` is the measured distance from the ray origin to the visible track bottom in hull space (`HullGeometry.TracksBottom`), `SpringSag = max(0.04, 0.22 * (RideDrop + 0.05))` and `WheelLength = RideDrop + SpringSag`. At rest each ray ends exactly at the track bottom; unloaded wheels keep the same droop.
- `StaticLoadShares` rates each wheel for its static share of the weight from the real centre of mass (linear along length and width, clamped to 0.35-1.65 of the mean), so hulls with an offset mass centre rest level with both track ends on the surface.
- A linear bump stop adds 4x the main rate once a track would sink more than 2 cm (partial support, landings). A steep/progressive stop was tried and rejected: end wheels act on a small rotational mass and it became numerically unstable at 60 Hz (tanks bounced, tumbled and fell through in the full battle smoke). Per-wheel force cap: 8x its weight share.

Drive, grip, mass, centre of mass, crush and fall rules are unchanged.

Verification: `Tools/unity.sh ride-height-smoke` (all 357 cards on flat PhysX ground; track within -0.08/+0.08 m, all wheels supported) and `Tools/unity.sh map-spawn-drive-smoke` (every spawn marker of the random-battle maps on the real landscape, grouped 45 m apart; mean track penetration < 0.06 m, 3 s forward drive > 1.5 m, then a turn).

| | before | after |
|---|---|---|
| Flat ground, 357 cards: visible track vs surface | -0.12..-0.40 m (almost every card fails) | -0.007..0.000 m |
| Real maps, 57 spawns: worst wheel burial | 0.14-0.29 m (57/57 fail) | 0.056 m (57/57 pass) |
| Maus 20-degree slope reversal | 1.916 s / 1.066 s | 1.949 s / 1.066 s |
| Full 10v10 battle smoke | pass | pass, all hulls stable |

### Verified arcade fall result — 2026-10-03

- Full EditMode gate: **1094/1094 passed**, 0 failed; includes all 398 cards and 4508 enumerated module combinations, monotone/bounded damage, thresholds, invalid inputs, gentle slopes, KpfPz/Maus/MS-1 repair and respawn.
- Real Play Mode/PhysX at **50/60/100 Hz**: KpfPz 70 from 2 m loses no hull/track HP; from 5 m loses no hull HP and retains 145/149/148 of 200 track HP respectively, never immobilized, exactly one damaging landing. The stock MS-1 from 16 m loses 57/58/57 hull HP, breaks tracks and survives.
- Existing ram/stack/slope reversal checks pass at all three rates; live stack 5/5 HP per second, wreck stack 5 HP per second, runtime errors=0. Spawn reapplies source physics defaults, so the smoke overrides timestep after every spawn, not before initialization.
- Linux build succeeded (2,007,937,159 bytes, 0 errors), and battle-flow smoke passed queue/cancel/intro/countdown/victory/result/profile/history/read-only-result/nickname/persist/hangar.
- QualitySettings and PC/Mobile URP SHA-256 unchanged; build-generated global URP settings restored from the pre-test snapshot. No vehicle-card, gravity, ram, crush, graphics setting or save changes. Build/log artifacts are not committed. No ADB device connected; on-device verification was not performed.
