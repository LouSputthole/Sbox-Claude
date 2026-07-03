# Dialogue & Speech-Bubble Systems

Showing NPC lines, branch choices, or confirm prompts to a player — author the text as data, trigger on proximity/interaction, then reveal it with a `PanelComponent` (often a typewriter). Use this whenever you need talking NPCs, story beats, tutorial barks, or a yes/no modal.

## What it IS (and when)

A dialogue system is three decoupled layers, and every mined game splits them the same way:

1. **Data** — lines/voice/name authored as a `GameResource` asset so designers edit `.npct` files with no recompile (repo facepunch.jumper: `jumper/Code/FunStuff/JumperNPCYapper.cs:1`).
2. **Trigger** — *when* to speak: a proximity `Component.ITriggerListener` or an interactable (repo facepunch.jumper: `jumper/Code/FunStuff/JumperNPCLooker.cs:24`).
3. **Presentation** — a `PanelComponent` razor panel that reveals the text and plays a per-char blip (repo facepunch.jumper: `jumper/Code/UI/JumperNPCTalker.razor:33`).

Keep them separate so the same UI panel serves NPC barks, tutorial prompts, and modal confirms.

## Canonical modern-s&box recipe

### 1. Author lines as a GameResource

```csharp
[GameResource( "NPC TEXT", "npct", "NPC dialogue", Icon = "sentiment_very_satisfied" )]
public class NPCTextGameResource : GameResource
{
    [Property] public string NPCName { get; set; }
    [Property] public List<string> NPCText { get; set; } = new();
    [Property, ResourceType( "sound" )] public string NPCVoice { get; set; }
}
```

This makes editable `.npct` assets in the asset browser; no code change to add lines (repo facepunch.jumper: `jumper/Code/FunStuff/JumperNPCYapper.cs:1-18`). Verbatim from source.

### 2. Trigger on the player entering a zone

The NPC component implements `Component.ITriggerListener` and gates on a tag. Note the collider that carries the `player` tag is a child, so the code climbs to `other.GameObject.Parent` for the real player root.

```csharp
public sealed class NpcTalkTrigger : Component, Component.ITriggerListener
{
    [Property] List<NPCTextGameResource> Resources { get; set; }
    NPCTextGameResource _pack;

    protected override void OnEnabled()
    {
        if ( Resources is { Count: > 0 } )
            _pack = Resources[ Game.Random.Int( 0, Resources.Count - 1 ) ];
    }

    void ITriggerListener.OnTriggerEnter( Collider other )
    {
        if ( !other.GameObject.Tags.Has( "player" ) ) return;
        var talker = other.GameObject.Parent
            .Components.Get<DialoguePanel>( FindMode.EnabledInSelfAndChildren );
        talker.NPCName = _pack.NPCName;
        talker.Voice   = _pack.NPCVoice;
        talker.DisplayMessage( PickNonRepeating() );
    }

    void ITriggerListener.OnTriggerExit( Collider other ) { }
}
```

Pattern verified in `JumperNPCLooker.cs:24-40` (tag-gate + `Parent` climb + random pack on enable) and `JumperFinishLine.cs:14` (same `ITriggerListener` + tag shape). The trigger needs a sibling `Collider` with **IsTrigger = true** and matching collision tags set in the prefab — without that, `OnTriggerEnter` never fires.

A non-repeating line picker avoids saying the same thing twice:

```csharp
int _last = -1;
string PickNonRepeating()
{
    int i = Game.Random.Int( 0, _pack.NPCText.Count - 1 );
    while ( _pack.NPCText.Count > 1 && i == _last )
        i = Game.Random.Int( 0, _pack.NPCText.Count - 1 );
    _last = i;
    return _pack.NPCText[ i ];
}
```

(repo facepunch.jumper: `jumper/Code/FunStuff/JumperNPCLooker.cs:52-61`).

### 3. Reveal it with a typewriter PanelComponent

A `PanelComponent` razor panel appends one char at a time, plays a pitch-randomized blip per letter (Undertale-style), and auto-hides via `RealTimeSince`.

```razor
@inherits PanelComponent

<root class=@(Visible ? "visible" : "")>
    <label class="message">@OutputText</label>
    <label class="name">@NPCName</label>
</root>

@code {
    public string OutputText { get; set; }
    public string NPCName { get; set; } = "Ben";
    public string Voice { get; set; } = "beep1";

    private string _message = "";
    private RealTimeSince _shown = 999;
    private bool Visible => _shown < 4;   // auto-hide 4s after last char

    public void DisplayMessage( string message )
    {
        _message = message;
        OutputText = null;                // null = "start a fresh reveal"
    }

    private async Task RevealTextAsync( string message )
    {
        foreach ( char c in message )
        {
            OutputText += c;
            _shown = 0f;
            await Task.DelaySeconds( Game.Random.Float( 0.05f, 0.2f ) );
            var snd = Sound.Play( Voice );
            snd.Pitch = Game.Random.Float( 0.9f, 1.1f );
            snd.Volume = 0.25f;
        }
    }

    protected override void OnUpdate()
    {
        if ( OutputText == _message ) return;
        if ( OutputText == null ) _ = RevealTextAsync( _message );
        _message = OutputText;            // guard: don't re-fire mid-reveal
    }

    protected override int BuildHash()
        => HashCode.Combine( OutputText, Visible ? 1 : 0 );
}
```

Faithful to `jumper/Code/UI/JumperNPCTalker.razor:33-78`. The `.visible` opacity is driven from SCSS off the `visible` class. `BuildHash` must include `OutputText` so the panel re-renders each appended char.

## Variations seen across games

- **Modal confirm dialog (static singleton + callback).** A purchase/confirm prompt is one panel opened via a static `Open(item, onConfirm)`; the click handler invokes the stored `System.Action` and closes. Affordability disables the button (`@(CanAfford ? "" : "disabled")`) and `Input.Pressed("use")` cancels (repo playbtg.elevator: `elevator/Code/UI/ShopConfirmation.razor:36-85`). This is the cleanest "yes/no" pattern — reuse it for any confirm gate, not just shops.
- **3D product/portrait display alongside text.** The shop renders the item's `IconModel` next to a world-panel sign and wires an interaction; a dialogue NPC can do the same to show a speaker portrait or held prop (repo playbtg.elevator: `elevator/Code/Inventory/ShopDisplay.cs:12`).
- **LookAt while talking.** The same trigger that opens dialogue points the NPC's `CitizenAnimationHelper.LookAt` at the player (`EyesWeight/HeadWeight/BodyWeight = 1`) and resets to a default `LookTarget` on exit — cheap "they noticed you" polish (repo facepunch.jumper: `jumper/Code/FunStuff/JumperNPCLooker.cs:29-47`).
- **Interaction-driven instead of proximity.** Trigger off an `IPressable`/use-key interactable rather than a trigger volume when the player should choose to talk (repo playbtg.elevator: `elevator/Code/Interaction/Interactables/ShopInteraction.cs:19`).

## Gotchas

- **Tag lives on the collider child, root lives on the parent.** Every game does `other.GameObject.Parent` (or `.Root`) after the tag check. Jumper is *inconsistent* — Finish/Wind mix `.Parent` and `.Root`, a latent add/remove mismatch (repo facepunch.jumper: `JumperFinishLine.cs:18` vs `JumperWindTunnel.cs`). Pick one (`other.GameObject.Root`) and stay consistent.
- **The reveal is fire-and-forget `async`.** `RevealTextAsync` is started without awaiting and has **no cancellation** — a new message arriving mid-reveal interleaves chars. Track a `CancellationTokenSource` (or a reveal-generation int) and bail if it changes before the next `await`.
- **Trigger needs a real trigger collider.** `ITriggerListener` callbacks only fire if a sibling `Collider` has `IsTrigger = true` and collision tags that match the player. Easy to forget in the prefab → silent no-op.
- **Modal singletons race on startup.** Static `Instance`/`Local` lookups (ShopConfirmation, doner_kiosk's cam panel resolved after a 3s `Task.DelaySeconds`) can be null before the panel awakes — null-guard every `Open()`/`Instance` access (repo playbtg.elevator: `ShopConfirmation.razor:44,58`).
- **The jumper talker wiring ships commented out.** In `JumperNPCLooker.cs:33-38` only the `LookAt` is live; the `DisplayMessage` call is in a `/* */` block. The pieces are all correct — you wire them together yourself.
- **Multiplayer:** the panels here are local/client UI. If the *decision* matters (which line everyone hears, a confirmed purchase), drive it from a host-authoritative source and replicate with `[Sync]` / `[Rpc.Broadcast]` — do not trust a client-only reveal.

## Seen in

- **facepunch.jumper** — full data→trigger→typewriter stack: `jumper/Code/FunStuff/JumperNPCYapper.cs` (GameResource), `JumperNPCLooker.cs` (trigger + LookAt), `jumper/Code/UI/JumperNPCTalker.razor` (typewriter), `jumper/Code/GamePlay/JumperFinishLine.cs` (ITriggerListener reference).
- **playbtg.elevator** — modal confirm dialog + 3D product display: `elevator/Code/UI/ShopConfirmation.razor`, `elevator/Code/Inventory/ShopDisplay.cs`, `elevator/Code/Interaction/Interactables/ShopInteraction.cs`.
- **luckygaming.doner_kiosk** — per-customer cam-dialog gated behind a CCTV panel + static singleton (startup-delay race): `Code/Game/CameraPanel.cs`, `Code/Game/VideoCamera.cs`.

---
**Verify live:** the installed SDK is authoritative — confirm members before coding with the bridge's reflection tools: `describe_type GameResource`, `describe_type Sandbox.Component+ITriggerListener`, `describe_type Sandbox.UI.PanelComponent`, `search_types CitizenAnimationHelper`. Reflection beats any snippet here if the API has moved.

**See also:** `sbox-api` (exact signatures for `PanelComponent`, `ITriggerListener`, `GameResource`, `RealTimeSince`) and `sbox-build-feature` (the screenshot-driven loop to wire the prefab trigger + panel and see it working).

## Corpus refresh (2026): more reference implementations

Three newly-mined games surface three distinct "dialogue-adjacent" patterns not covered above: **procedural per-player objectives** (despawn.murder), **imperative awaited branching narrative** (dimmies.terryspapers), and **server-authoritative multiplayer vote UI** (lowkeynetworks.newrp). Each is a different point in the design space from the existing typewriter/trigger/modal coverage.

---

### Pattern A — Polymorphic per-player objective generator (despawn.murder)

`Systems/GunAcquisition/` implements a procedural **quest generator**: 3 random tasks per player drawn from a polymorphic pool (FindClues / FindEvidence / VisitZone / FindBody / Survive / FindCluesOrEvidence). This is the cleanest per-player quest-contract reference in the corpus.

Key shapes:
- `GunTaskDefinition(Scene)` base — `IsEnabled()`, exclusion `Group` string (only one task per group is picked), `Make()` → returns a `GunTaskState`.
- `GunTaskManager.GenerateTasks()` shuffles enabled definitions, picks `TaskCount` honoring group exclusion, pads with clue tasks if variety is short.
- **Three progress strategies coexist**: event-hook (OnCluePickup/OnEvidencePickup called from gameplay code), polling (OnFixedUpdate zone/body/survival checks), OR-condition (`Progress >= Target || AltProgress >= AltTarget`).
- Per-player display strings (`[x/y]`) rebuilt and pushed **only to that player** via `Rpc.FilterInclude(connection)`.
- String-encoded task params: `ZoneVisitTracker.FromExtraData("zone1,zone2|seconds")` — lightweight, no extra asset type.
- Anti-pattern: progress tracked as plain fields with no cancellation path. Fix: add a `Cancel()` method to `GunTaskState` so tasks can be voided when a round ends without leaving dangling event hooks.

```csharp
// despawn.murder: Systems/GunAcquisition/Tasks/GunTaskDefinition.cs (condensed)
public abstract class GunTaskDefinition
{
    public abstract bool IsEnabled( Scene scene );
    public virtual string Group => null;           // null = no exclusion
    public abstract GunTaskState Make();
}

public class FindCluesTask : GunTaskDefinition
{
    static readonly int[] _targets = { 2, 3, 4 };
    public override bool IsEnabled( Scene scene ) => true;  // always available
    public override GunTaskState Make()
        => new GunTaskState { Description = $"Find {_target} clues", Target = _target = Game.Random.FromArray(_targets) };
}

// Manager rolls the set
void GenerateTasks( IEnumerable<GunTaskDefinition> defs )
{
    var shuffled = defs.Where( d => d.IsEnabled( Scene ) ).OrderBy( _ => Guid.NewGuid() ).ToList();
    var used = new HashSet<string>();
    var picked = new List<GunTaskState>();
    foreach ( var d in shuffled )
    {
        if ( d.Group != null && !used.Add( d.Group ) ) continue;
        picked.Add( d.Make() );
        if ( picked.Count >= TaskCount ) break;
    }
    // push [x/y] display string per-player via Rpc.FilterInclude
    using ( Rpc.FilterInclude( _ownerConnection ) )
        SyncTasks( picked.Select( t => t.Description ).ToArray() );
}
```

---

### Pattern B — Imperative awaited branching narrative (dimmies.terryspapers)

`PhoneUI.razor` (~2100 lines) is the entire life-sim story delivered as straight-line C# `async Task` methods. No tree-asset, no node graph — branches are plain `if/else`, state is flags on `PlayerData`. Three micro-primitives create a full VN engine:

```razor
@inherits PanelComponent
@* dimmies.terryspapers: Code/UI/PhoneUI.razor (condensed) *@
@code {
    bool clicked;
    string selectedChoice = "";

    // Awaitable "tap to continue"
    async Task WaitForClick()
    {
        interactUI.clickToContinue = true;
        while ( !clicked ) await GameTask.Delay( 1 );
        clicked = false;
        interactUI.clickToContinue = false;
    }

    // Awaitable binary choice — returns "left" or "right"
    async Task<string> StartChoice( string question, string left, string right )
    {
        choiceActive = true;
        choiceLeft = left; choiceRight = right; choiceQuestion = question;
        while ( selectedChoice == "" ) await GameTask.Delay( 1 );
        var result = selectedChoice;
        selectedChoice = ""; choiceActive = false;
        return result;
    }

    // "Days-since" scheduler — no timers, no queue; stamp IS the schedule
    async Task StartScene()
    {
        // e.g. "baby born 3 shifts after pregnancy start"
        if ( gameHandler.Shift - playerData.PregnancyOn == 3 ) { await BabyBornEvent(); goto send_day_stats; }
        if ( gameHandler.Shift - playerData.PregnancyOn == 8 ) { await MomDiesEvent();  goto send_day_stats; }
        // flavor events fall through; story events short-circuit via goto
        await FlavorEvent();
        send_day_stats: await SendDayStats();
    }
}
```

Key lesson: **ordering encodes priority** — checking story beats before flavor beats, then `goto` short-circuits so only one life-changing event fires per shift. The `...On` shift-stamp fields in `PlayerData` are the scheduler; no timer component needed.

Anti-pattern from the source: `playerData` is written directly to disk client-side with no server — fine for single-player but breaks under any multiplayer authority model. For networked games, keep the flag store server-side and push read-only copies via `[Sync]`.

---

### Pattern C — Host-authoritative time-boxed vote (lowkeynetworks.newrp)

`Code/modules/jobs/JobVoteService.cs` is a complete, reusable yes/no vote: snapshot electorate → filtered-RPC UI to voters only → host-tallied ballots → async countdown → apply. The cleanest vote-flow reference in the corpus.

```csharp
// lowkeynetworks.newrp: Code/modules/jobs/JobVoteService.cs (condensed)
public class VoteSession
{
    public Dictionary<Guid, bool> Votes = new();
    public List<Connection> Voters;
    public float Duration = 18f;
}

VoteSession _active;

// Host-only: kick off a vote targeting a candidate
public void StartVote( Connection candidate )
{
    if ( !Networking.IsHost ) return;
    var voters = Connection.All.Where( c => c != candidate ).ToList();
    if ( voters.Count == 0 ) { Apply( passed: true ); return; }   // auto-pass with no voters
    _active = new() { Voters = voters };
    using ( Rpc.FilterInclude( voters ) )   // UI shown ONLY to voters
        ShowVote( candidate.DisplayName );
    _ = FinishLater();
}

[Rpc.Host]
public void SubmitVote( bool yes )
{
    if ( _active == null ) return;
    if ( !_active.Voters.Any( v => v.Id == Rpc.Caller.Id ) ) return;  // non-voter guard
    _active.Votes[Rpc.Caller.Id] = yes;
    if ( _active.Votes.Count >= _active.Voters.Count ) Finish();       // early-finish when all in
}

async Task FinishLater()
{
    float remaining = _active.Duration;
    while ( remaining > 0 && _active != null )
    {
        await GameTask.Delay( 1000 );
        remaining -= 1f;
        BroadcastCountdown( (int)remaining );
    }
    if ( _active != null ) Finish();
}

void Finish()
{
    bool passed = _active.Votes.Count( kv => kv.Value ) > _active.Votes.Count( kv => !kv.Value );
    _active = null;
    Apply( passed );
}
```

Key technique: `using ( Rpc.FilterInclude( voters ) ) ShowVote(...)` — sends a `[Rpc.Broadcast]` only to those connections. This is the idiomatic "whisper / area / team" networking pattern and composes with any vote, notification, or reveal system.

Anti-pattern to avoid: storing `_active` as a plain field with no null-guard on `SubmitVote` after a round ends. Always null-check `_active` and return early if a late ballot arrives after `Finish()` has cleared it.

---

### How these three compose

A dialogue-driven quest game might use all three together:
1. **Pattern A**: NPC gives the player 3 procedurally-generated tasks on talk.
2. **Pattern B**: Story cutscenes between task completions are imperative `await` scripts in a Razor panel.
3. **Pattern C**: At end of round, players vote on which optional objective to unlock next — host-tallied, time-boxed, UI filtered to eligible voters only.

The existing typewriter trigger (jumper) stays as the NPC bark layer; Pattern A replaces the single-line `DisplayMessage` with a rich task list synced per-player.

---

**Read these games** (in addition to the existing set above):
- **despawn.murder** — `Systems/GunAcquisition/Tasks/GunTaskDefinition.cs` + `GunTaskManager.cs` (polymorphic objectives), `Systems/Rounds/States/MapVoteRoundState.cs` (map vote).
- **dimmies.terryspapers** — `Code/UI/PhoneUI.razor` (full imperative VN engine), `Code/Game/GameHandler.cs` (TCS gate, shift-stamp scheduler).
- **lowkeynetworks.newrp** — `Code/modules/jobs/JobVoteService.cs` (vote flow), `Code/modules/chat/ChatService.cs` (proximity chat as social dialogue).
