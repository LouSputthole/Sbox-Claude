using Editor;
using Sandbox;
using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
//  Dialogue & Camera FX pair — generate_lipsync_dialogue / create_camera_effects
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs, so it reuses the
//  shared helpers directly: ScaffoldHelpers.PrepareCodeFile / WriteCode,
//  ClaudeBridge.ParseVector3, and AiSystemsHelpers (Float/Int/Bool/Str/F/
//  EscString/PlaceOnTarget — internal to this assembly, defined in
//  AiSystemsHandlers.cs). Handler code here is UNSANDBOXED editor code.
//
//  The C# *strings these handlers generate* run in the SANDBOX (the game).
//  Both templates were live-verified on 2026-07-13 (default render written into
//  the live project, hotloaded, compile clean, TypeLibrary-load confirmed with
//  every public member present, then deleted). Whitelist findings proven by a
//  dedicated sandboxed compile probe the same day:
//    - CameraComponent.AddShake(amplitude, frequency, duration),
//      AddPunch(Vector3, amplitude, frequency=1, duration=0.3, fovAmplitude=0),
//      AddPunch(Angles, frequency=1, duration=0.3, fovAmplitude=0) and
//      AddTilt(Angles, duration, easeTime=0.2) ALL compile in game code, and the
//      return type is nameable as Sandbox.CameraEffectSystem.BaseEffect
//      (nested public: Stop()/IsDone/Epicenter/Radius/Duration/TimeAlive).
//    - SoundHandle.LipSync accessor (Enabled / Visemes / FrameNumber),
//      handle.IsPlaying / Finished / IsValid / Stop(fadeTime) compile in game code.
//    - SoundHandle.LipSync.Visemes is IReadOnlyList<float> — weights indexed in
//      the engine's viseme order. That order was read live from the engine's own
//      Sandbox.LipSync component (private string[] VisemeNames, 15 entries:
//      viseme_sil, viseme_PP, viseme_FF, viseme_TH, viseme_DD, viseme_KK,
//      viseme_CH, viseme_SS, viseme_NN, viseme_RR, viseme_AA, viseme_E,
//      viseme_I, viseme_O, viseme_U) and is baked into the generated component.
//    - Model.MorphCount / GetMorphName(int) / GetVisemeMorph(visemeName, morphIndex)
//      and SkinnedModelRenderer.Morphs.Set(name, weight) / Clear(name, fadeTime)
//      compile in game code. GetVisemeMorph returns REAL nonzero weights on
//      models with baked viseme data (verified against Citizen: e.g. viseme_AA →
//      openjawL=0.318, openjawR=0.308; viseme_PP → lowerLipsTowardAndPart=0.804),
//      so the mouth-drive is data-driven, not a hand-guessed morph table.
//  What the editor CANNOT verify: actual audio playback and whether the Visemes
//  list populates during it (runtime-only) — the generated component bakes in a
//  LogVisemes() helper + DebugLogVisemes property so a human playtest can confirm
//  in seconds. The tool notes say so honestly.
//
//  Registration lines + the _sceneMutatingCommands additions are wired by the
//  main agent in MyEditorMenu.cs (see this wave's summary) to avoid a merge
//  conflict with sibling handler files.
// ═══════════════════════════════════════════════════════════════════════════

// ═══════════════════════════════════════════════════════════════════════════
//  1. generate_lipsync_dialogue  (code-gen; scene-mutating [writes a file])
//     "NPCs speak their dialogue with moving mouths": a sealed Component that
//     plays a line list — per line it resolves the speaker GameObject by name,
//     speaks the text positionally via Sandbox.Speech.Synthesizer, drives the
//     speaker's SkinnedModelRenderer mouth morphs from Handle.LipSync.Visemes
//     through the model's own baked viseme->morph map, mirrors the line into a
//     create_dialogue_system HUD when one exists (loose TypeLibrary bind), and
//     advances when the handle stops. OnLineStarted/OnDialogueFinished + Skip().
// ═══════════════════════════════════════════════════════════════════════════
public class GenerateLipsyncDialogueHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "LipsyncDialogue", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var volume         = AiSystemsHelpers.Float( p, "volume", 1f );
			var positional     = AiSystemsHelpers.Bool(  p, "positional", true );
			var driveMouth     = AiSystemsHelpers.Bool(  p, "driveMouth", true );
			var morphScale     = AiSystemsHelpers.Float( p, "morphScale", 1f );
			var mouthSmooth    = AiSystemsHelpers.Float( p, "mouthSmoothSeconds", 0.05f );
			var lineGap        = AiSystemsHelpers.Float( p, "lineGapSeconds", 0.2f );
			var lineTimeout    = AiSystemsHelpers.Float( p, "lineTimeoutSeconds", 20f );
			var bindHud        = AiSystemsHelpers.Bool(  p, "bindHud", true );
			var autoStart      = AiSystemsHelpers.Bool(  p, "autoStart", false );
			var debugVisemes   = AiSystemsHelpers.Bool(  p, "debugLogVisemes", false );

			// ── Dialogue lines: explicit `lines` array wins, else a two-line demo.
			var lineInits = new StringBuilder();
			int lineCount = 0;
			if ( p.TryGetProperty( "lines", out var arr ) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 )
			{
				foreach ( var e in arr.EnumerateArray() )
				{
					var speaker = AiSystemsHelpers.Str( e, "speaker", "" );
					var text    = AiSystemsHelpers.Str( e, "text", "" );
					lineInits.Append( "\t\tnew DialogueLine { Speaker = \"" + AiSystemsHelpers.EscString( speaker )
						+ "\", Text = \"" + AiSystemsHelpers.EscString( text ) + "\" },\n" );
					lineCount++;
				}
			}
			else
			{
				lineInits.Append( "\t\tnew DialogueLine { Speaker = \"\", Text = \"Hello there.\" },\n" );
				lineInits.Append( "\t\tnew DialogueLine { Speaker = \"\", Text = \"My mouth moves while I talk.\" },\n" );
				lineCount = 2;
			}

			// ── Per-speaker voices: optional `voices` array, default empty (OS voice).
			var voiceInits = new StringBuilder();
			int voiceCount = 0;
			if ( p.TryGetProperty( "voices", out var varr ) && varr.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in varr.EnumerateArray() )
				{
					var speaker = AiSystemsHelpers.Str( e, "speaker", "" );
					var vName   = AiSystemsHelpers.Str( e, "voiceName", "" );
					var vGender = AiSystemsHelpers.Str( e, "voiceGender", "" );
					var vAge    = AiSystemsHelpers.Str( e, "voiceAge", "" );
					var rate    = AiSystemsHelpers.Int( e, "rate", 0 );
					voiceInits.Append( "\t\tnew SpeakerVoice { Speaker = \"" + AiSystemsHelpers.EscString( speaker )
						+ "\", VoiceName = \"" + AiSystemsHelpers.EscString( vName )
						+ "\", VoiceGender = \"" + AiSystemsHelpers.EscString( vGender )
						+ "\", VoiceAge = \"" + AiSystemsHelpers.EscString( vAge )
						+ "\", Rate = " + rate + " },\n" );
					voiceCount++;
				}
			}
			var voicesInit = voiceCount > 0 ? "new()\n\t{\n" + voiceInits + "\t}" : "new()";

			var code = LipsyncDialogueTemplate
				.Replace( "__CLASS__", className )
				.Replace( "__LINESINIT__", lineInits.ToString() )
				.Replace( "__VOICESINIT__", voicesInit )
				.Replace( "__VOLUME__", AiSystemsHelpers.F( volume ) )
				.Replace( "__POSITIONAL__", positional ? "true" : "false" )
				.Replace( "__DRIVEMOUTH__", driveMouth ? "true" : "false" )
				.Replace( "__MORPHSCALE__", AiSystemsHelpers.F( morphScale ) )
				.Replace( "__MOUTHSMOOTH__", AiSystemsHelpers.F( mouthSmooth ) )
				.Replace( "__LINEGAP__", AiSystemsHelpers.F( lineGap ) )
				.Replace( "__LINETIMEOUT__", AiSystemsHelpers.F( lineTimeout ) )
				.Replace( "__BINDHUD__", bindHud ? "true" : "false" )
				.Replace( "__AUTOSTART__", autoStart ? "true" : "false" )
				.Replace( "__DEBUGVISEMES__", debugVisemes ? "true" : "false" );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string placeNote = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = AiSystemsHelpers.PlaceOnTarget( tid.GetString(), className, out placeNote );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				lineCount,
				voiceCount,
				propertyNames = new[] { "Lines", "Voices", "Volume", "Positional", "DriveMouth", "MorphScale", "MouthSmoothSeconds", "LineGapSeconds", "LineTimeoutSeconds", "BindHud", "AutoStart", "DebugLogVisemes" },
				placedOn,
				placementNote = placeNote,
				note = "Start from game code with <class>.Begin() (or autoStart:true / a create_interactable that calls Begin). Per line it speaks the text " +
				       "via the OS synthesizer AT the speaker GameObject (line.Speaker = scene object name, empty = own GameObject), drives that speaker's " +
				       "SkinnedModelRenderer mouth morphs from the live viseme stream, and advances when the audio handle stops (Skip() cuts a line short). " +
				       "MOUTH DRIVE: data-driven — Handle.LipSync.Visemes (IReadOnlyList<float>, engine order baked in from Sandbox.LipSync.VisemeNames, read " +
				       "live 2026-07-13) x the model's own Model.GetVisemeMorph table (verified nonzero on Citizen; models WITHOUT baked viseme data log a " +
				       "warning and stay audio-only). HONESTY: the editor cannot playtest audio, so the live viseme stream is RUNTIME-UNVERIFIED — set " +
				       "DebugLogVisemes or call LogVisemes() in play mode to confirm weights flow; everything else (API surface, mapping data) verified live. " +
				       "HUD: bindHud loosely TypeLibrary-matches a create_dialogue_system component (List<string> Lines + Begin() + bool IsActive) and mirrors " +
				       "each line into it — the HUD's own typewriter/advance input stays active (its advance key ends only its display, not the audio). " +
				       "LOCAL-only (audio + morphs on the calling machine): call Begin() inside an [Rpc.Broadcast] for everyone. Voices are machine/OS-specific; " +
				       "TrySetVoice is best-effort. Events: OnLineStarted(dialogue, index, speaker), OnDialogueFinished(dialogue) — natural end only, not StopDialogue()."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"generate_lipsync_dialogue failed: {ex.Message}" } );
		}
	}

	// Verified template (default render live-compile-verified 2026-07-13). Tokens: __CLASS__ etc.
	// Plain verbatim string + Replace — no $@ brace-doubling risk in a template this size.
	private const string LipsyncDialogueTemplate =
@"using Sandbox;
using System;
using System.Collections.Generic;

// Local dialogue performance: each line is spoken via the OS speech synthesizer
// (Sandbox.Speech.Synthesizer) at the speaker GameObject while the speaker's
// SkinnedModelRenderer mouth morphs are driven from the live viseme stream
// (Handle.LipSync.Visemes -> Model.GetVisemeMorph mapping baked into the model).
// LOCAL only — call Begin() inside an [Rpc.Broadcast] for everyone to see/hear.
public sealed class __CLASS__ : Component
{
	/// <summary>One spoken line. Speaker = scene GameObject name (case-insensitive); empty = this GameObject.</summary>
	public sealed class DialogueLine
	{
		public string Speaker { get; set; } = """";
		public string Text { get; set; } = """";
	}

	/// <summary>Per-speaker voice settings, matched to DialogueLine.Speaker by name (case-insensitive).</summary>
	public sealed class SpeakerVoice
	{
		public string Speaker { get; set; } = """";
		/// <summary>Exact installed OS voice name. Empty = use Gender/Age hints, or the OS default.</summary>
		public string VoiceName { get; set; } = """";
		public string VoiceGender { get; set; } = """";
		public string VoiceAge { get; set; } = """";
		/// <summary>Speaking rate offset: negative = slower, positive = faster.</summary>
		public int Rate { get; set; } = 0;
	}

	[Property] public List<DialogueLine> Lines { get; set; } = new()
	{
__LINESINIT__	};

	[Property] public List<SpeakerVoice> Voices { get; set; } = __VOICESINIT__;

	[Property] public float Volume { get; set; } = __VOLUME__;
	/// <summary>True: 3D audio parented to the speaker. False: flat 2D narrator voice.</summary>
	[Property] public bool Positional { get; set; } = __POSITIONAL__;
	/// <summary>Drive the speaker's SkinnedModelRenderer mouth morphs from the viseme stream.</summary>
	[Property] public bool DriveMouth { get; set; } = __DRIVEMOUTH__;
	/// <summary>Multiplier on viseme-derived morph weights (same idea as Sandbox.LipSync.MorphScale).</summary>
	[Property] public float MorphScale { get; set; } = __MORPHSCALE__;
	/// <summary>Seconds of exponential smoothing on mouth morphs (0 = raw viseme weights).</summary>
	[Property] public float MouthSmoothSeconds { get; set; } = __MOUTHSMOOTH__;
	/// <summary>Pause between a line ending and the next line starting.</summary>
	[Property] public float LineGapSeconds { get; set; } = __LINEGAP__;
	/// <summary>Safety: a line whose audio never starts is skipped after this many seconds.</summary>
	[Property] public float LineTimeoutSeconds { get; set; } = __LINETIMEOUT__;
	/// <summary>Mirror each line into a generated create_dialogue_system HUD if one exists in the scene (loose TypeLibrary bind).</summary>
	[Property] public bool BindHud { get; set; } = __BINDHUD__;
	[Property] public bool AutoStart { get; set; } = __AUTOSTART__;
	/// <summary>Log the live viseme stream ~4x/second while speaking (runtime diagnostics).</summary>
	[Property] public bool DebugLogVisemes { get; set; } = __DEBUGVISEMES__;

	/// <summary>Fires as each line starts speaking — (dialogue, lineIndex, speakerName).</summary>
	public static event Action<__CLASS__, int, string> OnLineStarted;
	/// <summary>Fires when the last line finishes (not on StopDialogue abort).</summary>
	public static event Action<__CLASS__> OnDialogueFinished;

	public bool IsActive { get; private set; }
	public int CurrentIndex { get; private set; } = -1;
	/// <summary>The current line's SoundHandle (null between lines / before Begin).</summary>
	public SoundHandle Handle { get; private set; }
	public bool IsSpeaking => Handle != null && Handle.IsValid && Handle.IsPlaying;
	/// <summary>True when a scene dialogue HUD was found and lines are mirrored into it.</summary>
	public bool HudBound => _hud != null && _hud.IsValid();

	// Engine viseme order — copied from Sandbox.LipSync.VisemeNames (read live via reflection 2026-07-13).
	// Handle.LipSync.Visemes is an IReadOnlyList<float> of weights in THIS order.
	private static readonly string[] VisemeNames =
	{
		""viseme_sil"", ""viseme_PP"", ""viseme_FF"", ""viseme_TH"", ""viseme_DD"",
		""viseme_KK"", ""viseme_CH"", ""viseme_SS"", ""viseme_NN"", ""viseme_RR"",
		""viseme_AA"", ""viseme_E"", ""viseme_I"", ""viseme_O"", ""viseme_U"",
	};

	private GameObject _speakerGo;
	private SkinnedModelRenderer _mouth;
	private Model _matrixModel;
	private float[,] _matrix;                   // [viseme, morph] weights from Model.GetVisemeMorph
	private string[] _morphNames;
	private readonly List<int> _activeMorphs = new();
	private float[] _current;                   // smoothed morph weights
	private Component _hud;
	private PropertyDescription _hudLines;
	private MethodDescription _hudBegin;
	private bool _lineWasPlaying;
	private bool _lineEnded;
	private TimeSince _sinceLineStarted;
	private TimeSince _sinceLineEnded;
	private RealTimeSince _sinceVisemeLog;

	protected override void OnStart()
	{
		if ( AutoStart ) Begin();
	}

	/// <summary>Start (or restart) the conversation from the first line.</summary>
	public void Begin()
	{
		if ( Lines == null || Lines.Count == 0 )
		{
			Log.Warning( ""__CLASS__: no lines to speak."" );
			return;
		}
		if ( BindHud ) TryBindHud();
		IsActive = true;
		PlayLine( 0 );
	}

	/// <summary>Cut the current line short and move to the next one immediately.</summary>
	public void Skip()
	{
		if ( !IsActive ) return;
		if ( Handle != null && Handle.IsValid && Handle.IsPlaying ) Handle.Stop( 0.05f );
		RelaxMouth();
		Advance();
	}

	/// <summary>Abort the conversation (no OnDialogueFinished).</summary>
	public void StopDialogue()
	{
		if ( Handle != null && Handle.IsValid && Handle.IsPlaying ) Handle.Stop( 0.1f );
		RelaxMouth();
		IsActive = false;
		CurrentIndex = -1;
	}

	protected override void OnUpdate()
	{
		if ( !IsActive ) return;

		if ( Handle != null && Handle.IsValid && Handle.IsPlaying )
		{
			_lineWasPlaying = true;
			DriveMouthFrame();
			return;
		}

		if ( _lineWasPlaying )
		{
			// The line's audio just ended.
			_lineWasPlaying = false;
			_lineEnded = true;
			_sinceLineEnded = 0f;
			RelaxMouth();
			return;
		}

		// Line audio ended (or synth failed) — wait out the gap, then advance.
		if ( _lineEnded )
		{
			if ( _sinceLineEnded >= LineGapSeconds ) Advance();
			return;
		}

		// Audio was created but never reached IsPlaying (synthesis still pending, or silently failed).
		if ( _sinceLineStarted > LineTimeoutSeconds )
		{
			Log.Warning( $""__CLASS__: line {CurrentIndex} audio never started — skipping."" );
			Advance();
		}
	}

	private void PlayLine( int index )
	{
		CurrentIndex = index;
		var line = Lines[index];
		var speakerName = line != null && line.Speaker != null ? line.Speaker : """";

		_speakerGo = ResolveSpeaker( speakerName );
		_mouth = _speakerGo != null
			? _speakerGo.Components.Get<SkinnedModelRenderer>( FindMode.EnabledInSelfAndDescendants )
			: null;
		PrepareMatrix();

		_lineWasPlaying = false;
		_lineEnded = false;
		_sinceLineStarted = 0f;

		ShowOnHud( speakerName, line != null ? line.Text : """" );
		Speak( speakerName, line != null ? line.Text : """" );
		if ( Handle == null ) { _lineEnded = true; _sinceLineEnded = 0f; }   // synth failed — treat as instantly ended

		OnLineStarted?.Invoke( this, index, speakerName );
	}

	private void Advance()
	{
		int next = CurrentIndex + 1;
		if ( Lines == null || next >= Lines.Count )
		{
			IsActive = false;
			CurrentIndex = -1;
			OnDialogueFinished?.Invoke( this );
			return;
		}
		PlayLine( next );
	}

	private void Speak( string speakerName, string text )
	{
		if ( Handle != null && Handle.IsValid && Handle.IsPlaying ) Handle.Stop( 0.05f );   // e.g. Begin() mid-conversation
		Handle = null;
		if ( string.IsNullOrWhiteSpace( text ) ) return;

		var synth = new Sandbox.Speech.Synthesizer();
		int rate = 0;
		var voice = FindVoice( speakerName );
		if ( voice != null )
		{
			rate = voice.Rate;
			if ( !string.IsNullOrWhiteSpace( voice.VoiceName ) )
				synth.TrySetVoice( voice.VoiceName );
			else if ( !string.IsNullOrWhiteSpace( voice.VoiceGender ) && !string.IsNullOrWhiteSpace( voice.VoiceAge ) )
				synth.TrySetVoice( voice.VoiceGender, voice.VoiceAge );
		}

		var handle = synth.WithText( text ).WithRate( rate ).Play();
		if ( handle == null ) return;

		handle.Volume = Volume;
		if ( Positional && _speakerGo != null && _speakerGo.IsValid() )
		{
			handle.Position = _speakerGo.WorldPosition;
			handle.SetParent( _speakerGo );
		}
		else
		{
			handle.ListenLocal = true;
		}

		handle.LipSync.Enabled = true;   // viseme extraction for the mouth drive
		Handle = handle;
	}

	private SpeakerVoice FindVoice( string speakerName )
	{
		if ( Voices == null ) return null;
		foreach ( var v in Voices )
		{
			if ( v == null ) continue;
			if ( string.Equals( v.Speaker ?? """", speakerName ?? """", StringComparison.OrdinalIgnoreCase ) ) return v;
		}
		return null;
	}

	private GameObject ResolveSpeaker( string speakerName )
	{
		if ( string.IsNullOrWhiteSpace( speakerName ) ) return GameObject;
		var found = FindByNameRecursive( Scene, speakerName );
		if ( found == null )
		{
			Log.Warning( $""__CLASS__: speaker GameObject '{speakerName}' not found — speaking from this GameObject."" );
			return GameObject;
		}
		return found;
	}

	private static GameObject FindByNameRecursive( GameObject root, string name )
	{
		if ( root == null ) return null;
		foreach ( var child in root.Children )
		{
			if ( child == null ) continue;
			if ( string.Equals( child.Name, name, StringComparison.OrdinalIgnoreCase ) ) return child;
			var found = FindByNameRecursive( child, name );
			if ( found != null ) return found;
		}
		return null;
	}

	// ── Mouth drive ──────────────────────────────────────────────────────

	/// <summary>Build (or reuse) the viseme->morph weight matrix baked into the speaker's model.</summary>
	private void PrepareMatrix()
	{
		if ( _mouth == null || !_mouth.IsValid() || _mouth.Model == null ) { _matrix = null; _matrixModel = null; return; }
		if ( _mouth.Model == _matrixModel && _matrix != null ) return;

		var model = _mouth.Model;
		int morphCount = model.MorphCount;
		_matrix = new float[VisemeNames.Length, morphCount];
		_morphNames = new string[morphCount];
		_activeMorphs.Clear();
		for ( int m = 0; m < morphCount; m++ )
		{
			_morphNames[m] = model.GetMorphName( m );
			bool any = false;
			for ( int v = 0; v < VisemeNames.Length; v++ )
			{
				var w = model.GetVisemeMorph( VisemeNames[v], m );
				_matrix[v, m] = w;
				if ( w > 0.001f ) any = true;
			}
			if ( any ) _activeMorphs.Add( m );
		}
		_current = new float[morphCount];
		_matrixModel = model;

		if ( _activeMorphs.Count == 0 )
			Log.Warning( $""__CLASS__: model '{model.Name}' has no baked viseme->morph data — mouth won't move (audio still plays). Citizen has it; custom models may not."" );
	}

	private void DriveMouthFrame()
	{
		if ( DebugLogVisemes && _sinceVisemeLog > 0.25f ) LogVisemes();
		if ( !DriveMouth || _mouth == null || !_mouth.IsValid() || _matrix == null ) return;

		var visemes = Handle.LipSync.Visemes;
		if ( visemes == null || visemes.Count == 0 ) return;

		int vc = System.Math.Min( visemes.Count, VisemeNames.Length );
		float alpha = MouthSmoothSeconds <= 0f ? 1f : MathX.Clamp( Time.Delta / MouthSmoothSeconds, 0f, 1f );

		foreach ( var m in _activeMorphs )
		{
			float target = 0f;
			for ( int v = 0; v < vc; v++ )
				target += visemes[v] * _matrix[v, m];
			target = MathX.Clamp( target * MorphScale, 0f, 1f );
			_current[m] = MathX.Lerp( _current[m], target, alpha );
			_mouth.Morphs.Set( _morphNames[m], _current[m] );
		}
	}

	/// <summary>Release our morph overrides with a short fade (engine blends back to animation).</summary>
	private void RelaxMouth()
	{
		if ( _mouth == null || !_mouth.IsValid() || _morphNames == null ) return;
		foreach ( var m in _activeMorphs )
		{
			_mouth.Morphs.Clear( _morphNames[m], 0.15f );
			if ( _current != null ) _current[m] = 0f;
		}
	}

	/// <summary>Debug: log the live viseme stream state (call while a line plays, or set DebugLogVisemes).</summary>
	public void LogVisemes()
	{
		_sinceVisemeLog = 0f;
		if ( Handle == null || !Handle.IsValid ) { Log.Info( ""__CLASS__: no live handle."" ); return; }
		var ls = Handle.LipSync;
		var visemes = ls.Visemes;
		if ( visemes == null ) { Log.Info( $""__CLASS__: LipSync.Enabled={ls.Enabled} frame={ls.FrameNumber} Visemes=null"" ); return; }
		var parts = """";
		int vc = System.Math.Min( visemes.Count, VisemeNames.Length );
		for ( int v = 0; v < vc; v++ )
			if ( visemes[v] > 0.01f ) parts += $""{VisemeNames[v]}={visemes[v]:0.00} "";
		Log.Info( $""__CLASS__: frame={ls.FrameNumber} count={visemes.Count} {(parts.Length > 0 ? parts : ""(all zero)"")}"" );
	}

	// ── Loose HUD bind (create_dialogue_system contract) ─────────────────

	/// <summary>
	/// Capability-match a generated create_dialogue_system component anywhere in the scene:
	/// a List&lt;string&gt; property named Lines + a parameterless Begin method + an IsActive bool.
	/// Bound loosely via TypeLibrary so neither system references the other.
	/// </summary>
	private void TryBindHud()
	{
		_hud = null; _hudLines = null; _hudBegin = null;
		if ( Scene == null ) return;

		foreach ( var c in Scene.GetAllComponents<Component>() )
		{
			if ( c == null || c == this || !c.IsValid() ) continue;
			var td = TypeLibrary.GetType( c.GetType() );
			if ( td == null ) continue;
			var linesProp = td.GetProperty( ""Lines"" );
			if ( linesProp == null || linesProp.PropertyType != typeof( List<string> ) ) continue;
			var begin = td.GetMethod( ""Begin"" );
			if ( begin == null ) continue;
			var isActive = td.GetProperty( ""IsActive"" );
			if ( isActive == null || isActive.PropertyType != typeof( bool ) ) continue;

			_hud = c;
			_hudLines = linesProp;
			_hudBegin = begin;
			return;
		}
	}

	private void ShowOnHud( string speakerName, string text )
	{
		if ( _hud == null || !_hud.IsValid() || _hudLines == null || _hudBegin == null ) return;
		try
		{
			// Mutate the HUD's existing Lines list in place (read-only bind — no SetValue needed).
			if ( _hudLines.GetValue( _hud ) is List<string> hudLines )
			{
				hudLines.Clear();
				hudLines.Add( string.IsNullOrWhiteSpace( speakerName ) ? text : $""{speakerName}: {text}"" );
				_hudBegin.Invoke( _hud, null );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $""__CLASS__: HUD mirror failed ({ex.Message}) — continuing audio-only."" );
			_hud = null;
		}
	}

	protected override void OnDestroy()
	{
		if ( Handle != null && Handle.IsValid && Handle.IsPlaying ) Handle.Stop( 0f );
	}
}
";
}

// ═══════════════════════════════════════════════════════════════════════════
//  2. create_camera_effects  (code-gen; scene-mutating [writes a file])
//     Static conveniences over the SDK's NEW built-in camera effects
//     (CameraComponent.AddShake / AddPunch / AddTilt, all returning
//     Sandbox.CameraEffectSystem.BaseEffect — whitelist-verified in game code
//     2026-07-13): CameraFx.Shake/ShakeAt/Punch/PunchAngles/Tilt static API +
//     one-word preset triggers (HitPunch / ExplosionShake / LandingTilt) with
//     [Property] tunables. Resolves Scene.Camera, falls back to IsMainCamera
//     search, warns when the scene has no camera.
// ═══════════════════════════════════════════════════════════════════════════
public class CreateCameraEffectsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "CameraFx", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			string F( float v ) => AiSystemsHelpers.F( v );

			var hitAmp   = AiSystemsHelpers.Float( p, "hitPunchAmplitude", 8f );
			var hitFreq  = AiSystemsHelpers.Float( p, "hitPunchFrequency", 20f );
			var hitDur   = AiSystemsHelpers.Float( p, "hitPunchDuration", 0.25f );
			var hitFov   = AiSystemsHelpers.Float( p, "hitPunchFovAmplitude", 3f );
			var expAmp   = AiSystemsHelpers.Float( p, "explosionShakeAmplitude", 5f );
			var expFreq  = AiSystemsHelpers.Float( p, "explosionShakeFrequency", 25f );
			var expDur   = AiSystemsHelpers.Float( p, "explosionShakeDuration", 0.8f );
			var tiltDur  = AiSystemsHelpers.Float( p, "landingTiltDuration", 0.35f );
			var tiltEase = AiSystemsHelpers.Float( p, "landingTiltEase", 0.15f );

			// Optional Vector3 punch direction ({x,y,z} or "x,y,z"); default = camera-backward kick.
			var hitDirLit = "Vector3.Backward";
			if ( p.TryGetProperty( "hitPunchDirection", out var hd ) && hd.ValueKind != JsonValueKind.Null )
			{
				var v = ClaudeBridge.ParseVector3( hd );
				hitDirLit = $"new Vector3( {F( v.x )}, {F( v.y )}, {F( v.z )} )";
			}

			// Optional landing tilt as {x:pitch, y:yaw, z:roll} (or "p,y,r"); default 5° pitch + 2° roll dip.
			var tiltLit = "new Angles( 5f, 0f, 2f )";
			if ( p.TryGetProperty( "landingTiltAngles", out var ta ) && ta.ValueKind != JsonValueKind.Null )
			{
				var a = ClaudeBridge.ParseVector3( ta );
				tiltLit = $"new Angles( {F( a.x )}, {F( a.y )}, {F( a.z )} )";
			}

			var code = CameraFxTemplate
				.Replace( "__CLASS__", className )
				.Replace( "__HITDIR__", hitDirLit )
				.Replace( "__HITAMP__", F( hitAmp ) )
				.Replace( "__HITFREQ__", F( hitFreq ) )
				.Replace( "__HITDUR__", F( hitDur ) )
				.Replace( "__HITFOV__", F( hitFov ) )
				.Replace( "__EXPAMP__", F( expAmp ) )
				.Replace( "__EXPFREQ__", F( expFreq ) )
				.Replace( "__EXPDUR__", F( expDur ) )
				.Replace( "__TILTANGLES__", tiltLit )
				.Replace( "__TILTDUR__", F( tiltDur ) )
				.Replace( "__TILTEASE__", F( tiltEase ) );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string placeNote = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = AiSystemsHelpers.PlaceOnTarget( tid.GetString(), className, out placeNote );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				staticApi = new[]
				{
					"Shake(amplitude, frequency, duration)",
					"ShakeAt(position, radius, amplitude, frequency, duration)",
					"Punch(direction, amplitude, frequency, duration, fovAmplitude)",
					"PunchAngles(angles, frequency, duration, fovAmplitude)",
					"Tilt(angles, duration, easeTime)",
					"ResolveCamera()"
				},
				presetTriggers = new[] { "HitPunch()", "ExplosionShake()", "ExplosionShakeAt(position, radius)", "LandingTilt()" },
				propertyNames = new[] { "HitPunchDirection", "HitPunchAmplitude", "HitPunchFrequency", "HitPunchDuration", "HitPunchFovAmplitude", "ExplosionShakeAmplitude", "ExplosionShakeFrequency", "ExplosionShakeDuration", "LandingTiltAngles", "LandingTiltDuration", "LandingTiltEase" },
				placedOn,
				placementNote = placeNote,
				note = "Wraps the ENGINE'S built-in camera effects (CameraComponent.AddShake/AddPunch/AddTilt — fire-and-forget, self-expiring; " +
				       "whitelist-verified in sandboxed game code 2026-07-13). Call the statics from anywhere (<class>.Shake(4f, 25f, 0.8f)) — they resolve " +
				       "Scene.Camera, fall back to the IsMainCamera search, and warn+return null when the scene has no camera. Each returns the live " +
				       "Sandbox.CameraEffectSystem.BaseEffect (Stop()/IsDone; ShakeAt also sets Epicenter+Radius for distance falloff). The component itself " +
				       "just carries the [Property] preset tunables + one-word triggers (HitPunch/ExplosionShake/LandingTilt) — statics work with NO instance " +
				       "placed. RELATIONSHIP: create_camera_shake is the CONTINUOUS trauma model (AddTrauma accumulates, decays over time); these built-ins are " +
				       "ONE-SHOT engine effects — they compose safely, but don't stack both for the same event or hits feel doubled. HONESTY: compile + camera " +
				       "resolution are verified; the editor cannot judge FEEL — tune amplitudes in a human playtest. Effects are LOCAL visuals: trigger inside an " +
				       "[Rpc.Broadcast] handler if every client should feel it."
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_camera_effects failed: {ex.Message}" } );
		}
	}

	// Verified template (default render live-compile-verified 2026-07-13). Tokens: __CLASS__ etc.
	private const string CameraFxTemplate =
@"using Sandbox;
using System;

// Static conveniences over the engine's BUILT-IN camera effects (CameraComponent.AddShake /
// AddPunch / AddTilt — fire-and-forget, self-expiring). Complements create_camera_shake's
// continuous trauma model: use these for one-shot feel hits, the trauma component for
// sustained rumble; they compose safely (both feed the same CameraEffectSystem transform).
// LOCAL-only visuals — call from an [Rpc.Broadcast] handler if every client should feel it.
public sealed class __CLASS__ : Component
{
	// ── HitPunch preset (directional kick, e.g. taking damage) ────────────
	[Property] public Vector3 HitPunchDirection { get; set; } = __HITDIR__;
	[Property] public float HitPunchAmplitude { get; set; } = __HITAMP__;
	[Property] public float HitPunchFrequency { get; set; } = __HITFREQ__;
	[Property] public float HitPunchDuration { get; set; } = __HITDUR__;
	[Property] public float HitPunchFovAmplitude { get; set; } = __HITFOV__;

	// ── ExplosionShake preset (omnidirectional rumble) ────────────────────
	[Property] public float ExplosionShakeAmplitude { get; set; } = __EXPAMP__;
	[Property] public float ExplosionShakeFrequency { get; set; } = __EXPFREQ__;
	[Property] public float ExplosionShakeDuration { get; set; } = __EXPDUR__;

	// ── LandingTilt preset (brief pitch/roll dip, e.g. hard landing) ──────
	[Property] public Angles LandingTiltAngles { get; set; } = __TILTANGLES__;
	[Property] public float LandingTiltDuration { get; set; } = __TILTDUR__;
	[Property] public float LandingTiltEase { get; set; } = __TILTEASE__;

	/// <summary>The most recently enabled instance (used by the static resolvers).</summary>
	public static __CLASS__ Instance { get; private set; }

	protected override void OnEnabled() { Instance = this; }
	protected override void OnDisabled() { if ( Instance == this ) Instance = null; }

	/// <summary>Scene.Camera, else the IsMainCamera CameraComponent, else the first camera found. Null when the scene has none.</summary>
	public static CameraComponent ResolveCamera()
	{
		var scene = Instance != null && Instance.IsValid() ? Instance.Scene : Game.ActiveScene;
		if ( scene == null ) return null;

		var cam = scene.Camera;
		if ( cam != null && cam.IsValid() ) return cam;

		CameraComponent first = null;
		foreach ( var c in scene.GetAllComponents<CameraComponent>() )
		{
			if ( c == null || !c.IsValid() ) continue;
			if ( c.IsMainCamera ) return c;
			if ( first == null ) first = c;
		}
		return first;
	}

	/// <summary>Omnidirectional camera shake (engine built-in). Returns the live effect (Stop()/IsDone), or null when no camera.</summary>
	public static Sandbox.CameraEffectSystem.BaseEffect Shake( float amplitude = 1f, float frequency = 10f, float duration = 0.5f )
	{
		var cam = ResolveCamera();
		if ( cam == null ) { Log.Warning( ""__CLASS__.Shake: no camera in the scene."" ); return null; }
		return cam.AddShake( amplitude, frequency, duration );
	}

	/// <summary>Shake with a world epicenter + falloff radius — cameras farther from position feel less.</summary>
	public static Sandbox.CameraEffectSystem.BaseEffect ShakeAt( Vector3 position, float radius, float amplitude = 1f, float frequency = 10f, float duration = 0.5f )
	{
		var fx = Shake( amplitude, frequency, duration );
		if ( fx != null )
		{
			fx.Epicenter = position;
			fx.Radius = radius;
		}
		return fx;
	}

	/// <summary>Directional positional kick (engine built-in AddPunch, Vector3 overload).</summary>
	public static Sandbox.CameraEffectSystem.BaseEffect Punch( Vector3 direction, float amplitude, float frequency = 1f, float duration = 0.3f, float fovAmplitude = 0f )
	{
		var cam = ResolveCamera();
		if ( cam == null ) { Log.Warning( ""__CLASS__.Punch: no camera in the scene."" ); return null; }
		return cam.AddPunch( direction, amplitude, frequency, duration, fovAmplitude );
	}

	/// <summary>Rotational kick (engine built-in AddPunch, Angles overload).</summary>
	public static Sandbox.CameraEffectSystem.BaseEffect PunchAngles( Angles angles, float frequency = 1f, float duration = 0.3f, float fovAmplitude = 0f )
	{
		var cam = ResolveCamera();
		if ( cam == null ) { Log.Warning( ""__CLASS__.PunchAngles: no camera in the scene."" ); return null; }
		return cam.AddPunch( angles, frequency, duration, fovAmplitude );
	}

	/// <summary>Eased tilt toward an angle offset that releases over the duration (engine built-in AddTilt).</summary>
	public static Sandbox.CameraEffectSystem.BaseEffect Tilt( Angles angles, float duration = 0.4f, float easeTime = 0.2f )
	{
		var cam = ResolveCamera();
		if ( cam == null ) { Log.Warning( ""__CLASS__.Tilt: no camera in the scene."" ); return null; }
		return cam.AddTilt( angles, duration, easeTime );
	}

	// ── One-word preset triggers (use the [Property] tunables above) ──────

	public Sandbox.CameraEffectSystem.BaseEffect HitPunch()
		=> Punch( HitPunchDirection, HitPunchAmplitude, HitPunchFrequency, HitPunchDuration, HitPunchFovAmplitude );

	public Sandbox.CameraEffectSystem.BaseEffect ExplosionShake()
		=> Shake( ExplosionShakeAmplitude, ExplosionShakeFrequency, ExplosionShakeDuration );

	/// <summary>Explosion shake with falloff from a world position (great for grenades).</summary>
	public Sandbox.CameraEffectSystem.BaseEffect ExplosionShakeAt( Vector3 position, float radius = 512f )
		=> ShakeAt( position, radius, ExplosionShakeAmplitude, ExplosionShakeFrequency, ExplosionShakeDuration );

	public Sandbox.CameraEffectSystem.BaseEffect LandingTilt()
		=> Tilt( LandingTiltAngles, LandingTiltDuration, LandingTiltEase );
}
";
}
