using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

// This file contains five play-mode handler classes:
//   StartPlayHandler, StopPlayHandler, IsPlayingHandler, PausePlayHandler, ResumePlayHandler.

/// <summary>
/// Enters play mode in the s&amp;box editor.
/// Returns immediately if already playing.
/// </summary>
public class StartPlayHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		if ( EditorScene.IsPlaying )
			return Task.FromResult<object>( new { state = "playing", message = "Already in play mode" } );

		EditorScene.Play();

		return Task.FromResult<object>( new
		{
			state = "playing",
			message = "Play mode started",
		} );
	}
}

/// <summary>
/// Exits play mode and returns to editor.
/// </summary>
public class StopPlayHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		if ( !EditorScene.IsPlaying )
			return Task.FromResult<object>( new { state = "stopped", message = "Already stopped" } );

		EditorScene.Stop();

		return Task.FromResult<object>( new
		{
			state = "stopped",
			message = "Play mode stopped",
		} );
	}
}

/// <summary>
/// Checks whether the editor is currently in play mode.
/// </summary>
public class IsPlayingHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		string state;
		if ( !EditorScene.IsPlaying )
			state = "stopped";
		// API-NOTE: EditorScene.IsPaused may not exist — verify against SDK.
		// If it doesn't compile, remove the pause check and just report playing/stopped.
		else if ( EditorScene.IsPaused )
			state = "paused";
		else
			state = "playing";

		return Task.FromResult<object>( new { state } );
	}
}

/// <summary>
/// Pauses the running game (stays in play mode but freezes simulation).
/// </summary>
public class PausePlayHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		if ( !EditorScene.IsPlaying )
			throw new Exception( "Cannot pause — not in play mode. Call start_play first." );

		EditorScene.Pause();

		return Task.FromResult<object>( new
		{
			state = "paused",
			message = "Game paused",
		} );
	}
}

/// <summary>
/// Resumes a paused game.
/// </summary>
public class ResumePlayHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		if ( !EditorScene.IsPlaying )
			throw new Exception( "Cannot resume — not in play mode." );

		EditorScene.Resume();

		return Task.FromResult<object>( new
		{
			state = "playing",
			message = "Game resumed",
		} );
	}
}
