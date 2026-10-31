using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ResourceRegrowth
{
	/*
		Copper deposits higher out of the ground ([CopperDeposits] RaiseMeters; 0 is the game's height). A deposit is an iceberg: the game
		puts it 2 m under the ground (ZoneVegetation.m_groundOffset) and its 130 pieces reach 13 m down from there and 4 m up. On a copy
		of the live world (30 Oct 2026) all 427 untouched deposits stood at that height, within 4 cm, and a median of 46 of the 130 pieces reached
		over the ground, the worst 18; the three broken open had been left with 100, 75 and 17 pieces, nearly all under the ground. Raised 4 m,
		a median of 88 reach it, the worst 62, and the rock stands up to 6 m tall.

		Two things move it: the game's placement row for the zones generated from now on, and, when the setting changes, the deposits that stand
		(all of them at one height, which the state remembers). One broken open stays where it is, its player's pit around it (the first hit swaps the
		deposit for rock4_copper_frac, which is not moved), and so does one within
		BuildRadius of a player's building: its rock is 28 m across and must not come up through a floor. Vanilla clients read the height from the object.

		OverDeepestHole (0.9.1) raises each deposit further by what it needs itself, for every live piece to reach that far over the deepest hole a
		pickaxe can dig around it (OreReach). Even 4 m higher, 262 of the 427 deposits of that copy kept pieces under it, 521 in all, most on
		slopes; each raised by its own need, the kit's mining simulation took every piece out. The deposit keeps what it was raised by (its up
		key), so another margin or switching it off (-1) moves it by the difference, and what it was measured for (its measured key), so it is
		measured once. The deposits a pass finds (its scan, a few thousand objects a frame) are measured in the frames after it, about 4 ms a
		frame, never one a player's game may have loaded (Loaded; it waits for a later pass): a broken-open one too, its pieces coming up out of
		the player's pit. One in a zone generated now is raised in the same frame (ZoneSystem.SpawnZone), before anyone is sent it. One within
		BuildRadius of a building when it would move, up or down, is kept, and not moved again: the building may stand on it.

		FollowSlope (0.9.3, with OverDeepestHole on) tilts a deposit to lie along the slope it stands on, at most that many degrees: the game
		stands it upright, so on a slope the rock stands out downhill and is sunk uphill (on that copy, 20-30 degrees, 78 and 43 per cent of its
		pieces' length over the ground). It turns about its root, then rises by what it needs (OreReach.Measure). The turn the game gave it is
		kept (its turn key), so another FollowSlope, 0 or OverDeepestHole -1 stands it as it was. One broken open is not turned: a player is
		mining it.

		Silver veins ([SilverVeins], 0.9.4) are measured the same way and raised by what each needs, but never so high that any of a vein comes
		within UnderGround of the ground (the lowest the locations near can leave it): a vein is found with the wishbone, not seen. The game puts
		one 4 m under the ground on slopes of up to 30 degrees, upright; on a copy of the live world (30 Oct 2026) 22 of its 100 veins kept
		pieces under the deepest hole, 45 left in 12 of them after mining; raised by all they need, 5 would have stood 2-4.4 m out of the
		ground, and 4 stood out of it already, on slopes of 25-34 degrees. One that needs nothing stays as the game made it. With FollowSlope a
		short one is measured upright and tilted to its slope, and stands the way that leaves fewer of its pieces out of reach (upright when
		that is all the same): tilted, it lies along the ground, its uphill end no deeper under it than the rest, and can rise further before
		any of it comes out. A vein never stands further out of the ground than the game left it, nor out of it at all when the game left it
		under. Its own keys (silverup, silverturn ...): the two kinds never mix.
	*/
	internal static class OreDeposits
	{
		private const string CopperPrefab = "rock4_copper";
		private const float BuildRadius = 20f;
		private const double WorkMilliseconds = 4.0;
		private static readonly Dictionary<ZoneSystem.ZoneVegetation, float> gameOffsets = new Dictionary<ZoneSystem.ZoneVegetation, float>();
		// What a zone generated now is measured with; null until Regrowth runs.
		private static Settings current;
		private static RegrowthState currentState;
		// Each live piece's height over the deepest hole, from the last measure of a vein (Target).
		private static readonly List<float> overs = new List<float>();

		private sealed class Batch
		{
			public float margin, tilt, under;
			public int stamp;
			public bool dryRun;
			public int measured, enough, kept, postponed, failed, byLocation, bounded, frames, waitedFrames, upright, capped, shortVeins, outOfReach;
			public double workMilliseconds, worstMilliseconds;
			public readonly List<float> up = new List<float>(), down = new List<float>(), tilted = new List<float>();
		}

		// A kind of deposit, with what it is measured for and the keys it keeps on its object.
		private sealed class Kind
		{
			public string name, section;
			public OreReach.Shape shape;
			public int keptHash, upHash, measuredHash, turnHash;
			// OverDeepestHole, FollowSlope, and how far under the ground it stays (under 0: no limit).
			public System.Func<Settings, float> margin, tilt, under;
			public readonly Queue<ZDOID> queue = new Queue<ZDOID>();
			public Batch batch;
			public Dictionary<Vector2s, List<Vector3>> builds;
			// A dry run moves and marks nothing: what it measured for a stamp is remembered here instead, so it reports once.
			public readonly HashSet<ZDOID> dryMeasured = new HashSet<ZDOID>();
			public int dryStamp;
			// The failures of the last batch that measured nothing (Work).
			public int quietFailed;

			public bool Is(int prefab)
			{
				return prefab == shape.WholeHash || prefab == shape.FracturedHash;
			}
		}

		private static int Key(string name)
		{
			return (RegrowthPlugin.GUID + "." + name).GetStableHashCode();
		}

		private static readonly Kind Copper = new Kind
		{
			name = "Copper deposits",
			section = "CopperDeposits",
			shape = OreReach.Copper,
			keptHash = Key("copperkept"),
			upHash = Key("copperup"),
			measuredHash = Key("coppermeasured"),
			// A deposit FollowSlope turned: the turn the game gave it (Euler angles).
			turnHash = Key("copperturn"),
			margin = s => s.CopperOverDeepestHole.Value,
			tilt = s => Mathf.Clamp(s.CopperFollowSlope.Value, 0f, 45f),
			under = s => -1f,
		};

		private static readonly Kind Silver = new Kind
		{
			name = "Silver veins",
			section = "SilverVeins",
			shape = OreReach.Silver,
			keptHash = Key("silverkept"),
			upHash = Key("silverup"),
			measuredHash = Key("silvermeasured"),
			turnHash = Key("silverturn"),
			margin = s => s.SilverOverDeepestHole.Value,
			tilt = s => Mathf.Clamp(s.SilverFollowSlope.Value, 0f, 45f),
			under = s => Mathf.Clamp(s.SilverUnderGround.Value, 0f, 4f),
		};

		private static readonly Kind[] kinds = { Copper, Silver };

		/*
			True when the state changed and must be saved right away. When deposits moved, the world is saved at once too: until it is, a crash
			would leave them where they were while the state says they moved, and the next change would move them by the wrong height.
			nothingYet: the world has had no objects since the server started (a new one nobody has come to; a dedicated server generates zones
			only around players). Without objects otherwise, it is shutting down.
		*/
		public static bool Apply(Settings settings, RegrowthState state, MonoBehaviour host, HashSet<int> piecePrefabs, bool worldLoaded, bool nothingYet)
		{
			current = settings;
			currentState = state;
			OreReach.Start(host);
			bool changed = Raise(settings, state, piecePrefabs, worldLoaded, nothingYet);
			// A zone generated now is measured in its own frame: the locations near it must have been read by then.
			bool ask = false;
			foreach (Kind k in kinds)
			{
				ask |= k.margin(settings) >= 0f && worldLoaded && OreReach.Ready(k.shape);
			}
			if (ask)
			{
				OreReach.AskAll();
			}
			// A batch measured for another setting is of no use: the next scan queues what the new one needs.
			foreach (Kind k in kinds)
			{
				Batch b = k.batch;
				float margin = k.margin(settings), tilt = k.tilt(settings), under = k.under(settings);
				if (b != null && (b.margin != margin || b.tilt != tilt || b.under != under || b.dryRun != settings.DryRun.Value || b.stamp != Stamp(k, margin, tilt, under, state.CopperRaised)))
				{
					RegrowthPlugin.Log.LogInfo($"{k.name} over the deepest hole: the setting changed with {k.queue.Count} deposit(s) of the batch still to do; what is done stays, the rest is measured for the new one.");
					Report(k, b);
					End(k);
				}
			}
			return changed;
		}

		public static void Stop()
		{
			foreach (Kind k in kinds)
			{
				End(k);
				k.dryMeasured.Clear();
			}
			current = null;
			currentState = null;
			OreReach.Reset();
		}

		private static void End(Kind k)
		{
			k.queue.Clear();
			k.batch = null;
			k.builds = null;
			OreReach.Forget();
		}

		// No batch of any kind going on, nothing queued (a test waits for it).
		public static bool Idle()
		{
			foreach (Kind k in kinds)
			{
				if (k.batch != null || k.queue.Count > 0)
				{
					return false;
				}
			}
			return true;
		}

		private static bool Raise(Settings settings, RegrowthState state, HashSet<int> piecePrefabs, bool worldLoaded, bool nothingYet)
		{
			float want = Mathf.Clamp(settings.CopperRaiseMeters.Value, 0f, 8f);
			// A dry run changes nothing: what is generated meanwhile stands as high as the deposits that stand.
			float raised = settings.DryRun.Value ? state.CopperRaised : want;
			foreach (ZoneSystem.ZoneVegetation vegetation in ZoneSystem.instance.m_vegetation)
			{
				if (vegetation == null || !vegetation.m_prefab || vegetation.m_prefab.name != CopperPrefab)
				{
					continue;
				}
				if (!gameOffsets.TryGetValue(vegetation, out float own))
				{
					gameOffsets[vegetation] = own = vegetation.m_groundOffset;
				}
				vegetation.m_groundOffset = own + raised;
			}
			float delta = want - state.CopperRaised;
			if (settings.DryRun.Value || Mathf.Abs(delta) < 0.001f)
			{
				return false;
			}
			if (!worldLoaded)
			{
				// A new world has nothing standing yet, and all it generates stands at the new height. On shutdown the game empties its objects first: nothing is known then.
				if (!nothingYet)
				{
					return false;
				}
				state.CopperRaised = want;
				RegrowthPlugin.Log.LogInfo($"Copper deposits: {want:0.##} m higher than the game puts them, in a world with nothing generated yet.");
				return true;
			}
			int prefab = CopperPrefab.GetStableHashCode();
			List<ZDO> deposits = new List<ZDO>();
			foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
			{
				if (zdo.GetPrefab() == prefab)
				{
					deposits.Add(zdo);
				}
			}
			Dictionary<Vector2s, List<Vector3>> builds = Builds(piecePrefabs);
			int moved = 0, kept = 0;
			foreach (ZDO zdo in deposits)
			{
				Vector3 at = zdo.GetPosition();
				if (zdo.GetBool(Copper.keptHash) || NearBuild(at, builds))
				{
					zdo.Set(Copper.keptHash, true);
					kept++;
					continue;
				}
				at.y += delta;
				Move(zdo, at);
				moved++;
			}
			state.CopperRaised = want;
			RegrowthPlugin.Log.LogInfo($"Copper deposits: {want:0.##} m higher than the game puts them: {moved} moved by {delta:0.##} m, {kept} left where they are for a player's building within {BuildRadius:0} m. World save requested.");
			ZNet.instance.Save(sync: false, saveOtherPlayerProfiles: true, waitForNextFrame: true);
			return true;
		}

		// turn: Euler angles exactly as given (OreReach.Measure makes them those the world's save keeps), or null to keep its turn.
		private static void Move(ZDO zdo, Vector3 at, Vector3? turn = null)
		{
			zdo.SetOwner(ZDOMan.GetSessionID());
			ZNetView view = ZNetScene.instance.FindInstance(zdo);
			if (view)
			{
				view.transform.position = at;
				if (turn.HasValue)
				{
					view.transform.rotation = Quaternion.Euler(turn.Value);
				}
			}
			zdo.SetPosition(at);
			// Not SetRotation: it keeps the Euler angles a quaternion gives back, which the save may round down another half degree.
			if (turn.HasValue && zdo.m_rotation != turn.Value)
			{
				zdo.m_rotation = turn.Value;
				zdo.IncreaseDataRevision();
			}
		}

		// What a deposit was measured for: the margin, the height RaiseMeters gave it, the slope it may follow, and how it was measured; for a
		// silver vein the margin, how far under the ground it stays and the slope.
		private static int Stamp(Kind k, float margin, float tilt, float under, float raised)
		{
			if (k == Silver)
			{
				return $"s1 {margin:0.###} {under:0.###} {tilt:0.###}".GetStableHashCode();
			}
			return (tilt > 0f ? $"4 {margin:0.###} {raised:0.###} {tilt:0.###}" : $"3 {margin:0.###} {raised:0.###}").GetStableHashCode();
		}

		// The turn the game gave a deposit: the one FollowSlope kept, or its own (one broken open is never turned).
		private static Vector3 Upright(Kind k, ZDO zdo)
		{
			return zdo.GetPrefab() == k.shape.WholeHash ? zdo.GetVec3(k.turnHash, zdo.m_rotation) : zdo.m_rotation;
		}

		public static bool IsDeposit(int prefab)
		{
			foreach (Kind k in kinds)
			{
				if (k.Is(prefab))
				{
					return true;
				}
			}
			return false;
		}

		/*
			What a pass's scan found (Regrowth.Pass, over frames): the deposits and the player's building pieces by zone. Those not measured
			for this margin and height yet (with it off, those it moved) are queued for Work in the frames to come, by kind. A batch still going
			on finishes first; what it leaves is queued at the next pass.
		*/
		public static void Collected(List<ZDOID> deposits, Dictionary<Vector2s, List<Vector3>> pieces)
		{
			Settings settings = current;
			RegrowthState state = currentState;
			if (settings == null || state == null || ZDOMan.instance == null)
			{
				return;
			}
			bool asked = false;
			foreach (Kind k in kinds)
			{
				float margin = k.margin(settings);
				if (margin >= 0f)
				{
					if (!OreReach.Ready(k.shape))
					{
						continue;
					}
					// A zone generated now needs the locations near it read already.
					if (!asked)
					{
						OreReach.AskAll();
						asked = true;
					}
				}
				if (k.batch != null)
				{
					k.builds = pieces;
					continue;
				}
				float tilt = k.tilt(settings), under = k.under(settings);
				int stamp = Stamp(k, margin, tilt, under, state.CopperRaised);
				bool dryRun = settings.DryRun.Value;
				if (!dryRun || k.dryStamp != stamp)
				{
					k.dryMeasured.Clear();
					k.dryStamp = stamp;
				}
				foreach (ZDOID id in deposits)
				{
					ZDO zdo = ZDOMan.instance.GetZDO(id);
					if (zdo == null || !k.Is(zdo.GetPrefab()) || zdo.GetInt(k.measuredHash) == stamp || (dryRun && k.dryMeasured.Contains(id)))
					{
						continue;
					}
					if (margin >= 0f || zdo.GetFloat(k.upHash) != 0f || Upright(k, zdo) != zdo.m_rotation)
					{
						k.queue.Enqueue(id);
					}
				}
				if (k.queue.Count > 0)
				{
					k.batch = new Batch { margin = margin, tilt = tilt, under = under, stamp = stamp, dryRun = dryRun };
					k.builds = pieces;
				}
			}
		}

		// A few milliseconds of the deposits queued, every frame (Regrowth.Tick): copper first, then silver in what is left of them.
		public static void Work()
		{
			if (ZDOMan.instance == null || !ZNet.instance || !ZNetScene.instance)
			{
				return;
			}
			System.Diagnostics.Stopwatch watch = null;
			foreach (Kind k in kinds)
			{
				if (k.batch == null)
				{
					continue;
				}
				if (watch == null)
				{
					watch = System.Diagnostics.Stopwatch.StartNew();
				}
				else if (watch.Elapsed.TotalMilliseconds >= WorkMilliseconds)
				{
					return;
				}
				Work(k, watch);
			}
		}

		private static void Work(Kind k, System.Diagnostics.Stopwatch watch)
		{
			Batch batch = k.batch;
			double begun = watch.Elapsed.TotalMilliseconds;
			// Each deposit looked at once a frame at most: one waiting for a location to be read goes to the back.
			int looked = 0, todo = k.queue.Count;
			bool done = false;
			while (k.queue.Count > 0 && looked < todo && watch.Elapsed.TotalMilliseconds < WorkMilliseconds)
			{
				looked++;
				ZDOID id = k.queue.Dequeue();
				ZDO zdo = ZDOMan.instance.GetZDO(id);
				if (zdo == null)
				{
					continue;
				}
				bool waiting = false;
				// One deposit that cannot be done must not stop the rest of Regrowth's frame (Tick), nor the deposits after it.
				try
				{
					waiting = !One(k, batch, zdo);
				}
				catch (System.Exception e)
				{
					if (batch.failed++ == 0)
					{
						RegrowthPlugin.Log.LogWarning($"{k.name}: the one at {zdo.GetPosition()} could not be measured: {e}");
					}
				}
				if (waiting)
				{
					k.queue.Enqueue(id);
				}
				else
				{
					done = true;
				}
			}
			double spent = watch.Elapsed.TotalMilliseconds - begun;
			batch.workMilliseconds += spent;
			batch.worstMilliseconds = System.Math.Max(batch.worstMilliseconds, spent);
			if (done)
			{
				batch.frames++;
			}
			else if (looked > 0)
			{
				// (Not a frame the shared budget was spent in before this kind's turn came: nothing was looked at then.)
				batch.waitedFrames++;
			}
			if (k.queue.Count == 0)
			{
				// A batch that only waited for players to leave says nothing; one that could not measure says so when that changes. One that
				// puts deposits back (OverDeepestHole -1) measures none: what it moved counts.
				bool did = batch.measured > 0 || batch.kept > 0 || batch.enough > 0 || batch.up.Count > 0 || batch.down.Count > 0
					|| batch.tilted.Count > 0 || batch.upright > 0;
				if (did || batch.failed != k.quietFailed)
				{
					Report(k, batch);
				}
				k.quietFailed = did ? 0 : batch.failed;
				End(k);
			}
		}

		// False while it waits for a location near to be read.
		private static bool One(Kind k, Batch batch, ZDO zdo)
		{
			if (!k.Is(zdo.GetPrefab()))
			{
				return true;
			}
			if (zdo.GetBool(k.keptHash))
			{
				batch.kept++;
				Done(k, batch, zdo);
				return true;
			}
			Vector3 at = zdo.GetPosition();
			if (Loaded(zdo))
			{
				batch.postponed++;
				return true;
			}
			float up = zdo.GetFloat(k.upHash, 0f), target = 0f, tilted = 0f;
			Vector3 now = zdo.m_rotation, upright = Upright(k, zdo), turn = upright;
			bool whole = zdo.GetPrefab() == k.shape.WholeHash, capped = false;
			int outOfReach = 0;
			if (batch.margin >= 0f)
			{
				OreReach.Result result = Target(k, zdo, batch.margin, up, whole ? batch.tilt : 0f, batch.under, upright, out target, out turn, out tilted,
					out bool byLocation, out bool bounded, out capped, out outOfReach);
				if (result == OreReach.Result.Waiting)
				{
					return false;
				}
				if (result == OreReach.Result.Failed)
				{
					batch.failed++;
					return true;
				}
				batch.measured++;
				if (byLocation)
				{
					batch.byLocation++;
				}
				if (bounded)
				{
					batch.bounded++;
				}
			}
			float delta = target - up;
			bool turning = turn != now;
			if (Mathf.Abs(delta) < 0.01f && !turning)
			{
				batch.enough++;
				Short(batch, capped, outOfReach);
				Done(k, batch, zdo);
				return true;
			}
			// Down as well as up: a building may stand on it, or against it.
			if (NearBuild(at, k.builds ?? (k.builds = new Dictionary<Vector2s, List<Vector3>>())))
			{
				batch.kept++;
				if (!batch.dryRun)
				{
					zdo.Set(k.keptHash, true);
				}
				Done(k, batch, zdo);
				return true;
			}
			Short(batch, capped, outOfReach);
			if (Mathf.Abs(delta) >= 0.01f)
			{
				(delta > 0f ? batch.up : batch.down).Add(Mathf.Abs(delta));
			}
			if (turning)
			{
				if (tilted > 0f)
				{
					batch.tilted.Add(tilted);
				}
				else
				{
					batch.upright++;
				}
			}
			if (!batch.dryRun)
			{
				at.y += delta;
				Move(zdo, at, turning ? turn : (Vector3?)null);
				zdo.Set(k.upHash, target);
				if (turning && whole)
				{
					zdo.Set(k.turnHash, upright);
				}
			}
			Done(k, batch, zdo);
			return true;
		}

		// Counted for a vein that stands where it is to (not one left for a building): lower than it needs, and its pieces still out of reach so.
		private static void Short(Batch batch, bool capped, int outOfReach)
		{
			if (capped)
			{
				batch.capped++;
			}
			if (outOfReach > 0)
			{
				batch.shortVeins++;
				batch.outOfReach += outOfReach;
			}
		}

		/*
			Where a deposit is to stand, from the game's own height and turn: target, how much higher; turn, its turn. A copper deposit by all it
			needs, tilted to its slope with FollowSlope (maxTilt). A silver vein (under 0 or more) that needs nothing stays as the game made it;
			one that does rises no higher than leaves it `under` the ground, and is tilted only when that leaves fewer of its pieces out of reach,
			and never so that it stands further out of the ground than the game left it, or out of it at all when the game left it under.
			capped: it stands lower than it needs; outOfReach: its live pieces still under the hole, standing so. byLocation and bounded: of the
			measure of the turn it is to stand in.
		*/
		private static OreReach.Result Target(Kind k, ZDO zdo, float margin, float up, float maxTilt, float under, Vector3 upright,
			out float target, out Vector3 turn, out float tilted, out bool byLocation, out bool bounded, out bool capped, out int outOfReach)
		{
			target = 0f;
			turn = upright;
			tilted = 0f;
			bounded = false;
			capped = false;
			outOfReach = 0;
			float need;
			if (under < 0f)
			{
				OreReach.Result result = OreReach.Measure(k.shape, zdo, margin, up, maxTilt, upright, -1f, null, out need, out byLocation, out turn, out tilted, out _);
				bounded = OreReach.LastBound;
				if (result == OreReach.Result.Measured)
				{
					target = Mathf.Max(0f, need);
				}
				return result;
			}
			OreReach.Result standing = OreReach.Measure(k.shape, zdo, margin, up, 0f, upright, under, overs, out need, out byLocation, out _, out _, out float room);
			bounded = OreReach.LastBound;
			if (standing != OreReach.Result.Measured || need <= 0f)
			{
				return standing;
			}
			target = Capped(need, room);
			capped = target < need;
			outOfReach = OutOfReach(margin, target);
			if (maxTilt <= 0f || outOfReach == 0)
			{
				return standing;
			}
			OreReach.Result lying = OreReach.Measure(k.shape, zdo, margin, up, maxTilt, upright, under, overs, out float needTilted, out bool byLocationTilted,
				out Vector3 turnTilted, out float degrees, out float roomTilted);
			bool boundedTilted = OreReach.LastBound;
			if (lying != OreReach.Result.Measured || turnTilted == upright || roomTilted < Mathf.Min(0f, room))
			{
				return standing;
			}
			float raise = Capped(needTilted, roomTilted);
			int left = OutOfReach(margin, raise);
			if (left < outOfReach)
			{
				target = raise;
				turn = turnTilted;
				tilted = degrees;
				capped = raise < needTilted;
				outOfReach = left;
				byLocation = byLocationTilted;
				bounded = boundedTilted;
			}
			return standing;
		}

		// As much as it needs, as far as the ground leaves room: none when it stands out of the ground, or too near it, already.
		private static float Capped(float need, float room)
		{
			return Mathf.Clamp(Mathf.Min(need, room), 0f, Mathf.Max(0f, need));
		}

		// The live pieces of the last measure (overs) still short of the margin over the deepest hole, raised so.
		private static int OutOfReach(float margin, float raise)
		{
			int n = 0;
			foreach (float over in overs)
			{
				if (over + raise < margin - 0.001f)
				{
					n++;
				}
			}
			return n;
		}

		private static void Done(Kind k, Batch batch, ZDO zdo)
		{
			if (batch.dryRun)
			{
				k.dryMeasured.Add(zdo.m_uid);
			}
			else
			{
				zdo.Set(k.measuredHash, batch.stamp);
			}
		}

		/*
			Whether a player's game may have the deposit loaded: the zones ZDOMan.FindSectorObjects gives a client its objects from (its near
			simulation distance, the total for a distant object), one zone wider for a player on the move. A client never moves a rock it has
			loaded, so moved then it would stay where it was for that player and break open there.
		*/
		private static bool Loaded(ZDO zdo)
		{
			SimulationDistance distance = ZNet.instance.GetSyncedSimulationDistance();
			int reach = (zdo.Distant ? distance.TotalSimulationDistance : distance.NearSimulationDistance) + 1;
			Vector2s zone = zdo.GetSector();
			foreach (ZNetPeer peer in ZNet.instance.GetPeers())
			{
				Vector2s at = ZoneSystem.GetZone(peer.GetRefPos());
				if (Mathf.Abs(at.x - zone.x) <= reach && Mathf.Abs(at.y - zone.y) <= reach)
				{
					return true;
				}
			}
			return false;
		}

		private static void Report(Kind k, Batch b)
		{
			string verb = b.dryRun ? " would be" : "";
			string Spread(List<float> list, string unit = " m")
			{
				list.Sort();
				return list.Count == 0 ? "" : $" by {list[0]:0.0}–{list[list.Count - 1]:0.0}{unit}, median {list[list.Count / 2]:0.0}";
			}
			RegrowthPlugin.Log.LogInfo(
				(b.dryRun ? "Dry run: " : "") + $"{k.name} over the deepest hole ({(b.margin >= 0f ? $"{b.margin:0.##} m" : "off")}, {k.section} OverDeepestHole): "
				+ $"{b.measured} measured, {b.up.Count}{verb} raised{Spread(b.up)}, {b.down.Count}{verb} lowered back{Spread(b.down)}, {b.enough} " + (b.under >= 0f ? "left as they stand" : "high enough")
				+ (b.tilt > 0f || b.tilted.Count > 0 || b.upright > 0 ? $", {b.tilted.Count}{verb} tilted to the slope (at most {b.tilt:0} degrees, {k.section} FollowSlope){Spread(b.tilted, " degrees")}" + (b.upright > 0 ? $", {b.upright}{verb} stood as the game turned them" : "") : "")
				+ (b.under >= 0f && b.margin >= 0f ? $"; {b.capped} raised less than they need, to stay {b.under:0.##} m under the ground ({k.section} UnderGround), {b.outOfReach} piece(s) in {b.shortVeins} of them still out of reach" : "")
				+ $"; {b.byLocation} on ground a location levels" + (b.bounded > 0 ? $" ({b.bounded} by locations in too many turns to pair, at the highest each can leave)" : "")
				+ $", {b.kept} left for a player's building within {BuildRadius:0} m"
				+ (b.postponed > 0 ? $", {b.postponed} a player has loaded (at a later pass)" : "")
				+ (b.failed > 0 ? $", {b.failed} could not be measured" : "")
				+ $"; {b.workMilliseconds:0} ms of work over {b.frames} frames, the worst {b.worstMilliseconds:0.0} ms"
				+ (b.waitedFrames > 0 ? $", {b.waitedFrames} frames waiting for locations to be read ({OreReach.ReadReport()})" : "") + ".");
		}

		/*
			ZoneSystem.SpawnZone has just generated a zone (zone generation is the server's: a client spawns zones the server generated): its new
			deposits are raised by what each needs, before anyone is sent them. New objects are the ZDOs numbered from `first` on.
		*/
		public static void Generated(Vector2s zone, uint first)
		{
			Settings settings = current;
			// Both off since startup (the patch stays once it is in): no new object looked at, as with copper alone before 0.9.4.
			if (settings == null || currentState == null || settings.DryRun.Value || (Copper.margin(settings) < 0f && Silver.margin(settings) < 0f))
			{
				return;
			}
			long session = ZDOMan.GetSessionID();
			List<ZDO> copper = null, silver = null;
			for (uint uid = first; uid < ZDOMan.instance.m_nextUid; uid++)
			{
				ZDO zdo = ZDOMan.instance.GetZDO(new ZDOID(session, uid));
				if (zdo == null)
				{
					continue;
				}
				int prefab = zdo.GetPrefab();
				if (prefab == Copper.shape.WholeHash)
				{
					(copper ?? (copper = new List<ZDO>())).Add(zdo);
				}
				else if (prefab == Silver.shape.WholeHash)
				{
					(silver ?? (silver = new List<ZDO>())).Add(zdo);
				}
			}
			if (copper != null)
			{
				Generated(Copper, settings, zone, copper);
			}
			if (silver != null)
			{
				Generated(Silver, settings, zone, silver);
			}
		}

		private static void Generated(Kind k, Settings settings, Vector2s zone, List<ZDO> deposits)
		{
			float margin = k.margin(settings);
			if (margin < 0f)
			{
				return;
			}
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			float tilt = k.tilt(settings), under = k.under(settings);
			int stamp = Stamp(k, margin, tilt, under, currentState.CopperRaised);
			List<string> done = new List<string>();
			foreach (ZDO zdo in deposits)
			{
				Vector3 upright = zdo.m_rotation;
				OreReach.Result result = Target(k, zdo, margin, 0f, tilt, under, upright, out float target, out Vector3 turn, out float tilted,
					out bool byLocation, out bool bounded, out bool capped, out int outOfReach);
				if (result != OreReach.Result.Measured)
				{
					done.Add(result == OreReach.Result.Waiting ? "at a later pass (a location near is still being read)" : "not measured");
					continue;
				}
				bool turning = turn != upright;
				if (target >= 0.01f || turning)
				{
					Vector3 at = zdo.GetPosition();
					at.y += target;
					Move(zdo, at, turning ? turn : (Vector3?)null);
					zdo.Set(k.upHash, target);
					if (turning)
					{
						zdo.Set(k.turnHash, upright);
					}
				}
				zdo.Set(k.measuredHash, stamp);
				done.Add((target >= 0.01f ? $"{target:0.0} m" : "none") + (turning ? $", tilted {tilted:0} degrees" : "")
					+ (outOfReach > 0 ? $", {outOfReach} piece(s) still out of reach to stay under the ground" : "")
					+ (byLocation ? bounded ? " (on ground a location levels, in too many turns to pair)" : " (on ground a location levels)" : ""));
			}
			OreReach.Forget();
			RegrowthPlugin.Log.LogInfo($"{k.name} in the new zone {zone.x},{zone.y}, raised by what each needs for every piece to reach {margin:0.##} m over the deepest hole"
				+ (under >= 0f ? $", no higher than {under:0.##} m under the ground" : "") + $": {string.Join(", ", done)} ({watch.Elapsed.TotalMilliseconds:0.0} ms).");
		}

		private static Dictionary<Vector2s, List<Vector3>> Builds(HashSet<int> piecePrefabs)
		{
			Dictionary<Vector2s, List<Vector3>> builds = new Dictionary<Vector2s, List<Vector3>>();
			foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
			{
				if (zdo.GetLong(ZDOVars.s_creator, 0L) != 0L && piecePrefabs.Contains(zdo.GetPrefab()))
				{
					NoteBuild(builds, zdo.GetPosition());
				}
			}
			return builds;
		}

		// A player's building piece, by its zone (one in a dungeon, thousands of metres up, does not count).
		public static void NoteBuild(Dictionary<Vector2s, List<Vector3>> builds, Vector3 at)
		{
			if (at.y >= 3000f)
			{
				return;
			}
			Vector2s zone = ZoneSystem.GetZone(at);
			if (!builds.TryGetValue(zone, out List<Vector3> list))
			{
				builds[zone] = list = new List<Vector3>();
			}
			list.Add(at);
		}

		private static bool NearBuild(Vector3 at, Dictionary<Vector2s, List<Vector3>> builds)
		{
			Vector2s zone = ZoneSystem.GetZone(at);
			for (int y = zone.y - 1; y <= zone.y + 1; y++)
			{
				for (int x = zone.x - 1; x <= zone.x + 1; x++)
				{
					if (!builds.TryGetValue(new Vector2s(x, y), out List<Vector3> list))
					{
						continue;
					}
					foreach (Vector3 p in list)
					{
						float dx = p.x - at.x, dz = p.z - at.z;
						if (dx * dx + dz * dz <= BuildRadius * BuildRadius)
						{
							return true;
						}
					}
				}
			}
			return false;
		}
	}

	// A zone generated now: its copper deposits and silver veins raised by what each needs (OverDeepestHole), in the same frame.
	[HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
	internal static class ZoneSystem_SpawnZone_Patch
	{
		static void Prefix(out uint __state)
		{
			__state = ZDOMan.instance != null ? ZDOMan.instance.m_nextUid : 0u;
		}

		static void Postfix(Vector2s zoneID, ZoneSystem.SpawnMode mode, uint __state)
		{
			if (mode == ZoneSystem.SpawnMode.Client || __state == 0u || ZDOMan.instance.m_nextUid == __state)
			{
				return;
			}
			try
			{
				OreDeposits.Generated(zoneID, __state);
			}
			catch (System.Exception e)
			{
				RegrowthPlugin.Log.LogWarning($"Ore deposits in the new zone {zoneID.x},{zoneID.y}: {e}");
			}
		}
	}
}
