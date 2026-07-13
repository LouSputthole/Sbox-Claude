using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// =============================================================================
//  Stats & Achievements pack -- five scaffolds (code-gen; scene/file-mutating):
//
//    add_leaderboard_stat        batched write-side stat reporter (Stats.Increment)
//    create_achievement_set      achievement engine + unlock toast HUD (razor)
//    add_achievement_trigger     trigger zone that fires Progress/Unlock (creates a GO)
//    create_speedrun_leaderboard timer + min-aggregation submit + friends-filter panel
//    create_elo_rating_system    host-authoritative elo math + persisted ratings
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs,
//  so it reuses the shared statics on ClaudeBridge (TryResolveProjectPath,
//  SanitizeIdentifier, ParseVector3, SerializeGo) and ScaffoldHelpers
//  (PrepareCodeFile / WriteCode / Utf8NoBom). Handler code here is UNSANDBOXED
//  editor code (System.* is fine).
//
//  The C# *strings these handlers WRITE TO DISK* are SANDBOXED game code and must
//  obey the s&box sandbox rules:
//    - System.Math / MathF compile on the current SDK; Array.Clone() is still
//      whitelist-blocked (not used here).
//    - Sandbox.Services.Stats API verified live 2026-07-12: Increment(name,amount),
//      SetValue(name,amount,context,data)  <-- NO 2-arg overload; call with
//      explicit trailing nulls. Flush() / FlushAsync(CancellationToken).
//    - Leaderboards.GetFromStat(statName) returns Board2 with SetFriendsOnly(bool),
//      SetAggregationMin(), SetSortAscending(), Refresh(CancellationToken)
//      (token REQUIRED) -- all verified live via describe_type.
//    - NetDictionary<TKey,TValue> verified live: indexer, TryGetValue,
//      GetEnumerator(KeyValuePair), Count.
//    - FileSystem is fully qualified as Sandbox.FileSystem in generated code
//      (bare FileSystem is ambiguous in some compile contexts).
//    - Razor rules (razor_lint): PanelComponent MUST override BuildHash, no
//      switch EXPRESSIONS in @code, ASCII only in @code, class selectors only
//      in .razor.scss.
//
//  Register(...) lines + the _sceneMutatingCommands additions live in
//  MyEditorMenu.cs to keep the files decoupled (see the implementation summary).
// =============================================================================

/// <summary>
/// Shared helpers for the stats/achievements handlers. Mirrors GameFeelHelpers /
/// CreateSaveSystemHandler.PlaceOnTarget but stays local to this file so the
/// handler families remain decoupled.
/// </summary>
internal static class StatsAchievementsHelpers
{
	/// <summary>
	/// Attach a freshly generated component class to an existing GameObject by GUID.
	/// Only succeeds if the type is already in the TypeLibrary (i.e. after a
	/// hotload). Returns the serialized GameObject on success, null + note otherwise.
	/// </summary>
	public static object PlaceOnTarget( string targetId, string className, out string note )
	{
		note = null;
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) { note = "No active scene to place into."; return null; }
		if ( !Guid.TryParse( targetId, out var guid ) ) { note = "Invalid targetId GUID."; return null; }
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) { note = $"Target GameObject not found: {targetId}"; return null; }
		var typeDesc = Game.TypeLibrary.GetType( className );
		if ( typeDesc == null )
		{
			note = $"Generated {className}.cs but it is not in the TypeLibrary yet -- trigger_hotload, then add it with add_component_with_properties.";
			return null;
		}
		try { go.Components.Create( typeDesc ); return ClaudeBridge.SerializeGo( go ); }
		catch ( Exception ex ) { note = $"Placement failed ({ex.Message})."; return null; }
	}

	/// <summary>
	/// Escape an arbitrary string for embedding inside a REGULAR (non-verbatim)
	/// C# string literal in generated code: backslash and double-quote escaped,
	/// newlines flattened so a multi-line value cannot break the literal.
	/// </summary>
	public static string EscapeLit( string s )
	{
		if ( string.IsNullOrEmpty( s ) ) return "";
		return s.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ).Replace( "\r", " " ).Replace( "\n", " " );
	}

	/// <summary>
	/// Sanitize an achievement/stat id into the lowercase [a-z0-9_-] form the
	/// backend expects. Falls back to <paramref name="fallback"/> if nothing survives.
	/// </summary>
	public static string SanitizeStatId( string raw, string fallback )
	{
		if ( string.IsNullOrWhiteSpace( raw ) ) return fallback;
		var sb = new StringBuilder( raw.Length );
		foreach ( var c in raw.ToLowerInvariant() )
		{
			if ( ( c >= 'a' && c <= 'z' ) || ( c >= '0' && c <= '9' ) || c == '_' || c == '-' )
				sb.Append( c );
		}
		return sb.Length == 0 ? fallback : sb.ToString();
	}

	/// <summary>Invariant float literal for generated code, with the trailing f.</summary>
	public static string F( float v ) => v.ToString( System.Globalization.CultureInfo.InvariantCulture ) + "f";

	/// <summary>Invariant double literal for generated code (no suffix).</summary>
	public static string D( double v ) => v.ToString( System.Globalization.CultureInfo.InvariantCulture );
}

// -----------------------------------------------------------------------------
// add_leaderboard_stat -- write-side stat reporter. The READ side already exists
// (create_leaderboard_panel); this is its partner: gameplay code calls the
// static <Name>.Report("kills", 1) from anywhere, the component accumulates
// locally and pushes batched deltas to Sandbox.Services.Stats.Increment on a
// timer. Baseline-delta bookkeeping means a failed/partial flush retries the
// un-sent remainder next tick instead of double-counting.
// -----------------------------------------------------------------------------
public class AddLeaderboardStatHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "StatReporter", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float flushInterval = p.TryGetProperty( "flushIntervalSeconds", out var fv ) && fv.TryGetSingle( out var ff ) ? ff : 12f;
			double maxChunk     = p.TryGetProperty( "maxChunk",             out var mv ) && mv.TryGetDouble( out var md ) ? md : 1000.0;

			// Defensive clamps -- a 0 interval would flush every frame, a 0 chunk would loop forever.
			if ( flushInterval < 1f ) flushInterval = 1f;
			if ( maxChunk < 1.0 )     maxChunk = 1.0;

			var code = BuildCode( className, flushInterval, maxChunk );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = StatsAchievementsHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				flushIntervalSeconds = flushInterval,
				maxChunk,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place ONE in the scene: add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Report from any game code: {className}.Report( \"kills\", 1 ) -- amounts accumulate locally and flush as batched Stats.Increment deltas every {flushInterval.ToString( System.Globalization.CultureInfo.InvariantCulture )}s (also on disable/destroy).",
					"Stats are PER LOCAL PLAYER -- each client reports its own player's stats; no host gating needed.",
					"The stat must be registered for the project ident on sbox.game before leaderboards read it; pair with create_leaderboard_panel (read side)."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_leaderboard_stat failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float flushInterval, double maxChunk )
	{
		string fi = StatsAchievementsHelpers.F( flushInterval );
		string mc = StatsAchievementsHelpers.D( maxChunk );

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- batched write-side stat reporter for Sandbox.Services.Stats.
/// Drop ONE in the scene. Gameplay code calls the static Report() from anywhere:
///
///   {className}.Report( ""kills"", 1 );
///   {className}.Report( ""gold_earned"", 250 );
///
/// Amounts accumulate locally and flush as Stats.Increment deltas every
/// FlushIntervalSeconds (and on disable/destroy). Baseline-delta bookkeeping:
/// only the un-flushed remainder is ever sent, so a partial flush retries
/// instead of double-counting. Deltas larger than MaxChunk are sent in chunks.
///
/// Stats are PER LOCAL PLAYER -- each client reports its own stats, so there is
/// no host/proxy gating here. Pair with a leaderboard panel for the read side.
/// </summary>
public sealed class {className} : Component
{{
	/// Seconds between batched flushes to the backend.
	[Property] public float FlushIntervalSeconds {{ get; set; }} = {fi};

	/// Largest amount sent in a single Stats.Increment call; bigger deltas are chunked.
	[Property] public double MaxChunk {{ get; set; }} = {mc};

	public static {className} Instance {{ get; private set; }}

	// Total reported per stat this session (the local ledger).
	private readonly Dictionary<string, double> _reported = new Dictionary<string, double>();
	// Baseline: how much of _reported has already been handed to the backend.
	private readonly Dictionary<string, double> _flushed = new Dictionary<string, double>();

	private TimeUntil _nextFlush;

	/// <summary>Queue an increment for a stat. Safe to call every frame -- amounts
	/// batch locally and flush on the timer. Falls back to a direct
	/// Stats.Increment when no {className} is in the scene, so nothing is lost.</summary>
	public static void Report( string stat, double amount )
	{{
		if ( string.IsNullOrWhiteSpace( stat ) || amount <= 0 ) return;

		var inst = Instance;
		if ( inst == null )
		{{
			Sandbox.Services.Stats.Increment( stat, amount );
			return;
		}}

		double total;
		inst._reported.TryGetValue( stat, out total );
		inst._reported[stat] = total + amount;
	}}

	protected override void OnEnabled()
	{{
		Instance = this;
		_nextFlush = FlushIntervalSeconds;
	}}

	protected override void OnDisabled()
	{{
		FlushNow();
		if ( Instance == this ) Instance = null;
	}}

	protected override void OnUpdate()
	{{
		if ( FlushIntervalSeconds <= 0f ) return;
		if ( _nextFlush )
		{{
			_nextFlush = FlushIntervalSeconds;
			FlushNow();
		}}
	}}

	/// <summary>Push every un-flushed delta to Sandbox.Services.Stats immediately.</summary>
	public void FlushNow()
	{{
		bool sentAny = false;

		foreach ( var kv in _reported )
		{{
			double already;
			_flushed.TryGetValue( kv.Key, out already );
			double delta = kv.Value - already;
			if ( delta <= 0 ) continue;

			// Chunk oversized deltas so no single increment exceeds MaxChunk.
			double remaining = delta;
			double chunk = MaxChunk < 1.0 ? 1.0 : MaxChunk;
			while ( remaining > 0 )
			{{
				double send = remaining > chunk ? chunk : remaining;
				Sandbox.Services.Stats.Increment( kv.Key, send );

				// Advance the baseline per chunk -- if a later chunk throws, the
				// already-sent portion is never re-sent.
				double f;
				_flushed.TryGetValue( kv.Key, out f );
				_flushed[kv.Key] = f + send;

				remaining -= send;
				sentAny = true;
			}}
		}}

		if ( sentAny )
			Sandbox.Services.Stats.Flush();
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_achievement_set -- achievement engine + optional unlock toast HUD.
// Generates {Name}.cs (defs, per-achievement progress persisted via
// Sandbox.FileSystem.Data JSON, Progress/Unlock API, static OnAchievementUnlocked,
// optional Stats.Increment mirror) plus {Name}Toast.razor / .razor.scss
// (razor_lint clean: BuildHash override, no switch expressions, ASCII @code).
// -----------------------------------------------------------------------------
public class CreateAchievementSetHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "AchievementSet", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var directory   = p.TryGetProperty( "directory",   out var d )  && !string.IsNullOrWhiteSpace( d.GetString() )  ? d.GetString()  : "Code";
			var saveFile    = p.TryGetProperty( "fileName",    out var fn ) && !string.IsNullOrWhiteSpace( fn.GetString() ) ? fn.GetString() : "achievements.json";
			bool mirror     = !p.TryGetProperty( "mirrorToStats", out var mts ) || mts.ValueKind != JsonValueKind.False;
			bool makeToast  = !p.TryGetProperty( "makeToast",     out var mt )  || mt.ValueKind  != JsonValueKind.False;
			float toastSecs = p.TryGetProperty( "toastSeconds", out var ts ) && ts.TryGetSingle( out var tsf ) ? tsf : 4f;
			if ( toastSecs < 0.5f ) toastSecs = 0.5f;

			// Parse the achievement definitions (id/title/description/target) or fall
			// back to three sample achievements the user can edit in the generated file.
			var defs = ParseDefs( p );

			var code = BuildCode( className, saveFile, mirror, defs );
			ScaffoldHelpers.WriteCode( fullPath, code );

			// Optional toast HUD: {Name}Toast.razor + .razor.scss next to the .cs.
			string relToastRazor = null, relToastScss = null;
			var toastClass = className + "Toast";
			if ( makeToast )
			{
				var razorFile = toastClass + ".razor";
				var scssFile  = toastClass + ".razor.scss";
				if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, razorFile ), out var razorPath, out var razorErr ) )
					return Task.FromResult<object>( new { error = razorErr } );
				if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, scssFile ), out var scssPath, out var scssErr ) )
					return Task.FromResult<object>( new { error = scssErr } );
				if ( File.Exists( razorPath ) )
					return Task.FromResult<object>( new { error = $"File already exists: {directory}/{razorFile}. Choose a different name." } );

				ScaffoldHelpers.WriteCode( razorPath, BuildToastRazor( className, toastClass, toastSecs ) );
				ScaffoldHelpers.WriteCode( scssPath,  BuildToastScss() );
				relToastRazor = $"{directory}/{razorFile}";
				relToastScss  = $"{directory}/{scssFile}";
			}

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = StatsAchievementsHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				toastRazorPath = relToastRazor,
				toastScssPath = relToastScss,
				toastClassName = makeToast ? toastClass : null,
				achievements = defs.Select( a => a.Id ).ToArray(),
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place ONE in the scene: add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Report progress from game code: {className}.Instance?.Progress( \"{defs[0].Id}\", 1 ) or force with Unlock( id ). Progress persists to Sandbox.FileSystem.Data (\"{saveFile}\").",
					makeToast
						? $"The toast HUD ({toastClass}) renders only under a ScreenPanel host -- add_screen_panel, then add_component_with_properties (component=\"{toastClass}\") on the panel object."
						: "No toast HUD was generated (makeToast=false) -- subscribe to the static OnAchievementUnlocked yourself.",
					"add_achievement_trigger creates world trigger zones that call Progress/Unlock on this set by class name."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_achievement_set failed: {ex.Message}" } );
		}
	}

	internal struct DefSpec { public string Id, Title, Description; public double Target; }

	static List<DefSpec> ParseDefs( JsonElement p )
	{
		var defs = new List<DefSpec>();
		if ( p.TryGetProperty( "achievements", out var arr ) && arr.ValueKind == JsonValueKind.Array )
		{
			int i = 0;
			foreach ( var a in arr.EnumerateArray() )
			{
				if ( a.ValueKind != JsonValueKind.Object ) continue;
				i++;
				var id    = a.TryGetProperty( "id",          out var av ) ? av.GetString() : null;
				var title = a.TryGetProperty( "title",       out var tv ) ? tv.GetString() : null;
				var desc  = a.TryGetProperty( "description", out var dv ) ? dv.GetString() : null;
				double target = a.TryGetProperty( "target",  out var gv ) && gv.TryGetDouble( out var gd ) ? gd : 1.0;
				if ( target < 1.0 ) target = 1.0;

				defs.Add( new DefSpec
				{
					Id          = StatsAchievementsHelpers.SanitizeStatId( id, $"achievement_{i}" ),
					Title       = string.IsNullOrWhiteSpace( title ) ? $"Achievement {i}" : title,
					Description = desc ?? "",
					Target      = target
				} );
			}
		}

		if ( defs.Count == 0 )
		{
			defs.Add( new DefSpec { Id = "first_steps", Title = "First Steps", Description = "Enter the world.",     Target = 1 } );
			defs.Add( new DefSpec { Id = "collector",   Title = "Collector",   Description = "Gather 10 pickups.",   Target = 10 } );
			defs.Add( new DefSpec { Id = "veteran",     Title = "Veteran",     Description = "Finish 5 full rounds.", Target = 5 } );
		}

		return defs;
	}

	static string BuildCode( string className, string saveFile, bool mirror, List<DefSpec> defs )
	{
		var sb = new StringBuilder();
		foreach ( var a in defs )
		{
			sb.Append( "\t\tnew AchievementDef { Id = \"" ).Append( a.Id )
			  .Append( "\", Title = \"" ).Append( StatsAchievementsHelpers.EscapeLit( a.Title ) )
			  .Append( "\", Description = \"" ).Append( StatsAchievementsHelpers.EscapeLit( a.Description ) )
			  .Append( "\", Target = " ).Append( StatsAchievementsHelpers.D( a.Target ) )
			  .Append( " },\n" );
		}
		var defLines = sb.ToString().TrimEnd( '\n' );
		string save  = StatsAchievementsHelpers.EscapeLit( saveFile );
		string mirrorStr = mirror ? "true" : "false";

		return $@"using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// {className} -- achievement engine. Drop ONE in the scene; gameplay code talks
/// to it through {className}.Instance:
///
///   {className}.Instance?.Progress( ""collector"", 1 );  // advance toward Target
///   {className}.Instance?.Unlock( ""first_steps"" );      // force-unlock
///
/// Progress + unlocks persist to Sandbox.FileSystem.Data (""{save}"") so they
/// survive restarts. The static OnAchievementUnlocked fires on every unlock --
/// the generated toast HUD subscribes to it. When MirrorToStats is on, each
/// unlock also fires Stats.Increment( ""ach-&lt;id&gt;"", 1 ) so unlock rates show up
/// in the project's Sandbox.Services stats.
///
/// LOCAL-only by design: achievements belong to the local player on each client.
/// </summary>
public sealed class {className} : Component
{{
	/// One achievement definition. Edit the Defaults list below or add via code.
	public class AchievementDef
	{{
		public string Id {{ get; set; }}
		public string Title {{ get; set; }}
		public string Description {{ get; set; }}
		public double Target {{ get; set; }} = 1;
	}}

	public class SaveState
	{{
		public int Version {{ get; set; }} = 1;
		public Dictionary<string, double> Progress {{ get; set; }} = new Dictionary<string, double>();
		public List<string> Unlocked {{ get; set; }} = new List<string>();
	}}

	public static {className} Instance {{ get; private set; }}

	/// Save file name inside Sandbox.FileSystem.Data.
	[Property] public string FileName {{ get; set; }} = ""{save}"";

	/// Mirror each unlock into Sandbox.Services.Stats as ach-&lt;id&gt; += 1.
	[Property] public bool MirrorToStats {{ get; set; }} = {mirrorStr};

	/// Fires once per unlock with the definition. The toast HUD subscribes here.
	public static Action<AchievementDef> OnAchievementUnlocked {{ get; set; }}

	private readonly List<AchievementDef> _defs = new List<AchievementDef>()
	{{
{defLines}
	}};

	public IReadOnlyList<AchievementDef> Definitions => _defs;

	private SaveState _state = new SaveState();

	protected override void OnEnabled()
	{{
		Instance = this;
		Load();
	}}

	protected override void OnDestroy()
	{{
		if ( Instance == this ) Instance = null;
	}}

	public AchievementDef GetDefinition( string id ) => _defs.FirstOrDefault( a => a.Id == id );
	public bool IsUnlocked( string id ) => _state.Unlocked.Contains( id );

	public double GetProgress( string id )
	{{
		double v;
		_state.Progress.TryGetValue( id, out v );
		return v;
	}}

	/// <summary>Advance an achievement by amount; unlocks when progress reaches Target.</summary>
	public void Progress( string id, double amount = 1 )
	{{
		var def = GetDefinition( id );
		if ( def == null )
		{{
			Log.Warning( $""[{className}] Unknown achievement id '{{id}}'"" );
			return;
		}}
		if ( amount <= 0 || IsUnlocked( id ) ) return;

		double v;
		_state.Progress.TryGetValue( id, out v );
		v += amount;
		if ( v > def.Target ) v = def.Target;
		_state.Progress[id] = v;

		if ( v >= def.Target )
			UnlockInternal( def );
		else
			Save();
	}}

	/// <summary>Force-unlock an achievement regardless of progress.</summary>
	public void Unlock( string id )
	{{
		var def = GetDefinition( id );
		if ( def == null )
		{{
			Log.Warning( $""[{className}] Unknown achievement id '{{id}}'"" );
			return;
		}}
		if ( IsUnlocked( id ) ) return;
		_state.Progress[id] = def.Target;
		UnlockInternal( def );
	}}

	/// <summary>Wipe all progress and unlocks (debug / new-save).</summary>
	public void ResetAll()
	{{
		_state = new SaveState();
		Save();
	}}

	private void UnlockInternal( AchievementDef def )
	{{
		_state.Unlocked.Add( def.Id );
		Save();

		if ( MirrorToStats )
		{{
			Sandbox.Services.Stats.Increment( ""ach-"" + def.Id, 1 );
			Sandbox.Services.Stats.Flush();
		}}

		Log.Info( $""[{className}] Unlocked: {{def.Title}}"" );
		OnAchievementUnlocked?.Invoke( def );
	}}

	private void Load()
	{{
		var loaded = Sandbox.FileSystem.Data.ReadJsonOrDefault<SaveState>( FileName, null );
		if ( loaded == null || loaded.Version != 1 )
		{{
			_state = new SaveState();
			return;
		}}
		if ( loaded.Progress == null ) loaded.Progress = new Dictionary<string, double>();
		if ( loaded.Unlocked == null ) loaded.Unlocked = new List<string>();
		_state = loaded;
	}}

	private void Save()
	{{
		Sandbox.FileSystem.Data.WriteJson( FileName, _state );
	}}
}}
";
	}

	static string BuildToastRazor( string setClass, string toastClass, float toastSecs )
	{
		string tsec = StatsAchievementsHelpers.F( toastSecs );

		// NOTE: all {{ }} inside the $@"" are C# string-escape doubled braces.
		// The generated .razor uses single { } for Razor/C# expressions.
		return $@"@using Sandbox;
@using Sandbox.UI;
@using System.Collections.Generic;
@inherits PanelComponent;

@* {toastClass} -- achievement unlock toasts. Host it under a ScreenPanel
   (add_screen_panel). Subscribes to {setClass}.OnAchievementUnlocked and shows
   each unlock for ToastSeconds. Nothing renders while the list is empty. *@

<div class=""achievement-toasts"">
	@foreach ( var t in _toasts )
	{{
		<div class=""toast"">
			<div class=""toast-title"">@t.Title</div>
			<div class=""toast-desc"">@t.Description</div>
		</div>
	}}
</div>

@code {{
	[Property] public float ToastSeconds {{ get; set; }} = {tsec};

	private class ToastItem
	{{
		public string Title;
		public string Description;
		public RealTimeSince Age;
	}}

	private List<ToastItem> _toasts = new List<ToastItem>();

	protected override void OnEnabled()
	{{
		{setClass}.OnAchievementUnlocked += HandleUnlock;
	}}

	protected override void OnDisabled()
	{{
		{setClass}.OnAchievementUnlocked -= HandleUnlock;
	}}

	private void HandleUnlock( {setClass}.AchievementDef def )
	{{
		var t = new ToastItem();
		t.Title = def.Title;
		t.Description = def.Description;
		t.Age = 0f;
		_toasts.Add( t );
		StateHasChanged();
	}}

	protected override void OnUpdate()
	{{
		for ( int i = _toasts.Count - 1; i >= 0; i-- )
		{{
			if ( _toasts[i].Age > ToastSeconds )
				_toasts.RemoveAt( i );
		}}
	}}

	protected override int BuildHash() => System.HashCode.Combine( _toasts.Count );
}}
";
	}

	static string BuildToastScss()
	{
		return @".achievement-toasts {
	position: absolute;
	top: 80px;
	right: 24px;
	display: flex;
	flex-direction: column;
	align-items: flex-end;
}

.toast {
	display: flex;
	flex-direction: column;
	background-color: rgba(20, 20, 24, 0.9);
	border-left: 3px solid #ffcf40;
	border-radius: 4px;
	padding: 10px 14px;
	margin-bottom: 8px;
	min-width: 240px;
}

.toast-title {
	font-size: 16px;
	font-weight: bold;
	color: #ffcf40;
}

.toast-desc {
	font-size: 13px;
	color: rgba(255, 255, 255, 0.8);
	margin-top: 2px;
}
";
	}
}

// -----------------------------------------------------------------------------
// add_achievement_trigger -- data-driven trigger zone. Generates a reusable
// trigger component (BoxCollider.IsTrigger + ITriggerListener + once-only latch)
// that calls Progress/Unlock on the achievement set BY CLASS NAME, and creates
// the GameObject with a sized BoxCollider NOW (scene-mutating). The generated
// component attaches only after a hotload -- the result's nextSteps carry the
// exact follow-up call.
// -----------------------------------------------------------------------------
public class AddAchievementTriggerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var name      = p.TryGetProperty( "name",      out var n ) && !string.IsNullOrWhiteSpace( n.GetString() ) ? n.GetString() : "AchievementTrigger";
			var directory = p.TryGetProperty( "directory", out var d ) && !string.IsNullOrWhiteSpace( d.GetString() ) ? d.GetString() : "Code";
			var setClass  = ClaudeBridge.SanitizeIdentifier(
				p.TryGetProperty( "achievementSetClass", out var sc ) && !string.IsNullOrWhiteSpace( sc.GetString() ) ? sc.GetString() : "AchievementSet",
				"AchievementSet" );

			var achievementId = StatsAchievementsHelpers.SanitizeStatId(
				p.TryGetProperty( "achievementId", out var aid ) ? aid.GetString() : null, "goal" );

			double amount   = p.TryGetProperty( "amount", out var am ) && am.TryGetDouble( out var amd ) ? amd : 1.0;
			if ( amount < 0 ) amount = 0;
			bool unlock     = p.TryGetProperty( "unlock",           out var ul ) && ul.ValueKind == JsonValueKind.True;
			bool onceOnly   = !p.TryGetProperty( "onceOnly",        out var oo ) || oo.ValueKind != JsonValueKind.False;
			bool destroy    = p.TryGetProperty( "destroyAfterFire", out var df ) && df.ValueKind == JsonValueKind.True;
			bool reuseClass = p.TryGetProperty( "reuseClass",       out var rc ) && rc.ValueKind == JsonValueKind.True;
			bool makeObject = !p.TryGetProperty( "createObject",    out var co ) || co.ValueKind != JsonValueKind.False;
			var triggerTag  = p.TryGetProperty( "triggerTag", out var tt ) && !string.IsNullOrWhiteSpace( tt.GetString() ) ? tt.GetString() : "player";

			// --- 1. Generate (or reuse) the trigger component class -------------
			var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
			if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, fileName ), out var fullPath, out var pathErr ) )
				return Task.FromResult<object>( new { error = pathErr } );

			var className = ClaudeBridge.SanitizeIdentifier( Path.GetFileNameWithoutExtension( fileName ) );
			bool wroteFile = false;

			if ( File.Exists( fullPath ) )
			{
				if ( !reuseClass )
					return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}. Pass reuseClass=true to place another zone with the existing class, or choose a different name." } );
			}
			else
			{
				Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
				var code = BuildCode( className, setClass, achievementId, amount, unlock, onceOnly, destroy, triggerTag );
				ScaffoldHelpers.WriteCode( fullPath, code );
				wroteFile = true;
			}

			// Warn (don't fail) when the referenced set class isn't compiled yet --
			// it may have just been generated and not hotloaded.
			string setNote = Game.TypeLibrary.GetType( setClass ) == null
				? $"'{setClass}' is not in the TypeLibrary. The generated code references {setClass}.Instance -- run create_achievement_set (name=\"{setClass}\") first if it doesn't exist yet, or the project will not compile."
				: null;

			// --- 2. Create the trigger GameObject with a sized BoxCollider NOW ---
			object goSerialized = null; bool attached = false; string placeNote = null;
			if ( makeObject )
			{
				var scene = SceneEditorSession.Active?.Scene;
				if ( scene == null )
					return Task.FromResult<object>( new { error = "No active scene" } );

				var go = scene.CreateObject( true );
				go.Name = p.TryGetProperty( "objectName", out var on ) && !string.IsNullOrWhiteSpace( on.GetString() )
					? on.GetString() : $"{className}Zone";

				if ( p.TryGetProperty( "position", out var pos ) )
					go.WorldPosition = ClaudeBridge.ParseVector3( pos );

				var collider = go.GetOrAddComponent<BoxCollider>();
				collider.IsTrigger = true;
				collider.Scale = p.TryGetProperty( "scale", out var scl )
					? ClaudeBridge.ParseVector3Flexible( scl )
					: new Vector3( 100f, 100f, 100f );

				// Attach the generated component if (and only if) it is already
				// compiled -- true when reusing an existing, hotloaded class.
				var typeDesc = Game.TypeLibrary.GetType( className );
				if ( typeDesc != null )
				{
					var comp = go.Components.Create( typeDesc );
					attached = comp != null;
					if ( attached )
					{
						// Override the baked defaults for THIS zone (reuseClass path).
						TrySet( typeDesc, comp, "AchievementId",    achievementId );
						TrySet( typeDesc, comp, "Amount",           amount );
						TrySet( typeDesc, comp, "UnlockDirectly",   unlock );
						TrySet( typeDesc, comp, "OnceOnly",         onceOnly );
						TrySet( typeDesc, comp, "DestroyAfterFire", destroy );
						TrySet( typeDesc, comp, "TriggerTag",       triggerTag );
					}
				}
				else
				{
					placeNote = $"{className} is not in the TypeLibrary yet -- trigger_hotload, then attach it to this object (see nextSteps).";
				}

				goSerialized = ClaudeBridge.SerializeGo( go );
			}

			return Task.FromResult<object>( new
			{
				created = wroteFile,
				path = $"{directory}/{fileName}",
				className,
				achievementSetClass = setClass,
				achievementId,
				gameObject = goSerialized,
				attached,
				note = setNote ?? placeNote,
				nextSteps = new[]
				{
					wroteFile ? $"trigger_hotload to compile {className} into the game assembly." : $"{className} already existed (reuseClass) -- no hotload needed if it was compiled before.",
					attached
						? $"{className} is attached and configured on the new zone object."
						: $"After the hotload: add_component_with_properties on the zone object id with component=\"{className}\" and properties {{\"AchievementId\":\"{achievementId}\"}} (plus Amount/UnlockDirectly/OnceOnly/DestroyAfterFire/TriggerTag overrides as needed).",
					$"The zone fires when an object tagged '{triggerTag}' enters -- make sure the player GameObject carries that tag (set_tags).",
					$"It calls {setClass}.Instance.{( unlock ? "Unlock" : "Progress" )}(...) -- a {setClass} must be alive in the scene.",
					"Verify in play mode: start_play, walk into the zone (drive_player), and watch for the unlock toast / log line."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_achievement_trigger failed: {ex.Message}" } );
		}
	}

	static void TrySet( TypeDescription typeDesc, Component comp, string propName, object value )
	{
		try
		{
			var pd = typeDesc.Properties.FirstOrDefault( pp => pp.Name == propName );
			pd?.SetValue( comp, value );
		}
		catch { /* best-effort -- baked defaults still apply */ }
	}

	static string BuildCode( string className, string setClass, string achievementId, double amount, bool unlock, bool onceOnly, bool destroy, string triggerTag )
	{
		string am  = StatsAchievementsHelpers.D( amount );
		string ul  = unlock ? "true" : "false";
		string oo  = onceOnly ? "true" : "false";
		string df  = destroy ? "true" : "false";
		string tag = StatsAchievementsHelpers.EscapeLit( triggerTag );

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- achievement trigger zone. Ensures a BoxCollider (IsTrigger)
/// on its GameObject and, when an object tagged TriggerTag enters, reports to
/// the achievement set: {setClass}.Instance.Progress( AchievementId, Amount )
/// (or Unlock when UnlockDirectly). Once-only latch by default; can destroy
/// its GameObject after firing. Reusable -- add one per zone and set
/// AchievementId per instance in the inspector.
/// </summary>
public sealed class {className} : Component, Component.ITriggerListener
{{
	/// Id of the achievement in the {setClass} definitions.
	[Property] public string AchievementId {{ get; set; }} = ""{achievementId}"";

	/// Progress amount added per fire (ignored when UnlockDirectly).
	[Property] public double Amount {{ get; set; }} = {am};

	/// Call Unlock() instead of Progress().
	[Property] public bool UnlockDirectly {{ get; set; }} = {ul};

	/// Only the first tagged entry fires.
	[Property] public bool OnceOnly {{ get; set; }} = {oo};

	/// Destroy this GameObject after firing.
	[Property] public bool DestroyAfterFire {{ get; set; }} = {df};

	/// Tag the entering object must carry.
	[Property] public string TriggerTag {{ get; set; }} = ""{tag}"";

	private bool _fired;

	protected override void OnStart()
	{{
		var collider = GetOrAddComponent<BoxCollider>();
		collider.IsTrigger = true;
	}}

	public void OnTriggerEnter( Collider other )
	{{
		if ( _fired && OnceOnly ) return;
		if ( other == null || other.GameObject == null ) return;
		if ( !other.GameObject.Tags.Has( TriggerTag ) ) return;

		var set = {setClass}.Instance;
		if ( set == null )
		{{
			Log.Warning( $""[{className}] No {setClass} in the scene -- nothing to report to."" );
			return;
		}}

		if ( UnlockDirectly )
			set.Unlock( AchievementId );
		else
			set.Progress( AchievementId, Amount );

		_fired = true;

		if ( DestroyAfterFire )
			GameObject.Destroy();
	}}

	public void OnTriggerExit( Collider other )
	{{
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_speedrun_leaderboard -- speedrun timer + min-aggregation leaderboard.
// Generates {Name}.cs (TimeSince-based Start/Stop/Reset, local best persisted
// via Sandbox.FileSystem.Data, Stats.SetValue submit ONLY on improvement) plus
// {Name}Panel.razor / .razor.scss (Board2 via Leaderboards.GetFromStat with
// SetAggregationMin + SetSortAscending + SetFriendsOnly toggle, local-best
// overlay row). Board2 API verified live via describe_type.
// -----------------------------------------------------------------------------
public class CreateSpeedrunLeaderboardHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "SpeedrunTimer", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var directory = p.TryGetProperty( "directory", out var d )  && !string.IsNullOrWhiteSpace( d.GetString() )  ? d.GetString()  : "Code";
			var statName  = StatsAchievementsHelpers.SanitizeStatId(
				p.TryGetProperty( "statName", out var sn ) ? sn.GetString() : null, "best_time" );
			var saveFile  = p.TryGetProperty( "fileName", out var fn ) && !string.IsNullOrWhiteSpace( fn.GetString() ) ? fn.GetString() : "speedrun.json";
			var title     = p.TryGetProperty( "title",    out var t )  && !string.IsNullOrWhiteSpace( t.GetString() )  ? t.GetString()  : "Best Times";
			int maxRows   = p.TryGetProperty( "maxRows",  out var mr ) && mr.TryGetInt32( out var mri ) ? mri : 10;
			if ( maxRows < 1 )  maxRows = 1;
			if ( maxRows > 50 ) maxRows = 50;
			bool makePanel = !p.TryGetProperty( "makePanel", out var mp ) || mp.ValueKind != JsonValueKind.False;

			var code = BuildTimerCode( className, statName, saveFile );
			ScaffoldHelpers.WriteCode( fullPath, code );

			string relRazor = null, relScss = null;
			var panelClass = className + "Panel";
			if ( makePanel )
			{
				var razorFile = panelClass + ".razor";
				var scssFile  = panelClass + ".razor.scss";
				if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, razorFile ), out var razorPath, out var razorErr ) )
					return Task.FromResult<object>( new { error = razorErr } );
				if ( !ClaudeBridge.TryResolveProjectPath( Path.Combine( directory, scssFile ), out var scssPath, out var scssErr ) )
					return Task.FromResult<object>( new { error = scssErr } );
				if ( File.Exists( razorPath ) )
					return Task.FromResult<object>( new { error = $"File already exists: {directory}/{razorFile}. Choose a different name." } );

				ScaffoldHelpers.WriteCode( razorPath, BuildPanelRazor( panelClass, statName, title, maxRows, saveFile ) );
				ScaffoldHelpers.WriteCode( scssPath,  BuildPanelScss() );
				relRazor = $"{directory}/{razorFile}";
				relScss  = $"{directory}/{scssFile}";
			}

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = StatsAchievementsHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				panelRazorPath = relRazor,
				panelScssPath = relScss,
				panelClassName = makePanel ? panelClass : null,
				statName,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place ONE in the scene: add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Drive it from game code: {className}.Instance?.StartTimer() at run start, StopTimer() at the finish line (pairs with add_achievement_trigger / create_trigger_zone), ResetTimer() to abort.",
					$"StopTimer submits Stats.SetValue(\"{statName}\", seconds) ONLY when the run beats the persisted local best -- configure the '{statName}' stat with MIN aggregation on sbox.game so the global board keeps best times too.",
					makePanel
						? $"The panel ({panelClass}) renders only under a ScreenPanel/WorldPanel host -- add_screen_panel, then add_component_with_properties (component=\"{panelClass}\"). It has a Friends filter button and a local-best overlay row."
						: "No panel was generated (makePanel=false)."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_speedrun_leaderboard failed: {ex.Message}" } );
		}
	}

	static string BuildTimerCode( string className, string statName, string saveFile )
	{
		string stat = StatsAchievementsHelpers.EscapeLit( statName );
		string save = StatsAchievementsHelpers.EscapeLit( saveFile );

		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- speedrun timer. Drop ONE in the scene and drive it from game
/// code through {className}.Instance:
///
///   {className}.Instance?.StartTimer();  // run begins
///   {className}.Instance?.StopTimer();   // finish line reached
///   {className}.Instance?.ResetTimer();  // abort / restart
///
/// StopTimer compares against the persisted local best (Sandbox.FileSystem.Data
/// ""{save}"") and submits Stats.SetValue( ""{stat}"", seconds ) ONLY on an
/// improvement -- min-aggregation discipline, so a bad run never overwrites a
/// good one. Configure the stat with MIN aggregation on sbox.game as well.
/// LOCAL-only: each client times and submits its own runs.
/// </summary>
public sealed class {className} : Component
{{
	public static {className} Instance {{ get; private set; }}

	/// Sandbox.Services stat the best time is written to.
	[Property] public string StatName {{ get; set; }} = ""{stat}"";

	/// Save file name inside Sandbox.FileSystem.Data for the local best.
	[Property] public string FileName {{ get; set; }} = ""{save}"";

	public class SaveState
	{{
		public int Version {{ get; set; }} = 1;
		public float BestTime {{ get; set; }} = 0f;
	}}

	public bool IsRunning {{ get; private set; }}

	/// Elapsed seconds of the current run (live) or the last finished run.
	public float CurrentTime => IsRunning ? (float) _sinceStart : _lastTime;

	/// Persisted local best in seconds; 0 means no completed run yet.
	public float BestTime {{ get; private set; }}

	/// Fires after every StopTimer with the run time.
	public static Action<float> OnRunCompleted {{ get; set; }}

	/// Fires only when a run beats the local best (after the stat submit).
	public static Action<float> OnNewBest {{ get; set; }}

	private TimeSince _sinceStart;
	private float _lastTime;

	protected override void OnEnabled()
	{{
		Instance = this;
		LoadBest();
	}}

	protected override void OnDestroy()
	{{
		if ( Instance == this ) Instance = null;
	}}

	/// <summary>Start (or restart) the run clock.</summary>
	public void StartTimer()
	{{
		_sinceStart = 0f;
		IsRunning = true;
	}}

	/// <summary>Abort the current run without submitting anything.</summary>
	public void ResetTimer()
	{{
		_sinceStart = 0f;
		_lastTime = 0f;
		IsRunning = false;
	}}

	/// <summary>Finish the run. Persists + submits the time only if it beats the local best.</summary>
	public void StopTimer()
	{{
		if ( !IsRunning ) return;
		IsRunning = false;
		_lastTime = _sinceStart;

		OnRunCompleted?.Invoke( _lastTime );

		if ( BestTime <= 0f || _lastTime < BestTime )
		{{
			BestTime = _lastTime;
			SaveBest();

			// SetValue overload verified live: (name, amount, context, data).
			Sandbox.Services.Stats.SetValue( StatName, _lastTime, null, null );
			Sandbox.Services.Stats.Flush();

			OnNewBest?.Invoke( _lastTime );
			Log.Info( $""[{className}] New best: {{_lastTime:F2}}s"" );
		}}
	}}

	private void LoadBest()
	{{
		var loaded = Sandbox.FileSystem.Data.ReadJsonOrDefault<SaveState>( FileName, null );
		BestTime = ( loaded != null && loaded.Version == 1 && loaded.BestTime > 0f ) ? loaded.BestTime : 0f;
	}}

	private void SaveBest()
	{{
		var s = new SaveState();
		s.BestTime = BestTime;
		Sandbox.FileSystem.Data.WriteJson( FileName, s );
	}}
}}
";
	}

	static string BuildPanelRazor( string panelClass, string statName, string title, int maxRows, string saveFile )
	{
		string stat = StatsAchievementsHelpers.EscapeLit( statName );
		string ttl  = StatsAchievementsHelpers.EscapeLit( title );
		string save = StatsAchievementsHelpers.EscapeLit( saveFile );
		string max  = maxRows.ToString( System.Globalization.CultureInfo.InvariantCulture );

		// NOTE: all {{ }} inside the $@"" are C# string-escape doubled braces.
		// The generated .razor uses single { } for Razor/C# expressions.
		return $@"@using Sandbox;
@using Sandbox.UI;
@using System.Collections.Generic;
@inherits PanelComponent;

@* {panelClass} -- speedrun leaderboard backed by Sandbox.Services (Board2:
   min aggregation, ascending sort, optional friends-only). Host it under a
   ScreenPanel or WorldPanel. The stat must be configured for the project
   ident on sbox.game. The bottom row overlays the LOCAL best read from
   Sandbox.FileSystem.Data. *@

<div class=""speedrun-board"">
	<div class=""title"">@Title</div>
	<div class=""filters"">
		<div class=""@FriendsButtonClass"" onclick=@ToggleFriends>Friends</div>
	</div>
	@if ( _loading )
	{{
		<div class=""row""><span class=""name"">Loading...</span></div>
	}}
	else
	{{
		@foreach ( var row in _rows )
		{{
			<div class=""row"">
				<span class=""rank"">#@row.Rank</span>
				<span class=""name"">@row.DisplayName</span>
				<span class=""value"">@FormatTime( row.Value )</span>
			</div>
		}}
		@if ( _rows.Count == 0 )
		{{
			<div class=""row""><span class=""name"">No entries yet.</span></div>
		}}
	}}
	<div class=""row local-best"">
		<span class=""rank"">YOU</span>
		<span class=""name"">Local best</span>
		<span class=""value"">@( _localBest > 0f ? FormatTime( _localBest ) : ""--"" )</span>
	</div>
</div>

@code {{
	[Property] public string StatName      {{ get; set; }} = ""{stat}"";
	[Property] public string Title         {{ get; set; }} = ""{ttl}"";
	[Property] public int    MaxRows       {{ get; set; }} = {max};
	[Property] public string LocalBestFile {{ get; set; }} = ""{save}"";

	private struct BoardRow {{ public int Rank; public string DisplayName; public double Value; }}

	public class LocalSave {{ public int Version {{ get; set; }} = 1; public float BestTime {{ get; set; }} = 0f; }}

	private List<BoardRow> _rows        = new List<BoardRow>();
	private bool           _loading     = false;
	private bool           _friendsOnly = false;
	private bool           _fetchedOnce = false;
	private RealTimeSince  _lastFetch;
	private float          _localBest   = 0f;

	private string FriendsButtonClass => _friendsOnly ? ""filter-btn active"" : ""filter-btn"";

	protected override void OnUpdate()
	{{
		// Auto-refresh at most every 30 seconds; also fetch on first tick.
		if ( !_fetchedOnce || _lastFetch > 30f )
		{{
			_fetchedOnce = true;
			_ = RefreshAsync();
		}}
	}}

	public void ToggleFriends()
	{{
		_friendsOnly = !_friendsOnly;
		_ = RefreshAsync();
	}}

	public async System.Threading.Tasks.Task RefreshAsync()
	{{
		if ( _loading ) return;
		_loading = true;
		_lastFetch = 0f;
		StateHasChanged();
		try
		{{
			ReadLocalBest();

			var board = Sandbox.Services.Leaderboards.GetFromStat( StatName );
			board.MaxEntries = MaxRows;
			board.SetAggregationMin();
			board.SetSortAscending();
			board.SetFriendsOnly( _friendsOnly );
			await board.Refresh( default );

			var newRows = new List<BoardRow>();
			if ( board.Entries != null )
			{{
				foreach ( var e in board.Entries )
				{{
					var r = new BoardRow();
					r.Rank = (int) e.Rank;
					r.DisplayName = e.DisplayName;
					r.Value = (double) e.Value;
					newRows.Add( r );
				}}
			}}
			_rows = newRows;
		}}
		catch ( System.Exception ) {{ /* Services offline or project not configured -- show empty */ }}
		finally
		{{
			_loading = false;
			StateHasChanged();
		}}
	}}

	private void ReadLocalBest()
	{{
		try
		{{
			var loaded = Sandbox.FileSystem.Data.ReadJsonOrDefault<LocalSave>( LocalBestFile, null );
			_localBest = ( loaded != null && loaded.BestTime > 0f ) ? loaded.BestTime : 0f;
		}}
		catch ( System.Exception )
		{{
			_localBest = 0f;
		}}
	}}

	private string FormatTime( double seconds )
	{{
		if ( seconds < 0 ) seconds = 0;
		int mins = (int) ( seconds / 60.0 );
		double rem = seconds - mins * 60.0;
		return mins.ToString() + "":"" + rem.ToString( ""00.00"" );
	}}

	protected override int BuildHash() => System.HashCode.Combine( _rows.Count, _loading, _friendsOnly, _localBest );
}}
";
	}

	static string BuildPanelScss()
	{
		return @".speedrun-board {
	display: flex;
	flex-direction: column;
	background-color: rgba(0, 0, 0, 0.7);
	border-radius: 4px;
	padding: 8px;
	min-width: 280px;
}

.title {
	font-size: 18px;
	font-weight: bold;
	color: white;
	text-align: center;
	margin-bottom: 6px;
}

.filters {
	display: flex;
	flex-direction: row;
	justify-content: flex-end;
	margin-bottom: 6px;
}

.filter-btn {
	font-size: 12px;
	color: rgba(255, 255, 255, 0.6);
	background-color: rgba(255, 255, 255, 0.08);
	border-radius: 3px;
	padding: 3px 10px;
	cursor: pointer;
}

.filter-btn:hover {
	color: white;
}

.filter-btn.active {
	color: #101014;
	background-color: #ffe080;
}

.row {
	display: flex;
	flex-direction: row;
	padding: 3px 4px;
	border-bottom: 1px solid rgba(255, 255, 255, 0.1);
}

.rank {
	color: rgba(255, 255, 255, 0.5);
	min-width: 44px;
}

.name {
	flex-grow: 1;
	color: white;
}

.value {
	color: #ffe080;
	min-width: 76px;
	text-align: right;
}

.local-best {
	margin-top: 4px;
	border-bottom: none;
	background-color: rgba(255, 224, 128, 0.12);
	border-radius: 3px;
}
";
	}
}

// -----------------------------------------------------------------------------
// create_elo_rating_system -- self-contained elo math component. Host-
// authoritative: ratings live in a [Sync(SyncFlags.FromHost)] NetDictionary,
// mutations are IsProxy-guarded, changes broadcast via [Rpc.Broadcast] so the
// static OnRatingChanged fires on every machine. Ratings persist host-side via
// Sandbox.FileSystem.Data JSON (string keys -- JSON-safe).
// -----------------------------------------------------------------------------
public class CreateEloRatingSystemHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "EloRatingSystem", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float kFactor       = p.TryGetProperty( "kFactor",       out var kv ) && kv.TryGetSingle( out var kf ) ? kf : 32f;
			float defaultRating = p.TryGetProperty( "defaultRating", out var dv ) && dv.TryGetSingle( out var df ) ? df : 1000f;
			var   saveFile      = p.TryGetProperty( "fileName",      out var fn ) && !string.IsNullOrWhiteSpace( fn.GetString() ) ? fn.GetString() : "elo_ratings.json";
			if ( kFactor < 1f )        kFactor = 1f;
			if ( defaultRating < 1f )  defaultRating = 1f;

			var code = BuildCode( className, kFactor, defaultRating, saveFile );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = StatsAchievementsHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				kFactor,
				defaultRating,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place ONE in the scene and network it (its GameObject must be networked for [Sync(FromHost)] to replicate): add_component_to_new_object after the hotload, then network_spawn.",
					$"HOST-ONLY reporting: {className}.Instance?.ReportMatch( winnerSteamId, loserSteamId ) for 1v1, ReportTeamMatch( winnerIds, loserIds ) for teams (team-average elo, uniform delta). Calls on clients are IsProxy-no-ops.",
					$"Read anywhere: GetRating( steamId ) (unknown players = DefaultRating). Subscribe to the static OnRatingChanged (steamId, newRating) -- it fires on EVERY machine via an [Rpc.Broadcast].",
					$"Ratings persist HOST-side to Sandbox.FileSystem.Data (\"{saveFile}\") on every report and on destroy -- only the host's disk has the ledger.",
					"Tune KFactor with set_property: 32 = fast movement (default), 16 = stable veterans."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_elo_rating_system failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, float kFactor, float defaultRating, string saveFile )
	{
		string kf   = StatsAchievementsHelpers.F( kFactor );
		string dr   = StatsAchievementsHelpers.F( defaultRating );
		string save = StatsAchievementsHelpers.EscapeLit( saveFile );

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- host-authoritative elo rating ledger. Drop ONE in the scene
/// on a NETWORKED GameObject; the host reports results, everyone reads ratings:
///
///   {className}.Instance?.ReportMatch( winnerSteamId, loserSteamId );          // host only
///   {className}.Instance?.ReportTeamMatch( winnerIds, loserIds );              // host only
///   float r = {className}.Instance?.GetRating( steamId ) ?? {dr};              // anywhere
///
/// Standard elo: expected = 1 / (1 + 10^((Rb - Ra) / 400)), delta = K * (score
/// - expected). Team matches use team-average ratings and apply a uniform
/// delta to every member. Ratings replicate to clients via
/// [Sync(SyncFlags.FromHost)] and persist HOST-side to Sandbox.FileSystem.Data
/// (""{save}""). OnRatingChanged fires on every machine via an [Rpc.Broadcast].
/// </summary>
public sealed class {className} : Component
{{
	public static {className} Instance {{ get; private set; }}

	/// Elo K-factor: how far a single result moves ratings (32 = fast, 16 = stable).
	[Property] public float KFactor {{ get; set; }} = {kf};

	/// Rating assigned to players with no recorded matches.
	[Property] public float DefaultRating {{ get; set; }} = {dr};

	/// Save file name inside Sandbox.FileSystem.Data (host-side only).
	[Property] public string FileName {{ get; set; }} = ""{save}"";

	/// Host-authoritative ratings by SteamId, replicated to every client.
	[Sync( SyncFlags.FromHost )] public NetDictionary<long, float> Ratings {{ get; set; }} = new NetDictionary<long, float>();

	/// (steamId, newRating) -- fires on every machine after a rating changes.
	public static Action<long, float> OnRatingChanged {{ get; set; }}

	public class SaveState
	{{
		public int Version {{ get; set; }} = 1;
		public Dictionary<string, float> Ratings {{ get; set; }} = new Dictionary<string, float>();
	}}

	protected override void OnEnabled()
	{{
		Instance = this;
		if ( !IsProxy ) Load();
	}}

	protected override void OnDestroy()
	{{
		if ( !IsProxy ) Save();
		if ( Instance == this ) Instance = null;
	}}

	/// <summary>Current rating for a SteamId; DefaultRating when unknown. Works on any machine.</summary>
	public float GetRating( long steamId )
	{{
		float r;
		if ( Ratings.TryGetValue( steamId, out r ) ) return r;
		return DefaultRating;
	}}

	/// <summary>Probability (0..1) that a player rated ratingA beats one rated ratingB.</summary>
	public float ExpectedScore( float ratingA, float ratingB )
	{{
		return 1f / ( 1f + (float) Math.Pow( 10.0, ( ratingB - ratingA ) / 400.0 ) );
	}}

	/// <summary>Record a 1v1 result. HOST ONLY -- a no-op on clients.</summary>
	public void ReportMatch( long winnerSteamId, long loserSteamId )
	{{
		if ( IsProxy ) return;
		if ( winnerSteamId == loserSteamId ) return;

		float ra = GetRating( winnerSteamId );
		float rb = GetRating( loserSteamId );
		float ea = ExpectedScore( ra, rb );
		float eb = ExpectedScore( rb, ra );

		SetRating( winnerSteamId, ra + KFactor * ( 1f - ea ) );
		SetRating( loserSteamId,  rb + KFactor * ( 0f - eb ) );
		Save();
	}}

	/// <summary>Record a team result using team-average ratings; every member of a
	/// team moves by the same delta. HOST ONLY -- a no-op on clients.</summary>
	public void ReportTeamMatch( List<long> winnerSteamIds, List<long> loserSteamIds )
	{{
		if ( IsProxy ) return;
		if ( winnerSteamIds == null || loserSteamIds == null ) return;
		if ( winnerSteamIds.Count == 0 || loserSteamIds.Count == 0 ) return;

		float avgW = 0f;
		foreach ( var id in winnerSteamIds ) avgW += GetRating( id );
		avgW /= winnerSteamIds.Count;

		float avgL = 0f;
		foreach ( var id in loserSteamIds ) avgL += GetRating( id );
		avgL /= loserSteamIds.Count;

		float ea = ExpectedScore( avgW, avgL );
		float eb = ExpectedScore( avgL, avgW );
		float deltaW = KFactor * ( 1f - ea );
		float deltaL = KFactor * ( 0f - eb );

		foreach ( var id in winnerSteamIds ) SetRating( id, GetRating( id ) + deltaW );
		foreach ( var id in loserSteamIds )  SetRating( id, GetRating( id ) + deltaL );
		Save();
	}}

	private void SetRating( long steamId, float rating )
	{{
		Ratings[steamId] = rating;
		BroadcastRatingChanged( steamId, rating );
	}}

	/// Internal plumbing: fired by the host so OnRatingChanged runs everywhere.
	[Rpc.Broadcast]
	public void BroadcastRatingChanged( long steamId, float rating )
	{{
		OnRatingChanged?.Invoke( steamId, rating );
	}}

	private void Load()
	{{
		var loaded = Sandbox.FileSystem.Data.ReadJsonOrDefault<SaveState>( FileName, null );
		if ( loaded == null || loaded.Version != 1 || loaded.Ratings == null ) return;

		foreach ( var kv in loaded.Ratings )
		{{
			long id;
			if ( long.TryParse( kv.Key, out id ) )
				Ratings[id] = kv.Value;
		}}
	}}

	private void Save()
	{{
		var s = new SaveState();
		foreach ( var kv in Ratings )
			s.Ratings[kv.Key.ToString()] = kv.Value;
		Sandbox.FileSystem.Data.WriteJson( FileName, s );
	}}
}}
";
	}
}
