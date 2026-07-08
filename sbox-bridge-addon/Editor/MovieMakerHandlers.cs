using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════════════
// MovieMaker / cutscene family (v1.20.0) — first bridge coverage of
// Sandbox.MovieMaker, which landed in the shipping build (verified live via
// search_types on 2026-07-08; it was absent on 2026-07-02).
//
//   list_movies       enumerate the project's .movie resources
//   add_movie_player  wire a MoviePlayer component + MovieResource onto an object
//   play_movie        start playback (play mode for real playback; see note)
//   stop_movie        stop playback (optionally rewind)
//
// Surface verified live via describe_type (Sandbox.MovieMaker.MoviePlayer):
//   Resource : IMovieResource (writable — a MovieResource satisfies it)
//   IsPlaying/IsLooping : bool (writable)   TimeScale : float
//   Position : MovieTime   PositionSeconds : float
//   Play() / Play(MovieResource) / Play(IMovieClip)   UpdateTargets()
//   CreateTargets : bool   Binder : TrackBinder (read-only)
//
// MovieResource : GameResource (".movie" asset) — loads via
// ResourceLibrary.Get<MovieResource>(path), same pattern as LipSyncHandlers'
// SoundEvent load. Movies are AUTHORED in the editor's Movie Maker dock
// (Editor.MovieMaker.MovieEditor) — the bridge wires and plays them; it does
// not author keyframes.
//
// Types are fully-qualified (Sandbox.MovieMaker.*) — `using Editor;` is in
// scope and the Editor.MovieMaker namespace exists, so bare names risk the
// same ambiguity class as the FileSystem gotcha.
//
// Registration (MyEditorMenu.cs RegisterHandlers, Batch 45):
//   Register( "list_movies",      () => new ListMoviesHandler() );
//   Register( "add_movie_player", () => new AddMoviePlayerHandler() );
//   Register( "play_movie",       () => new PlayMovieHandler() );
//   Register( "stop_movie",       () => new StopMovieHandler() );
// Scene-mutating: ONLY "add_movie_player" (play/stop must stay callable in
// play mode, list is read-only).
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// list_movies — enumerate the project's .movie resources. No params.
/// File-scan of the assets tree (registration-independent), with a
/// ResourceLibrary load check so the response says which ones are loadable.
/// </summary>
public class ListMoviesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var assetsPath = Project.Current?.GetAssetsPath();
		if ( string.IsNullOrEmpty( assetsPath ) || !Directory.Exists( assetsPath ) )
			return Task.FromResult<object>( new { error = "No project assets path" } );

		var movies = new List<object>();
		foreach ( var file in Directory.EnumerateFiles( assetsPath, "*.movie", SearchOption.AllDirectories ) )
		{
			// Asset-relative forward-slash path — the form ResourceLibrary.Get expects.
			var rel = Path.GetRelativePath( assetsPath, file ).Replace( '\\', '/' );
			var res = ResourceLibrary.Get<Sandbox.MovieMaker.MovieResource>( rel );
			movies.Add( new
			{
				path = rel,
				name = Path.GetFileNameWithoutExtension( file ),
				loadable = res != null,
				hasCompiledClip = res?.Compiled != null
			} );
		}

		return Task.FromResult<object>( new
		{
			count = movies.Count,
			movies,
			note = movies.Count == 0
				? "No .movie resources yet — author one in the editor's Movie Maker dock (Window → Movie Maker), then add_movie_player to wire it."
				: "Wire one onto an object with add_movie_player, then play_movie in play mode."
		} );
	}
}

/// <summary>
/// add_movie_player — add + wire a Sandbox.MovieMaker.MoviePlayer. Params:
///   id            : optional GameObject GUID (created as "Movie Player" when omitted)
///   moviePath     : optional .movie resource path (asset-relative) → MoviePlayer.Resource
///   isLooping     : optional bool
///   timeScale     : optional float (1 = normal speed)
///   createTargets : optional bool (let the player create missing track-target objects)
///   playOnStart   : optional bool — sets IsPlaying so playback begins when play mode starts
/// </summary>
public class AddMoviePlayerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		GameObject go = null;
		if ( p.TryGetProperty( "id", out var idEl ) && Guid.TryParse( idEl.GetString(), out var guid ) )
		{
			go = scene.Directory.FindByGuid( guid );
			if ( go == null ) return Task.FromResult<object>( new { error = $"GameObject not found: {idEl.GetString()}" } );
		}
		else
		{
			go = scene.CreateObject( true );
			go.Name = "Movie Player";
		}

		var player = go.GetOrAddComponent<Sandbox.MovieMaker.MoviePlayer>();

		string movieInfo = null;
		if ( p.TryGetProperty( "moviePath", out var mp ) && !string.IsNullOrWhiteSpace( mp.GetString() ) )
		{
			var path = mp.GetString();
			var res = ResourceLibrary.Get<Sandbox.MovieMaker.MovieResource>( path );
			if ( res == null )
				return Task.FromResult<object>( new { error = $"MovieResource not found: '{path}' (list_movies shows available .movie assets — author them in the Movie Maker dock)" } );
			player.Resource = res;
			movieInfo = path;
		}

		if ( p.TryGetProperty( "isLooping", out var loop ) ) player.IsLooping = loop.GetBoolean();
		if ( p.TryGetProperty( "timeScale", out var ts ) && ts.TryGetSingle( out var tsf ) ) player.TimeScale = tsf;
		if ( p.TryGetProperty( "createTargets", out var ct ) ) player.CreateTargets = ct.GetBoolean();
		if ( p.TryGetProperty( "playOnStart", out var pos ) ) player.IsPlaying = pos.GetBoolean();

		return Task.FromResult<object>( new
		{
			moviePlayer = true,
			movie = movieInfo,
			note = movieInfo == null
				? "No movie wired yet — pass moviePath (see list_movies), or set the Resource in the inspector."
				: "Playback runs in PLAY MODE — start_play then play_movie, and verify with capture_view.",
			gameObject = ClaudeBridge.SerializeGo( go )
		} );
	}
}

/// <summary>
/// Shared MoviePlayer lookup for play_movie / stop_movie: explicit id first,
/// else the first MoviePlayer in the runtime scene (play mode) or editor scene.
/// </summary>
internal static class MoviePlayerLocator
{
	public static Sandbox.MovieMaker.MoviePlayer Find( JsonElement p, out Scene scene, out string error )
	{
		error = null;
		scene = Game.IsPlaying ? Game.ActiveScene : SceneEditorSession.Active?.Scene;
		if ( scene == null ) { error = "No active scene"; return null; }

		if ( p.TryGetProperty( "id", out var idEl ) && Guid.TryParse( idEl.GetString(), out var guid ) )
		{
			var go = scene.Directory.FindByGuid( guid );
			if ( go == null ) { error = $"GameObject not found: {idEl.GetString()}"; return null; }
			var onGo = go.GetComponent<Sandbox.MovieMaker.MoviePlayer>();
			if ( onGo == null ) { error = $"No MoviePlayer on '{go.Name}' — add_movie_player first"; return null; }
			return onGo;
		}

		var any = scene.GetAllComponents<Sandbox.MovieMaker.MoviePlayer>().FirstOrDefault();
		if ( any == null ) { error = "No MoviePlayer in the scene — add_movie_player first"; return null; }
		return any;
	}
}

/// <summary>
/// play_movie — start MoviePlayer playback. Params:
///   id              : optional GameObject GUID (first MoviePlayer in scene when omitted)
///   moviePath       : optional .movie to load + play (otherwise plays the wired Resource)
///   positionSeconds : optional float seek before playing
///   timeScale       : optional float
///   isLooping       : optional bool
/// Real playback advances in PLAY MODE; in edit mode this sets state (and the
/// Movie Maker dock previews), which the response calls out.
/// </summary>
public class PlayMovieHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var player = MoviePlayerLocator.Find( p, out var scene, out var err );
		if ( player == null ) return Task.FromResult<object>( new { error = err } );

		if ( p.TryGetProperty( "moviePath", out var mp ) && !string.IsNullOrWhiteSpace( mp.GetString() ) )
		{
			var res = ResourceLibrary.Get<Sandbox.MovieMaker.MovieResource>( mp.GetString() );
			if ( res == null )
				return Task.FromResult<object>( new { error = $"MovieResource not found: '{mp.GetString()}' (list_movies)" } );
			player.Resource = res;
		}

		if ( player.Resource == null && player.Clip == null )
			return Task.FromResult<object>( new { error = "MoviePlayer has no movie — pass moviePath or wire one via add_movie_player" } );

		if ( p.TryGetProperty( "isLooping", out var loop ) ) player.IsLooping = loop.GetBoolean();
		if ( p.TryGetProperty( "timeScale", out var ts ) && ts.TryGetSingle( out var tsf ) ) player.TimeScale = tsf;
		if ( p.TryGetProperty( "positionSeconds", out var seek ) && seek.TryGetSingle( out var seekF ) ) player.PositionSeconds = seekF;

		player.Play();

		return Task.FromResult<object>( new
		{
			playing = player.IsPlaying,
			positionSeconds = player.PositionSeconds,
			timeScale = player.TimeScale,
			isLooping = player.IsLooping,
			mode = Game.IsPlaying ? "play" : "edit",
			note = Game.IsPlaying
				? "Playing — capture_view/take_screenshot to see it."
				: "Edit mode: state set, but clips only advance in PLAY MODE (start_play) or via the Movie Maker dock preview."
		} );
	}
}

/// <summary>
/// stop_movie — stop MoviePlayer playback. Params:
///   id     : optional GameObject GUID (first MoviePlayer in scene when omitted)
///   rewind : optional bool — also reset the playhead to 0
/// </summary>
public class StopMovieHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var player = MoviePlayerLocator.Find( p, out var scene, out var err );
		if ( player == null ) return Task.FromResult<object>( new { error = err } );

		player.IsPlaying = false;
		if ( p.TryGetProperty( "rewind", out var rw ) && rw.GetBoolean() )
			player.PositionSeconds = 0f;

		return Task.FromResult<object>( new
		{
			playing = player.IsPlaying,
			positionSeconds = player.PositionSeconds
		} );
	}
}
