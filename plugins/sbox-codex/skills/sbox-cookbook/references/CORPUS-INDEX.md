# Corpus Index — Cross-Reference of 51 Mined Games

Cross-reference of which of the 51 mined games implement each system/genre — use it to find reference implementations to compose. Per-game depth is in `sbox-lessons/mining-v2/games/<game>.md`.

---

## SYSTEMS

### inventory

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `namicry.gacha_crawler` | Fixed equipment slots + typed free lists; consumables stack by deep field-equality not id | `gacha_crawler/Code/Models/PlayerCharacter.cs` |
| `enifun.shop_manager` | Physical item-flow graph (wholesale→box→carry→shelf→shelf-slot); visual stocking via deterministic child snap-point shuffle | `shop_manager/Code/Shop/ConsumerShelf.cs` |
| `thefancylads.farm_land` | 20-slot backpack + 10-slot hotbar; items are real spawned GameObjects on slot-switch; `ItemModifier` scales sell value | `farm_land/Code/Common/Items/Inventory/` |
| `lowkeynetworks.newrp` | 24-slot fixed-capacity; stack-first-then-empty-slot add; `ItemInstance` carries arbitrary `Dictionary<string,object>` per-instance state bag | `newrp/Code/modules/inventory/` |
| `klibatocorp.phenodex` | Four typed dicts (seeds/pots/fertilizer/flowers); `FlowerStack` stores `CumulQualityGrams` so weighted-average quality survives stacking | `phenodex/Code/Player.cs` |
| `suburbianites.blindloaded` | Per-round consumable item + equipped cosmetic skins; ownership sanitized vs Steam Stats on load to prevent syncing unowned items | `blindloaded/Code/Player/Player.cs` |
| `khamitech.battledraft` | Survival mode: grid inventory with `Width`/`Height`/`Weight`/`Slot`; item pickup auto-routes (wear→slot→hand) via events | `battledraft/Code/Addons/Survival/Asset/Item/ItemAsset.cs` |
| `bublic.stone_by_stone` | (see economy-currency — inventory is the currency store) | `stone_by_stone/Code/` |

---

### economy-currency

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `enifun.shop_manager` | Three-path host-auth singleton: `AddMoney` / `SpendMoney` (gated) / `ForceSpend` (allows negative); ring-buffer recent transactions | `shop_manager/Code/Economy/ShopFunds.cs` |
| `artisan.darkrpog` | Multi-pool audited economy (wallet/bank/org-treasury/bitcoin); bank has typed transaction history + `IMoneyBank` abstraction | `darkrpog/Code/Player/Player.Bank.cs` |
| `thefancylads.farm_land` | Polymorphic currency strategy (`CashCurrency` / `ItemCurrency`); product can cost "$5 + 3 wheat" via a `List<Currency>` | `farm_land/Code/Common/Economy/Currency.cs` |
| `lavagame.sandmoney_` | Double-precision cash (survives to 1e28); NaN/Infinity guard on every mutation site; sell-only tax reduced by upgrades | `sandmoney_/Code/Player/PlayerTrader.cs` |
| `lavagame.multis_cases` | Data-driven economy balance with EV normalization: cap-and-redistribute pass so weighted EV hits target ratio exactly | `multis_cases/Code/Game/Economy/Cs2CaseApiBuilder.cs` |
| `freddo.scoops` | Static `Econ.cs` of pure `const`/formula functions — all costs/rates in one retune-able file; `TrySpend`/`Earn` are the only two writers | `scoops/Code/Econ.cs` |
| `khamitech.battledraft` | `[Sync(FromHost)]` money with setter that fires both local `Action<uint>` and global static event for dual UI binding | `battledraft/Code/Addons/DMwS/Player/PlayerDeathmatch.cs` |
| `lowkeynetworks.newrp` | Wallet never `[Sync]` — clients learn balance through EventBus `PlayerStateChangedEvent`; `TakeMoney` returns bool and auto-saves | `newrp/Code/modules/player/PlayerData.cs` |

---

### shop-vendor

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `enifun.shop_manager` | Piecewise demand curve: buy-chance vs margin band; rich customers ignore markup; prices synced as one `"id:price,id:price"` string | `shop_manager/Code/Economy/PriceManager.cs` |
| `thefancylads.farm_land` | Daily-rotating barter vendor seeded by `new Random(daysSinceEpoch)` — all peers compute same stock independently, no networking | `farm_land/Code/Common/Economy/MushroomDealer.cs` |
| `klibatocorp.phenodex` | Family of vendor components each setting a static `IsOpen` bool that the single `Hud.razor` reads; purchases are static host-auth methods | `phenodex/Code/World/Shop.cs` |
| `lowkeynetworks.newrp` | `[Rpc.Host] Purchase` chain: find item → per-player cap (count valid tracked GOs) → `TakeMoney` → spawn → refund on fail; ownership tracked as live-object list | `newrp/Code/content/market/MarketService.cs` |
| `khamitech.battledraft` | CS-style buy menu: `TryBuy(itemId)` RPC validates ownership+funds host-side; `TrySelect` validates ownership before equipping slot | `battledraft/Code/Addons/DMwS/DeathmatchWithShopManager.cs` |
| `lavagame.multis_cases` | Every vendor is a tiny `Interactable` subclass that opens a Razor panel; real logic stays in the panel — client-side raycast detection | `multis_cases/Code/Game/Stations/` |
| `artisan.darkrpog` | Auto-shops are placeable, ownable, hackable; casino follows same "world-item + interactable + host-auth service" shape | `darkrpog/Code/Items/AutoShop/` |
| `facepunch.fair` | `Shop : Building` — `OnUse(guest)` picks a random item from a list, adds to guest inventory, increments uses | `fair/Code/Park/Buildings/Shop.cs` |

---

### save-persistence

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `enifun.shop_manager` | XOR-encrypt + atomic write/backup + versioned JSON-node migrations (`{version: migrateFn}`) + deferred apply with readiness gate | `shop_manager/Code/Save/SaveManager.cs` |
| `thefancylads.farm_land` | Registry of typed `IGridSaveDataHandler` classes; per-entity `JsonElement BuildingSpecificData`; debounced+interval autosave via event interfaces | `farm_land/Code/Persistence/SaveHandlers/GridSaveHandlers.cs` |
| `artisan.darkrpog` | Backup-before-write + corrupt-save quarantine + multi-partition self-healing load + progression-based save scoring (picks best of N copies) | `darkrpog/Code/Player/Persistence/PlayerRoleplayStorageRepository.cs` |
| `lavagame.multis_cases` | FNV-1a checksum + XOR cipher + hand-rolled versioned binary reader with `version >= N` field guards; cloud-authoritative with local as safety-net only | `multis_cases/Code/Game/Save/SaveCrypto.cs` |
| `facepunch.fair` | Interface-discovered ordered versioned JSON; `SpawnedPrefabSaveData<T,S>` saves/restores collections of prefab instances; version mismatch deletes and restarts | `fair/Code/Persistence/PersistenceManager.cs` |
| `dexlab.sandbox-reforged` | Diffs live scene against SceneFile baseline; saves `[Sync]`-not-`[Property]` values separately with JSON-first/BytePack-fallback; restores sync before ownership | `sandbox-reforged/Code/Save/SaveSystem.cs` |
| `namicry.gacha_crawler` | Two-tier: local JSON + external REST; migration via `FixAllSpritePaths()` on load; `[JsonIgnore]` on computed props | `gacha_crawler/Code/GameManager.cs` |
| `facepunch.jumper` | Per-map save keyed by `Scene.Name`; clean DTO↔component split; `if (IsProxy) return` so only the owner loads | `jumper/Code/Player/JumperProgress.cs` |

---

### progression-upgrades

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `facepunch.ss2` | Central stat table `Dictionary<PlayerStat,float>`; modifiers keyed by source object so removal is clean; `RefreshProperty` applies Set→Add→Mult | `ss2/Code/Player/Player.Stats.cs` |
| `facepunch.ss1` | Priority-ordered stat stack (highest-priority Set, then sum Add, then multiply Mult); perk event-bus via ~18 virtual `Status` hooks | `ss1/Code/things/Player.cs` |
| `thefancylads.farm_land` | ~55 challenges authored as C# literals; polymorphic `IProgressTracker` types; completion grants `BuffManager` passive buffs | `farm_land/Code/Progression/Challenges/Challenger.cs` |
| `enifun.shop_manager` | 1 XP/unit sold; `XpThresholds[]` table; each level = 1 unlock token; separate shop upgrade tree feeding demand/patience multipliers | `shop_manager/Code/Economy/PlayerProgression.cs` |
| `lavagame.multis_cases` | Rank = `TotalCasesSpent + CumulativeSkinValue`; 17 tiers; `GetRarityBonus(rank)` feeds back into loot roll weight | `multis_cases/Code/Game/Core/RankSystem.cs` |
| `vault77.chop_the_forest` | Pure static `readonly Definition[]` arrays per upgrade track; code never changes when balance does | `chop_the_forest/Code/Player/AxeUpgradeBalance.cs` |
| `lavagame.sandmoney_` | Exponential-cost tracks; effect values are `switch` expressions not stored numbers; purchase is transactional with rollback on exception | `sandmoney_/Code/UpgradeSystem.cs` |
| `klibatocorp.phenodex` | Tier ladders as plain ints/bools; all bonuses consumed at single point in `Plant.GetCurrentPhaseDuration()` and `Plant.Harvest()` | `phenodex/Code/Player.cs` |

---

### gacha-loot

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `namicry.gacha_crawler` | Layered rarity rolls per source context; pity flags on `PlayerCharacter`; 100-item roulette strip with cubic-ease spin and result at index 85 | `gacha_crawler/Code/Data/ItemGenerator.cs` |
| `lavagame.multis_cases` | Weighted pool with weight per item; per-rarity base weights in `GetWeightForRarity`; wear/float as value multiplier (FN 1.35× → BS 0.70×) | `multis_cases/Code/Game/Economy/ItemDefinition.cs` |
| `facepunch.ss2` | Reflection-driven catalog (`TypeLibrary.GetTypes<Perk>()`); weighted draw without replacement; per-pick reweighting by existing level; synergy gates | `ss2/Code/PerkManager.cs` |
| `facepunch.ss1` | Weighted perk draft with prerequisite-combo gating, "Specialist" stat that biases toward owned perks, difficulty-gated rarity | `ss1/Code/StatusManager.cs` |
| `thefancylads.farm_land` | Two-tier fishing: category roll then level-windowed weighted pick; mushroom dealer: cumulative-weight walk over `SpawnWeights` | `farm_land/Code/Common/Fishing/FishingModel.cs` |
| `artisan.darkrpog` | Weighted roll in `LootboxRoller.cs`; fragment-exchange (pity/dust) catalog; per-player bound-item rules | `darkrpog/Code/Lootboxes/LootboxRoller.cs` |
| `suburbianites.blindloaded` | Single weighted table + gamble layer (casino prep: TRIPLE 3%/DOUBLE 33%/BUST 25%/KEEP 39%); effects applied host-side | `blindloaded/Code/Items/ItemDef.cs` |

---

### genetics-breeding

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `klibatocorp.phenodex` | Value-type genome struct; Box-Muller gaussian inheritance with generation-driven variance reduction (F1=100%→F8=30%); bucket-hash collapses phenotype variants | `phenodex/Code/Cultivation/Breeding.cs` |
| `thefancylads.farm_land` | Mutation registry shuffled then each rolled against `chance * buff("farming.mutation.chance")`; mutation id `[Sync]` so visual model swaps on all clients | `farm_land/Code/Common/Farming/Mutations/MutationModel.cs` |
| `facepunch.fair` | Animal-enclosure escape sim with `IsInEnclosure()` cell check; `GetPersistentMetadata` hooks ready for genetics extension | `fair/Code/AI/Animals/Animal.cs` |

---

### leaderboards-services

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `namicry.gacha_crawler` | `Stats.SetValue` with rich metadata dict per stat; "best character represents account" by highest `GetPowerLevel()`; `SetAggregationLast` shows current not peak | `gacha_crawler/Code/Services/LeaderboardService.cs` |
| `facepunch.ss2` | Multi-outcome single-stat encoding (victory/boss-loss/early-death sort on one number); `LEADERBOARD_VERSION` baked into stat name for clean resets; hardcoded hidden-entry filter | `ss2/Code/Manager.Stats.cs` |
| `yellowletter.terrys_crash_course` | Per-level stat key; friends scope via wide-fetch + client-side `IsFriend` filter + re-rank; self-row fallback with `CenterOnMe`; local-best overlay | `terrys_crash_course/Code/CrashCourse/LevelLeaderboardService.cs` |
| `fluffybagel.chess_otb` | Dual persistence: s&box Stats + external HTTP; targeted RPC pushes Elo to each player's own client so they write to their own Stats | `chess_otb/Code/Game/Systems/EloSystem.cs` |
| `lavagame.multis_cases` | Change-detected push (skips if nothing changed); fetch-once then "Load More" reveals rows without a second API call; `FETCH_COOLDOWN` | `multis_cases/Code/Game/Social/LeaderboardData.cs` |
| `klibatocorp.phenodex` | Client-side static cache with 60s TTL; `EnsureFreshAsync()` de-dups concurrent callers; achievements use `CheckThreshold` helper to avoid s&box early-fire | `phenodex/Code/Net/StrainLeaderboard.cs` |
| `facepunch.fair` | Double-writes every stat to both local `NetDictionary` (for gameplay/goals) and `Sandbox.Services.Stats` (for cloud leaderboard) | `fair/Code/Park/Progression/Stats/Stats.cs` |

---

### idle-offline

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `thefancylads.farm_land` | Same `Crop.UpdateStage(simulate:true)` used for real-time and offline catch-up; clamp iterations to remaining stages to prevent runaway loops | `farm_land/Code/Persistence/FarmStateManager.cs` |
| `klibatocorp.phenodex` | All timers are UTC-tick deltas (`DateTime.UtcNow.Ticks`), never accumulators; `DEV_TIME_SCALE` const compresses the whole clock for testing | `phenodex/Code/Plant.cs` |
| `lavagame.sandmoney_` | Two faucets reconciled from single `LastSeenUnix`: infra with 50% penalty + 24h cap, bots burn fuel offline then disable themselves | `sandmoney_/Code/InfrastructureManager.cs` |
| `enifun.shop_manager` | Day/night clock `[Sync]` with `TotalGameHours` delay primitive; day-rollover fans out to all subsystems; `SkipNight` is a two-phase permission-gated RPC | `shop_manager/Code/Time/TimeManager.cs` |
| `clearlyy.s_miner` | (tycoon-idle with offline progress via timestamped start) | `s_miner/Code/` |
| `sino.s_sino` | Online idle only — `PassiveIncome` accrues via `Time.Delta` in `OnUpdate`; `sqrt`-damped so no single skin dominates | `s_sino/Code/Core/GameManager.cs` |

---

### building-placement

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `enifun.shop_manager` | 8-unit snap; validity: floor-tag + arrow-front floor check + overlap trace + front-clearance BBox extension; `[Rpc.Host]` place + `NavMesh.SetDirty()` | `shop_manager/Code/Shop/ShopBuilder.cs` |
| `thefancylads.farm_land` | `[Sync] NetDictionary<Vector2Int, GridCell>` grid; client phantom strips all but `ModelRenderer`; server re-validates ownership + mid-load gate | `farm_land/Code/Common/Building/FarmGrid.cs` |
| `facepunch.fair` | Two-phase: client ghost (shader `Ghost=1/2` for green/red) + host commit; `TryAutoRotate` toward adjacent paths; supports Animal/Building/PathFurniture polymorphic types | `fair/Code/Park/Buildings/BuildingPlacer.cs` |
| `klibatocorp.phenodex` | Pre-cloned ghost with business components disabled + green tint; `[Rpc.Broadcast] CommitRpc` re-checks `IsHost`; `RestoreSavedPots()` on load | `phenodex/Code/World/PotPlacement.cs` |
| `thefancylads.restaurant_dev` | Dual-resolution grid (furni at 12.5 / walls at 50); multi-cell footprints with rotation; transactional move: clear old cells first, restore on any failure | `restaurant_dev/Code/Common/Restaurants/RestaurantGrid.cs` |
| `klavs.basebuilder` | Pre-spawned pool of block objects (not free-spawned props); `OwnerTransfer.Fixed` keeps host authoritative except while held; optimistic client prediction | `basebuilder/Code/BaseBuilder/BaseBuilderPlacementTool.cs` |
| `lavagame.sandmoney_` | Idle infrastructure — 7 tiers bought in bulk; geometric bulk-buy price via closed-form geometric series; speed upgrade preserves in-progress fraction | `sandmoney_/Code/InfrastructureManager.cs` |

---

### crafting

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `namicry.gacha_crawler` | Ingredient-flag system: `[Flags] CraftIngredientEffect` forces item category/class/tier during procedural generation; three recipes (fuse/reroll/property-reroll) | `gacha_crawler/Code/Data/CraftIngredientData.cs` |
| `thefancylads.farm_land` | Composter = timed converter; `TimeFinished` persisted as `DateTime` so it survives offline; hold-to-fill UX with `HoldDuration` | `farm_land/Code/Common/Farming/Composter.cs` |
| `thefancylads.restaurant_dev` | Cooking-as-assembly: `RecipeStep` graph with `CookedStateMask`, dependency index, `IsRequired`; assembly strategy pattern; `PossibleRecipes` narrowed as ingredients added | `restaurant_dev/Code/Common/Cooking/` |
| `meteorlab.vehicle_tool_example` | Runtime vehicle assembly: editor `CreateCar()` button rigs a model into drivable car (adds `WheelCollider`, powertrain, sound) inside one `UndoScope` | `vehicle_tool_example/Code/VehicleCreator.cs` |
| `khamitech.battledraft` | GameResource recipes with embedded `$N.MetaKey` expression DSL for output-meta referencing input-meta; arithmetic evaluated at craft time | `battledraft/Code/Addons/Survival/Asset/Craft/CraftAsset.cs` |
| `gabreusenra.wjse` | (crafting present — see per-game file) | `wjse/Code/` |
| `meteorlab.garden` | (crafting present — see per-game file) | `garden/Code/` |

---

### dialogue

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `klibatocorp.phenodex` | Event-driven FTUE: 11-step enum persisted on player; `TryAdvance(from, to)` guards so steps are idempotent and reorder-safe; no "Next" button | `phenodex/Code/UI/TutorialManager.cs` |
| `dimmies.terryspapers` | Coroutine-driven branching narrative; `TaskCompletionSource` gate prevents day-end mid-animation; flag-driven life-sim narrative between shifts | `terryspapers/Code/GameHandler.cs` |
| `artisan.darkrpog` | Proximity chat with channels; OOC/advert/local dispatch; `Rpc.FilterInclude` sends broadcast only to range-computed recipients | `darkrpog/Code/` |
| `lowkeynetworks.newrp` | Proximity voice/text: `Rpc.FilterInclude(recipients)` on a `[Rpc.Broadcast]` for targeted whisper/area/team messages | `newrp/Code/modules/chat/ChatService.cs` |
| `despawn.murder` | Procedural objective system: polymorphic `GunTaskDefinition` base; `IsEnabled` (map has zones?), exclusion `Group`, `Make()` → randomized task state | `despawn.murder/Code/Systems/GunAcquisition/Tasks/GunTaskDefinition.cs` |
| `vault108.suspectra` | Voting chat `[Sync] NetList<ChatMsg>` cap 80; bilingual fields; host-side `NormalizeNetworkText` strips CR/LF and length-clamps | `suspectra/Code/GameManager.cs` |

---

### round-match

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `despawn.murder` | Abstract `RoundState : Component` with `Begin/Tick/Finish`; `TransitionTo<T>` with init-callback; RPC mirrors host event to clients for instant local reaction | `despawn.murder/Code/Systems/Rounds/RoundManager.cs` |
| `vidya.terry_games` | 5-state FSM; `[Sync] TimeUntil TimerEnds` (deadline, not countdown) so late-joiners see correct time; gamemode can reset timer in `OnTimerEnd` to pause FSM | `terry_games/Code/Logic/GameSystem.State.cs` |
| `vault108.suspectra` | Paired local+RPC apply pattern: host calls `ApplyXStateLocal` then `[Rpc.Broadcast] RpcApplyXState` for low-latency convergence without waiting for `[Sync]` | `suspectra/Code/GameManager.cs` |
| `aethercore.versus` | Best-of-N FSM; double-KO and timeout-by-HP%; `[Sync] GameObject` for fighters (not private fields) survives host migration | `versus/Code/ArenaManager.cs` |
| `facepunch.ss2` | `GameState { Lobby, Playing }`; single-axis multi-outcome leaderboard encoding; `LEADERBOARD_VERSION` baked into stat name | `ss2/Code/Manager.cs` |
| `goders.natural_disaster_survival` | 5-state host-only machine; `GetTimeRatio()` feeds disaster pacing curves; "first round is longer" via persisted round count | `natural_disaster_survival/Code/globals/RoundManager.cs` |
| `slamdunk.minigolf` | `[Sync(FromHost)]` throughout; "most players done" countdown; late-joiner gets average of completed holes; self-resetting loop without scene reload | `minigolf/Code/RoundManager.cs` |

---

### spawning-waves

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `facepunch.ss2` | `EnemySpawnConfig` with curve-based weight ramp, early-spawn incentive, catch-up bonus, pop-cap reducer, all per-difficulty arrays; hot-reloadable via `[ConCmd]` | `ss2/Code/Manager.Spawning.cs` |
| `facepunch.ss1` | Continuous director with per-type t-gated chance; spawn cadence curve-driven + crowd-aware; self-limiting population valve via `Utils.Map(existing, 0, cap, 1, 0)` | `ss1/Code/Manager.cs` |
| `despawn.murder` | L4D-style clue spawn Director: `base interval × product of N independent multipliers × per-map penalty`; cadence adapts to live telemetry | `despawn.murder/Code/Systems/Rounds/RoundDirector/` |
| `goders.natural_disaster_survival` | Curve-driven per-disaster spawn frequency (`LightningSpawnCurve.Evaluate(timeRatio)`); hard global cap via `ActiveDisasters.Count >= MaxDisasters` | `natural_disaster_survival/Code/disasters/disaster_manager.cs` |
| `enifun.shop_manager` | Demand-scaled customer director: `maxCustomers = stockedShelves × perShelf`; rush-hour multiplier seeded by day; 4% are rich customers | `shop_manager/Code/AI/CustomerSpawner.cs` |
| `namicry.gacha_crawler` | Template-based monster factory with difficulty multiplier, archetype stat bonuses, and per-difficulty wave sizes; `MakeMonsterAdaptive` reverse-solves HP/attack from player's peak stats | `gacha_crawler/Code/Models/MonsterData.cs` |
| `ataco.sdoomresurrection` | Data-driven entity table: `ThingGenerator` maps Doom thing-type IDs to component factories; difficulty filter from thing flags; item respawn loop with randomized 8–300s delay | `sdoomresurrection/Code/doomwad/ThingGenerator.cs` |

---

### anti-cheat

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `enifun.shop_manager` | 4-tier permission ladder (`Visitor<Employee<Manager<CoOwner`); host stores table keyed by SteamId; every interactable re-checks before acting | `shop_manager/Code/Multiplayer/PermissionManager.cs` |
| `lowkeynetworks.newrp` | Server-authoritative-by-construction: all state in static services + non-`[Sync]` server fields; client only sends intent via `[Rpc.Host]`; host re-resolves actor from `Rpc.Caller` | `newrp/Code/modules/` |
| `fluffybagel.chess_otb` | Physical board reconciliation: XOR snapshot bitboards vs current piece positions; legal-move mask comparison distinguishes cheat (superset) from mid-move (subset) | `chess_otb/Code/Game/Gameplay/ChessGameState.Displacement.cs` |
| `lavagame.multis_cases` | Client-side server-legitimacy check: reads `Connection.Host.SteamId` against Supabase whitelist; `BlockSaving = true` + countdown + disconnect on unofficial server | `multis_cases/Code/Game/Security/ServerVerifier.cs` |
| `vault108.suspectra` | Single `CmdUseTarget(typeIndex, targetId)` RPC for all world interactions; host re-derives aim independently; re-checks distance + LOS + facing for kills | `suspectra/Code/PlayerRole.cs` |
| `despawn.murder` | Persisted weighted murderer-ticket selection: non-murderers +1, murderers -playerCount; `IMurdererSelectionStrategy` pluggable; bad-luck protection with `Math.Max(1,tickets)` floor | `despawn.murder/Code/Systems/MurdererTickets/MurdererTicketManager.cs` |
| `suburbianites.blindloaded` | Host-authoritative footsteps via `[Rpc.Host(OwnerOnly)]` — owner triggers, host reads position/sound and broadcasts; rate-limited at `MinHostFootstepInterval` | `blindloaded/Code/Player/Player.cs` |

---

### level-design

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `ataco.sdoomresurrection` | Parse binary WAD format → emit s&box `Mesh` + collision + trace meshes at runtime; UV peg math for arbitrary extruded walls | `sdoomresurrection/Code/doomwad/MapLoader.cs` |
| `mishmaps.backrooms` | 3-state RNG flicker for horror fluorescent lighting; `RealTimeSince` for wall-clock-independent timing; decentralized (one component per fixture, no manager) | `backrooms/Code/LightFlicker.cs` |
| `facepunch.fair` | Buy-land chunks with `INetworkSnapshot` raw ByteStream sync; perimeter-outline wall-follow to place fence props + `LineRenderer` boundary | `fair/Code/Park/BuildingZone.cs` |
| `suburbianites.blindloaded` | Procedural shrinking NxN panel grid spawned at round start; concentric ring + random inner tile telegraph; auto-sizes by player count | `blindloaded/Code/Arena/ArenaManager.cs` |
| `lavagame.multis_cases` | `ITriggerListener` reward pads with `HashSet<Guid>` root-id debounce (fixes multi-collider double-fire); rank-gated wall toggles collider based on `GetRank >= RequiredRank` | `multis_cases/Code/Game/Obby/ObbyRewardButton.cs` |
| `master.digging_simulator` | (procedural level design — see per-game file) | `digging_simulator/Code/` |
| `stepdev.xtrem_road` | (procedural obstacle course — see per-game file) | `xtrem_road/Code/` |

---

### ai-director

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `despawn.murder` | L4D-style clue Director: spawn interval = base × N independent multipliers (heat, pacing, map, lobby-size); adapts to live task/kill telemetry | `despawn.murder/Code/Systems/Rounds/RoundDirector/` |
| `facepunch.ss2` | Per-enemy `EnemySpawnConfig` declarative weight curves; threat-boost adds weight when total on-field threat is low; hot-reloadable `[ConCmd]` | `ss2/Code/Manager.Spawning.cs` |
| `facepunch.fair` | Utility-AI scorer over behavior-tree actions: `ScoreInternal()` + cooldown guards; centralized single-pass `AgentTickSystem` at 0.25s fixed rate | `fair/Code/AI/ActionSystem/AgentActionController.cs` |
| `enifun.shop_manager` | Two-stage customer brain: demand-weighted visit plan, shelf-reservation dict, patience timer in `TotalGameHours`; static FIFO task queue for stocker NPCs | `shop_manager/Code/AI/CustomerAI.cs` |
| `goders.natural_disaster_survival` | `Curve`-driven per-disaster cadence keyed on round-progress ratio; each disaster owns its `[Property] Curve XSpawnCurve` for designer tuning | `natural_disaster_survival/Code/disasters/disaster_manager.cs` |
| `namicry.gacha_crawler` | Adaptive "nemesis" monsters: reverse-solves attack/HP from player's PEAK stats so fight length targets 4–6 hits to die and 8–12 hits to kill | `gacha_crawler/Code/Models/MonsterData.cs` |
| `lavagame.sandmoney_` | World event director: anti-monotony filter blocks 3+ same-direction events; two independent event clocks (market vs personal); "personal" events use blended `cash*0.35 + netWorth*0.015` basis | `sandmoney_/Code/Core/WorldEventManager.cs` |

---

### services-backend

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `namicry.gacha_crawler` | External Laravel/SQL REST via `Http.RequestStringAsync`; load tries server first then local fallback; bearer token in C# (anti-pattern documented as teaching example) | `gacha_crawler/Code/GameManager.cs` (~L198–511) |
| `klibatocorp.phenodex` | JWT cached to `FileSystem.Data`; monotone nonce on every mutating route defeats replay; 401-retry without `throw;`; `EnsureReadyForMutationsAsync` de-dups concurrent callers | `phenodex/Code/Net/Backend.cs` |
| `lavagame.multis_cases` | Supabase REST upsert (`Prefer: resolution=merge-duplicates`); compact JSONB inventory (2–3 char keys); URL+key in `FileSystem.Data` config file; retry circuit-breaker | `multis_cases/Code/Game/Save/SaveCloud.cs` |
| `despawn.murder` | `ApiClient` with exponential-backoff retry; optimistic-update + reconcile + debounced-flush `MurderDataStore`; `WatchProfile/WatchLoadout` return `IDisposable` subscriptions | `despawn.murder/Code/API/ApiClient.cs` |
| `sino.s_sino` | Engine-as-renderer, server-as-truth: all math lives on external Node/WebSocket backend; balance in cents as decimal strings; local file is display cache only | `s_sino/Code/Core/WebSocketManager.cs` |
| `fluffybagel.chess_otb` | Ranked game results shipped to HTTP API; dev settings file lets two editor instances spoof different SteamIDs for local two-player testing | `chess_otb/Code/Game/Services/ChessOtbApi.cs` |

---

## GENRES

### tycoon-idle

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `enifun.shop_manager` | AI shoppers driven by price-elasticity demand model; autonomous NPC cashiers + restockers with shared FIFO task queue | `shop_manager/Code/AI/CustomerAI.cs` |
| `thefancylads.farm_land` | Per-player networked farm plots; 5-stage crop growth with water/soil-quality gates; offline catch-up via shared `simulate` flag | `farm_land/Code/Common/Farming/Crop.cs` |
| `lavagame.sandmoney_` | Procedural OHLC market with layered phases + lookahead buffer; future-peeking AI bots; prestige via Heritage generation reset | `sandmoney_/Code/Core/MarketManager.cs` |
| `klibatocorp.phenodex` | Cultivation tycoon with genetics-breeding meta; all timers are UTC-tick deltas so growth advances correctly across sessions | `phenodex/Code/Plant.cs` |
| `facepunch.fair` | Theme-park sim with utility-AI guests; goal/unlock stat gating; centralized single-pass agent tick system | `fair/Code/AI/AgentTickSystem.cs` |
| `vault77.chop_the_forest` | Defense-in-depth economy: dual-path (local vs backend-confirmed) for every spend action; hard cap `Math.Clamp(Money, 0, 1_500_000_000)` | `chop_the_forest/Code/Player/PlayerProgression.cs` |
| `freddo.scoops` | All balance in a static `Econ.cs` pure-functions file; driveable vans with roaming hot-zones for 3× pay | `scoops/Code/Econ.cs` |

---

### shopkeeper

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `enifun.shop_manager` | Full Supermarket Simulator: price-elasticity demand curve, phone-order async channel, co-op permission tiers | `shop_manager/Code/Economy/PriceManager.cs` |
| `thefancylads.restaurant_dev` | Restaurant tycoon: charge→act→refund discipline on every spend; REST backend for cross-session persistence | `restaurant_dev/Code/Common/BuildMode/BuildModeServer.cs` |
| `artisan.darkrpog` | DarkRP: job-salary payday, ownable auto-shops, car dealer, casino — all "world-item + interactable + host-auth service" | `darkrpog/Code/Items/AutoShop/` |
| `lavagame.multis_cases` | Case-opening shop: walk up to station, get Razor panel; interaction is client-side raycasted | `multis_cases/Code/Game/Stations/` |
| `klibatocorp.phenodex` | ~8 identical boilerplate vendor components each setting a static `IsOpen` bool read by one `Hud.razor` | `phenodex/Code/World/Shop.cs` |

---

### document-sim

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `dimmies.terryspapers` | Papers-Please clone: random applicant generation + cross-reference rules engine + flag-driven life-sim narrative between shifts | `terryspapers/Code/GameHandler.cs` |
| `vault108.suspectra` | Several tasks are office/paperwork minigames (shredder, scanner, virus, documents) in a social-deduction frame | `suspectra/Code/` |

---

### roleplay

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `artisan.darkrpog` | Production-grade DarkRP: per-object interest culling, off-thread write pipeline, audited multi-pool economy, diff-based scene save | `darkrpog/Code/Player/Persistence/PlayerRoleplayStorageRepository.cs` |
| `lowkeynetworks.newrp` | Framework-first: dependency-ordered module kernel, topological sort with cycle detection, 5-phase lifecycle, fail-isolated modules | `newrp/Code/framework/modules/ModuleManager.cs` |

---

### sandbox-voxel

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `dexlab.sandbox-reforged` | Toolgun framework with declarative actions + cookie persistence; contraption-graph walk for duplication; per-player undo with bounded history | `sandbox-reforged/Code/Weapons/ToolGun/ToolMode.cs` |
| `klavs.basebuilder` | Sandbox + build-then-fight: `GameFeaturePolicy` component bolt-on makes plain sandbox a competitive round game | `basebuilder/Code/BaseBuilder/BaseBuilderRoundManager.cs` |

---

### social-hub

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `sino.s_sino` | 13 gambling minigames + idle tycoon floor; engine-as-renderer, all math on external WebSocket backend | `s_sino/Code/Core/WebSocketManager.cs` |
| `enifun.shop_manager` | Drop-in 4-player co-op with role-gated permissions; late-joiner hydration via one explicit `SyncShopStateToClients` RPC bundle | `shop_manager/Code/Multiplayer/` |
| `lowkeynetworks.newrp` | Proximity voice with `Rpc.FilterInclude`; OOC/advert/local channels; nameplates and scoreboard | `newrp/Code/modules/chat/ChatService.cs` |
| `facepunch.jumper` | 32-player shared persistent climb; everyone races the same tower; sideboard shows all heights in real time | `jumper/Code/Player/JumperProgress.cs` |

---

### platformer-obstacle

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `facepunch.jumper` | Charge-and-release jump; no air control once launched; wall-bounce reflects velocity; commitment = genre signature | `jumper/Code/` |
| `yellowletter.terrys_crash_course` | 2.5D speedrun obstacle course; per-level stat leaderboard; medal unlock-gate; friends leaderboard via wide-fetch + client-side filter | `terrys_crash_course/Code/CrashCourse/` |
| `vidya.terry_games` | Floor-is-Lava / Glass-Bridge / Race among many microgames in party-battle-royale shell | `terry_games/Code/Logic/Gamemodes/` |
| `stepdev.xtrem_road` | (obstacle road racing — see per-game file) | `xtrem_road/Code/` |

---

### deathmatch-arena

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `aethercore.versus` | 1v1 souls-like melee: combo cancel windows + input buffer + root-motion attacks + i-frame dodge + directional parry + guard meter | `versus/Code/PlayerController.cs` |
| `ataco.sdoomresurrection` | Full DOOM source port: WAD parser, BSP triangulation, linedef specials, actor state machines, hitscan hitscan with penetration | `sdoomresurrection/Code/entities/DoomMap.cs` |
| `facepunch.ss1` | Top-down twin-stick survivor: spatial-hash broadphase for 350 entities; perk event-bus via 18 virtual `Status` hooks | `ss1/Code/Manager.cs` |
| `khamitech.battledraft` | Multi-gamemode framework: Arena/DM/GunGame/TDM/Survival hot-swappable via plugin without engine restart | `battledraft/Code/Plugin/` |
| `klavs.basebuilder` | Build-then-fight with zombie infection ladder; escalating `ZombieHealthSteps` indexed by death count | `basebuilder/Code/BaseBuilder/BaseBuilderRoundManager.cs` |

---

### card-battler

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `khamitech.battledraft` | Battle-draft shop: player drafts a loadout during buy phase; weapons are `ItemShop` with category→slot→bone-attach mapping | `battledraft/Code/Addons/DMwS/DeathmatchWithShopManager.cs` |
| `namicry.gacha_crawler` | Turn-based auto-resolved dungeon combat: `BattlePhase` state machine, tap-to-skip, ELO-ish async PvP by reusing PvE loop with opponent snapshot as a single monster | `gacha_crawler/Code/GameManager.cs` |

---

### survival-horror

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `mishmaps.backrooms` | Atmosphere primitive: 3-state RNG fluorescent flicker; `RealTimeSince` for timescale-independent cosmetic timing | `backrooms/Code/LightFlicker.cs` |
| `ataco.sdoomresurrection` | Full retro survival FPS from binary WAD; sector brightness as `ModelRenderer.Tint` (no real lights) | `sdoomresurrection/Code/entities/DoomMap.cs` |
| `khamitech.battledraft` | Survival mode: hunger/thirst/radiation buffs, harvesting, crafting with expression DSL recipes | `battledraft/Code/Addons/Survival/` |

---

### gacha-crawler

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `namicry.gacha_crawler` | Deep gacha idle-RPG: roulette spin animation, layered rarity rolls per source, pity flags, async PvP arena, adaptive nemesis monsters | `gacha_crawler/Code/Data/ItemGenerator.cs` |
| `lavagame.multis_cases` | CS2 case-opening hub: EV normalization, wear/float as value modifier, host-authoritative case battles + jackpot with pending-win-until-ACK | `multis_cases/Code/Game/Gambling/CaseBattle.cs` |
| `facepunch.ss2` | Bullet-heaven roguelite: run-based weighted perk draft with synergy gating; persistent meta-shop with `progress.json` | `ss2/Code/PerkManager.cs` |
| `facepunch.ss1` | Survivor bullet-heaven: weighted perk draft with prerequisite-combo gating and "Specialist" bias | `ss1/Code/StatusManager.cs` |

---

### puzzle

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `simalami.15_puzzle_master` | (tile sliding puzzle — see per-game file) | `15_puzzle_master/Code/` |
| `fluffybagel.chess_otb` | 3D OTB chess with physical drag: bitboard displacement anti-cheat; Elo persistence; Arena tournament Swiss pairing | `chess_otb/Code/Game/Gameplay/ChessGameState.Displacement.cs` |
| `itacho.fill_the_void` | (puzzle — see per-game file) | `fill_the_void/Code/` |

---

### vehicles

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `meteorlab.vehicle_tool_example` | Physics-accurate powertrain chain (Engine→Clutch→Transmission→Differential→Wheels); Pacejka tire model; in-editor `CreateCar()` VehicleCreator button | `vehicle_tool_example/Code/Vehicle/VehicleController.cs` |
| `dexlab.sandbox-reforged` | Contraption-graph walk + `IPlayerControllable` seam: any wheel/thruster reads the seated player's input during a `ClientInput.PushScope` | `sandbox-reforged/Code/Game/ControlSystem/ControlSystem.cs` |
| `freddo.scoops` | Driveable ice-cream vans; park to draw customer queue; GPS hot-zone routing for 3× pay; `[Sync]` driver-owned Empire | `scoops/Code/` |
| `vault77.chop_the_forest` | Expedition vehicles for harvesting; host-auth spawn/seat/ownership flow | `chop_the_forest/Code/` |

---

### party-microgame

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `vidya.terry_games` | Party battle-royale shell: 5-state FSM with virtual hooks per minigame; synced `TimeUntil` deadline so late-joiners see correct timer | `terry_games/Code/Logic/GameSystem.State.cs` |
| `vault108.suspectra` | ~25 task minigames in social-deduction wrapper; shared task progress tracked host-side via `CmdUseTarget` dispatcher | `suspectra/Code/` |
| `goders.natural_disaster_survival` | Short timed disaster-survival rounds with curve-driven wave cadence and between-round vote flow | `natural_disaster_survival/Code/globals/RoundManager.cs` |

---

### social-deduction

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `despawn.murder` | TTT-style: hidden roles, L4D Director for clue pacing, persisted murder-ticket bad-luck protection, optimistic-reconcile backend profile | `despawn.murder/Code/Systems/Rounds/RoundManager.cs` |
| `vault108.suspectra` | Among-Us-style: discussion→vote→ejection flow; single `CmdUseTarget` for all world interactions; Procrustes minimap calibration | `suspectra/Code/GameManager.cs` |

---

### survivor-roguelite

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `facepunch.ss2` | 300+ perks via reflection; source-keyed stat modifier stack; persistent meta-shop; single-axis multi-outcome leaderboard encoding | `ss2/Code/Player/Player.Stats.cs` |
| `facepunch.ss1` | Layered priority stat-modifier engine; spatial-hash broadphase for 350 entities; coin-debt throttling for perceived-fair loot | `ss1/Code/things/Player.cs` |

---

### coop-kitchen

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `thefancylads.restaurant_dev` | Overcooked-style cooking: recipe step graph with `CookedStateMask` + dependency; assembly strategy pattern; `PossibleRecipes` narrowed as ingredients added | `restaurant_dev/Code/Common/Cooking/` |

---

### board-game

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `fluffybagel.chess_otb` | 3D over-the-board chess: physical piece drag + bitboard anti-cheat reconciliation; full Elo system + Arena Swiss tournament pairing | `chess_otb/Code/Game/` |

---

### casino-gambling

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `sino.s_sino` | 13 minigames with external authoritative backend; balance in decimal-string cents; WebPanel hosts React UIs for each game | `s_sino/Code/Core/` |
| `lavagame.multis_cases` | Case battles (host re-validates case identity+cost against own DB), jackpot (pending-win-until-ACK), upgrader, trade-up contract | `multis_cases/Code/Game/Gambling/CaseBattle.cs` |
| `lavagame.sandmoney_` | Crypto-sim with weighted world events, personal hack/lottery/tax-audit events; bots read pre-computed `FuturePrices` lookahead | `sandmoney_/Code/Core/WorldEventManager.cs` |
| `artisan.darkrpog` | Casino items (poker/roulette/slots/coinflip); data-driven ROI simulator for balance tuning built into the game itself | `darkrpog/Code/Items/Casino/` |

---

### physics-sports

| Game | One-line approach | Key file to read |
|------|-------------------|-----------------|
| `slamdunk.minigolf` | Rigidbody ball physics golf: difficulty-grouped random course selection; "most players done" countdown; late-joiner gets average score | `minigolf/Code/RoundManager.cs` |
| `alcoholics.nice_putt_idiot` | (physics putt sports — see per-game file) | `nice_putt_idiot/Code/` |
| `barrelproto.ragroll` | (ragdoll physics sports — see per-game file) | `ragroll/Code/` |
| `pldr.duck_pond` | (physics toy — see per-game file) | `duck_pond/Code/` |
