# Card-Battler Recipe (deckbuilder / TCG / auto-battler)

Build a turn-based card game in modern s&box: a deck of data-authored cards, a hand/board UI, an authoritative turn loop, and resolved card effects (damage / heal / draw / buff). Two players or player-vs-AI.

## Honesty note on the source corpus

The only mined "card-battler" entry is **khamitech.battledraft**, and its own summary flags it as **NOT a card/draft battler** — it is a mature multiplayer FPS *framework* (battledraft: Code/Asset/Asset.cs:6). So treat this recipe as an **adaptation**: battledraft contributes the genre-agnostic spine a card game actually needs — a data-driven `GameResource` content pipeline, a host-authoritative RPC mutation convention, a round/turn state machine, self-seeding JSON config, and a meta/expression-driven "recipe → result" evaluator that maps almost 1:1 onto card effects. The card-specific glue (hand, board, mana, targeting) is composed from those plus the shared cookbook systems below. Cite-and-adapt, don't lift a "card game" that isn't there.

## What DEFINES the genre + the core loop

A card-battler is a **turn-structured contest over a shared, replicated game state**, where the *atoms of action are data-authored cards* rather than real-time inputs. The defining traits:

- **Cards are content, not code.** Each card is a designer-authored asset (cost, type, effect spec) reconstructed at runtime — the same role battledraft's `Asset : GameResource` plays for weapons/recipes (battledraft: Code/Asset/Asset.cs:7).
- **Strict turn ownership.** Only the active player may act, and only the *host* may mutate truth. Everything else is a request.
- **Deterministic effect resolution.** Playing a card runs an effect against the board (deal N, draw N, buff X), exactly the "inputs → outputs with value passthrough" shape of battledraft's craft system (battledraft: Code/Addons/Survival/Asset/Craft/CraftAsset.cs:73).

**Core loop:** Mulligan/draw opening hand → *(active player)* gain resource (mana/energy) → play cards from hand (cost-gated) → resolve effects on the board/opponent → check win → pass turn → repeat until a life total hits 0 or a deck-out. This is a `Round/Match` state machine with an extra inner **per-turn** phase — see `references/systems/round-match.md` for the phase/timer skeleton; the card loop just nests inside the `Round` phase.

## The system stack to compose

| Layer | What it does for a card game | Reference / source pattern |
|---|---|---|
| **Card definitions (data)** | `CardDef : GameResource` — cost, type, art, effect list. Author in editor, look up by id at runtime. | `references/systems/save-persistence.md` (GameResource pattern); battledraft Code/Asset/Asset.cs:7 |
| **Turn / match flow** | Phase FSM (Mulligan→Turn→Resolve→GameOver) + active-player ownership + turn timer. | `references/systems/round-match.md`; battledraft GunGameManager.cs:19 ([Sync] RoundExpires/IsPlayMode) |
| **Card-play RPC pipeline** | Client asks to play card X at target Y; host validates turn+cost+legality, mutates, fans result to all. | battledraft GunGameManager.cs:171 (host-authoritative broadcast idiom) |
| **Effect resolver** | Turn a card's effect spec into board mutations (damage/heal/draw/buff), with value passthrough. | `references/systems/crafting.md`; battledraft CraftAsset.cs:73 (`GetResult`) |
| **Deck / hand / discard zones** | Per-player ordered lists (`Deck`, `Hand`, `Board`, `Graveyard`); draw/shuffle; reveal-to-owner only. | `references/systems/inventory.md` (zone lists + owner-scoped reveal) |
| **Resource (mana/energy)** | Per-turn refill, spend-gate on play. | `references/systems/economy-currency.md` |
| **Card / board UI** | Razor hand fan, drag-to-play, targeting arrow, life/mana HUD. | `references/systems/inventory.md` (grid/drag UI is the closest analog) |
| **Win / scoreboard** | Life totals, match result, late-joiner snapshot. | `references/systems/leaderboards-services.md`; battledraft GunGameManager.cs:289 (snapshot) |
| **AI opponent (optional)** | Greedy "play the best affordable card" bot on the host. | `references/systems/spawning-waves.md` (host-side actor ticking) |
| **Config / balance** | Starting life, hand size, deck rules in self-seeding JSON. | `references/systems/save-persistence.md`; battledraft FileManager.cs:11 |

## Build order

1. **Card data first.** Define `CardDef : GameResource` and author 5–10 cards. Get a registry that resolves id→def. Nothing networked yet.
2. **Match manager + turn FSM.** One host-ticked `Component` holding `[Sync(SyncFlags.FromHost)] ActivePlayerId` and a turn timer. Pass-turn works with zero cards.
3. **Zones + draw.** Per-player `Deck/Hand/Board` lists on the host; draw N at turn start; reveal hand to its owner only.
4. **Play-card RPC.** `[Rpc.Host]` request → validate (your turn? enough mana? card in hand?) → move card to board / resolve → broadcast.
5. **Effect resolver.** Wire the simplest effects (deal damage, draw, gain life). Check win after each resolution.
6. **UI.** Razor hand panel + drag-to-play + targeting + life/mana HUD. This is where screenshots matter — iterate with `take_screenshot`.
7. **Polish:** mulligan, AI bot, config-driven balance, match result screen.

## How the source actually does it (adapt to cards)

### 1. Cards as `GameResource` content (not hardcoded classes)

battledraft authors *every* weapon/recipe as a `GameResource` with a dotted `Type`, an `ID`, and an effect blob, then reconstructs by id at load (battledraft: Code/Asset/Asset.cs:7-52, `GetFirstType` :95). Display text is auto-derived from the id (`{type}_{id}_name`) so content is localizable with zero per-card code (battledraft: Code/Asset/Asset.cs:127). Mirror that for cards:

```csharp
[GameResource( "Card", "card", "A playable card." )]
public sealed class CardDef : GameResource
{
    public string Id { get; set; } = "strike";   // stable key, like Asset.ID
    public string Type { get; set; } = "spell";   // "spell" | "minion" | "buff"
    public int Cost { get; set; } = 1;            // mana to play
    public int Attack { get; set; }               // for minions
    public int Health { get; set; }
    public List<CardEffect> Effects { get; set; } = new();   // resolved on play
    public Model Art { get; set; }
}
```

Build the registry once on load (host + client) — battledraft keys by a hashed id into a dictionary (battledraft: Code/Managers/AssetManager.cs:27). Modern, simpler:

```csharp
public static class CardLibrary
{
    static Dictionary<string, CardDef> _byId;
    public static CardDef Get( string id ) => (_byId ??= Build())[id];
    static Dictionary<string,CardDef> Build() =>
        ResourceLibrary.GetAll<CardDef>().ToDictionary( c => c.Id );
}
```

> Why `GameResource`, not a plain class: designers (or a non-coder via the bridge) author cards in the editor inspector, and the same assets are visible host- and client-side, so a card id resolves identically on both ends. Verify the attribute shape with `describe_type GameResource` — the ctor args differ across SDK builds.

### 2. Authoritative turn flow: host owns truth, clients read a synced field

battledraft's round state is a `[Sync(SyncFlags.FromHost)]` field whose **setter is the transition hook**, polled in the host's update loop (battledraft: Code/Addons/GunGame/GunGameManager.cs:19-41 RoundExpires/IsPlayMode; OnUpdate poll at GunGame.cs:327). `FromHost` means clients physically cannot write it. Apply that to whose turn it is:

```csharp
public sealed class MatchManager : Component
{
    [Sync( SyncFlags.FromHost )] public Guid ActivePlayerId { get; set; }
    [Sync( SyncFlags.FromHost )] public TimeUntil TurnExpires { get; set; }
    [Sync( SyncFlags.FromHost )] public int Turn { get; set; }

    protected override void OnUpdate()             // host-authoritative tick
    {
        if ( !Networking.IsHost ) return;          // clients only read the synced fields
        if ( TurnExpires ) EndTurn();              // timer ran out → auto-pass
    }

    void EndTurn()
    {
        ActivePlayerId = NextPlayer();
        TurnExpires = TurnSeconds;
        Turn++;
        StartOfTurn( ActivePlayerId );             // refill mana, draw a card
    }
}
```

battledraft compares `RoundExpires` against wall-clock `DateTime.UtcNow`; modern s&box `TimeUntil`/`TimeSince` is the cleaner heartbeat (see `references/systems/round-match.md`). Turn timing is fine off a synced value — it does **not** need tick accuracy.

### 3. Playing a card: request → host-validate → broadcast (the core net idiom)

The single most-reused battledraft convention: a client *asks* via `[Rpc.Host]`, the host **re-validates the caller**, mutates host-side truth, then fans the result out — and to avoid double-applying its own broadcast, the host excludes itself and applies locally (battledraft: Code/Addons/GunGame/GunGameManager.cs:171-186, `using (Rpc.FilterExclude(Connection.Host))`). The storage interactable validates the *caller's identity* on every mutation the same way (battledraft: Code/Addons/Arena/Interactables/Storage.cs:136). For cards:

```csharp
[Rpc.Host]
public void RequestPlayCard( string cardId, Guid targetId )
{
    var caller = Rpc.Caller;                              // who actually sent it
    if ( PlayerIdOf( caller ) != ActivePlayerId ) return; // not your turn
    var hand = HandOf( caller );
    if ( !hand.Contains( cardId ) ) return;               // not in your hand (anti-cheat)
    var def = CardLibrary.Get( cardId );
    if ( ManaOf( caller ) < def.Cost ) return;            // can't afford

    SpendMana( caller, def.Cost );
    hand.Remove( cardId );
    ResolveEffects( def, caller, targetId );              // §4
    BroadcastBoardState();                                // fan truth to everyone
    CheckWin();
}
```

Validate **every** field on the host — never trust the client's "I have mana / this card is in my hand." This is the `anti-cheat` posture; see `references/systems/anti-cheat.md`. Reveal a player's hand only to its owner, the way battledraft sends a container's contents only to the current opener via `Rpc.FilterInclude(owner)` (battledraft: Code/Addons/Arena/Interactables/Storage.cs:401):

```csharp
void RevealHand( Connection owner, string[] cards )
{
    using ( Rpc.FilterInclude( owner ) ) ReceiveHand( cards );   // opponent never sees it
}
[Rpc.Broadcast] void ReceiveHand( string[] cards ) => LocalHand = cards;
```

### 4. Resolving card effects (the crafting evaluator, repurposed)

battledraft's `CraftAsset.GetResult` is a clean "inputs → outputs, with value passthrough" engine: it rolls weighted outputs, generates result data, and resolves output fields from input fields — `$0` copies item 0, `$1.durability` pulls a field, and a full expression like `$0.durability + $1.durability` runs through a tiny evaluator (battledraft: Code/Addons/Survival/Asset/Craft/CraftAsset.cs:73 `GetResult`, :102 `ParseMetaValue`). A card effect is the same shape — a small, data-authored op applied to a target — minus the stringly-typed expression mini-language (skip it; it silently returns the raw string on parse failure — battledraft CraftAsset.cs:~140). Prefer a typed effect enum:

```csharp
public struct CardEffect { public EffectOp Op; public int Amount; public TargetKind Target; }
public enum EffectOp { Damage, Heal, Draw, GainMana, BuffAttack }

void ResolveEffects( CardDef def, Connection caster, Guid targetId )
{
    foreach ( var e in def.Effects )
    {
        var who = Resolve( e.Target, caster, targetId );
        switch ( e.Op )
        {
            case EffectOp.Damage:    DamagePlayer( who, e.Amount ); break;
            case EffectOp.Heal:      HealPlayer( who, e.Amount );   break;
            case EffectOp.Draw:      Draw( caster, e.Amount );      break;
            case EffectOp.GainMana:  AddMana( caster, e.Amount );   break;
            case EffectOp.BuffAttack:BuffBoard( who, e.Amount );    break;
        }
    }
}
```

Keep effects pure host-side; UI reads the resulting synced board. For weighted/random outputs (a "draw a random card" mechanic), battledraft uses a `FromEnumerableWithChance` weighted roll (battledraft CraftAsset.cs:77) — replicate with `Game.Random` and broadcast the chosen result so all clients agree (never let two machines roll independently).

### 5. Win check + result, with late-joiner snapshot

After every resolution, test life totals / deck-out and transition the FSM to `GameOver`. battledraft mutates scores only on the host via a `HostOnly` broadcast and additionally writes scores into the join snapshot (`INetworkSnapshot`) so late-joiners get the current board (battledraft: Code/Addons/GunGame/GunGameManager.cs:131, snapshot at :289). For a 1v1 you usually don't need snapshots, but if you support spectators, write the board into one. Match result + history → `references/systems/leaderboards-services.md`.

### 6. Self-seeding balance config

Don't hardcode starting life / hand size / deck limits. battledraft's `ReadOrWriteJsonFile<T>(path, default)` writes its own default when the file is missing and returns it, loaded once on the host (battledraft: Code/Managers/FileManager.cs:11; loaded in `OnAwake` when `IsHost` at GunGameManager.cs:63). Same pattern via modern `FileSystem.Data` → see `references/systems/save-persistence.md`.

## Modern-API gotchas (do / don't)

- **Do** use `GameObject`/`Component`/`Scene`, `[Sync]`, `[Rpc.Host]`/`[Rpc.Broadcast]`, `Scene.Trace` for any board-pick raycast. **Don't** use legacy `Entity`/`Pawn`/`[Net]`/`RootPanel` — battledraft predates some of this and uses a custom `ComponentNetwork` base + a global static event bus (battledraft: GunGameManager.cs:5, EventManager.cs); on a fresh project you don't need either — a plain `Component` with `[Sync]` fields is enough.
- **Host validates everything.** The genre's whole integrity is "the client can't lie about its turn, mana, or hand." Mirror battledraft's per-mutation caller re-validation (Storage.cs:136), never the client's word.
- **One source of randomness.** Roll on the host, broadcast the outcome — independent client rolls desync the board.
- **Reveal-scoped data.** Hands are private; use `Rpc.FilterInclude(owner)` (battledraft Storage.cs:401), don't `[Sync]` a hand list to everyone.
- **Static event buses leak.** If you copy battledraft's static-`Action` bus, you *must* unsubscribe on teardown or it leaks across hotloads (battledraft: EventManager.cs UnlinkDelegates at :107). On a new card game, prefer instance `Scene.RunEvent<T>` or direct component refs and skip the static bus entirely.

## Verify live

Reflection is authoritative for the installed SDK — confirm the real shapes before coding: `describe_type GameResource` (constructor args vary), `describe_type Sandbox.Connection` (Rpc.Caller / FilterInclude / FilterExclude), and `search_types Rpc` / `search_types Sync` for the current attribute surface. Then `search_types TimeUntil` for the turn-timer heartbeat.

See also: **sbox-api** (reflection-first type/method lookup before you write code) and **sbox-build-feature** (the screenshot-driven build/iterate loop — essential for the hand/board UI).

## Corpus refresh (2026): more reference implementations

Four games added in the 2026 mining pass — facepunch.ss2, despawn.murder, facepunch.fair, barrelproto.ragroll — do not implement card-battler mechanics directly. The techniques below are genuinely net-new vs the existing file and are directly composable into a card game.

### A. Reflection-driven draft pool (facepunch.ss2 — `PerkManager.cs`, `Player.Perks.cs`)

facepunch.ss2 is a bullet-heaven roguelite, but its **level-up perk draft** is structurally identical to "draw N cards to offer, player picks one": a pool discovered at boot by `TypeLibrary.GetTypes<Perk>()`, filtered by per-card attribute gates, then weighted-reservoir-sampled without replacement. This is the missing "draft offer" layer for a card game:

```csharp
// ss2: perks/Perk.cs + PerkManager.cs (adapted — replace Perk with CardDef)
[AttributeUsage(AttributeTargets.Class)]
public sealed class CardAttribute : Attribute
{
    public Rarity Rarity;
    public bool AvailableFromStart;
    public CardAttribute( Rarity rarity, bool availableFromStart = true )
        { Rarity = rarity; AvailableFromStart = availableFromStart; }
}

// Build pool once at match start — adding a new card file auto-includes it:
static IReadOnlyList<TypeDescription> BuildPool() =>
    TypeLibrary.GetTypes<CardDef>()
               .Where( t => t.GetAttribute<CardAttribute>() is { AvailableFromStart: true } )
               .ToList();

// Weighted-reservoir draw of N offers WITHOUT replacement (ss2 pattern):
static List<TypeDescription> DraftOffers( IReadOnlyList<TypeDescription> pool,
                                           int n, Random rng )
{
    var result = new List<TypeDescription>();
    var weights = pool.Select( GetRarityWeight ).ToList();
    while ( result.Count < n && weights.Any( w => w > 0 ) )
    {
        float total = weights.Sum();
        float roll  = rng.NextFloat() * total;
        float acc   = 0;
        for ( int i = 0; i < pool.Count; i++ )
        {
            acc += weights[i];
            if ( roll <= acc ) { result.Add( pool[i] ); weights[i] = 0; break; }
        }
    }
    return result;
}
static float GetRarityWeight( TypeDescription t ) =>
    t.GetAttribute<CardAttribute>()?.Rarity switch
        { Rarity.Common => 425, Rarity.Uncommon => 85, Rarity.Rare => 22, _ => 3 };
```

Anti-pattern in ss2: the pool is built host-side and the offered indices are broadcast, but perk *display metadata* (name, icon, description) lives in per-class static ctors that only fire when that type is actually instantiated client-side. If you broadcast `cardId` strings to clients who haven't instantiated the `CardDef`, the display dict is empty. ss2 fixes this with `Perk.EnsureRegistered(type)` — it does `TypeLibrary.GetType(type).Create<Perk>()` once solely to trigger the static ctor. For a `GameResource`-based card game you avoid the problem entirely: `ResourceLibrary.GetAll<CardDef>()` loads all assets on both sides at boot, so there is no "static ctor hasn't fired" hole.

### B. Draft-offer sync with `[Rpc.FilterInclude]` + `PerkChoiceHash` (facepunch.ss2)

The existing file already covers `Rpc.FilterInclude` for hand privacy. ss2 adds a complementary trick: **hash-counter UI reactivity**. A `PerkChoiceHash` int is incremented every time the draft offer list mutates; the Razor panel's `BuildHash()` returns it. This means the panel re-renders exactly once per offer-list change, never per-frame. Adapt for a card-play or draft-offer panel:

```csharp
// MatchManager addition — cheap dirty flag for UI
[Sync( SyncFlags.FromHost )] public int DraftHash { get; private set; }

void SetDraftOffers( Guid playerId, List<string> cardIds )
{
    _draftOffers[playerId] = cardIds;
    DraftHash++;                      // panel rebuilds exactly here, not every frame
    using ( Rpc.FilterInclude( ConnectionOf( playerId ) ) )
        ReceiveDraftOffers( cardIds.ToArray() );
}
// Razor panel:
// protected override int BuildHash() => HashCode.Combine( MatchManager.Instance.DraftHash );
```

### C. `INetworkSnapshot` for late-join board state (khamitech.battledraft — `Manager.cs:42`)

The existing file uses `[Sync]` fields for everything. battledraft adds a **bulk-state-transfer** path for late-joiners via `INetworkSnapshot { OnWrite(ref ByteStream); OnRead(ref ByteStream); }`. Each manager that implements the interface gets a `SnapshotComponent` wired at map load; the new client deserializes the whole board in one blob instead of waiting for incremental `[Sync]` catchup. For a card game this matters if a spectator joins mid-match and needs the complete board instantly:

```csharp
public sealed class MatchManager : Component, INetworkSnapshot   // battledraft Manager.cs:42 pattern
{
    // Called once when a new client connects:
    public void OnWrite( ref ByteStream s )
    {
        s.Write( ActivePlayerId );
        s.Write( Turn );
        s.Write( (byte)_boardCards.Count );
        foreach ( var (pid, cards) in _boardCards )
        {
            s.Write( pid );
            s.Write( (byte)cards.Count );
            foreach ( var id in cards ) s.Write( id );
        }
    }
    public void OnRead( ref ByteStream s )
    {
        ActivePlayerId = s.Read<Guid>();
        Turn           = s.Read<int>();
        int pCount     = s.Read<byte>();
        _boardCards.Clear();
        for ( int i = 0; i < pCount; i++ )
        {
            var pid = s.Read<Guid>();
            int n   = s.Read<byte>();
            var cards = new List<string>();
            for ( int j = 0; j < n; j++ ) cards.Add( s.Read<string>() );
            _boardCards[pid] = cards;
        }
    }
}
```

Anti-pattern in battledraft: `ItemShop` and `JsonConfiguration` distinguish `[JsonIgnoreNetwork]` fields (rich disk schema) from the slim wire format. Keep the same discipline — don't include display fields (art path, localised name) in the snapshot stream; the client already has them via `GameResource`.

### D. Disk-vs-wire schema split (`[JsonIgnoreNetwork]`) (khamitech.battledraft — `ItemShop.cs:115`, `JsonConfiguration.cs:64`)

battledraft serialises the full balance config to `Servers/<dir>/Configurations/*.json` for server-owner hand-editing, but networks **only** the 2 fields the client actually needs. Use the same split for card-game config:

```csharp
// Only BalanceConfig fields without [JsonIgnoreNetwork] go over the wire.
public class BalanceConfig
{
    public int StartingLife        { get; set; } = 20;
    public int HandSize            { get; set; } = 5;
    public int TurnMana            { get; set; } = 1;   // networked — clients need to show HUD
    [JsonIgnoreNetwork] public string DesignerNotes { get; set; } = "";   // disk only
    [JsonIgnoreNetwork] public List<string> BannedCards { get; set; } = new();
}
```

Note: `[JsonIgnoreNetwork]` is a battledraft custom attribute on their `JsonConfiguration`, not a built-in s&box attribute. You replicate the intent by hand-rolling the `OnWrite`/`OnRead` in the snapshot to skip those fields, or by maintaining a separate, smaller DTO for the wire.

### E. Stat-modifier engine for card buffs (facepunch.ss2 — `Player.Stats.cs`)

The existing file uses a plain `BuffAttack` effect op. ss2 shows a more composable spine: a `Dictionary<IStatModifier, Dictionary<PlayerStat, ModifierData>>` where each buff source (a played card) owns its own slot. Removal by source (when the card leaves the board) is O(1) and never leaves stale values. The `ModifierType` enum — `Set`, `Add`, `Mult` — resolves in priority order so +3 attack (`Add`) and ×2 multiplier (`Mult`) compose correctly:

```csharp
public enum ModifierType { Set, Add, Mult }
public record ModifierData( float Value, ModifierType Type, float Priority = 0 );

// On the game board — host only:
Dictionary<IStatModifier, Dictionary<CardStat, ModifierData>> _mods = new();

public void Modify( IStatModifier source, CardStat stat, float value, ModifierType type )
{
    if ( !_mods.TryGetValue( source, out var d ) ) _mods[source] = d = new();
    d[stat] = new( value, type );
}
public void RemoveModifiers( IStatModifier source ) => _mods.Remove( source );

public float GetStat( CardStat stat, float baseVal )
{
    float v = baseVal;
    foreach ( var (_, d) in _mods.OrderBy( x => 0 ) )    // Set first
        if ( d.TryGetValue( stat, out var m ) && m.Type == ModifierType.Set ) v = m.Value;
    foreach ( var (_, d) in _mods )
        if ( d.TryGetValue( stat, out var m ) && m.Type == ModifierType.Add ) v += m.Value;
    foreach ( var (_, d) in _mods )
        if ( d.TryGetValue( stat, out var m ) && m.Type == ModifierType.Mult ) v *= m.Value;
    return v;
}
```

ss2 also keeps a **whitelist** of stats that actually sync to proxy clients (`_syncedStats`). For cards this means: only sync the stats the hand-panel HUD needs (attack, health); leave internal combat intermediates host-only.

### F. Vote-to-next-round / map-vote as draft selection (khamitech.battledraft — `VoteMapSystem.cs`)

battledraft's map-vote FSM is a clean, general-purpose "N players choose from M options, resolve by majority-live or plurality-at-timeout" primitive. It reuses almost 1:1 as a **"choose next draft pick" or "vote on a mulligan"** mechanic:

```csharp
// battledraft VoteMapSystem.cs pattern — generalised:
public sealed class VoteSystem : Component
{
    readonly Dictionary<Guid, int> _votes = new();
    TimeSince _opened;
    const float TimeoutSec = 15f;

    [Rpc.Host] public void SendVote( int index )
    {
        _votes[PlayerIdOf( Rpc.Caller )] = index;
        if ( MajorityReached() ) Resolve( majority: true );
    }

    protected override void OnUpdate()
    {
        if ( !Networking.IsHost ) return;
        if ( _votes.Count > 0 && _opened > TimeoutSec ) Resolve( majority: false );
    }

    void Resolve( bool majority )
    {
        // plurality: pick the most-voted index
        var winner = _votes.GroupBy( kv => kv.Value )
                           .OrderByDescending( g => g.Count() ).First().Key;
        OnResolved?.Invoke( winner );
    }

    public Action<int> OnResolved;
    bool MajorityReached() =>
        _votes.GroupBy( kv => kv.Value ).Any( g => g.Count() * 2 > _votes.Count + PlayerCount );
    int PlayerCount => 2;   // replace with real count
}
```

Anti-pattern in battledraft: the debounce uses `GameTimer.InvokeOnce("check_votes", 2f)` — a named global timer that can be overwritten by concurrent callers. Prefer `TimeSince` (shown above) to avoid the name-collision footgun.

---

### Read these games

For a card-draft battler, the highest-value source games in order of relevance:

| Game | File | What to mine |
|---|---|---|
| **khamitech.battledraft** | `sbox-lessons/mining-v2/games/khamitech.battledraft.md` | Host-auth RPC mutation convention, `GameResource` asset pipeline, round/turn FSM (`RoundExpires`/`IsPlayMode`), `INetworkSnapshot` late-join, `CraftAsset.GetResult` effect evaluator, `FilterExclude(Host)` idiom, `Rpc.FilterInclude(owner)` for hand privacy |
| **facepunch.ss2** | `sbox-lessons/mining-v2/games/facepunch.ss2.md` | Reflection-driven draft pool (`TypeLibrary.GetTypes<Perk>()`), weighted-reservoir draw, rarity weights, synergy/prerequisite gate, stat-modifier engine (Set/Add/Mult), hash-counter UI reactivity (`PerkChoiceHash`) |
| **despawn.murder** | `sbox-lessons/mining-v2/games/despawn.murder.md` | `RoundState` base class with `Begin/Tick/Finish` virtuals + `TimeUntil TimeLeft`; `[Rpc.Host] PurchaseHost` host-authoritative buy with ConVar-priced items; per-recipient reveal via ghost clones (`Rpc.FilterInclude`) |
| **facepunch.fair** | `sbox-lessons/mining-v2/games/facepunch.fair.md` | `INetworkSnapshot` for `ByteStream` bulk-state (owned-chunk set); reason-tagged economy transactions; `ISaveDataProperty` versioned persistence |
| **barrelproto.ragroll** | `sbox-lessons/mining-v2/games/barrelproto.ragroll.md` | `IGameMode` swappable-interface pattern; owner-gated score write (`[Sync]` setter early-returns unless `Network.IsOwner`); `VoteSystem`-style debounced majority/plurality resolution |
