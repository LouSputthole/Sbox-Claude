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
//  Economy & Save family (Track E) -- six Tier-2 scaffolds (code-gen; scene-mutating):
//
//    create_currency_account    audited host-authoritative ledger: [Sync(FromHost)]
//                               balance + Deposit/Withdraw/TryTransfer + fixed-size
//                               transaction ring buffer (Time.Now, reason, amount)
//    create_idle_economy        geometric bulk-buy: BaseCost * Growth^Owned, closed-form
//                               Buy 1 / Buy N / Buy Max, income tick auto-wired to a
//                               sibling wallet via TypeLibrary reflection
//    create_signed_save         tamper-evident save: FNV-1a signature over payload+salt,
//                               verify-on-load, clamp Sanitize() hook, forced reset on
//                               mismatch, versioned
//    create_meta_progression    between-runs roguelite meta: persistent meta-currency +
//                               unlock flags, Grant/TrySpend/Unlock/IsUnlocked,
//                               OnUnlocked static event, BankRun(int) run-end seam
//    add_steam_stat_currency    currency persisted over Sandbox.Services.Stats
//                               (SetValue/Flush; read-back via GetLocalPlayerStats)
//    create_loot_table_resource GameResource-based loot tables ([AssetType], .loot files)
//                               with nested-table entries + depth-capped resolver component
//
//  Compiles into the SAME editor assembly as MyEditorMenu.cs / ScaffoldHandlers.cs,
//  so it reuses the shared statics on ClaudeBridge (TryResolveProjectPath,
//  SanitizeIdentifier, SerializeGo) and ScaffoldHelpers (PrepareCodeFile /
//  WriteCode / Utf8NoBom). Handler code here is UNSANDBOXED editor code (System.* fine).
//
//  The C# *strings these handlers WRITE TO DISK* are SANDBOXED game code:
//    - sealed Component classes, no virtual members.
//    - [Sync(SyncFlags.FromHost)] for host-auth state (create_economy_wallet-verified);
//      IsProxy guards on every mutation.
//    - System.Math/MathF compile on this SDK; Array.Clone() is blocked (not used).
//    - FileSystem.Data.ReadJsonOrDefault<T>/WriteJson + ReadAllText/WriteAllText/
//      FileExists/DeleteFile all verified live via describe_type BaseFileSystem.
//    - Sandbox.Json.Serialize(object)/Deserialize<T>(string) verified live.
//    - Sandbox.Services.Stats: Increment(string,double), SetValue(string,double,string,object),
//      Flush(), GetLocalPlayerStats(string packageIdent) -> Stats.PlayerStats (NESTED type;
//      .Get(name) returns Stats.PlayerStat with .Value) -- all verified live. There is NO
//      Stats.LocalPlayer on this SDK.
//    - GameResourceAttribute is [Obsolete] on this SDK -- generated resources use
//      [AssetType( Name=..., Extension=..., Category=... )] (the modern corpus pattern).
//    - TypeLibrary wallet wiring copies the compile-verified create_idle_income shape:
//      Game.TypeLibrary.GetType(comp.GetType()) -> Methods.FirstOrDefault(...) ->
//      Invoke / InvokeWithReturn<bool> (both verified on MethodDescription);
//      PropertyDescription.GetValue(object) verified live.
//
//  Register(...) lines + the _sceneMutatingCommands additions live in MyEditorMenu.cs
//  (orchestrator integration) to keep the files decoupled -- see the handoff summary.
// =============================================================================

// -----------------------------------------------------------------------------
// create_currency_account -- the audited sibling of create_economy_wallet.
// Wallet = simple money (AddMoney/TrySpend). Account = money + a fixed-size
// transaction ring buffer (timestamp, reason, amount, balance-after) with
// GetRecentTransactions() for ledger UIs / audit trails, plus TryTransfer
// between accounts. Folds the corpus asks create_economy_ledger / create_currency.
// -----------------------------------------------------------------------------
public class CreateCurrencyAccountHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "CurrencyAccount", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			long start = p.TryGetProperty( "startingBalance", out var sv ) && sv.TryGetInt64( out var sl ) ? sl : 0L;
			int history = p.TryGetProperty( "historySize", out var hv ) && hv.TryGetInt32( out var hi ) ? hi : 32;
			if ( history < 1 ) history = 1;
			if ( history > 4096 ) history = 4096;

			var code = BuildCode( className, start, history );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = EconomySaveHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				startingBalance = start,
				historySize = history,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place it on a per-player or bank GameObject: add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Move money host-side: GetComponent<{className}>()?.Deposit( 100, \"quest reward\" ); .Withdraw( 50, \"shop\" ); .TryTransfer( other, 25, \"trade\" );",
					$"Read the ledger (host-side, newest first): foreach ( var t in GetComponent<{className}>().GetRecentTransactions() ) Log.Info( $\"{{t.Time}} {{t.Amount}} {{t.Reason}} -> {{t.BalanceAfter}}\" );",
					$"Bind a HUD: GetComponent<{className}>().OnBalanceChanged = bal => {{ /* update label */ }}; Balance is [Sync(FromHost)] so clients can read it directly.",
					$"History keeps the last {history} transactions (HistorySize, fixed once the first transaction is recorded); older entries are overwritten silently. The ledger itself is host-side only -- it does not replicate."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_currency_account failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, long start, int history )
	{
		var ci = System.Globalization.CultureInfo.InvariantCulture;
		string st = start.ToString( ci );
		string hs = history.ToString( ci );

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- a host-authoritative currency ACCOUNT: an audited ledger.
///
/// Use create_economy_wallet's Wallet when you just need money; use this when you need
/// money PLUS an audit trail. Balance is [Sync(SyncFlags.FromHost)] so only the host
/// writes it (clients can't author their own balance); every Deposit / Withdraw /
/// TryTransfer records a Transaction (Time.Now, reason, signed amount, balance-after)
/// into a fixed-size ring buffer, newest overwriting oldest past HistorySize.
///
/// The ledger is HOST-SIDE ONLY -- it does not replicate. Balance replicates; feed a
/// client-side ledger UI over an RPC if you need remote history. Single-player safe
/// (IsProxy is false with no networking active).
///
/// Usage (host-side):
///   GetComponent&lt;{className}&gt;()?.Deposit( 100, ""quest reward"" );
///   if ( GetComponent&lt;{className}&gt;().Withdraw( 50, ""shop"" ) ) {{ /* grant the item */ }}
///   GetComponent&lt;{className}&gt;().TryTransfer( otherAccount, 25, ""trade"" );
///   foreach ( var t in GetComponent&lt;{className}&gt;().GetRecentTransactions() ) {{ /* newest first */ }}
/// </summary>
public sealed class {className} : Component
{{
	/// Balance the account opens with (host seeds it in OnStart).
	[Property] public long StartingBalance {{ get; set; }} = {st}L;

	/// Ring-buffer capacity. Fixed once the first transaction is recorded.
	[Property] public int HistorySize {{ get; set; }} = {hs};

	// Host-authoritative balance -- replicates to clients, only the host writes.
	[Sync( SyncFlags.FromHost )] public long Balance {{ get; set; }}

	/// One ledger line. Amount is signed: positive = deposit, negative = withdrawal.
	public struct Transaction
	{{
		public float Time;          // Time.Now when recorded
		public long Amount;         // signed delta
		public string Reason;       // free-form audit string
		public long BalanceAfter;   // balance after applying the delta
	}}

	/// Fired (on the writing machine) whenever the balance changes -- bind a HUD here.
	public Action<long> OnBalanceChanged {{ get; set; }}

	// Host-side ring buffer. _head = next write slot, _count = filled slots.
	private Transaction[] _history;
	private int _head;
	private int _count;

	protected override void OnStart()
	{{
		if ( IsProxy ) return;            // only the authority seeds the balance
		Balance = StartingBalance;
		if ( StartingBalance != 0 ) Record( StartingBalance, ""opening balance"" );
		OnBalanceChanged?.Invoke( Balance );
	}}

	public bool CanAfford( long amount ) => Balance >= amount;

	/// <summary>Deposit (host-authoritative). Non-positive amounts are ignored.</summary>
	public void Deposit( long amount, string reason = ""deposit"" )
	{{
		if ( IsProxy || amount <= 0 ) return;
		Balance += amount;
		Record( amount, reason );
		OnBalanceChanged?.Invoke( Balance );
	}}

	/// <summary>Withdraw if affordable; returns false and changes nothing if not (host-authoritative).</summary>
	public bool Withdraw( long amount, string reason = ""withdraw"" )
	{{
		if ( IsProxy || amount <= 0 ) return false;
		if ( Balance < amount ) return false;
		Balance -= amount;
		Record( -amount, reason );
		OnBalanceChanged?.Invoke( Balance );
		return true;
	}}

	/// <summary>
	/// Atomically move money into another account (host-authoritative). Both legs are
	/// recorded in their respective ledgers. Returns false (nothing moves) when the
	/// target is missing/self, the amount is non-positive, or funds are short.
	/// </summary>
	public bool TryTransfer( {className} to, long amount, string reason = ""transfer"" )
	{{
		if ( IsProxy || to == null || to == this || amount <= 0 ) return false;
		if ( Balance < amount ) return false;
		Balance -= amount;
		Record( -amount, reason );
		OnBalanceChanged?.Invoke( Balance );
		to.ReceiveTransfer( amount, reason );
		return true;
	}}

	// The receiving leg of TryTransfer -- runs on the host alongside the sending leg.
	private void ReceiveTransfer( long amount, string reason )
	{{
		Balance += amount;
		Record( amount, reason );
		OnBalanceChanged?.Invoke( Balance );
	}}

	/// <summary>
	/// The most recent transactions, NEWEST FIRST. max = 0 returns everything retained
	/// (up to HistorySize). Host-side only -- proxies always get an empty list.
	/// </summary>
	public List<Transaction> GetRecentTransactions( int max = 0 )
	{{
		var list = new List<Transaction>();
		if ( _history == null || _count == 0 ) return list;
		int take = _count;
		if ( max > 0 && max < take ) take = max;
		for ( int i = 0; i < take; i++ )
		{{
			int idx = ( _head - 1 - i + _history.Length * 2 ) % _history.Length;
			list.Add( _history[idx] );
		}}
		return list;
	}}

	private void Record( long amount, string reason )
	{{
		if ( _history == null )
			_history = new Transaction[HistorySize < 1 ? 1 : HistorySize];

		_history[_head] = new Transaction
		{{
			Time = Time.Now,
			Amount = amount,
			Reason = reason ?? """",
			BalanceAfter = Balance
		}};
		_head = ( _head + 1 ) % _history.Length;
		if ( _count < _history.Length ) _count++;
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_idle_economy -- geometric bulk-buy purchasing. Generators follow the
// classic BaseCost * Growth^Owned curve; Buy 1 / Buy N / Buy Max use the
// closed-form geometric series (no loops). Income ticks grant into a sibling
// wallet's AddMoney via TypeLibrary reflection (the compile-verified
// create_idle_income pattern); purchases spend via the sibling's TrySpend and
// Buy Max reads its Money property the same way.
// -----------------------------------------------------------------------------
public class CreateIdleEconomyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			var ci = System.Globalization.CultureInfo.InvariantCulture;

			if ( !ScaffoldHelpers.PrepareCodeFile( p, "IdleEconomy", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			float tick = p.TryGetProperty( "tickSeconds", out var tv ) && tv.TryGetSingle( out var tf ) ? tf : 1f;
			if ( tick < 0.1f ) tick = 0.1f;

			var gens = ParseGenerators( p );

			var code = BuildCode( className, gens, tick, ci );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = EconomySaveHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				generators = gens.Select( g => g.Name ).ToArray(),
				tickSeconds = tick,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place it NEXT TO a wallet component (create_economy_wallet / create_currency_account) on the same GameObject: add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					"It auto-wires the sibling wallet by reflection: income invokes AddMoney(long|int), purchases invoke TrySpend(long|int), Buy Max reads the Money property. No wallet sibling = purchases refused with a Log.Warning (never silent).",
					$"Buy from game code: GetComponent<{className}>().TryBuy( 0, 1 ); .TryBuy( 0, 10 ); int n = GetComponent<{className}>().BuyMax( 0 );",
					$"Show prices: double cost = GetComponent<{className}>().CostOf( 0, 10 ); int max = GetComponent<{className}>().MaxAffordable( 0 ); -- both closed-form geometric series, no loops.",
					$"React to events: {className}.OnPurchased += ( index, count, cost ) => {{ }}; {className}.OnIncomeTick += ( amount, total ) => {{ }};",
					"Tune GeneratorNames / BaseCosts / Growths / IncomesPerSecond (parallel lists) in the inspector or with set_property. Owned counts are host-side state (not replicated); pair with create_offline_progress for away-time earnings."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_idle_economy failed: {ex.Message}" } );
		}
	}

	internal struct GeneratorDef { public string Name; public float BaseCost; public float Growth; public float IncomePerSecond; }

	static List<GeneratorDef> ParseGenerators( JsonElement p )
	{
		var result = new List<GeneratorDef>();
		if ( p.TryGetProperty( "generators", out var gv ) && gv.ValueKind == JsonValueKind.Array )
		{
			foreach ( var item in gv.EnumerateArray() )
			{
				var g = new GeneratorDef
				{
					Name = item.TryGetProperty( "name", out var nv ) && !string.IsNullOrWhiteSpace( nv.GetString() ) ? nv.GetString() : "Generator",
					BaseCost = item.TryGetProperty( "baseCost", out var bv ) && bv.TryGetSingle( out var bf ) ? bf : 15f,
					Growth = item.TryGetProperty( "growth", out var grv ) && grv.TryGetSingle( out var grf ) ? grf : 1.15f,
					IncomePerSecond = item.TryGetProperty( "incomePerSecond", out var iv ) && iv.TryGetSingle( out var inf ) ? inf : 0.5f
				};
				// Escape-strip: the name is baked into a generated string literal.
				g.Name = ( g.Name ?? "Generator" ).Replace( "\\", "" ).Replace( "\"", "" );
				if ( g.BaseCost <= 0f ) g.BaseCost = 1f;
				if ( g.Growth < 1f ) g.Growth = 1f;
				if ( g.IncomePerSecond < 0f ) g.IncomePerSecond = 0f;
				result.Add( g );
			}
		}
		if ( result.Count == 0 )
		{
			result.Add( new GeneratorDef { Name = "Cursor", BaseCost = 15f, Growth = 1.15f, IncomePerSecond = 0.5f } );
			result.Add( new GeneratorDef { Name = "Farm", BaseCost = 200f, Growth = 1.15f, IncomePerSecond = 4f } );
			result.Add( new GeneratorDef { Name = "Factory", BaseCost = 3000f, Growth = 1.12f, IncomePerSecond = 30f } );
		}
		return result;
	}

	static string BuildCode( string className, List<GeneratorDef> gens, float tick, System.Globalization.CultureInfo ci )
	{
		string nameLits = string.Join( ", ", gens.Select( g => $"\"{g.Name}\"" ) );
		string costLits = string.Join( ", ", gens.Select( g => g.BaseCost.ToString( ci ) + "f" ) );
		string growthLits = string.Join( ", ", gens.Select( g => g.Growth.ToString( ci ) + "f" ) );
		string incomeLits = string.Join( ", ", gens.Select( g => g.IncomePerSecond.ToString( ci ) + "f" ) );
		string tk = tick.ToString( ci ) + "f";

		return $@"using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// {className} -- a geometric idle economy: generators, bulk buying, passive income.
///
/// COST CURVE: buying copy k of generator i costs BaseCosts[i] * Growths[i]^k -- the
/// classic incremental-game curve. CostOf / MaxAffordable / TryBuy all use the CLOSED-FORM
/// geometric series (no per-copy loops), so Buy 1000 is the same math as Buy 1:
///   cost(n)  = c0 * (g^n - 1) / (g - 1)        where c0 = BaseCost * g^Owned
///   buyMax   = floor( log_g( funds*(g-1)/c0 + 1 ) )
///
/// WALLET WIRING (TypeLibrary reflection -- no compile-time wallet dependency): income
/// invokes AddMoney(long|int) on the first sibling component that has one; purchases
/// invoke TrySpend(long|int); Buy Max reads the sibling's Money property. Works out of
/// the box next to a create_economy_wallet or create_currency_account scaffold. No wallet
/// sibling = purchases are REFUSED with a Log.Warning (never silent).
///
/// HOST-AUTHORITATIVE: all mutation is IsProxy-guarded; owned counts are host-side state
/// (not replicated -- replicate via your own [Sync]/RPC if clients need them). TotalEarned
/// is [Sync(FromHost)]. Single-player safe.
///
/// Usage:
///   GetComponent&lt;{className}&gt;().TryBuy( 0, 1 );          // Buy 1
///   GetComponent&lt;{className}&gt;().TryBuy( 0, 10 );         // Buy N
///   int bought = GetComponent&lt;{className}&gt;().BuyMax( 0 ); // Buy Max
///   {className}.OnPurchased += ( i, count, cost ) => {{ /* refresh shop UI */ }};
///   {className}.OnIncomeTick += ( amount, total ) => {{ /* +N popup */ }};
/// </summary>
public sealed class {className} : Component
{{
	/// Generator display names -- parallel to BaseCosts / Growths / IncomesPerSecond.
	[Property] public List<string> GeneratorNames {{ get; set; }} = new List<string> {{ {nameLits} }};

	/// Cost of the FIRST copy of each generator (curve: BaseCost * Growth^Owned).
	[Property] public List<float> BaseCosts {{ get; set; }} = new List<float> {{ {costLits} }};

	/// Per-copy cost multiplier (1.15 = the classic curve). Values below 1 are treated as 1 (flat cost).
	[Property] public List<float> Growths {{ get; set; }} = new List<float> {{ {growthLits} }};

	/// Income each owned copy produces per second.
	[Property] public List<float> IncomesPerSecond {{ get; set; }} = new List<float> {{ {incomeLits} }};

	/// Seconds between income grants.
	[Property] public float TickSeconds {{ get; set; }} = {tk};

	/// Total income ever granted (host-authoritative, replicates to clients).
	[Sync( SyncFlags.FromHost )] public float TotalEarned {{ get; set; }}

	/// Fires host-side after a purchase: (generatorIndex, countBought, totalCost).
	public static Action<int, int, double> OnPurchased {{ get; set; }}

	/// Fires host-side after each income grant: (amount, newTotalEarned).
	public static Action<float, float> OnIncomeTick {{ get; set; }}

	// Host-side owned counts, parallel to the property lists.
	private int[] _owned;
	private TimeUntil _nextTick;

	protected override void OnStart()
	{{
		_nextTick = TickSeconds;
	}}

	protected override void OnFixedUpdate()
	{{
		if ( IsProxy ) return;
		if ( !_nextTick ) return;
		_nextTick = TickSeconds;

		EnsureOwned();
		float amount = 0f;
		for ( int i = 0; i < _owned.Length; i++ )
			amount += _owned[i] * IncomeOf( i ) * TickSeconds;
		if ( amount <= 0f ) return;

		TotalEarned += amount;
		GrantIncome( amount );
		OnIncomeTick?.Invoke( amount, TotalEarned );
	}}

	/// <summary>Copies of a generator owned (host-side state; 0 on proxies).</summary>
	public int GetOwned( int index )
	{{
		EnsureOwned();
		return index >= 0 && index < _owned.Length ? _owned[index] : 0;
	}}

	/// <summary>
	/// Closed-form cost of the next `count` copies of generator `index` from the current
	/// owned count. 0 for an invalid index or non-positive count.
	/// </summary>
	public double CostOf( int index, int count )
	{{
		if ( count <= 0 || !ValidIndex( index ) ) return 0.0;
		double g = GrowthOf( index );
		double c0 = BaseCosts[index] * Math.Pow( g, GetOwned( index ) );
		if ( Math.Abs( g - 1.0 ) < 0.0001 ) return c0 * count;
		return c0 * ( Math.Pow( g, count ) - 1.0 ) / ( g - 1.0 );
	}}

	/// <summary>
	/// Closed-form Buy-Max count against the sibling wallet's current Money.
	/// 0 when nothing is affordable or no wallet sibling exposes a Money property.
	/// </summary>
	public int MaxAffordable( int index )
	{{
		if ( !ValidIndex( index ) ) return 0;
		double funds = ReadWalletBalance();
		if ( funds <= 0.0 ) return 0;
		double g = GrowthOf( index );
		double c0 = BaseCosts[index] * Math.Pow( g, GetOwned( index ) );
		if ( c0 <= 0.0 ) return 0;
		if ( Math.Abs( g - 1.0 ) < 0.0001 ) return (int) Math.Floor( funds / c0 );
		return (int) Math.Floor( Math.Log( funds * ( g - 1.0 ) / c0 + 1.0 ) / Math.Log( g ) );
	}}

	/// <summary>
	/// Buy `count` copies if the sibling wallet's TrySpend accepts the closed-form cost
	/// (rounded up to whole currency). Host-only; false when unaffordable or no wallet.
	/// </summary>
	public bool TryBuy( int index, int count )
	{{
		if ( IsProxy || count <= 0 || !ValidIndex( index ) ) return false;
		EnsureOwned();
		double cost = CostOf( index, count );
		if ( !SpendFromWallet( cost ) ) return false;
		_owned[index] += count;
		OnPurchased?.Invoke( index, count, cost );
		return true;
	}}

	/// <summary>
	/// Buy as many copies as the wallet can afford. Returns the count bought (0 = none).
	/// Steps down once past a whole-currency rounding edge rather than failing.
	/// </summary>
	public int BuyMax( int index )
	{{
		int n = MaxAffordable( index );
		while ( n > 0 )
		{{
			if ( TryBuy( index, n ) ) return n;
			n--;   // ceil-rounding edge: the closed form said n, the wallet said no -- step down
		}}
		return 0;
	}}

	private bool ValidIndex( int index )
		=> BaseCosts != null && index >= 0 && index < BaseCosts.Count;

	private double GrowthOf( int index )
	{{
		float g = Growths != null && index < Growths.Count ? Growths[index] : 1.15f;
		return g < 1f ? 1.0 : g;
	}}

	private float IncomeOf( int index )
		=> IncomesPerSecond != null && index < IncomesPerSecond.Count && index >= 0 ? IncomesPerSecond[index] : 0f;

	private void EnsureOwned()
	{{
		int size = BaseCosts?.Count ?? 0;
		int names = GeneratorNames?.Count ?? 0;
		if ( names > size ) size = names;
		if ( size < 1 ) size = 1;

		if ( _owned == null )
		{{
			_owned = new int[size];
		}}
		else if ( _owned.Length < size )
		{{
			var grown = new int[size];
			for ( int i = 0; i < _owned.Length; i++ ) grown[i] = _owned[i];
			_owned = grown;
		}}
	}}

	// ---- sibling-wallet wiring (TypeLibrary reflection; no hard wallet dependency) ----

	// Deliver income: AddMoney(long|int) on the first sibling that has one.
	private void GrantIncome( float amount )
	{{
		foreach ( var comp in Components.GetAll() )
		{{
			if ( comp == this || comp is null ) continue;
			var type = Game.TypeLibrary?.GetType( comp.GetType() );
			var method = type?.Methods?.FirstOrDefault( m => m.Name == ""AddMoney"" );
			if ( method == null ) continue;
			try {{ method.Invoke( comp, new object[] {{ (long) amount }} ); return; }}
			catch {{ }}
			try {{ method.Invoke( comp, new object[] {{ (int) amount }} ); return; }}
			catch {{ /* wrong signature -- keep looking */ }}
		}}
		// No wallet sibling -- TotalEarned still accumulates; read it directly.
	}}

	// Spend: TrySpend(long|int) on the first sibling that has one. Never silent on failure.
	private bool SpendFromWallet( double cost )
	{{
		if ( cost <= 0.0 ) return false;
		long rounded = (long) Math.Ceiling( cost );
		foreach ( var comp in Components.GetAll() )
		{{
			if ( comp == this || comp is null ) continue;
			var type = Game.TypeLibrary?.GetType( comp.GetType() );
			var method = type?.Methods?.FirstOrDefault( m => m.Name == ""TrySpend"" );
			if ( method == null ) continue;
			try {{ return method.InvokeWithReturn<bool>( comp, new object[] {{ rounded }} ); }}
			catch {{ }}
			try {{ return method.InvokeWithReturn<bool>( comp, new object[] {{ (int) rounded }} ); }}
			catch {{ /* wrong signature -- keep looking */ }}
		}}
		Log.Warning( $""[{className}] No sibling wallet with TrySpend found -- add a create_economy_wallet / create_currency_account component next to it. Purchase refused."" );
		return false;
	}}

	// Read funds for Buy Max: the first sibling exposing a numeric Money property.
	private double ReadWalletBalance()
	{{
		foreach ( var comp in Components.GetAll() )
		{{
			if ( comp == this || comp is null ) continue;
			var type = Game.TypeLibrary?.GetType( comp.GetType() );
			var prop = type?.Properties?.FirstOrDefault( pp => pp.Name == ""Money"" || pp.Name == ""Balance"" );
			if ( prop == null ) continue;
			try
			{{
				object v = prop.GetValue( comp );
				if ( v is long l ) return l;
				if ( v is int i ) return i;
				if ( v is float f ) return f;
				if ( v is double d ) return d;
			}}
			catch {{ /* unreadable -- keep looking */ }}
		}}
		return 0.0;
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_signed_save -- tamper-evident save file. The payload POCO is serialized
// to JSON (Sandbox.Json), FNV-1a-64 hashed together with a salt + version, and
// written inside a signed envelope via FileSystem.Data. Load verifies the
// signature; a mismatch = forced reset (delete + defaults) + OnTampered event.
// Clamp-on-load Sanitize() hook + versioning copy create_save_system's shape.
// -----------------------------------------------------------------------------
public class CreateSignedSaveHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "SignedSave", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var ci = System.Globalization.CultureInfo.InvariantCulture;
			string fileName = p.TryGetProperty( "fileName", out var fn ) && !string.IsNullOrWhiteSpace( fn.GetString() ) ? fn.GetString() : "save_signed.json";
			int version = p.TryGetProperty( "version", out var vv ) && vv.TryGetInt32( out var vi ) ? vi : 1;
			float autosave = p.TryGetProperty( "autosaveSeconds", out var av ) && av.TryGetSingle( out var af ) ? af : 10f;
			string salt = p.TryGetProperty( "salt", out var sv ) && !string.IsNullOrWhiteSpace( sv.GetString() )
				? sv.GetString()
				: Guid.NewGuid().ToString( "N" );   // unique per generated file by default

			// These are baked into generated string literals -- strip escape characters.
			fileName = fileName.Replace( "\\", "" ).Replace( "\"", "" );
			salt = salt.Replace( "\\", "" ).Replace( "\"", "" );

			var code = BuildCode( className, fileName, version.ToString( ci ), autosave.ToString( ci ) + "f", salt );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = EconomySaveHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				fileName,
				version,
				autosaveSeconds = autosave,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place it on your save-manager GameObject: add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Add your game fields to the SaveData inner class in {className}.cs, extend Sanitize() to clamp them, and bump Version when the shape changes.",
					$"Use it: GetComponent<{className}>().Data.Money += 100; GetComponent<{className}>().MarkDirty(); -- the dirty-flag autosave (or OnDestroy) writes and re-signs.",
					$"React: {className}.OnLoaded += d => {{ }}; {className}.OnSaved += d => {{ }}; {className}.OnTampered += reason => {{ /* tell the player their save was reset */ }};",
					"TAMPER = FORCED RESET: an edited payload fails the FNV-1a signature check on load, the file is DELETED and defaults are used (OnTampered fires with the reason). This is tamper-EVIDENT, not cryptographically secure -- the salt ships in the game code, so a determined user can re-sign; it stops casual notepad edits, not reverse engineers."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_signed_save failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, string fileName, string version, string autosave, string salt )
	{
		return $@"using Sandbox;
using System;

/// <summary>
/// {className} -- a tamper-evident, versioned save system.
///
/// The SaveData payload is serialized to JSON, hashed with FNV-1a-64 over
/// payload + version + salt, and written inside a signed envelope to
/// FileSystem.Data. Load re-computes the signature: a mismatch (hand-edited or
/// corrupt file) triggers a FORCED RESET -- the file is deleted, defaults are
/// used, and the static OnTampered event fires. A version mismatch starts fresh
/// (add migrations in Load if you need them). Loaded values pass through the
/// Sanitize() clamp hook so even a re-signed save can't smuggle absurd values.
///
/// NOT cryptography: the salt ships inside the game assembly, so this is
/// tamper-EVIDENT (stops notepad edits), not tamper-PROOF.
///
/// Host/owner-only (IsProxy-guarded). Dirty-flag autosave every AutosaveSeconds
/// plus a final save in OnDestroy.
///
/// Usage:
///   var save = GetComponent&lt;{className}&gt;();
///   save.Data.Money += 100; save.MarkDirty();
///   {className}.OnTampered += reason => Log.Warning( $""save reset: {{reason}}"" );
/// </summary>
public sealed class {className} : Component
{{
	/// FileSystem.Data path the signed envelope is written to.
	[Property] public string FileName {{ get; set; }} = ""{fileName}"";

	/// Autosave cadence in seconds. 0 disables the heartbeat (OnDestroy still saves).
	[Property] public float AutosaveSeconds {{ get; set; }} = {autosave};

	/// Save-shape version -- bump when SaveData changes so old files start fresh.
	public const int Version = {version};

	// Baked-in signing salt (unique to this generated file). Changing it invalidates existing saves.
	private const string Salt = ""{salt}"";

	/// The save payload. Add your own fields here; clamp them in Sanitize().
	public class SaveData
	{{
		public int Money {{ get; set; }}
		public int Day {{ get; set; }} = 1;
		// Add game fields here.
	}}

	/// The envelope actually written to disk: version + raw payload JSON + signature.
	public class SaveEnvelope
	{{
		public int Version {{ get; set; }}
		public string Payload {{ get; set; }}
		public ulong Signature {{ get; set; }}
	}}

	public SaveData Data {{ get; private set; }} = new SaveData();
	public bool IsDirty {{ get; private set; }}

	/// Fires after a successful Load() with the loaded (sanitized) data.
	public static Action<SaveData> OnLoaded {{ get; set; }}
	/// Fires after every Save().
	public static Action<SaveData> OnSaved {{ get; set; }}
	/// Fires when the signature check fails and the save is force-reset. Arg = reason.
	public static Action<string> OnTampered {{ get; set; }}

	private TimeUntil _nextAutosave;

	protected override void OnStart()
	{{
		if ( IsProxy ) return;   // only the owning machine loads
		Load();
		_nextAutosave = AutosaveSeconds;
	}}

	protected override void OnUpdate()
	{{
		if ( IsProxy || AutosaveSeconds <= 0f ) return;
		if ( _nextAutosave )
		{{
			_nextAutosave = AutosaveSeconds;
			if ( IsDirty ) Save();
		}}
	}}

	protected override void OnDestroy()
	{{
		if ( !IsProxy && IsDirty ) Save();
	}}

	/// Mark the data changed so the next autosave tick (or OnDestroy) writes + re-signs it.
	public void MarkDirty() => IsDirty = true;

	public void Load()
	{{
		var envelope = FileSystem.Data.ReadJsonOrDefault<SaveEnvelope>( FileName, null );
		if ( envelope == null )
		{{
			// Missing or unreadable envelope: start fresh (not treated as tampering).
			Data = new SaveData();
			IsDirty = true;
		}}
		else if ( envelope.Version != Version )
		{{
			// Old save shape: start fresh (add migrations here later).
			Data = new SaveData();
			IsDirty = true;
		}}
		else if ( envelope.Payload == null || ComputeSignature( envelope.Payload ) != envelope.Signature )
		{{
			ForceReset( ""signature mismatch -- save file was modified outside the game"" );
			return;   // ForceReset already fired OnLoaded
		}}
		else
		{{
			SaveData loaded = null;
			try {{ loaded = Json.Deserialize<SaveData>( envelope.Payload ); }}
			catch {{ }}
			if ( loaded == null )
			{{
				ForceReset( ""payload failed to parse despite a valid signature"" );
				return;
			}}
			Data = Sanitize( loaded );
			IsDirty = false;
		}}
		OnLoaded?.Invoke( Data );
	}}

	public void Save()
	{{
		var payload = Json.Serialize( Data );
		var envelope = new SaveEnvelope
		{{
			Version = Version,
			Payload = payload,
			Signature = ComputeSignature( payload )
		}};
		FileSystem.Data.WriteJson( FileName, envelope );
		IsDirty = false;
		OnSaved?.Invoke( Data );
	}}

	/// <summary>Delete the save file and reset to defaults. Fires OnTampered then OnLoaded.</summary>
	public void ForceReset( string reason )
	{{
		try
		{{
			if ( FileSystem.Data.FileExists( FileName ) )
				FileSystem.Data.DeleteFile( FileName );
		}}
		catch {{ }}
		Data = new SaveData();
		IsDirty = true;
		OnTampered?.Invoke( reason ?? ""forced reset"" );
		OnLoaded?.Invoke( Data );
	}}

	/// Clamp-on-load: keep loaded values inside sane ranges so even a re-signed
	/// save can't smuggle absurd values. Extend per field you add.
	private SaveData Sanitize( SaveData d )
	{{
		if ( d.Money < 0 ) d.Money = 0;
		if ( d.Day < 1 ) d.Day = 1;
		return d;
	}}

	// FNV-1a 64-bit over payload + version + salt. Deterministic, allocation-light.
	private static ulong ComputeSignature( string payload )
	{{
		const ulong offsetBasis = 14695981039346656037UL;
		const ulong prime = 1099511628211UL;

		ulong hash = offsetBasis;
		string material = payload + ""|"" + Version + ""|"" + Salt;
		for ( int i = 0; i < material.Length; i++ )
		{{
			hash ^= material[i];
			hash *= prime;
		}}
		return hash;
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_meta_progression -- the between-runs roguelite meta layer: persistent
// meta-currency + unlock-flag dictionary saved to FileSystem.Data JSON.
// Grant/TrySpend/Unlock/IsUnlocked + a BankRun(int) run-end seam + a static
// OnUnlocked event. Persistence copies create_save_system's dirty-flag shape.
// -----------------------------------------------------------------------------
public class CreateMetaProgressionHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "MetaProgression", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			var ci = System.Globalization.CultureInfo.InvariantCulture;
			string fileName = p.TryGetProperty( "fileName", out var fn ) && !string.IsNullOrWhiteSpace( fn.GetString() ) ? fn.GetString() : "meta.json";
			int version = p.TryGetProperty( "version", out var vv ) && vv.TryGetInt32( out var vi ) ? vi : 1;
			float autosave = p.TryGetProperty( "autosaveSeconds", out var av ) && av.TryGetSingle( out var af ) ? af : 10f;

			fileName = fileName.Replace( "\\", "" ).Replace( "\"", "" );

			var code = BuildCode( className, fileName, version.ToString( ci ), autosave.ToString( ci ) + "f" );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = EconomySaveHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				fileName,
				version,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place it on a persistent manager GameObject (one that exists in your hub/menu scene): add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"At run end, bank the earnings: GetComponent<{className}>().BankRun( runCurrencyEarned ); -- it grants and saves immediately.",
					$"Gate content: if ( GetComponent<{className}>().TrySpend( 50 ) ) GetComponent<{className}>().Unlock( \"double_jump\" ); then check IsUnlocked( \"double_jump\" ) when building the player.",
					$"React to unlocks anywhere: {className}.OnUnlocked += key => {{ /* flash the new item in the meta shop */ }};",
					"MetaCurrency and the unlock flags persist to FileSystem.Data across sessions (dirty-flag autosave + OnDestroy). IsProxy-guarded: in multiplayer each machine banks only its own meta file."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_meta_progression failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, string fileName, string version, string autosave )
	{
		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// {className} -- the between-runs roguelite meta layer.
///
/// Persists a meta-currency plus an unlock-flag dictionary to FileSystem.Data JSON
/// (dirty-flag autosave + OnDestroy, create_save_system's shape). During a run you earn
/// normal run-currency; at run end call BankRun(earned) to convert it into persistent
/// meta-currency. Spend meta-currency on permanent Unlock() flags and gate content with
/// IsUnlocked(). The static OnUnlocked event fires on every new unlock.
///
/// Owner-only (IsProxy-guarded): each machine banks only its own meta file.
///
/// Usage:
///   GetComponent&lt;{className}&gt;().BankRun( 120 );                    // run over
///   if ( GetComponent&lt;{className}&gt;().TrySpend( 50 ) )
///       GetComponent&lt;{className}&gt;().Unlock( ""double_jump"" );
///   if ( GetComponent&lt;{className}&gt;().IsUnlocked( ""double_jump"" ) ) {{ /* enable it */ }}
///   {className}.OnUnlocked += key => {{ /* celebrate */ }};
/// </summary>
public sealed class {className} : Component
{{
	/// FileSystem.Data path the meta state is written to.
	[Property] public string FileName {{ get; set; }} = ""{fileName}"";

	/// Autosave cadence in seconds. 0 disables the heartbeat (OnDestroy still saves).
	[Property] public float AutosaveSeconds {{ get; set; }} = {autosave};

	/// The persisted payload. Bump Version when the shape changes so old files start fresh.
	public class MetaData
	{{
		public int Version {{ get; set; }} = {version};
		public long MetaCurrency {{ get; set; }}
		public int RunsBanked {{ get; set; }}
		public Dictionary<string, bool> Unlocks {{ get; set; }} = new Dictionary<string, bool>();
	}}

	public MetaData Data {{ get; private set; }} = new MetaData();
	public bool IsDirty {{ get; private set; }}

	/// Fires (on the owning machine) when a key is unlocked for the FIRST time.
	public static Action<string> OnUnlocked {{ get; set; }}

	/// Fires whenever MetaCurrency changes -- bind the meta-shop balance label here.
	public Action<long> OnCurrencyChanged {{ get; set; }}

	private TimeUntil _nextAutosave;

	protected override void OnStart()
	{{
		if ( IsProxy ) return;   // only the owning machine loads
		Load();
		_nextAutosave = AutosaveSeconds;
	}}

	protected override void OnUpdate()
	{{
		if ( IsProxy || AutosaveSeconds <= 0f ) return;
		if ( _nextAutosave )
		{{
			_nextAutosave = AutosaveSeconds;
			if ( IsDirty ) Save();
		}}
	}}

	protected override void OnDestroy()
	{{
		if ( !IsProxy && IsDirty ) Save();
	}}

	/// <summary>Add meta-currency. Non-positive amounts are ignored.</summary>
	public void Grant( long amount )
	{{
		if ( IsProxy || amount <= 0 ) return;
		Data.MetaCurrency += amount;
		IsDirty = true;
		OnCurrencyChanged?.Invoke( Data.MetaCurrency );
	}}

	/// <summary>Spend meta-currency if affordable; false and no change otherwise.</summary>
	public bool TrySpend( long amount )
	{{
		if ( IsProxy || amount <= 0 ) return false;
		if ( Data.MetaCurrency < amount ) return false;
		Data.MetaCurrency -= amount;
		IsDirty = true;
		OnCurrencyChanged?.Invoke( Data.MetaCurrency );
		return true;
	}}

	/// <summary>Set a permanent unlock flag. Idempotent; OnUnlocked fires only the first time. Saves immediately.</summary>
	public void Unlock( string key )
	{{
		if ( IsProxy || string.IsNullOrEmpty( key ) ) return;
		if ( Data.Unlocks.TryGetValue( key, out var already ) && already ) return;
		Data.Unlocks[key] = true;
		Save();   // unlocks are precious -- write through immediately
		OnUnlocked?.Invoke( key );
	}}

	/// <summary>True when a key has been permanently unlocked.</summary>
	public bool IsUnlocked( string key )
		=> !string.IsNullOrEmpty( key ) && Data.Unlocks.TryGetValue( key, out var v ) && v;

	/// <summary>
	/// Run-end seam: convert this run's earnings into persistent meta-currency and
	/// save immediately. Call it from your round machine's end-of-run transition.
	/// </summary>
	public void BankRun( int earned )
	{{
		if ( IsProxy ) return;
		if ( earned > 0 ) Data.MetaCurrency += earned;
		Data.RunsBanked += 1;
		Save();
		OnCurrencyChanged?.Invoke( Data.MetaCurrency );
	}}

	/// Mark the data changed so the next autosave tick (or OnDestroy) writes it.
	public void MarkDirty() => IsDirty = true;

	public void Load()
	{{
		var loaded = FileSystem.Data.ReadJsonOrDefault<MetaData>( FileName, null );
		if ( loaded == null || loaded.Version != {version} )
		{{
			Data = new MetaData();
			IsDirty = true;
		}}
		else
		{{
			if ( loaded.MetaCurrency < 0 ) loaded.MetaCurrency = 0;
			if ( loaded.RunsBanked < 0 ) loaded.RunsBanked = 0;
			if ( loaded.Unlocks == null ) loaded.Unlocks = new Dictionary<string, bool>();
			Data = loaded;
			IsDirty = false;
		}}
		OnCurrencyChanged?.Invoke( Data.MetaCurrency );
	}}

	public void Save()
	{{
		FileSystem.Data.WriteJson( FileName, Data );
		IsDirty = false;
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// add_steam_stat_currency -- currency persisted over Sandbox.Services.Stats.
// Verified live on this SDK: static Stats.Increment(string,double),
// Stats.SetValue(string,double,string,object), Stats.Flush(), and
// Stats.GetLocalPlayerStats(string packageIdent) returning the NESTED
// Stats.PlayerStats (Get(name) -> Stats.PlayerStat with .Value). There is
// NO Stats.LocalPlayer property on this SDK.
// -----------------------------------------------------------------------------
public class AddSteamStatCurrencyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "SteamStatCurrency", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			string statName = p.TryGetProperty( "statName", out var sv ) && !string.IsNullOrWhiteSpace( sv.GetString() ) ? sv.GetString() : "currency";
			string packageIdent = p.TryGetProperty( "packageIdent", out var pv ) && !string.IsNullOrWhiteSpace( pv.GetString() ) ? pv.GetString() : "";
			bool flushEveryChange = p.TryGetProperty( "flushEveryChange", out var fv ) && fv.ValueKind == JsonValueKind.True;

			// Baked into generated string literals -- strip escape characters.
			statName = statName.Replace( "\\", "" ).Replace( "\"", "" );
			packageIdent = packageIdent.Replace( "\\", "" ).Replace( "\"", "" );

			var code = BuildCode( className, statName, packageIdent, flushEveryChange );
			ScaffoldHelpers.WriteCode( fullPath, code );

			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = EconomySaveHelpers.PlaceOnTarget( tid.GetString(), className, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				statName,
				packageIdent = string.IsNullOrEmpty( packageIdent ) ? "(Game.Ident -- the running package)" : packageIdent,
				flushEveryChange,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} into the game assembly.",
					placedOn != null
						? $"{className} was attached to the target GameObject."
						: $"Place it on the LOCAL player's GameObject (each player writes only their own Steam stat): add_component_to_new_object (component=\"{className}\") after the hotload, or re-run with targetId.",
					$"Use it: GetComponent<{className}>().Add( 25 ); if ( GetComponent<{className}>().TrySpend( 10 ) ) {{ }} -- Balance is the in-session truth; every change pushes Stats.SetValue.",
					$"React: {className}.OnBalanceLoaded += bal => {{ }}; and instance OnBalanceChanged for HUD labels. Wait for IsLoaded before showing the balance -- the read-back is async.",
					"CLOUD SEMANTICS: stats writes are buffered by the backend (Flush() pushes; the component flushes on destroy) and only apply to the LOCAL Steam user -- calling it for another player silently does nothing. Read-back is eventually consistent and can lag minutes; the in-session Balance property is authoritative while playing.",
					"Stats persist per Steam account per package ident -- dev sessions without a real published ident may read back nothing (you'll get balance 0 + a log line). This is Steam-cloud persistence, not a local save file; pair with create_signed_save if you need offline saves."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"add_steam_stat_currency failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, string statName, string packageIdent, bool flushEveryChange )
	{
		string flushLit = flushEveryChange ? "true" : "false";

		return $@"using Sandbox;
using Sandbox.Services;
using System;

/// <summary>
/// {className} -- a currency persisted over Sandbox.Services.Stats (Steam cloud).
///
/// The stat named StatName stores the ABSOLUTE balance (Stats.SetValue on every change);
/// on start the component reads it back asynchronously via
/// Stats.GetLocalPlayerStats(ident).Refresh() -> Get(StatName).Value and fires
/// OnBalanceLoaded. While playing, the in-session Balance property is the authoritative
/// value -- the cloud read-back is eventually consistent and can lag behind writes.
///
/// SCOPE: stats writes apply only to the LOCAL Steam user (writes for other players
/// silently no-op) and persist per package ident. Attach this to the local player's
/// GameObject; IsProxy guards keep remote copies inert. Dev sessions without a real
/// published ident may read back nothing (balance starts at 0).
///
/// Usage:
///   GetComponent&lt;{className}&gt;().Add( 25 );
///   if ( GetComponent&lt;{className}&gt;().TrySpend( 10 ) ) {{ /* grant the thing */ }}
///   {className}.OnBalanceLoaded += bal => {{ /* show the wallet */ }};
/// </summary>
public sealed class {className} : Component
{{
	/// The Sandbox.Services stat that stores the balance.
	[Property] public string StatName {{ get; set; }} = ""{statName}"";

	/// Package ident to read stats from. Empty = the running package (Game.Ident).
	[Property] public string PackageIdent {{ get; set; }} = ""{packageIdent}"";

	/// Push Stats.Flush() after every change (rate-limited by the backend) instead of
	/// relying on the buffered flush + the OnDestroy flush.
	[Property] public bool FlushEveryChange {{ get; set; }} = {flushLit};

	/// In-session balance -- authoritative while playing. Cloud value catches up on flush.
	public double Balance {{ get; private set; }}

	/// True once the async cloud read-back has completed (successfully or not).
	public bool IsLoaded {{ get; private set; }}

	/// Fires once after the cloud read-back completes, with the loaded balance.
	public static Action<double> OnBalanceLoaded {{ get; set; }}

	/// Fires on every balance change (including the initial load) -- bind a HUD here.
	public Action<double> OnBalanceChanged {{ get; set; }}

	protected override void OnStart()
	{{
		if ( IsProxy ) return;   // only the local player's machine touches their stats
		_ = LoadAsync();
	}}

	protected override void OnDestroy()
	{{
		if ( !IsProxy && IsLoaded ) Stats.Flush();
	}}

	/// <summary>Re-read the balance from the stats backend (async; also runs on start).</summary>
	public async System.Threading.Tasks.Task LoadAsync()
	{{
		double loaded = 0.0;
		try
		{{
			string ident = string.IsNullOrWhiteSpace( PackageIdent ) ? Game.Ident : PackageIdent;
			var stats = Stats.GetLocalPlayerStats( ident );
			await stats.Refresh();
			loaded = stats.Get( StatName ).Value;
		}}
		catch ( Exception ex )
		{{
			Log.Warning( $""[{className}] Stat read-back failed ({{ex.Message}}) -- starting at 0. Stats need a valid package ident + Steam session."" );
		}}
		Balance = loaded;
		IsLoaded = true;
		OnBalanceLoaded?.Invoke( Balance );
		OnBalanceChanged?.Invoke( Balance );
	}}

	public bool CanAfford( double amount ) => Balance >= amount;

	/// <summary>Add currency and push the new balance to the stats backend. Non-positive ignored.</summary>
	public void Add( double amount )
	{{
		if ( IsProxy || amount <= 0.0 ) return;
		Balance += amount;
		Push();
	}}

	/// <summary>Spend if affordable; returns false and changes nothing if not.</summary>
	public bool TrySpend( double amount )
	{{
		if ( IsProxy || amount <= 0.0 ) return false;
		if ( Balance < amount ) return false;
		Balance -= amount;
		Push();
		return true;
	}}

	/// <summary>Force-push buffered stat writes to the backend now (rate-limited upstream).</summary>
	public void Flush() => Stats.Flush();

	// Write the absolute balance to the stat and notify listeners.
	private void Push()
	{{
		Stats.SetValue( StatName, Balance, null, null );
		if ( FlushEveryChange ) Stats.Flush();
		OnBalanceChanged?.Invoke( Balance );
	}}
}}
";
	}
}

// -----------------------------------------------------------------------------
// create_loot_table_resource -- the data-asset sibling of create_weighted_loot_table.
// Generates ONE .cs containing: an entry POCO (name, weight, optional nested table
// reference), a GameResource loot-table asset type ([AssetType] -- the modern
// attribute; GameResourceAttribute is [Obsolete] on this SDK), and a resolver
// Component that rolls a table by cumulative weight with a resolve depth cap.
// Designers author .loot files in the asset browser; code rolls them.
// -----------------------------------------------------------------------------
public class CreateLootTableResourceHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			if ( !ScaffoldHelpers.PrepareCodeFile( p, "LootTableResource", out var fullPath, out var relPath, out var className, out var err ) )
				return Task.FromResult<object>( err );

			string extension = p.TryGetProperty( "extension", out var ev ) && !string.IsNullOrWhiteSpace( ev.GetString() ) ? ev.GetString() : "loot";
			string title = p.TryGetProperty( "title", out var tv ) && !string.IsNullOrWhiteSpace( tv.GetString() ) ? tv.GetString() : "Loot Table";
			int maxDepth = p.TryGetProperty( "maxDepth", out var mv ) && mv.TryGetInt32( out var mi ) ? mi : 4;
			if ( maxDepth < 0 ) maxDepth = 0;
			if ( maxDepth > 16 ) maxDepth = 16;

			// Extension: lowercase alphanumerics only.
			var extChars = new StringBuilder();
			foreach ( var c in extension.ToLowerInvariant() )
				if ( ( c >= 'a' && c <= 'z' ) || ( c >= '0' && c <= '9' ) ) extChars.Append( c );
			extension = extChars.Length > 0 ? extChars.ToString() : "loot";

			// Title is baked into an attribute string literal.
			title = title.Replace( "\\", "" ).Replace( "\"", "" );

			var resolverClass = className + "Resolver";
			var code = BuildCode( className, resolverClass, extension, title, maxDepth );
			ScaffoldHelpers.WriteCode( fullPath, code );

			// Placement attaches the RESOLVER component (the resource itself is an asset type, not a component).
			object placedOn = null; string note = null;
			if ( p.TryGetProperty( "targetId", out var tid ) && tid.ValueKind == JsonValueKind.String )
				placedOn = EconomySaveHelpers.PlaceOnTarget( tid.GetString(), resolverClass, out note );

			return Task.FromResult<object>( new
			{
				created = true,
				path = relPath,
				className,
				resolverClass,
				extension,
				maxDepth,
				placedOn,
				note,
				nextSteps = new[]
				{
					$"trigger_hotload to compile {className} + {resolverClass} into the game assembly -- the '.{extension}' asset type registers on compile.",
					$"Author tables as ASSETS: in the editor asset browser, New > {title} creates a .{extension} file; fill Entries (Name, Weight, optional NestedTable reference to another .{extension}) in the inspector.",
					placedOn != null
						? $"{resolverClass} was attached to the target GameObject -- assign its Table property to a .{extension} asset (set_property with the asset path)."
						: $"Attach the resolver: add_component_to_new_object (component=\"{resolverClass}\") after the hotload, then set its Table property to a .{extension} asset path.",
					$"Roll from game code (host-side): string drop = GetComponent<{resolverClass}>().Roll(); {resolverClass}.OnLoot += ( go, item ) => {{ }};",
					$"Nested tables: an entry with a NestedTable rolls INTO that table instead of dropping its Name -- capped at MaxDepth ({maxDepth}) with a self-reference guard, so cycles terminate.",
					"Use create_weighted_loot_table instead when you want a single inline component with no asset files; use create_gacha_drop_table for pity + duplicate mechanics. Pick an extension that is NOT a suffix of a built-in one (e.g. avoid 'cfg') or ResourceLibrary.GetAll will pick up engine files as phantom instances."
				}
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"create_loot_table_resource failed: {ex.Message}" } );
		}
	}

	static string BuildCode( string className, string resolverClass, string extension, string title, int maxDepth )
	{
		var ci = System.Globalization.CultureInfo.InvariantCulture;
		string md = maxDepth.ToString( ci );

		return $@"using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// One row of a {className} asset. Amount is picked by cumulative weight; when
/// NestedTable is set the roll continues INTO that table instead of dropping Name.
/// </summary>
public sealed class {className}Entry
{{
	/// What drops when this entry wins (ignored when NestedTable is set).
	[Property] public string Name {{ get; set; }} = """";

	/// Relative chance. Bigger = more likely. Entries with weight &lt;= 0 never win.
	[Property] public float Weight {{ get; set; }} = 1f;

	/// Optional: roll this table instead of dropping Name (depth-capped on resolve).
	[Property] public {className} NestedTable {{ get; set; }}
}}

/// <summary>
/// {className} -- a designer-authored loot table ASSET (.{extension} files).
///
/// Each .{extension} file holds weighted entries; entries may reference other
/// .{extension} assets as nested tables (rarity tiers, per-biome sub-tables).
/// Resolve() rolls by cumulative weight and follows nested references up to a
/// depth cap, so cyclic references terminate. Author the files in the editor
/// asset browser; roll them with {resolverClass} or call Resolve() directly.
/// </summary>
[AssetType( Name = ""{title}"", Extension = ""{extension}"", Category = ""Game"" )]
public sealed class {className} : GameResource
{{
	/// The weighted rows of this table.
	[Property] public List<{className}Entry> Entries {{ get; set; }} = new List<{className}Entry>();

	/// <summary>
	/// Roll once: pick an entry by cumulative weight; if it references a nested table,
	/// keep rolling into it until a plain entry wins or maxDepth is exhausted (then the
	/// deepest entry's Name is returned). Null when the table is empty. HOST-authoritative:
	/// roll on the host and replicate the result -- clients rolling their own loot is the
	/// classic economy exploit.
	/// </summary>
	public string Resolve( int maxDepth = {md} )
	{{
		var entry = RollEntry();
		if ( entry == null ) return null;
		if ( entry.NestedTable != null && entry.NestedTable != this && maxDepth > 0 )
			return entry.NestedTable.Resolve( maxDepth - 1 );
		return entry.Name;
	}}

	// Cumulative-weight pick over Entries. Null when empty; first entry when all weights are zero.
	private {className}Entry RollEntry()
	{{
		if ( Entries == null || Entries.Count == 0 ) return null;

		float total = 0f;
		foreach ( var e in Entries )
			if ( e != null && e.Weight > 0f ) total += e.Weight;
		if ( total <= 0f ) return Entries[0];

		float roll = Game.Random.Float( 0f, total );
		float cumulative = 0f;
		{className}Entry winner = null;
		foreach ( var e in Entries )
		{{
			if ( e == null || e.Weight <= 0f ) continue;
			winner = e;
			cumulative += e.Weight;
			if ( roll < cumulative ) break;
		}}
		return winner;
	}}
}}

/// <summary>
/// {resolverClass} -- rolls a {className} asset from the scene.
///
/// Assign Table to a .{extension} asset in the inspector (or via set_property with the
/// asset path). Roll() resolves through nested tables up to MaxDepth and fires the
/// static OnLoot event with the winning item name. Call it host-side and replicate
/// the result yourself ([Sync] or an [Rpc.Broadcast]).
///
/// Usage:
///   string drop = GetComponent&lt;{resolverClass}&gt;().Roll();
///   {resolverClass}.OnLoot += ( go, item ) => Log.Info( $""{{go.Name}} got {{item}}"" );
/// </summary>
public sealed class {resolverClass} : Component
{{
	/// The loot table asset this resolver rolls.
	[Property] public {className} Table {{ get; set; }}

	/// How deep nested-table references may chain before the roll settles.
	[Property] public int MaxDepth {{ get; set; }} = {md};

	/// Fires (on the rolling machine) when Roll() picks a winner: (roller, itemName).
	public static Action<GameObject, string> OnLoot {{ get; set; }}

	/// <summary>
	/// Roll the assigned table once. Null (with a warning) when no Table is assigned or
	/// the table is empty. HOST-authoritative by convention -- see the class summary.
	/// </summary>
	public string Roll()
	{{
		if ( Table == null )
		{{
			Log.Warning( $""[{resolverClass}] No Table assigned on {{GameObject.Name}} -- assign a .{extension} asset."" );
			return null;
		}}

		var drop = Table.Resolve( MaxDepth );
		if ( drop != null ) OnLoot?.Invoke( GameObject, drop );
		return drop;
	}}
}}
";
	}
}

/// <summary>
/// Shared placement helper for the economy/save handlers -- mirrors the standard scaffold
/// placement (create_economy_wallet / create_weighted_loot_table / LootEconomyHelpers).
/// </summary>
internal static class EconomySaveHelpers
{
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
}
