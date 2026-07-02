using Editor;
using Sandbox;
using System;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
// add_lipsync — wire up s&box's new Sandbox.LipSync component (shipped in the
// 2026-07-01 engine update, Feature/lipsync soundeditor #5227).
//
// LipSync drives a SkinnedModelRenderer's facial morphs (visemes) from a
// BaseSoundComponent's playing audio. Surface verified live via describe_type:
//   Sound          : BaseSoundComponent
//   Renderer       : SkinnedModelRenderer
//   MorphScale     : float
//   MorphSmoothTime: float
//
// This handler attaches LipSync to a GameObject (e.g. a spawn_citizen), wires
// the renderer (same GO, or rendererId), and optionally wires audio: an
// existing BaseSoundComponent on the GO, or a new SoundPointComponent bound to
// a .sound event path (ResourceLibrary.Get<SoundEvent> — same load pattern as
// dress_citizen's Clothing). Morphs animate at RUNTIME while the sound plays —
// verify in play mode (capture_view), not the static editor pose.
//
// Registration (MyEditorMenu.cs RegisterHandlers, characters group):
//   Register( "add_lipsync", () => new AddLipSyncHandler() );
// Scene-mutating (adds components) — add "add_lipsync" to _sceneMutatingCommands.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// add_lipsync — add + wire a LipSync component. Params:
///   id             : GameObject GUID (required; holds the SkinnedModelRenderer, e.g. a citizen)
///   rendererId     : optional GUID of a different GameObject holding the SkinnedModelRenderer
///   soundEvent     : optional path to a .sound event — creates/reuses a SoundPointComponent and wires it
///   playOnStart    : bool, default true (only applied when soundEvent is given)
///   volume         : optional float for the SoundPointComponent
///   morphScale     : optional float (LipSync.MorphScale; engine default kept when omitted)
///   morphSmoothTime: optional float (LipSync.MorphSmoothTime; engine default kept when omitted)
/// </summary>
public class AddLipSyncHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		if ( !p.TryGetProperty( "id", out var idEl ) || !Guid.TryParse( idEl.GetString(), out var guid ) )
			return Task.FromResult<object>( new { error = "id (GameObject GUID) is required" } );
		var go = scene.Directory.FindByGuid( guid );
		if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );

		// Renderer: same GO by default, or an explicit rendererId
		var rendererGo = go;
		if ( p.TryGetProperty( "rendererId", out var rid ) && Guid.TryParse( rid.GetString(), out var rguid ) )
		{
			rendererGo = scene.Directory.FindByGuid( rguid );
			if ( rendererGo == null ) return Task.FromResult<object>( new { error = $"rendererId GameObject not found: {rid.GetString()}" } );
		}
		var smr = rendererGo.GetComponent<SkinnedModelRenderer>();
		if ( smr == null )
			return Task.FromResult<object>( new { error = $"No SkinnedModelRenderer on '{rendererGo.Name}' — spawn_citizen first, or pass rendererId" } );

		var lip = go.GetOrAddComponent<LipSync>();
		lip.Renderer = smr;

		// Audio: explicit sound event > existing sound component on the GO
		string soundInfo = null;
		if ( p.TryGetProperty( "soundEvent", out var se ) && !string.IsNullOrWhiteSpace( se.GetString() ) )
		{
			var path = se.GetString();
			var evt = ResourceLibrary.Get<SoundEvent>( path );
			if ( evt == null )
				return Task.FromResult<object>( new { error = $"SoundEvent not found: '{path}' (list_sounds shows available .sound assets; create_sound_event makes one)" } );

			var sp = go.GetOrAddComponent<SoundPointComponent>();
			sp.SoundEvent = evt;
			sp.PlayOnStart = !p.TryGetProperty( "playOnStart", out var ps ) || ps.GetBoolean();
			if ( p.TryGetProperty( "volume", out var vol ) ) sp.Volume = vol.GetSingle();
			lip.Sound = sp;
			soundInfo = path;
		}
		else
		{
			var existing = go.GetComponent<BaseSoundComponent>();
			if ( existing != null )
			{
				lip.Sound = existing;
				soundInfo = existing.GetType().Name + " (existing)";
			}
		}

		if ( p.TryGetProperty( "morphScale", out var msc ) ) lip.MorphScale = msc.GetSingle();
		if ( p.TryGetProperty( "morphSmoothTime", out var mst ) ) lip.MorphSmoothTime = mst.GetSingle();

		return Task.FromResult<object>( new
		{
			lipsync = true,
			renderer = rendererGo.Name,
			sound = soundInfo,
			note = soundInfo == null
				? "No sound wired yet — pass soundEvent, or add a SoundPointComponent and set LipSync.Sound to it."
				: "Morphs animate at RUNTIME while the sound plays — verify in play mode (capture_view), not the static editor pose.",
			gameObject = ClaudeBridge.SerializeGo( go )
		} );
	}
}
