# Document-Sim (Papers-Please-like) Genre Recipe

Build a first-person inspection game: an NPC presents procedurally-generated documents, the player checks them against a randomized rule set, and accepts/denies — scored against a verdict engine. Mined from `dimmies.terryspapers` (Terry's Papers), a single-player, offline, modern GameObject/Component/Scene build.

## What defines the genre

- **The desk loop, not the world.** The player is mostly stationary at an inspection station. Gameplay is *look at document → spot discrepancy → decide*. There is almost no locomotion or combat.
- **A rules-vs-data tension.** Each shift randomizes which checks are required; each subject is generated to *sometimes* violate them. The fun is the gap between "what the rules demand" and "what this subject's papers actually say."
- **Coherent, subtle forgeries.** A good fake is *derived* from the real value with one thing wrong — not independently random (a random fingerprint is trivially obvious; a real one with one swapped digit is fair-but-hard) (terryspapers: `RandomTerryData.cs:1392-1415`).
- **Soft-fail economy.** Wrong calls dock mood/money/score rather than instant game-over; you get fired only when a meter bottoms out at low rank.

### Core loop
`StartDay → roll shift rules → generate subject + planted errors → reveal documents → player inspects (raycast 'use') → accept/deny → verdict (WrongList/CorrectList) → score + mood + punishment → next subject → quota met? → EndDay → narrative beat → save`.

## System stack (compose these)

| System | Role | Reference |
|---|---|---|
| Raycast-tag interaction dispatcher | "look at thing, press use" on any prop | `references/systems/interaction-use-prompt.md` |
| Rules-vs-data verdict engine | the actual Papers-Please core | `references/systems/rules-validation-engine.md` |
| Procedural subject + planted-error generator | data-table / weighted-roll NPC factory | `references/systems/procedural-data-generator.md` |
| Camera-to-texture mugshot | live 3D portrait on ID/passport UI | `references/systems/render-to-texture-portrait.md` |
| In-game clock & day flow | tick clock, quota, EndDay gate | `references/systems/day-cycle-round-flow.md` |
| JSON persistence | save/continue to `FileSystem.Data` | `references/systems/json-save-load.md` |
| Async scripted-sequence pattern | skippable cutscenes / staggered reveals | `references/systems/async-sequence.md` |
| Cinematic camera handler | fade + SmoothDamp/Slerp scripted beats | `references/systems/cinematic-camera.md` |
| Mood/economy progression | soft-fail meter + promotion/permadeath | `references/systems/economy-progression.md` |
| Service-locator (`GameCore`) | typed `[Property]` handles to every manager | (pattern below) |

The first three are the irreducible core — a document-sim is *interaction dispatcher + verdict engine + data generator*. The rest are polish.

## Build order

1. **Service-locator first.** One `GameCore : Component` holding `[Property]` handles to every manager, wired by drag-drop in the editor. Subsystems take `[Property] GameCore gameCore` and reach peers via `gameCore.terryHandler.X`, avoiding fragile `Scene.Children.Where(name==...)` lookups (terryspapers: `GameCore.cs:4-23`).
2. **Interaction dispatcher.** One per-frame camera raycast filtered on a tag, dispatching to `Interactable.RunLogic()`. New props need zero dispatcher changes (terryspapers: `Interactable.cs:3-6`, `Interact.razor:91-117`).
3. **Subject generator.** Weighted-roll a strongly-typed data record + a `Dictionary<string,bool>` of planted-error flags. Make required traits likely (~0.9), non-required unlikely (~0.2), "bad" flags rare (~0.05) so most subjects are valid (terryspapers: `RandomTerryData.cs:1345-1374`).
4. **Rules roller + diegetic notes.** Randomize per-shift requirement booleans and *mirror the same data* into in-world rule-note GameObjects (single source of truth) (terryspapers: `GameRules.cs:16-65`).
5. **Verdict engine.** A method returning `(WrongList, CorrectList)` of plain-English reasons that feeds scoring, punishment branching, AND the feedback UI (terryspapers: `TerryHandler.cs:568-630`).
6. **Document props + reveal.** Spawn physical paper GameObjects on the desk; stagger their appearance with an async sequence.
7. **Mugshot, clock, economy, persistence, narrative** — layer in as polish.

## How the real game does it

### 1. The interaction dispatcher (most portable recipe)

One abstract Component is the entire contract (terryspapers: `Interactable.cs:3-6`):

```csharp
public abstract class Interactable : Component
{
    public abstract void RunLogic();
}
```

A single raycast in a per-frame method finds the hovered prop by tag, draws a hover outline, and dispatches on click (terryspapers: `Interact.razor:75-117`):

```csharp
var look = Scene.Trace.Ray(
        Scene.Camera.WorldPosition,
        Scene.Camera.WorldPosition + Scene.Camera.WorldRotation.Forward * 1000f )
    .Radius( 0.1f )
    .WithoutTags( "player" )   // camera-origin ray would self-hit a third-person body
    .Run();

if ( look.Hit && look.GameObject.Tags.Has( "interactable" ) )
{
    var outline = look.GameObject.Components.Get<HighlightOutline>();
    if ( outline is not null ) outline.Width = 0.2f;   // hover feedback
}

if ( Input.Pressed( "attack1" ) && look.Hit && look.GameObject.Tags.Has( "interactable" ) )
{
    var interactable = look.GameObject.Components.Get<Interactable>();
    if ( interactable.IsValid() ) interactable.RunLogic();
}
```

Every interactive object (`ViewDocument`, `ShadyScanner`, `CoffeeCupInteract`, `PanicButtonInteract`, `TerryInteract`...) subclasses `Interactable` and self-contains its behavior. To add one: subclass, add the `"interactable"` tag, optionally add `HighlightOutline`. **Gotcha:** the ray originates at the *camera*, so `.WithoutTags("player")` is mandatory for any non-FP rig; and there is no central "can I interact now?" gate, so each subclass re-checks its own guard booleans — consider a shared `bool CanInteract` to stop guard drift.

### 2. Rules roller mirrored into world props

`SetShiftRules` rolls four required-document booleans + a quota, then reflects the *same* state into diegetic rule notes — one structure drives both validation and the in-world signage the player reads (terryspapers: `GameRules.cs:16-65`):

```csharp
public void SetShiftRules( int shift )
{
    reqBirthCert   = Game.Random.Next( 2 ) == 0;
    reqEntryTicket = Game.Random.Next( 2 ) == 0;
    reqVaccine     = Game.Random.Next( 2 ) == 0;
    reqFingerprint = Game.Random.Next( 2 ) == 0;
    shiftQuota     = Game.Random.Next( 7, 11 );

    if ( shift == 1 ) { /* tutorial: all false, quota 2 */ }

    foreach ( var n in ruleNotes ) n.Enabled = false;
    int i = 0;
    if ( reqBirthCert ) {
        ruleNotes[i].Enabled = true;
        ruleNotes[i].Children.First( c => c.Name == "Text" )
            .Components.Get<TextRenderer>().Text = "Must provide \nBirth Certificate!";
        i++;
    }
    // ...one block per active rule
}
```

### 3. Verdict as two reason-lists (not a bool)

The correctness check returns `(WrongList, CorrectList)` of plain-English strings; `WrongList.Count > 0` means "should have been denied." The same lists feed scoring, punishment branching, and `SendResponse` which animates each reason into the UI — decoupling *why it's wrong* into a reusable list is the cleanest pattern in the game (terryspapers: `TerryHandler.cs:568-630`):

```csharp
public (List<string>, List<string>) DidWeGetItWrong()
{
    var wrong = new List<string>();
    var ok    = new List<string>();

    if ( !CurrentTerryData.HasId ) wrong.Add( "Didn't have ID Card" ); else ok.Add( "Had ID Card" );
    if ( gameCore.gameRules.reqBirthCert && !CurrentTerryData.HasBirthCert )
        wrong.Add( "Didn't have Birth Certificate" ); else ok.Add( "Had Birth Certificate" );
    if ( CurrentTerryData.HasFingerprint && CurrentTerryData.HasFakeFingerprint )
        wrong.Add( "Fingerprint Record didn't match!" );

    if ( CurrentTerryData.HasWeapon )       wrong.Add( "Person had a weapon" );
    if ( CurrentTerryData.IsWanted )        wrong.Add( "Person was wanted" );
    if ( CurrentTerryData.HasIllegalGoods ) wrong.Add( "Person had illegal goods" );
    // ...plus expiry compares against the in-game clock
    return (wrong, ok);
}
```

**Two real gotchas to fix in your version:** (a) this ~60-line block is *duplicated* in a debug ConCmd (`GameHandler.genterries()`) — keep it in one place. (b) Expiry dates are `"M/D/YYYY"` strings compared via `.Split("/")[n].ToInt()`, which silently breaks on any format change — prefer `DateTime`. Reasons are raw strings that both UI and scoring string-match — an enum keyed to display text is safer.

### 4. Generate forgeries coherently

Don't randomize a fake independently — derive it from the real value with one thing wrong, so "spot the discrepancy" is fair (terryspapers: `RandomTerryData.cs:1392-1415`):

```csharp
// Copy the genuine 5-number fingerprint, then swap exactly one digit.
string[] GetFakeFingerprint( string[] real )
{
    var fake = (string[])real.Clone();
    int idx  = Game.Random.Next( fake.Length );
    fake[idx] = Game.Random.Next( 0, 10 ).ToString();   // subtle, not obvious
    return fake;
}
```

The weighted-trait roll (`GetRandomOptions`) reads the *active rules* and some already-assigned fields (e.g. Country forces an entry-ticket requirement), so **field assignment order matters** — assign anything the roll reads before calling it. Upgrade target: move the hard-coded inline name/address pools (a 1400-line file) into a `GameResource` or CSV, and use a single seedable `Game.Random` instead of mixing `System.Random` instances so shifts are reproducible.

## Modern-API notes & pitfalls

- Use `Component` / `GameObject` / `Scene`, `[Property]` for editor-wired refs, `Scene.Trace.Ray(...)`, `Tags.Has(...)`, `Components.Get<T>()`, `.IsValid()`. This game is networking-free; only add `[Sync]`/`[Rpc.*]` if you go multiplayer.
- Mugshot: `Texture.CreateRenderTarget("Mugshot", ImageFormat.RGBA8888, 512)` + `camera.RenderToTexture(rt)`, bound into Razor `<image>`. **Pool the render target** — the source allocates one per subject with no disposal and leaks GPU memory over a long session (terryspapers: `TerryHandler.cs:136-141`).
- Sequencing is `async void` + `await GameTask.Delay(ms)`. Scatter `if (!Game.IsPlaying) return;` after each await so stop-play doesn't run dead sequences, and prefer a `CancellationToken` over ad-hoc bool flags.
- Persistence: `FileSystem.Data.WriteAllText("data.json", JsonSerializer.Serialize(data))`. Don't hand-copy fields property-by-property on load (the source warns you to edit three places per new field) — assign the deserialized object and add a version int for migration.
- Avoid the source's string-state-machine fields (`currLocation`, `zoomDir`) and magic-string event-id dictionaries (a typo throws `KeyNotFoundException`) — use enums.

Verify live: the installed SDK is the source of truth — confirm `Scene.Trace.Ray`, `HighlightOutline`, `Texture.CreateRenderTarget`, `CameraComponent.RenderToTexture`, `TextRenderer`, and `GameTask.Delay` signatures with `describe_type` / `search_types` reflection before coding, as the API shifts between versions.

See also: the **sbox-api** skill (reflection-verified type signatures) and the **sbox-build-feature** skill (screenshot-driven iteration loop) when implementing any system above.

## Corpus refresh (2026): more reference implementations

The four newly-mined games (facepunch.ss2, despawn.murder, facepunch.fair, barrelproto.ragroll) do not implement document-inspection mechanics — they contribute no net-new patterns to this genre. The additional pass over `dimmies.terryspapers` itself surfaces several standout techniques not covered in the sections above.

### A. Named-event delta table as the entire progression system

`GameHandler.SetMood(string id)` adjusts a single `int mood` via a `Dictionary<string,int>` of 35 named deltas. The same method guards promotion and game-over. All balance lives in one place; adding an outcome is one dictionary entry (terryspapers: `GameHandler.cs`, `moodChanges` field):

```csharp
// terryspapers: GameHandler.cs — mood as a string-keyed delta table
static readonly Dictionary<string, int> moodChanges = new() {
    { "validProcess",   +1  },   { "invalidProcess",   -5 },
    { "babyBorn",       +25 },   { "momDie",           -40 },
    { "wifeVacation",   +75 },   // ... 30 more named events
};

public void SetMood( string id )
{
    playerData.mood += moodChanges[id];          // single write
    if ( playerData.mood >= 100 && playerData.employeeLevel < 3 ) {
        playerData.employeeLevel++;
        playerData.mood = 40;                    // promote + reset meter
    }
    if ( playerData.mood <= 0 ) GetFired();      // game-over branch
    SavePlayerData();                            // save after every change
}
```

**Anti-pattern:** raw string keys — a typo throws `KeyNotFoundException` at runtime. Fix: an enum keyed to a `ReadOnlyDictionary<MoodEvent, int>`. The upside of plain strings is zero ceremony to add events; the enum version makes it impossible to reference an undefined event.

### B. `TaskCompletionSource` as a "wait for all animations to settle" barrier

Long async punishment sequences (boss slap, chair spin) would corrupt state if the day ended mid-animation. The pattern: create a fresh `TaskCompletionSource<bool>`, `await` it before advancing, and resolve it from every state-change path that could satisfy the condition (terryspapers: `GameHandler.cs`):

```csharp
// terryspapers: GameHandler.cs — TCS gate prevents day-end mid-animation
TaskCompletionSource<bool> _settled;

async Task WaitForAnimationsToSettle()
{
    _settled = new TaskCompletionSource<bool>();
    bool alreadyClear = terryHandler.CurrLocation == "SPAWN"
                     && !terryHandler.SpinningChair
                     && !terryHandler.GotSlapped;
    if ( !alreadyClear )
        await _settled.Task;                     // park here
}

// Called from every flag-clearing site (animation end callbacks, etc.)
public void OnAnimationStateChanged()
{
    if ( terryHandler.CurrLocation == "SPAWN"
      && !terryHandler.SpinningChair
      && !terryHandler.GotSlapped )
        _settled?.TrySetResult( true );          // release the awaiter
}
```

The same TCS pattern reappears in `TweenManager` to make a tween awaitable from outside the tween loop — a general recipe for "make any external condition awaitable without polling the caller."

### C. Clock-driven day (simulated time in `OnUpdate`, not a wave count)

The day ends when an in-game clock reaches 5 PM, not when N subjects are processed. The entire clock runs in `OnUpdate` off `Time.Delta` with a float accumulator (terryspapers: `GameHandler.cs`):

```csharp
// terryspapers: GameHandler.cs — real-time accumulator drives sim clock
float _elapsed;
const float MinuteInterval = 0.7f;   // real seconds per in-game minute

protected override void OnUpdate()
{
    _elapsed += Time.Delta;
    if ( _elapsed < MinuteInterval ) return;
    _elapsed = 0f;
    Minute++;
    if ( Minute >= 60 ) { Minute = 0; Hour++; }
    if ( Hour >= 5 && TimeAMPM == "PM" ) EndDay( "End of Day" );
}
```

**Composable lesson:** difficulty in a document-sim scales with the rule set per shift (what you must check) not with NPC count alone. `shiftQuota` is a *minimum correct decisions* floor, not a cap — a slow player still has to meet quota before the clock runs out.

### D. Save-on-every-mutation rather than autosave-on-timer

`SavePlayerData()` is a one-liner called after *every* state change (mood, money, narrative flag). This eliminates "session lost" bugs at the cost of tiny per-operation disk writes that are acceptable in a singleplayer turn-based loop (terryspapers: `GameHandler.cs`):

```csharp
// terryspapers: GameHandler.cs
public void SavePlayerData() =>
    FileSystem.Data.WriteAllText( "data.json", JsonSerializer.Serialize( playerData ) );
```

**The triple-write discipline:** adding a new field to `PlayerData` requires touching three places — the field declaration, `GetDefaultData()` (new-game seed), and the field-by-field load path. The source has an explicit comment warning about this. If you start from this pattern, consider a generated save struct that keeps those in sync, or use the `facepunch.fair` `ISaveDataProperty` interface approach for larger projects.

### E. `[ConCmd]` Monte-Carlo balance harness for the data generator

The game ships a console command that runs the full subject-generation + verdict pipeline 20 times in a loop and logs the valid/invalid split. This is an in-engine statistical test that ensures a shift rule combination is not 100% rejects before it ships (terryspapers: `GameHandler.genterries()`):

```csharp
// terryspapers: GameHandler.cs — [ConCmd] balance-validation harness
[ConCmd( "genrandomterries" )]
public static void GenerateRandomTerries()
{
    if ( !Game.IsEditor ) return;              // ship dead in public builds

    int valid = 0, invalid = 0;
    for ( int i = 0; i < 20; i++ )
    {
        var data = RandomTerryData.GetRandomTerry( Instance.gameCore );
        var (wrong, _) = Instance.terryHandler.DidWeGetItWrong( data );
        if ( wrong.Count == 0 ) valid++; else invalid++;
    }
    Log.Info( $"Valid: {valid}  Invalid: {invalid}  ({valid*100/20}% clean)" );
}
```

**Anti-pattern to note:** the verdict logic was duplicated inside this ConCmd in the original source rather than calling the real `DidWeGetItWrong` — a maintenance footgun. The fix shown above calls the real method. Gate every debug/cheat ConCmd behind `Game.IsEditor` so it compiles but is unreachable in shipped builds.

### F. Embedded async minigame as a sub-state-machine inside the inspection loop

`HackUI.razor` pops a full "hacking" minigame as one `async void TriggerHack()` driving an `int Stage` (0-6). The Razor switches on `Stage` to show different UI; stages run via `await GameTask.Delay(50)` loops. This is the `document-sim + embedded-minigame` composition pattern: the minigame is a co-routine inside the shift loop, charges a money penalty on failure, and returns to the inspection desk on completion (terryspapers: `HackUI.razor`):

```csharp
// terryspapers: HackUI.razor — async stage machine as inline minigame
int Stage = 0;

async void TriggerHack()
{
    Stage = 1;                          // typewriter fake-terminal output
    for ( int i = 0; i < 70; i++ ) {
        AppendTerminalLine( FakeLogLines[i] );
        await GameTask.Delay( 50 );
        if ( !Game.IsPlaying ) return;  // guard every await
    }
    Stage = 2;                          // ASCII skull reveal
    await GameTask.Delay( 1200 );

    // Stage 3: build memory grid (Simon-says)
    int n = Game.Random.Next( 4, 13 );
    hackSquares = Enumerable.Range( 1, 16 ).OrderBy( _ => Game.Random.Next() ).Take( n ).ToArray();
    Stage = 3;
    await WaitForPlayerRecall();        // awaits player click-back all N squares

    if ( recallCorrect ) {
        Stage = 6;                      // success + achievement
        Services.Achievements.Unlock( "hack_success" );
    } else {
        Stage = 5;                      // fail + money penalty
        int penalty = Game.Random.Next( 150, 450 );
        gameHandler.playerData.money -= penalty;
        gameHandler.SavePlayerData();
    }
}
```

**Composable lesson:** a self-contained minigame needs no new scene and no networking — it is one Razor panel + one `async void` + one `int Stage`. The pattern generalizes to lockpicking, scanner calibration, or any "press correct sequence under time pressure" skill check inside a larger loop.

### G. Random recurring threat on a reset timer (boss visit)

`PunisherHandler` adds tension via a visit that fires at a random time and resets its own timer each visit. This creates unpredictable pressure without a scripted event schedule (terryspapers: `PunisherHandler.cs`):

```csharp
// terryspapers: PunisherHandler.cs — random recurring threat
float _nextVisitTime;

protected override void OnStart()
{
    ResetVisitTimer();
}

void ResetVisitTimer()
{
    _nextVisitTime = Time.Now + Game.Random.Float( 420f, 900f );  // 7–15 min
}

protected override void OnUpdate()
{
    if ( gameHandler.DayOver || gameHandler.Shift == 1 ) return;  // no threat tutorial or after day
    if ( Time.Now < _nextVisitTime ) return;
    BossVisit();
    ResetVisitTimer();                  // reschedule immediately after visit
}
```

**Note:** `Game.Random.Float(min, max)` is correct s&box API; `System.Random` and `MathF` do not exist in the sandbox. The source mixes both — prefer `Game.Random` throughout for reproducibility.

### H. Static email/hint DB as the tutorial layer

`EmailDB.cs` is a `static Dictionary<string, EmailData>` of ~26 hand-authored emails. Some are flavor spam; others are in-fiction hints from an NPC ("Sophie") that teach mechanics — e.g. "if you can't find someone in the database and the spelling's right, they're using a fake name." This decouples tutorial text from code: designers edit the dictionary, mechanics never change. Pattern is directly reusable for any "NPC messages teach the rules" tutorial approach (terryspapers: `EmailDB.cs`).

---

**Cross-genre borrowing worth noting:**
- `despawn.murder` (`zips-code/despawn.murder`) implements a *procedural per-player task system* (`GunAcquisition/Tasks/`) that maps onto the document-sim objective layer — if you want to add secondary objectives ("process 3 Drakorian travellers before end of shift"), its `GunTaskDefinition / GunTaskManager` pattern (polymorphic task base, group-exclusion, progress-tracking via event-hooks + polling) is a cleaner scaffold than ad-hoc flags.
- `facepunch.fair` (`fair/Code/Persistence/`) has the best save-system in the corpus (`ISaveDataProperty` interfaces, `PropertyOrder`, corrupt-section isolation, version-delete migration) — worth adopting over terryspapers' hand-rolled triple-write discipline for any document-sim that adds many fields over time.

**Read these games** for the document-sim genre:
- `dimmies.terryspapers` — the only direct implementation in the corpus; mine it for the verdict engine, diegetic document raising, render-target mugshot, forgery generation, and all patterns above.
- `despawn.murder` — for the procedural task/objective layer and the optimistic-store pattern if you add a backend.
- `facepunch.fair` — for a production-grade save/persistence system to replace the triple-write footgun.
