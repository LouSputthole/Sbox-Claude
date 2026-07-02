using Editor;
using Sandbox;
using System;
using System.Text.Json;
using System.Threading.Tasks;

// ═════════════════════════════════════════════════════════════════════════════
// create_save_slots -- multi-slot save manager over FileSystem.Data.
//
// The multi-slot / slot-picker sibling of create_save_system (single-file
// autosave). Emits ONE sealed Component that manages N save slots: list / create
// / load / save / delete, each with lightweight picker metadata (name +
// timestamp + playtime), versioned payloads with delete-on-version-mismatch, and
// an OPTIONAL GUID scene-object reconciliation on load (destroy objects the save
// marks destroyed / reposition survivors / skip missing).
//
// STORAGE CHOICE -- index-file (manifest) pattern. The only FileSystem.Data
// members compile-verified by create_save_system's generated code are
// ReadJsonOrDefault<T> and WriteJson. There is no verified directory-listing API
// on that surface, so slots are enumerated via a small MANIFEST file
// (saveslots.json) rather than FindFile/enumeration -- every read/write here
// stays inside ReadJsonOrDefault / WriteJson / DeleteFile. (DeleteFile is
// cookbook-cited from facepunch.fair's delete-on-version-mismatch.)
//
// Same conventions as ScaffoldHandlers.cs: reuses ScaffoldHelpers.PrepareCodeFile
// / WriteCode and ClaudeBridge.SanitizeIdentifier / SerializeGo; guarded in
// try/catch and returns `new { error = ... }` on failure. The generated C# STRING
// is SANDBOXED game code (System.Math/MathF ok; Array.Clone() blocked; no
// System.Net; List<T> via System.Collections.Generic).
// ═════════════════════════════════════════════════════════════════════════════
public class CreateSaveSlotsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "SaveSlotManager", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var ci = System.Globalization.CultureInfo.InvariantCulture;

			int maxSlots = p.TryGetProperty( "maxSlots", out var ms ) && ms.TryGetInt32( out var msi ) ? msi : 3;
			if ( maxSlots < 1 ) maxSlots = 1;
			if ( maxSlots > 100 ) maxSlots = 100;

			bool reconcile = p.TryGetProperty( "sceneReconciliation", out var sr ) &&
				( sr.ValueKind == JsonValueKind.True ||
				  ( sr.ValueKind == JsonValueKind.String && bool.TryParse( sr.GetString(), out var srb ) && srb ) );

			string maxStr       = maxSlots.ToString( ci );
			string reconcileStr = reconcile ? "true" : "false";

			var code = BuildCode( className, maxStr, reconcileStr );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = PlaceOnTarget( tid.GetString(), className, out note );

			var nextSteps = new[]
			{
				"trigger_hotload so the new component type registers in the TypeLibrary.",
				$"Add {className} to a scene object (add_component_with_properties) or pass targetId next time.",
				"Slot picker: read Manifest.Slots (Index / Used / Name / SavedAtUnix / PlaytimeSeconds); call CreateSlot / Load / Save / DeleteSlot by index.",
				"Add your own fields to the SlotData inner class; bump the Version const when the shape changes (old slots delete-on-load).",
				reconcile
					? "SceneReconciliation is ON: call RecordObject(go) for placeables to persist; RecordObject(go, true) writes a tombstone when one is removed."
					: "SceneReconciliation is OFF (simple slot save). Set it true to reconcile scene objects by GameObject.Id on load."
			};

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				maxSlots,
				sceneReconciliation = reconcile,
				placedOn,
				note,
				nextSteps
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_save_slots failed: {ex.Message}" } );
		}
	}

	// Optionally drop the component onto an existing scene object by GUID -- copied
	// verbatim from CreateSaveSystemHandler.PlaceOnTarget so behaviour matches.
	static object PlaceOnTarget( string targetId, string className, out string note )
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

	static string BuildCode( string className, string maxSlots, string reconcile )
	{
		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- multi-slot save manager over FileSystem.Data (the multi-slot,
/// slot-picker variant of a single-file save system).
///
/// LAYOUT (index-file pattern): a lightweight MANIFEST file (saveslots.json) holds
/// per-slot metadata for a slot picker -- Used flag + Name + timestamp + playtime
/// -- so listing slots never loads a heavy payload; each slot's actual game state
/// lives in its own saveslot_&lt;i&gt;.json. This stays entirely within the
/// FileSystem.Data.ReadJsonOrDefault / WriteJson / DeleteFile surface (no directory
/// enumeration).
///
/// Add your own fields to the SlotData inner class. Bump the Version const when the
/// shape changes so old slots delete-on-load instead of crashing. Runs only on the
/// owning machine (IsProxy guard) -- clients display, never write disk.
///
/// SCENE RECONCILIATION (optional, off by default): saved records carry each
/// object's GameObject.Id GUID; on load each is looked up via
/// Scene.Directory.FindByGuid -- objects the save marks destroyed are destroyed,
/// survivors are repositioned, missing ones are skipped. Call RecordObject(go) to
/// track a placeable; RecordObject(go, true) writes a tombstone when it is removed.
/// </summary>
public sealed class {className} : Component
{{
	/// Bump this when SlotData's shape changes -- old slots are deleted on load.
	public const int Version = 1;

	[Property] public int MaxSlots {{ get; set; }} = {maxSlots};
	[Property] public string ManifestFile {{ get; set; }} = ""saveslots.json"";
	[Property] public bool SceneReconciliation {{ get; set; }} = {reconcile};

	/// Per-slot metadata for the slot picker (kept small -- no game payload).
	public class SlotInfo
	{{
		public int Index {{ get; set; }}
		public bool Used {{ get; set; }}
		public string Name {{ get; set; }} = """";
		public long SavedAtUnix {{ get; set; }}
		public float PlaytimeSeconds {{ get; set; }}
	}}

	/// The manifest: one small record per slot. Versioned like the payload.
	public class SlotManifest
	{{
		public int Version {{ get; set; }}
		public List<SlotInfo> Slots {{ get; set; }} = new List<SlotInfo>();
	}}

	/// A reconciliation record -- one saved scene object, keyed by GameObject.Id.
	public class SavedObject
	{{
		public string Id {{ get; set; }}
		public bool Destroyed {{ get; set; }}
		public Vector3 Position {{ get; set; }}
		public Rotation Rotation {{ get; set; }}
	}}

	/// The per-slot payload -- the actual saved game state. Add your fields here.
	public class SlotData
	{{
		public int Version {{ get; set; }}
		public string Name {{ get; set; }} = """";
		public float PlaytimeSeconds {{ get; set; }}
		public int Money {{ get; set; }}
		public int Day {{ get; set; }}
		/// Populated only when SceneReconciliation is on. See RecordObject.
		public List<SavedObject> Objects {{ get; set; }} = new List<SavedObject>();
		// Add your own game fields here.
	}}

	/// The loaded manifest -- the slot list a picker binds to.
	public SlotManifest Manifest {{ get; private set; }} = new SlotManifest();
	/// The currently loaded slot payload, or null if none is loaded.
	public SlotData Active {{ get; private set; }}
	/// Index of the loaded slot, or -1 if none.
	public int ActiveSlot {{ get; private set; }} = -1;

	/// Fires after Load(index) succeeds, with the slot index + loaded payload.
	public static Action<int, SlotData> OnSlotLoaded {{ get; set; }}
	/// Fires after Save(index).
	public static Action<int, SlotData> OnSlotSaved {{ get; set; }}
	/// Fires after DeleteSlot(index).
	public static Action<int> OnSlotDeleted {{ get; set; }}

	static readonly DateTime Epoch = new DateTime( 1970, 1, 1, 0, 0, 0, DateTimeKind.Utc );
	static long NowUnix() => (long)( DateTime.UtcNow - Epoch ).TotalSeconds;

	string SlotFile( int index ) => $""saveslot_{{index}}.json"";

	protected override void OnStart()
	{{
		if ( IsProxy ) return;   // only the owning machine touches disk
		LoadManifest();
	}}

	protected override void OnUpdate()
	{{
		// Accrue playtime on the live slot so the picker shows an honest number.
		if ( IsProxy || Active == null ) return;
		Active.PlaytimeSeconds += Time.Delta;
	}}

	/// Read the manifest (or start fresh on missing / version mismatch) and
	/// normalize it to exactly MaxSlots entries indexed 0..MaxSlots-1.
	public void LoadManifest()
	{{
		if ( IsProxy ) return;
		var loaded = FileSystem.Data.ReadJsonOrDefault<SlotManifest>( ManifestFile, null );
		Manifest = ( loaded == null || loaded.Version != Version )
			? new SlotManifest {{ Version = Version }}
			: loaded;

		var slots = new List<SlotInfo>();
		for ( int i = 0; i < MaxSlots; i++ )
		{{
			var existing = Manifest.Slots.Find( s => s.Index == i );
			slots.Add( existing ?? new SlotInfo {{ Index = i }} );
		}}
		Manifest.Slots = slots;
	}}

	/// Write the manifest to disk. Cheap -- call after any slot metadata change.
	public void SaveManifest()
	{{
		if ( IsProxy ) return;
		FileSystem.Data.WriteJson( ManifestFile, Manifest );
	}}

	SlotInfo Info( int index ) => ( index >= 0 && index < Manifest.Slots.Count ) ? Manifest.Slots[index] : null;

	/// True if a slot holds a saved game.
	public bool IsUsed( int index )
	{{
		var info = Info( index );
		return info != null && info.Used;
	}}

	/// Start a brand-new game in a slot (empty payload) and make it Active.
	public SlotData CreateSlot( int index, string name )
	{{
		if ( IsProxy || index < 0 || index >= MaxSlots ) return null;
		Active = new SlotData {{ Version = Version, Name = name ?? """" }};
		ActiveSlot = index;
		Save( index );
		return Active;
	}}

	/// Write the Active payload into a slot and refresh its manifest metadata.
	public void Save( int index )
	{{
		if ( IsProxy || Active == null || index < 0 || index >= MaxSlots ) return;
		Active.Version = Version;
		FileSystem.Data.WriteJson( SlotFile( index ), Active );

		var info = Info( index );
		if ( info != null )
		{{
			info.Used = true;
			info.Name = Active.Name;
			info.PlaytimeSeconds = Active.PlaytimeSeconds;
			info.SavedAtUnix = NowUnix();
		}}
		ActiveSlot = index;
		SaveManifest();
		OnSlotSaved?.Invoke( index, Active );
	}}

	/// Load a slot's payload and make it Active. Returns false for an empty slot.
	/// Delete-on-version-mismatch: an incompatible payload is deleted and refused.
	public bool Load( int index )
	{{
		if ( IsProxy || index < 0 || index >= MaxSlots ) return false;
		var loaded = FileSystem.Data.ReadJsonOrDefault<SlotData>( SlotFile( index ), null );
		if ( loaded == null ) return false;                 // empty slot

		if ( loaded.Version != Version )
		{{
			FileSystem.Data.DeleteFile( SlotFile( index ) ); // old/incompatible -> discard
			MarkUnused( index );
			SaveManifest();
			return false;
		}}

		Active = Sanitize( loaded );
		ActiveSlot = index;
		if ( SceneReconciliation ) Reconcile( Active );
		OnSlotLoaded?.Invoke( index, Active );
		return true;
	}}

	/// Delete a slot's payload and free its manifest entry.
	public void DeleteSlot( int index )
	{{
		if ( IsProxy || index < 0 || index >= MaxSlots ) return;
		if ( IsUsed( index ) ) FileSystem.Data.DeleteFile( SlotFile( index ) );
		MarkUnused( index );
		if ( ActiveSlot == index ) {{ Active = null; ActiveSlot = -1; }}
		SaveManifest();
		OnSlotDeleted?.Invoke( index );
	}}

	void MarkUnused( int index )
	{{
		var info = Info( index );
		if ( info == null ) return;
		info.Used = false;
		info.Name = """";
		info.PlaytimeSeconds = 0f;
		info.SavedAtUnix = 0;
	}}

	/// Clamp-on-load: keep loaded values inside sane ranges so a corrupt or
	/// hand-edited slot cannot break the game. Extend per field.
	SlotData Sanitize( SlotData d )
	{{
		if ( d.Money < 0 ) d.Money = 0;
		if ( d.Day < 1 ) d.Day = 1;
		if ( d.PlaytimeSeconds < 0f ) d.PlaytimeSeconds = 0f;
		if ( d.Objects == null ) d.Objects = new List<SavedObject>();
		return d;
	}}

	/// Record a scene object's live state into the Active slot so
	/// SceneReconciliation can restore it on load. Pass destroyed:true to write a
	/// tombstone when the player removes a placeable (so load re-destroys it).
	public void RecordObject( GameObject go, bool destroyed = false )
	{{
		if ( Active == null || go == null ) return;
		var id = go.Id.ToString();
		var rec = Active.Objects.Find( o => o.Id == id );
		if ( rec == null ) {{ rec = new SavedObject {{ Id = id }}; Active.Objects.Add( rec ); }}
		rec.Destroyed = destroyed;
		rec.Position = go.WorldPosition;
		rec.Rotation = go.WorldRotation;
	}}

	/// Reconcile the live scene against the saved object list by GameObject.Id:
	/// destroy objects the save marks destroyed, reposition survivors, skip missing.
	/// (Cloning a missing survivor back in is game-specific -- it needs a prefab
	/// reference -- so missing records are skipped here; extend if you need it.)
	void Reconcile( SlotData data )
	{{
		if ( data == null || data.Objects == null ) return;
		foreach ( var rec in data.Objects )
		{{
			if ( string.IsNullOrEmpty( rec.Id ) || !Guid.TryParse( rec.Id, out var guid ) ) continue;
			var go = Scene.Directory.FindByGuid( guid );
			if ( go == null ) continue;                 // missing: skip
			if ( rec.Destroyed ) {{ go.Destroy(); continue; }}
			go.WorldPosition = rec.Position;
			go.WorldRotation = rec.Rotation;
		}}
	}}

	protected override void OnDestroy()
	{{
		// Final flush of the live slot so quitting doesn't lose progress.
		if ( !IsProxy && Active != null && ActiveSlot >= 0 ) Save( ActiveSlot );
	}}
}}
";
	}
}
