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
		pickaxe can dig around it (CopperReach). Even 4 m higher, 262 of the 427 deposits of that copy kept pieces under it, 521 in all, most on
		slopes; each raised by its own need, the kit's mining simulation took every piece out. The deposit keeps what it was raised by (UpHash), so
		another margin or switching it off (-1) moves it by the difference, and what it was measured for (MeasuredHash), so it is measured once.
		The deposits a pass finds (its scan, a few thousand objects a frame) are measured in the frames after it, about 4 ms a frame, never one a
		player's game may have loaded (Loaded; it waits for a later pass): a broken-open one too, its pieces coming up out of the player's pit. One
		in a zone generated now is raised in the same frame (ZoneSystem.SpawnZone), before anyone is sent it. One within BuildRadius of a
		building when it would move, up or down, is kept, and not moved again: the building may stand on it.

		FollowSlope (0.9.3, with OverDeepestHole on) tilts a deposit to lie along the slope it stands on, at most that many degrees: the game
		stands it upright, so on a slope the rock stands out downhill and is sunk uphill (on that copy, 20-30 degrees, 78 and 43 per cent of its
		pieces' length over the ground). It turns about its root, then rises by what it needs (CopperReach.Measure). The turn the game gave it is
		kept (TurnHash), so another FollowSlope, 0 or OverDeepestHole -1 stands it as it was. One broken open is not turned: a player is mining it.
	*/
	internal static class CopperDeposits
	{
		private const string Prefab = "rock4_copper";
		private const float BuildRadius = 20f;
		private const double WorkMilliseconds = 4.0;
		private static readonly int KeptHash = (RegrowthPlugin.GUID + ".copperkept").GetStableHashCode();
		private static readonly int UpHash = (RegrowthPlugin.GUID + ".copperup").GetStableHashCode();
		private static readonly int MeasuredHash = (RegrowthPlugin.GUID + ".coppermeasured").GetStableHashCode();
		// A deposit FollowSlope turned: the turn the game gave it (Euler angles).
		private static readonly int TurnHash = (RegrowthPlugin.GUID + ".copperturn").GetStableHashCode();
		private static readonly Dictionary<ZoneSystem.ZoneVegetation, float> gameOffsets = new Dictionary<ZoneSystem.ZoneVegetation, float>();
		private static readonly Queue<ZDOID> queue = new Queue<ZDOID>();
		private static Batch batch;
		private static Dictionary<Vector2s, List<Vector3>> builds;
		// A dry run moves and marks nothing: what it measured for a stamp is remembered here instead, so it reports once.
		private static readonly HashSet<ZDOID> dryMeasured = new HashSet<ZDOID>();
		private static int dryStamp;
		// The failures of the last batch that measured nothing (Work).
		private static int quietFailed;
		// What a zone generated now is measured with; null until Regrowth runs.
		private static Settings current;
		private static RegrowthState currentState;

		private sealed class Batch
		{
			public float margin, tilt;
			public int stamp;
			public bool dryRun;
			public int measured, enough, kept, postponed, failed, byLocation, bounded, frames, waitedFrames, upright;
			public double workMilliseconds, worstMilliseconds;
			public readonly List<float> up = new List<float>(), down = new List<float>(), tilted = new List<float>();
		}

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
			CopperReach.Start(host);
			bool changed = Raise(settings, state, piecePrefabs, worldLoaded, nothingYet);
			// A zone generated now is measured in its own frame: the locations near it must have been read by then.
			if (settings.CopperOverDeepestHole.Value >= 0f && worldLoaded && CopperReach.Ready())
			{
				CopperReach.AskAll();
			}
			// A batch measured for another setting is of no use: the next scan queues what the new one needs.
			float margin = settings.CopperOverDeepestHole.Value, tilt = Tilt(settings);
			if (batch != null && (batch.margin != margin || batch.tilt != tilt || batch.dryRun != settings.DryRun.Value || batch.stamp != Stamp(margin, state.CopperRaised, tilt)))
			{
				RegrowthPlugin.Log.LogInfo($"Copper deposits over the deepest hole: the setting changed with {queue.Count} deposit(s) of the batch still to do; what is done stays, the rest is measured for the new one.");
				Report(batch);
				End();
			}
			return changed;
		}

		public static void Stop()
		{
			End();
			dryMeasured.Clear();
			current = null;
			currentState = null;
			CopperReach.Reset();
		}

		private static void End()
		{
			queue.Clear();
			batch = null;
			builds = null;
			CopperReach.Forget();
		}

		private static bool Raise(Settings settings, RegrowthState state, HashSet<int> piecePrefabs, bool worldLoaded, bool nothingYet)
		{
			float want = Mathf.Clamp(settings.CopperRaiseMeters.Value, 0f, 8f);
			// A dry run changes nothing: what is generated meanwhile stands as high as the deposits that stand.
			float raised = settings.DryRun.Value ? state.CopperRaised : want;
			foreach (ZoneSystem.ZoneVegetation vegetation in ZoneSystem.instance.m_vegetation)
			{
				if (vegetation == null || !vegetation.m_prefab || vegetation.m_prefab.name != Prefab)
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
			int prefab = Prefab.GetStableHashCode();
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
				if (zdo.GetBool(KeptHash) || NearBuild(at, builds))
				{
					zdo.Set(KeptHash, true);
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

		// turn: Euler angles exactly as given (CopperReach.Measure makes them those the world's save keeps), or null to keep its turn.
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

		// What a deposit was measured for: the margin, the height RaiseMeters gave it, the slope it may follow, and how it was measured.
		private static int Stamp(float margin, float raised, float tilt)
		{
			return (tilt > 0f ? $"4 {margin:0.###} {raised:0.###} {tilt:0.###}" : $"3 {margin:0.###} {raised:0.###}").GetStableHashCode();
		}

		private static float Tilt(Settings settings)
		{
			return Mathf.Clamp(settings.CopperFollowSlope.Value, 0f, 45f);
		}

		// The turn the game gave a deposit: the one FollowSlope kept, or its own (one broken open is never turned).
		private static Vector3 Upright(ZDO zdo)
		{
			return zdo.GetPrefab() == CopperReach.WholeHash ? zdo.GetVec3(TurnHash, zdo.m_rotation) : zdo.m_rotation;
		}

		public static bool IsDeposit(int prefab)
		{
			return prefab == CopperReach.WholeHash || prefab == CopperReach.FracturedHash;
		}

		/*
			What a pass's scan found (Regrowth.Pass, over frames): the deposits and the player's building pieces by zone. Those not measured
			for this margin and height yet (with it off, those it raised) are queued for Work in the frames to come. A batch still going on
			finishes first; what it leaves is queued at the next pass.
		*/
		public static void Collected(List<ZDOID> deposits, Dictionary<Vector2s, List<Vector3>> pieces)
		{
			Settings settings = current;
			RegrowthState state = currentState;
			if (settings == null || state == null || ZDOMan.instance == null)
			{
				return;
			}
			float margin = settings.CopperOverDeepestHole.Value;
			if (margin >= 0f)
			{
				if (!CopperReach.Ready())
				{
					return;
				}
				// A zone generated now needs the locations near it read already.
				CopperReach.AskAll();
			}
			if (batch != null)
			{
				builds = pieces;
				return;
			}
			float tilt = Tilt(settings);
			int stamp = Stamp(margin, state.CopperRaised, tilt);
			bool dryRun = settings.DryRun.Value;
			if (!dryRun || dryStamp != stamp)
			{
				dryMeasured.Clear();
				dryStamp = stamp;
			}
			foreach (ZDOID id in deposits)
			{
				ZDO zdo = ZDOMan.instance.GetZDO(id);
				if (zdo == null || !IsDeposit(zdo.GetPrefab()) || zdo.GetInt(MeasuredHash) == stamp || (dryRun && dryMeasured.Contains(id)))
				{
					continue;
				}
				if (margin >= 0f || zdo.GetFloat(UpHash) != 0f || Upright(zdo) != zdo.m_rotation)
				{
					queue.Enqueue(id);
				}
			}
			if (queue.Count > 0)
			{
				batch = new Batch { margin = margin, tilt = tilt, stamp = stamp, dryRun = dryRun };
				builds = pieces;
			}
		}

		// A few milliseconds of the deposits queued, every frame (Regrowth.Tick).
		public static void Work()
		{
			if (batch == null || ZDOMan.instance == null || !ZNet.instance || !ZNetScene.instance)
			{
				return;
			}
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			// Each deposit looked at once a frame at most: one waiting for a location to be read goes to the back.
			int looked = 0, todo = queue.Count;
			bool done = false;
			while (queue.Count > 0 && looked < todo && watch.Elapsed.TotalMilliseconds < WorkMilliseconds)
			{
				looked++;
				ZDOID id = queue.Dequeue();
				ZDO zdo = ZDOMan.instance.GetZDO(id);
				if (zdo == null)
				{
					continue;
				}
				bool waiting = false;
				// One deposit that cannot be done must not stop the rest of Regrowth's frame (Tick), nor the deposits after it.
				try
				{
					waiting = !One(zdo);
				}
				catch (System.Exception e)
				{
					if (batch.failed++ == 0)
					{
						RegrowthPlugin.Log.LogWarning($"Copper deposit at {zdo.GetPosition()} could not be measured: {e}");
					}
				}
				if (waiting)
				{
					queue.Enqueue(id);
				}
				else
				{
					done = true;
				}
			}
			double spent = watch.Elapsed.TotalMilliseconds;
			batch.workMilliseconds += spent;
			batch.worstMilliseconds = System.Math.Max(batch.worstMilliseconds, spent);
			if (done)
			{
				batch.frames++;
			}
			else
			{
				batch.waitedFrames++;
			}
			if (queue.Count == 0)
			{
				// A batch that only waited for players to leave says nothing; one that could not measure says so when that changes.
				if (batch.measured > 0 || batch.kept > 0 || batch.failed != quietFailed)
				{
					Report(batch);
				}
				quietFailed = batch.measured > 0 || batch.kept > 0 ? 0 : batch.failed;
				End();
			}
		}

		// False while it waits for a location near to be read.
		private static bool One(ZDO zdo)
		{
			if (!IsDeposit(zdo.GetPrefab()))
			{
				return true;
			}
			if (zdo.GetBool(KeptHash))
			{
				batch.kept++;
				Done(zdo);
				return true;
			}
			Vector3 at = zdo.GetPosition();
			if (Loaded(zdo))
			{
				batch.postponed++;
				return true;
			}
			float up = zdo.GetFloat(UpHash, 0f), target = 0f, tilted = 0f;
			Vector3 now = zdo.m_rotation, upright = Upright(zdo), turn = upright;
			bool whole = zdo.GetPrefab() == CopperReach.WholeHash;
			if (batch.margin >= 0f)
			{
				CopperReach.Result result = CopperReach.Measure(zdo, batch.margin, up, whole ? batch.tilt : 0f, upright, out float need, out bool byLocation, out turn, out tilted);
				if (result == CopperReach.Result.Waiting)
				{
					return false;
				}
				if (result == CopperReach.Result.Failed)
				{
					batch.failed++;
					return true;
				}
				batch.measured++;
				if (byLocation)
				{
					batch.byLocation++;
				}
				if (CopperReach.LastBound)
				{
					batch.bounded++;
				}
				target = Mathf.Max(0f, need);
			}
			float delta = target - up;
			bool turning = turn != now;
			if (Mathf.Abs(delta) < 0.01f && !turning)
			{
				batch.enough++;
				Done(zdo);
				return true;
			}
			// Down as well as up: a building may stand on it, or against it.
			if (NearBuild(at, builds ?? (builds = new Dictionary<Vector2s, List<Vector3>>())))
			{
				batch.kept++;
				if (!batch.dryRun)
				{
					zdo.Set(KeptHash, true);
				}
				Done(zdo);
				return true;
			}
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
				zdo.Set(UpHash, target);
				if (turning && whole)
				{
					zdo.Set(TurnHash, upright);
				}
			}
			Done(zdo);
			return true;
		}

		private static void Done(ZDO zdo)
		{
			if (batch.dryRun)
			{
				dryMeasured.Add(zdo.m_uid);
			}
			else
			{
				zdo.Set(MeasuredHash, batch.stamp);
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

		private static void Report(Batch b)
		{
			string verb = b.dryRun ? " would be" : "";
			string Spread(List<float> list, string unit = " m")
			{
				list.Sort();
				return list.Count == 0 ? "" : $" by {list[0]:0.0}–{list[list.Count - 1]:0.0}{unit}, median {list[list.Count / 2]:0.0}";
			}
			RegrowthPlugin.Log.LogInfo(
				(b.dryRun ? "Dry run: " : "") + $"Copper deposits over the deepest hole ({(b.margin >= 0f ? $"{b.margin:0.##} m" : "off")}, CopperDeposits OverDeepestHole): "
				+ $"{b.measured} measured, {b.up.Count}{verb} raised{Spread(b.up)}, {b.down.Count}{verb} lowered back{Spread(b.down)}, {b.enough} high enough"
				+ (b.tilt > 0f || b.tilted.Count > 0 || b.upright > 0 ? $", {b.tilted.Count}{verb} tilted to the slope (at most {b.tilt:0} degrees, CopperDeposits FollowSlope){Spread(b.tilted, " degrees")}" + (b.upright > 0 ? $", {b.upright}{verb} stood as the game turned them" : "") : "")
				+ $"; {b.byLocation} on ground a location levels" + (b.bounded > 0 ? $" ({b.bounded} by locations in too many turns to pair, at the highest each can leave)" : "")
				+ $", {b.kept} left for a player's building within {BuildRadius:0} m"
				+ (b.postponed > 0 ? $", {b.postponed} a player has loaded (at a later pass)" : "")
				+ (b.failed > 0 ? $", {b.failed} could not be measured" : "")
				+ $"; {b.workMilliseconds:0} ms of work over {b.frames} frames, the worst {b.worstMilliseconds:0.0} ms"
				+ (b.waitedFrames > 0 ? $", {b.waitedFrames} frames waiting for locations to be read ({CopperReach.ReadReport()})" : "") + ".");
		}

		/*
			ZoneSystem.SpawnZone has just generated a zone (zone generation is the server's: a client spawns zones the server generated): its new
			deposits are raised by what each needs, before anyone is sent them. New objects are the ZDOs numbered from `first` on.
		*/
		public static void Generated(Vector2s zone, uint first)
		{
			Settings settings = current;
			if (settings == null || currentState == null || settings.DryRun.Value || settings.CopperOverDeepestHole.Value < 0f)
			{
				return;
			}
			float margin = settings.CopperOverDeepestHole.Value;
			long session = ZDOMan.GetSessionID();
			List<ZDO> deposits = new List<ZDO>();
			for (uint uid = first; uid < ZDOMan.instance.m_nextUid; uid++)
			{
				ZDO zdo = ZDOMan.instance.GetZDO(new ZDOID(session, uid));
				if (zdo != null && zdo.GetPrefab() == CopperReach.WholeHash)
				{
					deposits.Add(zdo);
				}
			}
			if (deposits.Count == 0)
			{
				return;
			}
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			float tilt = Tilt(settings);
			int stamp = Stamp(margin, currentState.CopperRaised, tilt);
			List<string> done = new List<string>();
			foreach (ZDO zdo in deposits)
			{
				Vector3 upright = zdo.m_rotation;
				CopperReach.Result result = CopperReach.Measure(zdo, margin, 0f, tilt, upright, out float need, out bool byLocation, out Vector3 turn, out float tilted);
				if (result != CopperReach.Result.Measured)
				{
					done.Add(result == CopperReach.Result.Waiting ? "at a later pass (a location near is still being read)" : "not measured");
					continue;
				}
				float target = Mathf.Max(0f, need);
				bool turning = turn != upright;
				if (target >= 0.01f || turning)
				{
					Vector3 at = zdo.GetPosition();
					at.y += target;
					Move(zdo, at, turning ? turn : (Vector3?)null);
					zdo.Set(UpHash, target);
					if (turning)
					{
						zdo.Set(TurnHash, upright);
					}
				}
				zdo.Set(MeasuredHash, stamp);
				done.Add((target >= 0.01f ? $"{target:0.0} m" : "none") + (turning ? $", tilted {tilted:0} degrees" : "") + (byLocation ? CopperReach.LastBound ? " (on ground a location levels, in too many turns to pair)" : " (on ground a location levels)" : ""));
			}
			CopperReach.Forget();
			RegrowthPlugin.Log.LogInfo($"Copper deposits in the new zone {zone.x},{zone.y}, raised by what each needs for every piece to reach {margin:0.##} m over the deepest hole: {string.Join(", ", done)} ({watch.Elapsed.TotalMilliseconds:0.0} ms).");
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

	// A zone generated now: its copper deposits raised by what each needs (CopperDeposits OverDeepestHole), in the same frame.
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
				CopperDeposits.Generated(zoneID, __state);
			}
			catch (System.Exception e)
			{
				RegrowthPlugin.Log.LogWarning($"Copper deposits in the new zone {zoneID.x},{zoneID.y}: {e}");
			}
		}
	}
}
