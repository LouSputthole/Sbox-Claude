# Crafting

How to build a crafting system in modern s&box: turn input items into output items via designer-authored recipes. Covers ingredient-assembly cooking, fusion/upgrade tables, and meta-passthrough expression crafting.

## What it is / when you need it

A crafting system consumes one or more **input** items and produces one or more **output** items according to a **recipe**. Across the mined games it shows up in three shapes:

- **Sequential assembly** — add ingredients in a (partial) order, each must be in a valid state; when all required steps match, consume the inputs and spawn the result. (cooking boards, meal prep)
- **Fusion / upgrade** — N items of rarity R → 1 item of rarity R+1 (or a reroll), optionally steered by a consumable ingredient. (gacha fusion)
- **Recipe asset + meta passthrough** — a `GameResource` declares Inputs/Outputs and copies/computes output stats from the consumed inputs. (survival crafting)

If your recipes are pure code `switch` statements you'll repaint them every balance pass. The reusable win is **recipes-as-`GameResource`** + a small matcher, so designers add content by creating asset files.

## Canonical modern approach

### 1. A recipe is a `GameResource`

Define recipes as asset files, not code. Systems enumerate them with `ResourceLibrary.GetAll<T>()` and look up by string `Id`.

```csharp
[AssetType( Name = "Recipe", Extension = "recipe", Category = "Game/Crafting" )]
public class RecipeResource : GameResource
{
    public string Id { get; set; } = default!;
    public ItemResource Output { get; set; } = default!;
    public Dictionary<int, RecipeStep> Steps { get; set; } = new();
    public FurniCategory RequiredAppliances { get; set; } // [Flags] gate
}
```
(thefancylads.restaurant_dev: Code/Common/Objects/Food/RecipeResource.cs:6) — `[AssetType(... Extension="recipe" ...)]`, `Dictionary<int,RecipeStep> Steps`, and a `RequiredAppliances` flags gate.

### 2. Each step matches an ingredient by id + bit-flag state

State (thermal, cut, etc.) is a `[Flags]` mask so one step can accept several states. Build the ingredient's current state into a 1-bit mask and `&` it against what the step accepts.

```csharp
public readonly bool MatchesIngredient( Ingredient ingredient )
{
    var thermalMask = (CookedStateMask)(1 << (int)ingredient.CookedState);
    return (AcceptedStates & thermalMask) != 0
        && (RequiredCutState == null || ingredient.CutState == RequiredCutState);
}
```
(thefancylads.restaurant_dev: Code/Common/Objects/Food/RecipeResource.cs:43) — `1 << (int)state` makes the mask; `MustComeAfterStep` (:34) and `IsRequired` (:38) drive partial ordering and optional steps.

### 3. Complete: consume inputs, spawn output, host-authoritative

When every required step is satisfied, destroy the inputs and `Clone()` the output prefab. All mutation runs host-side (`if (!Networking.IsHost) return;` upstream).

```csharp
public void CompleteMeal( RecipeResource recipe )
{
    var pos = WorldPosition; var rot = WorldRotation;
    foreach ( var ingredient in CurrentIngredients )
        ingredient.GameObject.Destroy();              // consume inputs first

    var instance = recipe.Output.Prefab.Clone();      // then materialize result
    instance.WorldPosition = pos;
    instance.WorldRotation = rot;
}
```
(thefancylads.restaurant_dev: Code/Common/Cooking/MealPreparationComponent.cs:209) — destroy tracked ingredients (:219), `recipe.Meal.Prefab.Clone()` (:232), reposition at the assembly point.

### 4. Fusion variant: validate, consume, generate

For "N items → 1 better item", verify the inputs are homogeneous and not maxed, remove them, then generate. **Guard the generate first** — if you remove inputs and the generate returns null you've vaporized the materials.

```csharp
public GameItem CraftItems( List<GameItem> items, CraftIngredient ingredient = null )
{
    if ( items.Count != 5 ) return null;
    var rarity = items[0].Rarity;
    if ( !items.All( i => i.Rarity == rarity ) ) return null;
    if ( rarity == ItemRarity.Mythic ) return null;          // can't upgrade max

    foreach ( var item in items ) ActiveCharacter.Inventory.Remove( item );
    if ( ingredient != null ) ActiveCharacter.CraftIngredients.Remove( ingredient );

    var newRarity = (ItemRarity)((int)rarity + 1);
    var newItem = ItemGenerator.GenerateCraftedItem(
        (int)items.Average( i => i.Level ), newRarity, ActiveCharacter.Class, ingredient );
    ActiveCharacter.Inventory.Add( newItem );
    return newItem;
}
```
(namicry.gacha_crawler: Code/GameManager.cs:1913) — same-rarity check (:1919), Mythic block (:1920), remove-then-generate (:1923-1937).

### 5. Steer output with a `[Flags]` ingredient-effect enum

An optional consumable biases the result. Map `(ingredientType, tier)` → a `[Flags]` effect the generator reads with `HasFlag`.

```csharp
[Flags] public enum CraftIngredientEffect
{ None = 0, ClassMatch = 1, TierGuarantee = 2, WeaponType = 4, ArmorType = 8,
  JewelryType = 16, SpecificItemType = 32, PropertyReroll = 64 }

CraftIngredientType.Rune => tier switch {
    2 => (CraftIngredientEffect.ClassMatch, 0, null),
    5 => (CraftIngredientEffect.ClassMatch | CraftIngredientEffect.TierGuarantee, 5, null),
    _ => (CraftIngredientEffect.None, 0, null) },
```
(namicry.gacha_crawler: Code/Data/CraftIngredientData.cs:183) — `GetEffectsForTier`; the generator branches on `effects.HasFlag(...)` in Code/Data/ItemGenerator.cs:1210 (`GenerateCraftedItem`).

## Variations seen across games

- **Recipe asset + Inputs/Outputs[] + meta passthrough** — a `CraftAsset : GameResource` rolls weighted outputs and copies/computes output stats from the inputs. `$0` copies input 0's whole meta bag, `$1.durability` pulls one field, and `$0.durability + $1.durability` is variable-substituted then run through a tiny `Expression.Evaluate()`. (khamitech.battledraft: Code/Addons/Survival/Asset/Craft/CraftAsset.cs:73 `GetResult` weighted roll via `Game.Random.FromEnumerableWithChance`, :102 `ParseMetaValue` `$`-refs + expression eval, :148 `ReplaceVariablesWithValues`.)
- **Sequential board / state machine** — instead of a Steps dict, a `BoardState` enum advances (`AwaitBread → AwaitSauce → … → Finish`); two parallel dictionaries map heldItem → expected-state and heldItem → placement-action, gated by a 1s hold-to-place. (luckygaming.doner_kiosk: Code/Game/EntityBoard.cs:66, Code/Player/Player.cs:318 / :96.)
- **Strategy-per-meal matcher** — recipe matching is pluggable: an `IMealPreparationStrategy` (Burger/Pizza) in a static enum-keyed registry decides `CanAddIngredient` / `GetCompletedRecipe` and per-index snap transform. (thefancylads.restaurant_dev: Code/Common/Cooking/MealPreparationStrategy.cs:10.)
- **Reroll variants** — same plumbing, different arity/output: 2 → 1 same rarity, or a Tome ingredient rerolls one item's affixes in place keeping type/rarity/level. (namicry.gacha_crawler: Code/GameManager.cs:1954 `CraftRerollItems`, :1993 `CraftRerollProperties`.)
- **Weighted output roll** — outputs are picked by chance, not deterministically: `Game.Random.FromEnumerableWithChance(Outputs, Count)` (battledraft) or a manual cumulative-weight walk `Random.Next(1,total+1)` (vidya.terry_games: Code/Logic/Gamemodes/RLGL/RedLightGreenLight.cs:301).

## Gotchas

- **Consume order: generate in a copy / guard first.** Fusion removes inputs before generating; a null/failed generate loses the materials. Validate or generate-into-a-temp before removing. (namicry.gacha_crawler: Code/GameManager.cs:1923-1937.)
- **Host authority.** All consume/spawn must be host-only (`if (!Networking.IsHost) return;`); reference recipes/items by string `Id` over RPCs, never the object. (thefancylads.restaurant_dev MealPreparationComponent; khamitech.battledraft AssetManager keys assets by id.)
- **Bitmask state needs `[Flags]` with explicit bit values** and a `1 << (int)state` mask, not `==`, so a step can accept multiple states. (thefancylads.restaurant_dev: RecipeResource.cs:45.)
- **Enum-keyed registries still need code edits.** Recipes-as-assets are data-driven, but the strategy registry and the effect-enum `switch` are not auto-discovered — adding a meal type / ingredient effect means editing the enum + dictionary/switch. (thefancylads.restaurant_dev MealPreparationStrategy.cs; namicry.gacha_crawler CraftIngredientData.cs.)
- **Stringly-typed expression evaluators fail silently.** `ParseMetaValue` returns the raw string on parse failure and forces invariant culture — easy to ship a broken recipe with no error. (khamitech.battledraft: CraftAsset.cs:138 empty `catch`.)
- **`Material.Load`/`Texture.Load`/asset-path strings fail silently** if the path is wrong; config tables built in code (vs assets) hide these until runtime. (clearlyy.s_miner: BlockConfigs.cs.)
- **Recipes that flow through JSON export + snapshot replication** must `[JsonIgnore]` editor-only fields and watch large content sets bloating the join snapshot. (khamitech.battledraft: AssetManager.cs:439 `INetworkSnapshot`.)

## Seen in

- **thefancylads.restaurant_dev (GASTROTOWN)** — recipe-as-`GameResource`, `[Flags]` thermal-state step matching, strategy-per-meal, host-side consume + `Clone()`. `Code/Common/Cooking/`, `Code/Common/Objects/Food/`.
- **namicry.gacha_crawler** — fusion/reroll (5→1, 2→1, affix reroll) steered by a `[Flags]` ingredient-effect enum. `Code/GameManager.cs:1913-2014`, `Code/Data/CraftIngredientData.cs`, `Code/Data/ItemGenerator.cs`.
- **khamitech.battledraft** — `CraftAsset : GameResource` with weighted outputs + `$`-ref meta passthrough + mini expression evaluator. `Code/Addons/Survival/Asset/Craft/CraftAsset.cs`.
- **luckygaming.doner_kiosk** — sequential `BoardState` cooking board with table-driven item→state / item→action dicts + hold-to-place. `Code/Game/EntityBoard.cs`, `Code/Player/Player.cs`.
- **vidya.terry_games** — drop-in weighted-pick loot/output table. `Code/Logic/Gamemodes/RLGL/RedLightGreenLight.cs:301`.
- **clearlyy.s_miner** — code-built data table of block/loot configs (anti-pattern reference: designers edit C#, paths fail silently). `Code/.../BlockConfigs.cs`.

Verify live: confirm `GameResource`, `[AssetType]`, `ResourceLibrary.GetAll<T>()`, `Prefab.Clone()`, and `Game.Random.FromEnumerableWithChance` against the installed SDK with `describe_type` / `search_types` reflection — that is authoritative, not this doc or training data.

See also: **sbox-api** (resolve exact signatures for `GameResource` / `ResourceLibrary` / `Game.Random`) and **sbox-build-feature** (screenshot-driven loop to wire the recipe asset + UI into a running scene).

## Corpus refresh (2026): more reference implementations

### Conveyor-trigger transmutation chain (enum-keyed, no recipe asset)

`stellawisps.lumberyard` skips `GameResource` recipes entirely for a linear pipeline. A `PlankCutter` trigger volume carries a `CutterType` enum (`Plank`/`Paper`/`Money`). On `OnTriggerEnter` it identity-checks ownership, reads the incoming item's current type and `CutType`, and routes to a `SpawnX` method that destroys the input, clones a new `GameObject` with updated components, multiplies value by a data-asset float (`TreeResource.PlankMultiplier`), copies material/tint, and `NetworkSpawn()`s. Balance lives in `.tree` GameResource assets — zero code change to re-tune.

```csharp
// PlankCutter.cs — stellawisps.lumberyard Code/Trees/PlankCutter.cs:21-80
public enum CutterType { Plank, Paper, Money }
[Property] public CutterType Type { get; set; }

protected override void OnTriggerEnter( Collider other )
{
    if ( !Networking.IsHost ) return;
    if ( other.Root.Network.Owner != Root.Network.Owner ) return; // ownership gate

    var wood = other.Root.Components.Get<Wood>();
    if ( wood == null ) return;

    if ( Type == CutterType.Plank && wood.GetComponent<Plank>() == null )
        SpawnPlank( other.Root, wood.Value * wood.Resource.PlankMultiplier );
    else if ( Type == CutterType.Paper && wood.GetComponent<Plank>() is { CutType: CutType.Plank } )
        SpawnPaper( other.Root, wood.Value * wood.Resource.PaperMultiplier );
    // ... SpawnMoney for Paper→Money stage
}
```

(stellawisps.lumberyard: `Code/Trees/PlankCutter.cs:86-215`) — each stage is a separate world trigger; resources flow through space as physics rigidbodies, not abstract counters. Anti-pattern avoided: **do not use recipe assets for a fixed linear pipeline**; an enum + trigger is simpler and allows physics-based "miss the conveyor = lose the resource" gameplay.

### Timed offline-safe converter (DateTime-stamped completion)

`thefancylads.farm_land` shows the correct pattern for a crafting station that should still complete while the game is closed. The `Composter` stamps `TimeFinished` as a real `DateTime` (not a countdown `TimeSince`) and its `[Rpc.Owner]` `OnUpdate` checks `IsComposting && TimeFinished < DateTime.Now`. Speed is modified by a buff key at schedule-time so the finish time accounts for the player's current upgrades when the craft starts, not when it checks.

```csharp
// Composter.cs — thefancylads.farm_land Common/Farming/Composter.cs
void BeginComposting()
{
    float speed = BuffManager.Instance.GetModifier( "farming.compost.speed" );
    TimeFinished = DateTime.Now + TimeSpan.FromSeconds( BaseDuration / AverageSpeed / speed );
    IsComposting = true;
}

[Rpc.Owner]
protected override void OnUpdate()
{
    if ( IsComposting && TimeFinished < DateTime.Now )
        FinishComposting(); // yields fertiliser, clears IsComposting
}
```

(thefancylads.farm_land: `Common/Farming/Composter.cs`) — persists `TimeFinished` as a `DateTime` string so offline completion is free. **Anti-pattern: using `TimeSince` or a float countdown for a crafting timer** — `TimeSince` resets to zero on load and the craft never completes after a session restart. Always stamp the absolute finish time.

### Timed slot-growth as crafting-over-time

`bublic.stone_by_stone` implements crafting-over-time as a `GardenComponent` with per-slot state. Each `PlantSlot` tracks `GrowthElapsed`/`GrowDuration`/`Progress`; `OnUpdate` advances `GrowthElapsed += Time.Delta` and lerps `WorldScale` from `MinVisualScale*target → target` so the output visually manifests. Harvest clones the slot's `HarvestItem ×HarvestCount` into the bag, fires quest progress, then **cycles the slot** (round-robin or random). Upgrading calls `ReduceGrowDuration` across all slots. This is architecturally identical to a smelter/kiln — a "slot" is a running recipe whose output appears after a duration.

```csharp
// GardenComponent.cs — bublic.stone_by_stone Code/GardenComponent.cs
protected override void OnUpdate()
{
    foreach ( var slot in Slots.Where( s => s.IsOccupied && !s.CanHarvest ) )
    {
        slot.GrowthElapsed += Time.Delta;
        slot.Progress = slot.GrowthElapsed / slot.GrowDuration; // 0→1
        var t = MathX.Clamp( slot.Progress, 0f, 1f );
        slot.PlantModel.WorldScale = Vector3.Lerp(
            slot.MinVisualScale * slot.TargetScale, slot.TargetScale, t );
        if ( t >= 1f ) slot.CanHarvest = true;
    }
}
```

(bublic.stone_by_stone: `Code/GardenComponent.cs`) — the slot array is the "queue"; no recipe asset needed when every slot produces the same item. Reuse for kilns, furnaces, or any station with multiple parallel slots.

### Sell terminal as implicit item-to-currency conversion

`master.digging_simulator` shows the simplest possible "refine" primitive: a `ShopTerminal` with a `[Property] Dictionary<string,int> OrePrices` keyed by item name. `SellAll` iterates the bag, looks up price by name, sums, clears. No recipe asset, no output item — the terminal *is* the crafting step (ore → gold). For survival/mining games where every gathered resource converts to a flat currency value, this beats a full recipe system.

```csharp
// ShopTerminal.cs — master.digging_simulator Code/ShopTerminal.cs:33,142
[Property] public Dictionary<string, int> OrePrices { get; set; } = new();

public void SellAll( PlayerInventory bag )
{
    int total = 0;
    foreach ( var (name, qty) in bag.Items )
        if ( OrePrices.TryGetValue( name, out int price ) )
            total += price * qty;
    bag.Clear();
    bag.Owner.AddMoney( total );
}
```

(master.digging_simulator: `Code/ShopTerminal.cs:142`) — balance lives in the inspector dictionary; designers set prices without touching code. Pair with a `[Button]`-wired `RestoreLevels` for the load path.

### Anti-patterns flagged in these games

- **`master.digging_simulator` uses `MathF.Pow`** in `GetCost` (`ShopTerminal.cs:49`). `System.MathF` does not exist in s&box's C# sandbox — replace with `MathX` or a manual `float pow = 1f; for(int i=0;i<level;i++) pow *= mult;` loop.
- **Conveyor ownership gate must be host-side.** `PlankCutter` gates on `!Networking.IsHost` before doing anything. Skipping this causes every client to destroy the resource and spawn a duplicate output. (stellawisps.lumberyard: `PlankCutter.cs:21`.)
- **`DateTime.Now` offline timers require the save to store the `DateTime`, not a remaining duration.** Storing a remaining float and subtracting session length is a second integration that drifts; store `TimeFinished` directly. (thefancylads.farm_land: `Composter.cs`.)

### Read these games

- **stellawisps.lumberyard** — conveyor-trigger transmutation chain, physics-rigidbody resource economy, Branch.Refine fuse-and-multiply. `Code/Trees/PlankCutter.cs`, `Code/Trees/Branch.cs`.
- **thefancylads.farm_land** — timed offline-safe converter (Composter), buff-speed-modified craft duration, event-interface bus for output reactions. `Common/Farming/Composter.cs`, `Common/Players/Buffs/BuffManager.cs`.
- **bublic.stone_by_stone** — timed slot-growth as crafting-over-time, `ReduceGrowDuration` upgrade hook, quest-progress on harvest. `Code/GardenComponent.cs`.
- **master.digging_simulator** — sell-terminal as implicit item→currency conversion, `[Property] Dictionary<string,int>` price table. `Code/ShopTerminal.cs`.
