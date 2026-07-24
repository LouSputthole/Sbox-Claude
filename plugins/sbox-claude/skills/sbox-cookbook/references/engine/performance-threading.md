
# Performance & Threading

<!-- reference-toc:start -->
## Contents

- [Mental model](#mental-model)
- [Recipe: never block OnUpdate — offload + observe the task](#recipe-never-block-onupdate--offload--observe-the-task)
- [Recipe: timed lifetimes — TimeUntil + Destroy, not async void](#recipe-timed-lifetimes--timeuntil--destroy-not-async-void)
- [Recipe: worker→main handoff with a lock-free triple-buffer](#recipe-workermain-handoff-with-a-lock-free-triple-buffer)
- [Recipe: per-player limits from a scoped tracked list](#recipe-per-player-limits-from-a-scoped-tracked-list)
- [Recipe: tunable limits as replicated server ConVars](#recipe-tunable-limits-as-replicated-server-convars)
- [Recipe: strip presentation on headless dedicated servers](#recipe-strip-presentation-on-headless-dedicated-servers)
- [Recipe: custom GPU work via CommandList on the scene camera](#recipe-custom-gpu-work-via-commandlist-on-the-scene-camera)
- [Recipe: async GPU readback with GetPixelsAsync](#recipe-async-gpu-readback-with-getpixelsasync)
- [Recipe: low-latency PCM streaming with SoundStream + backpressure](#recipe-low-latency-pcm-streaming-with-soundstream--backpressure)
- [Recipe: client sound scheduler with game-speed pitch + ambient crossfade](#recipe-client-sound-scheduler-with-game-speed-pitch--ambient-crossfade)
- [Gotcha table](#gotcha-table)
- [Corpus refresh (2026): more reference implementations](#corpus-refresh-2026-more-reference-implementations)
  - [Recipe: GPU-instanced scatter with frustum + distance culling — no per-frame allocation](#recipe-gpu-instanced-scatter-with-frustum--distance-culling--no-per-frame-allocation)
  - [Recipe: async respawn with a generation guard — cancel stale Tasks after reset](#recipe-async-respawn-with-a-generation-guard--cancel-stale-tasks-after-reset)
  - [Recipe: BuildHash from cheap revision counters — O(changes) Razor re-renders](#recipe-buildhash-from-cheap-revision-counters--ochanges-razor-re-renders)
  - [Recipe: [DontExecuteOnServer] — declarative server strip for visual-only components](#recipe-dontexecuteonserver--declarative-server-strip-for-visual-only-components)
  - [Recipe: spatial hash for O(1) point-in-zone lookup](#recipe-spatial-hash-for-o1-point-in-zone-lookup)
  - [Recipe: frame-budgeted main-thread drain queue](#recipe-frame-budgeted-main-thread-drain-queue)
  - [Recipe: coalescing off-thread write queue with priority backpressure](#recipe-coalescing-off-thread-write-queue-with-priority-backpressure)
  - [Gotcha additions](#gotcha-additions)
<!-- reference-toc:end -->

Keep s&box games inside frame budget: offload heavy work off the main thread, never block `OnUpdate`, hand state across threads lock-free, scope limits per-player, strip presentation on headless servers, and stream audio without underruns.

## Mental model

`OnUpdate` runs on the main thread and feeds the render thread. Anything that overruns the frame budget there (procedural gen, pathfinding bakes, large AI/physics batches, file IO, emulation, synchronous GPU readback) stalls rendering and tanks framerate. The discipline:

- **Cheap, per-frame, lifetime-owned work** stays in `OnUpdate` — but use `TimeUntil`/`TimeSince` for timing, not `async void` + `await DelaySeconds`.
- **Heavy or blocking work** moves to a background `Task` via `GameTask.RunInThreadAsync`, with a `CancellationToken` for teardown and an *observe-task* so faults aren't swallowed.
- **Worker→main handoff** uses a preallocated lock-free triple-buffer (Interlocked slot swaps), never locks or per-frame allocations.
- **Counting/limits** scope to a per-player tracked list, never a whole-scene scan.
- **Headless dedicated servers** disable renderers/controllers nobody sees.
- **GPU work** goes through `Sandbox.Rendering.CommandList` on the scene camera; readbacks use `GetPixelsAsync`.

Profile before optimizing — none of the below is free complexity worth adding speculatively.

## Recipe: never block `OnUpdate` — offload + observe the task

`GameTask.RunInThreadAsync` is the sanctioned offload primitive. A faulted background Task is silently swallowed, so always start a second task that just `await`s the worker to surface exceptions, and cancel on teardown (sgba: `Code/EmulatorComponent.CoreThread.cs:78`).

```csharp
private CancellationTokenSource _cts;
private Task _workerTask;

public void Start()
{
    if ( _cts != null ) return;
    _cts = new CancellationTokenSource();
    _workerTask = GameTask.RunInThreadAsync( Run );
    _ = ObserveWorkerTaskAsync( _workerTask ); // surface faults — fire and forget
}

private async Task Run()
{
    var token = _cts.Token;
    while ( !token.IsCancellationRequested )
    {
        DoExpensiveTick();         // the work that would blow the frame budget
        await GameTask.Yield();    // cooperate; let other tasks run
    }
}

private async Task ObserveWorkerTaskAsync( Task t )
{
    try { await t; }
    catch ( OperationCanceledException ) { }
    catch ( Exception ex ) { Log.Warning( $"Worker faulted: {ex.Message}" ); }
}
```

Tear down in `OnDisable`/`OnDestroy`: `_cts?.Cancel();` then clear any wake signals so the loop exits — never let the thread outlive the component (sgba `CoreThread.cs:89` `End()` cancels the CTS and forces the sync signals).

## Recipe: timed lifetimes — `TimeUntil` + `Destroy`, not `async void`

Do NOT write `async void OnUpdate` with `await Task.DelaySeconds(life); go.Destroy();` — the continuation outlives the GameObject/scene, isn't cancelled on disable or hotload, and `async void` swallows exceptions. (This is exactly the footgun shown in sbox-scenestaging `Code/ExampleComponents/SpawnObjectPeriodically.cs:9`, where the spawned object is destroyed via an awaited delay — fine as a demo, wrong as a pattern.)

Instead let a component synchronously check a `TimeUntil` it owns:

```csharp
public sealed class DestroyAfter : Component
{
    [Property] public float LifeTime { get; set; } = 5f;
    private TimeUntil _life;

    protected override void OnEnabled() => _life = LifeTime;

    protected override void OnUpdate()
    {
        if ( _life ) GameObject.Destroy(); // synchronous, cancelled with the object
    }
}
```

Reserve `async`/`await` loops for components whose lifetime *owns* the loop (stepping a traversal with `await GameTask.Frame()`), never for fire-and-forget delays.

## Recipe: worker→main handoff with a lock-free triple-buffer

When a background thread produces data the main thread consumes each frame, don't lock a shared object or enqueue freshly-allocated frames (contention + GC churn on the hot path). Preallocate three slots (write/ready/read) and swap ownership with `Interlocked.Exchange` (sgba: `Code/Emulator/GbaVideo.Rendering.cs:475` producer, `:639` consumer).

```csharp
// slots preallocated ONCE — never reallocate on the hot path
private int _writeSlot = 0, _readySlot = 1, _readSlot = 2;
private int _frameReady; // 0/1, Interlocked

// Producer (worker thread), after filling _frames[_writeSlot]:
void CommitFrame()
{
    _writeSlot = Interlocked.Exchange( ref _readySlot, _writeSlot );
    Interlocked.Exchange( ref _frameReady, 1 );
}

// Consumer (main thread), to claim the newest complete frame:
bool TryClaimLatest()
{
    if ( Interlocked.Exchange( ref _frameReady, 0 ) != 1 ) return false;
    _readSlot = Interlocked.Exchange( ref _readySlot, _readSlot );
    return true; // _frames[_readSlot] is now the newest finished frame
}
```

Lock-free and allocation-free: the consumer always gets the newest complete frame, the producer never waits. This supports decoupled simulation/render pipelines.

## Recipe: per-player limits from a scoped tracked list

To cap props/entities per player at scale, never iterate the whole scene on every spawn. Keep a `Dictionary<long, List<GameObject>>` keyed by SteamId plus a `HashSet<GameObject>` for O(1) dedupe, populated from post-spawn events; count by walking only that player's list and lazy-prune dead entries as you go (sandbox-plus-plus: `Code/GameLoop/LimitsSystem.cs:53`, `:98`).

```csharp
private readonly Dictionary<long, List<GameObject>> _tracked = new();
private readonly HashSet<GameObject> _allTracked = new();

private void Track( long steamId, GameObject go )
{
    if ( !go.IsValid() || !_allTracked.Add( go ) ) return; // dedupe
    if ( !_tracked.TryGetValue( steamId, out var list ) )
        _tracked[steamId] = list = new();
    list.Add( go );
}

private int Count( long steamId, Func<GameObject, bool> filter = null )
{
    if ( !_tracked.TryGetValue( steamId, out var list ) ) return 0;
    var count = 0;
    for ( int i = list.Count - 1; i >= 0; i-- ) // prune the HashSet AND the list together
    {
        var go = list[i];
        if ( !go.IsValid() ) { _allTracked.Remove( go ); list.RemoveAt( i ); continue; }
        if ( filter is null || filter( go ) ) count++;
    }
    return count;
}
```

The count is O(player's objects), not O(scene), and the list self-heals with no separate GC pass. For batch ops (a duplicator paste) pre-check atomically — `current + dupeCount > limit` rejects the whole paste so it can't partially overrun the cap (`LimitsSystem.cs:135`).

## Recipe: tunable limits as replicated server ConVars

Make caps live-tunable, not hardcoded. A `Replicated | Server` ConVar lets admins change limits without a redeploy, and clients see the same value the server enforces. Use a sentinel convention (`-1` = unlimited, `0` = none) (sandbox-plus-plus: `LimitsSystem.cs:12`).

```csharp
[ConVar( "sb.limit.props", ConVarFlags.Replicated | ConVarFlags.Server,
    Help = "Max props per player. -1 = unlimited, 0 = none." )]
public static int MaxPropsPerPlayer { get; set; } = -1;

private static bool IsExceeded( int limit, int count ) => limit >= 0 && count >= limit;
```

## Recipe: strip presentation on headless dedicated servers

On a host with no display, animating skinned meshes and running client controllers is wasted CPU. Wrap the engine flag so you can fake/test it in-editor, then periodically disable every `SkinnedModelRenderer` and `PlayerController` — gated on host-only checks so it never fires for real players (dxrp: `game/code/GameManager.cs:47`, `game/code/GameNetworkManager.cs:179`).

```csharp
public static bool IsHeadless => Application.IsHeadless; // wrap so editor can fake it

private void HandleHeadlessServerOptimizations()
{
    if ( !Networking.IsHost || !Networking.IsActive || !GameManager.IsHeadless )
        return; // never disable controllers on clients/editor

    foreach ( var r in Scene.GetAllComponents<SkinnedModelRenderer>() )
        r.Enabled = false;
    foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        pc.Enabled = false;
}
```

## Recipe: custom GPU work via `CommandList` on the scene camera

For post-FX, procedural textures, or GPU-side sim surfaced as a sampleable `Texture`: build a `CommandList`, set inputs via `cmd.Attributes.Set`, dispatch compute, and insert `cmd.UavBarrier(tex)` *between dependent passes* so reads see finished writes. Register on the camera and ALWAYS remove on teardown or passes leak across hotload (sgba: `Code/Emulator/GbaVideo.Rendering.cs:218`, `:267`, `:485`; `Code/EmulatorComponent.cs:162`/`:95`).

```csharp
// build once
var cs  = new ComputeShader( "shaders/my_pass.shader" );
var cmd = new CommandList( "My PPU" );
var tex = Texture.CreateRenderTarget()
    .WithSize( w, h ).WithFormat( ImageFormat.RGBA8888 )
    .WithUAVBinding().WithGPUOnlyUsage().Create();

// per dispatch, ordering dependent passes:
cmd.Attributes.Set( "OutputColor", tex );
cmd.DispatchCompute( cs, w, h, 1 );
cmd.UavBarrier( tex ); // before any pass that reads `tex`

// register on the camera (OnEnabled) ...
_camera.AddCommandList( cmd, Stage.AfterOpaque, 0 );
// ... and ALWAYS remove on teardown (OnDisable/OnDestroy):
_camera.RemoveCommandList( cmd );
```

## Recipe: async GPU readback with `GetPixelsAsync`

Synchronous `texture.GetPixels()` forces a CPU/GPU sync point that blocks until the GPU finishes — a framerate cliff under load. Any feature reading a render target every frame (network video, live thumbnails, photo mode, AI vision) must read back asynchronously and accept the deferred callback (sgba: `Code/Emulator/GbaVideo.Rendering.cs:903`).

```csharp
tex.GetPixelsAsync<byte>( span =>
{
    // runs later, off the render-thread sync point
    var bytes = span.ToArray();
    OnPixels( bytes );
}, ImageFormat.RGBA8888, (0, 0, width, height) );
```

Reserve synchronous `GetPixels()` for rare one-shot captures where a single stall is acceptable.

## Recipe: low-latency PCM streaming with `SoundStream` + backpressure

For dynamically generated audio (synth, emulator/DSP output, networked voice), use `SoundStream`, not canned sound events. Prefill a little silence to avoid an initial underrun, defeat the 3D pipeline for UI/2D audio, and only feed samples while below a high-water mark or latency grows unbounded (sgba: `Code/EmulatorComponent.cs:194`; backpressure at `Code/EmulatorComponent.cs:404`).

```csharp
_audioStream = new SoundStream( sampleRate, channels );
_audioStream.WriteData( new short[samplesPerFrame * channels * PrefillFrames] ); // prefill silence
_soundHandle = _audioStream.Play( volume: 1f );

// 2D/UI: explicitly defeat spatialization or it gets panned/attenuated by listener position
_soundHandle.SpacialBlend = 0f;
_soundHandle.OcclusionEnabled = false;
_soundHandle.DistanceAttenuation = false;
_soundHandle.AirAbsorption = false;

// each tick: gate writes on the high-water mark
if ( _audioStream.QueuedSampleCount <= samplesPerFrame * HighWaterFrames )
    _audioStream.WriteData( pcm.AsSpan( 0, count ) );

// streams die on scene reload / device change — re-init when invalid
if ( !_soundHandle.IsValid() ) ReinitStream();
```

## Recipe: client sound scheduler with game-speed pitch + ambient crossfade

For timed/sequenced cues (countdowns, stingers) keep a small static client scheduler: pre-queue `(soundEvent, Time.Now + delay)`, flush due cues in `Update()`, track every returned `SoundHandle` so you can prune and `StopAll`. Push `Pitch = SpeedPercent / 100f` onto each handle so audio follows time scaling, and lerp an ambient handle's `Volume` toward a target each frame to crossfade. Audio is client-only — early-return on the dedicated server (garryware: `Code/Ware/UI/WareSounds.cs:135`, `:214`, `:222`).

```csharp
public static void Update()
{
    if ( Application.IsDedicatedServer ) return;            // audio is client-side only
    ActiveHandles.RemoveAll( h => !h.IsValid() || h.IsStopped ); // prune dead handles

    for ( int i = QueuedSounds.Count - 1; i >= 0; i-- )
    {
        if ( Time.Now < QueuedSounds[i].PlayTime ) continue;
        Track( Sound.Play( QueuedSounds[i].SoundEvent ) );
        QueuedSounds.RemoveAt( i );
    }
    UpdateAmbient();
}

private static void Track( SoundHandle h )
{
    if ( !h.IsValid() ) return;
    h.Pitch = SpeedPercent / 100f; // follow Scene.TimeScale-derived speed
    ActiveHandles.Add( h );
}

// ambient crossfade: step Volume toward the target each frame
var step = Time.Delta * 1.2f;
_ambientHandle.Pitch = SpeedPercent / 100f;
if ( v < _target ) v = MathF.Min( _target, v + step );
else if ( v > _target ) v = MathF.Max( _target, v - step );
_ambientHandle.Volume = v;
```

## Gotcha table

| Gotcha | Why it bites | Fix |
| --- | --- | --- |
| `async void` in the per-frame lifecycle | Continuation outlives the GameObject/scene, isn't cancelled on disable/hotload, swallows exceptions | `TimeUntil` + `Destroy` for delays; reserve async loops for lifetime-owning components |
| Faulted `RunInThreadAsync` Task | Invisible by default — worker dies silently, you debug a "frozen" system with no error | Start an observe-task that `await`s the worker and logs |
| Worker not torn down | CTS not cancelled / wake signals not cleared → thread leaks across hotload & scene change | Cancel CTS + clear signals in `OnDisable`/`OnDestroy` |
| `AddCommandList` without `RemoveCommandList` | Passes accumulate on the camera across hotloads | Pair every `AddCommandList` with `RemoveCommandList` on teardown |
| Missing `UavBarrier` between compute passes | Second pass may read before the first finishes writing → garbage/nondeterministic | Insert `cmd.UavBarrier(tex)` between dependent passes |
| Synchronous `GetPixels()` per frame | CPU/GPU sync point — a framerate cliff that only shows up under real load | `GetPixelsAsync` with a deferred callback |
| Full-scene scan to count limits | O(scene), silently a hotspot as object counts grow | Per-player tracked `List` + `HashSet`, O(player's objects) |
| Per-player `List`/`HashSet` pruned separately | The O(1) dedupe set drifts out of sync with the list | Prune both together inside the count loop |
| Headless strip disabling `PlayerController` on clients | Disables controllers for real players | Gate on wrapped `IsHeadless` + `Networking.IsHost`/`IsActive` |
| Reallocating triple-buffer slots per frame | Defeats the point (GC churn) and breaks the lock-free invariant | Preallocate three slots once; only swap with `Interlocked.Exchange` |
| `SoundStream.WriteData` ungated | Accumulates unbounded latency + buffer growth | Gate on `QueuedSampleCount <= highWater` (a few frames of audio) |
| New `SoundStream` not prefilled | Audible startup underrun before the generator catches up | Prefill a short buffer of silence |
| Streamed/UI sound spatialized by default | Panned/attenuated by listener position | Set `SpacialBlend=0`, `OcclusionEnabled/DistanceAttenuation/AirAbsorption=false` |
| Dropping the `SoundHandle` from `Sound.Play` | Can't stop/fade/retune/speed-scale a sound after the fact | Keep + track handles; prune invalid/stopped ones |
| Audio in networked/proxy/server code | Won't play or plays on the wrong machine | Run scheduler on the local client; early-return on `Application.IsDedicatedServer` |

Verify live: API names drift between SDK builds — confirm exact members (`GameTask.RunInThreadAsync`, `CommandList`, `Texture.CreateRenderTarget`, `GetPixelsAsync`, `SoundStream`, `SoundHandle.SpacialBlend`, `ConVarFlags`) with `describe_type`/`search_types`/`get_method_signature`; bridge reflection is authoritative for the installed SDK. No bridge tool profiles frame time, GC allocations, ConVars, or background-task/CommandList state, so these patterns are verified by static inspection — measure in-engine before optimizing.

See also: **sbox-api** (resolve exact type/method signatures) and **sbox-build-feature** (screenshot-driven iteration loop for landing the change).

## Corpus refresh (2026): more reference implementations

Seven net-new patterns sourced from real public-source s&box games (2026 mining pass).

### Recipe: GPU-instanced scatter with frustum + distance culling — no per-frame allocation

For dense decorative worldgen (trees, rocks, foliage), GPU instancing via `SceneCustomObject` beats spawning individual GameObjects by orders of magnitude. The key discipline: reuse arrays and pass `Span<Transform>` so nothing allocates on the hot path (bublic.stone_by_stone: `Code/RecourcesGeneratorComponent.cs`).

```csharp
// One SceneCustomObject with RenderOverride — lives as long as the component
_renderer = new SceneCustomObject( Scene.SceneWorld );
_renderer.RenderOverride = RenderInstances;

private readonly Transform[] _transformBuf = new Transform[MaxInstances]; // preallocated once

void RenderInstances( SceneObject self )
{
    var cam   = Scene.Camera;
    var frustum = new Frustum( cam.ViewProjectionMatrix ); // current frame frustum
    int count = 0;
    foreach ( var inst in _instances )
    {
        var bounds = new BBox( inst.Pos, 1f ).Grow( 0.5f );
        var distSq = (inst.Pos - cam.WorldPosition).LengthSquared;
        if ( distSq > MaxDistanceSq ) continue;              // distance² — no sqrt
        if ( !frustum.IsInside( bounds ) ) continue;         // frustum cull
        _transformBuf[count++] = inst.Transform;
    }
    if ( count == 0 ) return;
    Graphics.DrawModelInstanced( _model, _transformBuf.AsSpan( 0, count ), _attrs );
}
```

Anti-pattern: allocating `new Transform[count]` each frame for the span argument triggers GC churn on the hot render path. Preallocate the buffer to `MaxInstances` once.

### Recipe: async respawn with a generation guard — cancel stale Tasks after reset

When a harvested resource should respawn after a delay, an `async Task` is natural — but naively using `await Task.DelaySeconds` lets stale continuations fire after a scene reset or the component is disabled. Capture an integer "generation" at Task start and bail if it has advanced (bublic.stone_by_stone: `Code/RecourcesGeneratorComponent.cs`).

```csharp
private int _spawnGeneration;

private async Task RespawnResourceAsync( ResourceInstance res, int generation )
{
    await Task.DelaySeconds( res.RespawnDelay );
    if ( generation != _spawnGeneration ) return; // scene reset / OnDisabled fired
    if ( !this.IsValid() ) return;
    SpawnReal( res );
}

protected override void OnDisabled()
{
    _spawnGeneration++;    // invalidates every in-flight Task
    _renderer?.Delete();
}
```

This is the canonical fix for "zombie resurrections" — objects reappearing after a reset because a delayed Task continued past the disable/scene-reload boundary.

### Recipe: `BuildHash` from cheap revision counters — O(changes) Razor re-renders

The anti-pattern `BuildHash() => HashCode.Combine(Time.Delta)` (seen in bublic.stone_by_stone all-panel shortcut) forces a full re-render every frame — correct for data that always changes, wasteful for panels that update rarely. The fix: expose a monotonic `Revision` int that bumps only on real change, and quantize continuous progress so the hash changes at most N times per unit (pldr.duck_pond: `Code/UI/DuckSwitcherPanel.razor`).

```text
data-source state:
  expose a read-only monotonic revision counter
  increment it only when the corresponding collection or value actually changes

panel hash calculation:
  combine the revision counters of each discrete dependency
  quantize continuous progress into the desired number of visible buckets
  include that bucket index instead of frame time
```

Anti-pattern: `HashCode.Combine(Time.Delta)` is fine for a single counter panel but silently becomes a 60 fps re-render budget hole when multiplied across many panels. Use it deliberately, not by default.

### Recipe: `[DontExecuteOnServer]` — declarative server strip for visual-only components

Rather than scattering `if (Application.IsHeadless) return;` checks, annotate visual-only components with `[DontExecuteOnServer]` so the engine never runs `OnUpdate`/`OnEnabled` on a dedicated server at all (pldr.duck_pond: `Code/Water/WaterManager.cs`, `WaterQuad.cs`, `WaterBodyRenderer.cs`).

```csharp
[DontExecuteOnServer]
public sealed class WaterBodyRenderer : Component
{
    // OnUpdate, OnEnabled, etc. never fire on a headless server.
    // No runtime guard needed inside the methods.
}
```

This is the declarative companion to the existing "headless strip" recipe (which uses runtime `Application.IsHeadless` checks). Use `[DontExecuteOnServer]` for components that are *always* presentation-only; use the runtime check when the strip should be conditional or you need teardown logic.

### Recipe: spatial hash for O(1) point-in-zone lookup

When you need to answer "which zone does this point belong to?" many times per frame (sector hit-tests, navmesh zone queries, dungeon room lookups), a brute-force per-zone polygon test is O(N×V). Bucket zones by world cell and only test the handful whose cell overlaps the query point (ataco.sdoomresurrection: `Code/entities/DoomMap.cs`, `Sector.SectorChunks`).

```csharp
// Build once (e.g. OnStart / after worldgen):
private readonly Dictionary<(int, int), List<Zone>> _grid = new();
private const float CellSize = 256f;

void AddZone( Zone z )
{
    var cell = ToCell( z.Centroid );
    if ( !_grid.TryGetValue( cell, out var bucket ) )
        _grid[cell] = bucket = new();
    bucket.Add( z );
}
(int, int) ToCell( Vector2 p ) => ((int)MathX.Floor( p.X / CellSize ),
                                    (int)MathX.Floor( p.Y / CellSize ));

Zone FindZone( Vector2 point )
{
    var cell = ToCell( point );
    if ( !_grid.TryGetValue( cell, out var bucket ) ) return null;
    foreach ( var z in bucket )
        if ( z.ContainsPoint( point ) ) return z; // polygon test on small bucket only
    return null;
}
```

Use `MathX.Floor` — `System.MathF` does not exist in the s&box sandbox.

### Recipe: frame-budgeted main-thread drain queue

For bursty work (mass disconnects, batch prop destruction, NPC cleanup) that must run on the main thread but must not block an entire frame, drain under a dual budget: max items AND max wall-clock milliseconds per tick, whichever triggers first. Swallow per-item exceptions so one bad item does not stall the queue (artisan.darkrpog: `Concurrency/FrameBudgetQueue.cs`).

```csharp
public sealed class FrameBudgetQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly Action<T>          _process;
    private readonly int                _maxItemsPerTick;
    private readonly float              _maxMsPerTick;

    public void Enqueue( T item ) => _queue.Enqueue( item );

    public void Tick()  // call from OnUpdate on the main thread
    {
        var start = RealTime.Now;
        var count = 0;
        while ( _queue.TryDequeue( out var item ) )
        {
            try { _process( item ); }
            catch ( Exception ex ) { Log.Warning( $"FrameBudgetQueue item faulted: {ex.Message}" ); }
            if ( ++count >= _maxItemsPerTick ) break;
            if ( (RealTime.Now - start) * 1000f >= _maxMsPerTick ) break;
        }
    }
}
```

Pairs with `GameTask.RunInThreadAsync` for the *producer* side: background thread enqueues work; main-thread `Tick()` drains it safely under budget.

### Recipe: coalescing off-thread write queue with priority backpressure

Saving a player's state on every balance change or inventory move hammers the disk if done naively. A coalescing write queue deduplicates: a second `Enqueue` for the same path replaces the pending payload, and low-priority writes are dropped under memory pressure while gameplay saves are never dropped (artisan.darkrpog: `Concurrency/PersistenceFlushQueue.cs`).

```csharp
// Priority order — lower value = higher priority = never dropped
public enum WritePriority { Critical = 0, Gameplay = 1, Autosave = 2, Diagnostic = 3 }

private readonly Dictionary<string, (string payload, WritePriority priority)> _pending = new();
private readonly object _lock = new();

public void Enqueue( string normalizedPath, string payload, WritePriority priority )
{
    lock ( _lock )
    {
        // Coalesce: replace an existing entry for the same path
        _pending[normalizedPath] = (payload, priority);
        if ( _pending.Count > MaxPendingPaths )
            DropLowestPriority(); // drop Diagnostic/Autosave first; never drop Critical/Gameplay
    }
}

// Worker (GameTask.RunInThreadAsync) drains the dict, writes files, signals completion.
// DrainSynchronouslyForShutdown() flushes everything before the process exits.
```

Critical rule: validate paths inside `Enqueue` (reject `..`, absolute paths, `:`) so a bad key can't write outside the save directory.

### Gotcha additions

| Gotcha | Why it bites | Fix |
| --- | --- | --- |
| `new Transform[count]` inside `RenderOverride` | Per-frame GC allocation on the render hot path | Preallocate `Transform[]` to `MaxInstances` once; pass `AsSpan(0, count)` |
| `await Task.DelaySeconds` without a generation guard | Continuation fires after scene reset / component disable, resurrecting dead objects | Capture `_spawnGeneration` before the delay; bail if it changed |
| `BuildHash() => HashCode.Combine(Time.Delta)` on every panel | 60 fps re-renders across all panels — silent perf sink at scale | Use monotonic `Revision` ints + quantized continuous values |
| Per-component `OnUpdate` on hundreds of entities | 500 components × `OnUpdate` = 500 virtual calls + cache misses per frame | Centralize in a `GameObjectSystem` that single-passes `Scene.GetAll<T>()` |
| Brute-force point-in-zone scan per frame | O(N×V) test silently becomes a hotspot as zone count grows | Spatial hash by cell size; test only the bucket that contains the point |
| Synchronous disk write on the main thread (save on every mutation) | Blocks the frame for the duration of the I/O | Off-thread coalescing queue; gameplay saves are never dropped, low-priority writes are |
| `[DontExecuteOnServer]` omitted on visual-only components | OnUpdate runs on the headless server burning CPU for nothing | Add `[DontExecuteOnServer]` to any component that is presentation-only |

Read these games for full working implementations: **bublic.stone_by_stone** (GPU-instanced scatter + generation-guard respawn), **pldr.duck_pond** (revision-based `BuildHash`, `[DontExecuteOnServer]`, `CommandList` ordering), **ataco.sdoomresurrection** (spatial hash sector lookup, runtime mesh-from-polygon), **artisan.darkrpog** (FrameBudgetQueue, coalescing off-thread writes, `PerFramePanelCache`, distance-gated `OnUpdate`). Use the public links in [SOURCE-PROVENANCE.md](../SOURCE-PROVENANCE.md) to inspect the cited projects.
