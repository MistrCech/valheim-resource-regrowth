using System.Collections.Generic;
using System.Linq;
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
		A deposit standing is measured in the frames after each pass, a few milliseconds a frame, never one with a player within PlayerRadius (it waits
		for a later pass): a broken-open one too, its pieces coming up out of the player's pit. One in a zone generated now is raised in the same frame
		(ZoneSystem.SpawnZone), before anyone is sent it.
	*/
	internal static class CopperDeposits
	{
		private const string Prefab = "rock4_copper";
		private const float BuildRadius = 20f;
		private const float PlayerRadius = 200f;
		private const double WorkMilliseconds = 4.0;
		private static readonly int KeptHash = (RegrowthPlugin.GUID + ".copperkept").GetStableHashCode();
		private static readonly int UpHash = (RegrowthPlugin.GUID + ".copperup").GetStableHashCode();
		private static readonly int MeasuredHash = (RegrowthPlugin.GUID + ".coppermeasured").GetStableHashCode();
		private static readonly Dictionary<ZoneSystem.ZoneVegetation, float> gameOffsets = new Dictionary<ZoneSystem.ZoneVegetation, float>();
		private static readonly Queue<ZDOID> queue = new Queue<ZDOID>();
		private static Batch batch;
		private static Dictionary<Vector2s, List<Vector3>> builds;
		// What a zone generated now is measured with; null until Regrowth runs.
		private static Settings current;
		private static RegrowthState currentState;

		private sealed class Batch
		{
			public float margin;
			public int stamp;
			public bool dryRun;
			public int measured, enough, kept, postponed, failed, byLocation;
			public readonly List<float> up = new List<float>(), down = new List<float>();
		}

		/*
			True when the state changed and must be saved right away. When deposits moved, the world is saved at once too: until it is, a crash
			would leave them where they were while the state says they moved, and the next change would move them by the wrong height.
			nothingYet: the world has had no objects since the server started (a new one nobody has come to; a dedicated server generates zones
			only around players). Without objects otherwise, it is shutting down.
		*/
		public static bool Apply(Settings settings, RegrowthState state, HashSet<int> piecePrefabs, bool worldLoaded, bool nothingYet)
		{
			current = settings;
			currentState = state;
			bool changed = Raise(settings, state, piecePrefabs, worldLoaded, nothingYet);
			if (worldLoaded)
			{
				Enqueue(settings, state);
			}
			return changed;
		}

		public static void Stop()
		{
			queue.Clear();
			batch = null;
			builds = null;
			current = null;
			currentState = null;
			CopperReach.Reset();
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

		private static void Move(ZDO zdo, Vector3 at)
		{
			zdo.SetOwner(ZDOMan.GetSessionID());
			ZNetView view = ZNetScene.instance.FindInstance(zdo);
			if (view)
			{
				view.transform.position = at;
			}
			zdo.SetPosition(at);
		}

		// What a deposit was measured for: the margin, the height RaiseMeters gave it, and how it was measured.
		private static int Stamp(float margin, float raised)
		{
			return $"1 {margin:0.###} {raised:0.###}".GetStableHashCode();
		}

		// The deposits not measured for this margin and height yet (with it off, those it raised), for Work in the frames to come.
		private static void Enqueue(Settings settings, RegrowthState state)
		{
			float margin = settings.CopperOverDeepestHole.Value;
			int stamp = Stamp(margin, state.CopperRaised);
			bool any = queue.Count > 0;
			foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
			{
				int prefab = zdo.GetPrefab();
				if (prefab == CopperReach.ProxyHash)
				{
					CopperReach.NoteProxy(zdo);
				}
				else if (!any && (prefab == CopperReach.WholeHash || prefab == CopperReach.FracturedHash) && zdo.GetInt(MeasuredHash) != stamp
					&& (margin >= 0f || zdo.GetFloat(UpHash) != 0f))
				{
					queue.Enqueue(zdo.m_uid);
				}
			}
			if (!any && queue.Count > 0)
			{
				batch = new Batch { margin = margin, stamp = stamp, dryRun = settings.DryRun.Value };
				builds = null;
			}
		}

		// A few milliseconds of the deposits queued, every frame (Regrowth.Tick).
		public static void Work(HashSet<int> piecePrefabs)
		{
			if (queue.Count == 0 || batch == null)
			{
				return;
			}
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			while (queue.Count > 0 && watch.Elapsed.TotalMilliseconds < WorkMilliseconds)
			{
				ZDO zdo = ZDOMan.instance.GetZDO(queue.Dequeue());
				if (zdo != null)
				{
					One(zdo, piecePrefabs);
				}
			}
			if (queue.Count == 0)
			{
				Report(batch);
				batch = null;
				builds = null;
				CopperReach.Forget();
			}
		}

		private static void One(ZDO zdo, HashSet<int> piecePrefabs)
		{
			int prefab = zdo.GetPrefab();
			if (prefab != CopperReach.WholeHash && prefab != CopperReach.FracturedHash)
			{
				return;
			}
			if (zdo.GetBool(KeptHash))
			{
				batch.kept++;
				zdo.Set(MeasuredHash, batch.stamp);
				return;
			}
			Vector3 at = zdo.GetPosition();
			foreach (ZNetPeer peer in ZNet.instance.GetPeers())
			{
				if (Utils.DistanceXZ(peer.m_refPos, at) < PlayerRadius)
				{
					batch.postponed++;
					return;
				}
			}
			float up = zdo.GetFloat(UpHash, 0f), target = 0f;
			if (batch.margin >= 0f)
			{
				if (!CopperReach.Measure(zdo, batch.margin, up, out float need, out bool byLocation))
				{
					batch.failed++;
					return;
				}
				batch.measured++;
				if (byLocation)
				{
					batch.byLocation++;
				}
				target = Mathf.Max(0f, need);
			}
			float delta = target - up;
			if (Mathf.Abs(delta) < 0.01f)
			{
				batch.enough++;
				if (!batch.dryRun)
				{
					zdo.Set(MeasuredHash, batch.stamp);
				}
				return;
			}
			if (delta > 0f && NearBuild(at, builds ?? (builds = Builds(piecePrefabs))))
			{
				batch.kept++;
				if (!batch.dryRun)
				{
					zdo.Set(KeptHash, true);
					zdo.Set(MeasuredHash, batch.stamp);
				}
				return;
			}
			(delta > 0f ? batch.up : batch.down).Add(Mathf.Abs(delta));
			if (batch.dryRun)
			{
				return;
			}
			at.y += delta;
			Move(zdo, at);
			zdo.Set(UpHash, target);
			zdo.Set(MeasuredHash, batch.stamp);
		}

		private static void Report(Batch b)
		{
			string verb = b.dryRun ? "would be" : "";
			string Spread(List<float> list)
			{
				list.Sort();
				return list.Count == 0 ? "" : $" by {list[0]:0.0}–{list[list.Count - 1]:0.0} m, median {list[list.Count / 2]:0.0}";
			}
			RegrowthPlugin.Log.LogInfo(
				(b.dryRun ? "Dry run: " : "") + $"Copper deposits over the deepest hole ({(b.margin >= 0f ? $"{b.margin:0.##} m" : "off")}, CopperDeposits OverDeepestHole): "
				+ $"{b.measured} measured, {b.up.Count} {verb} raised{Spread(b.up)}, {b.down.Count} {verb} lowered back{Spread(b.down)}, {b.enough} high enough"
				+ $"; {b.byLocation} counted from a location's levelled ground, {b.kept} left for a player's building within {BuildRadius:0} m"
				+ (b.postponed > 0 ? $", {b.postponed} with a player within {PlayerRadius:0} m (at a later pass)" : "")
				+ (b.failed > 0 ? $", {b.failed} could not be measured" : "") + ".");
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
				if (zdo == null)
				{
					continue;
				}
				if (zdo.GetPrefab() == CopperReach.ProxyHash)
				{
					CopperReach.NoteProxy(zdo);
				}
				else if (zdo.GetPrefab() == CopperReach.WholeHash)
				{
					deposits.Add(zdo);
				}
			}
			if (deposits.Count == 0)
			{
				return;
			}
			int stamp = Stamp(margin, currentState.CopperRaised);
			List<string> done = new List<string>();
			foreach (ZDO zdo in deposits)
			{
				if (!CopperReach.Measure(zdo, margin, 0f, out float need, out bool byLocation))
				{
					continue;
				}
				float target = Mathf.Max(0f, need);
				if (target >= 0.01f)
				{
					Vector3 at = zdo.GetPosition();
					at.y += target;
					Move(zdo, at);
					zdo.Set(UpHash, target);
				}
				zdo.Set(MeasuredHash, stamp);
				done.Add($"{target:0.0} m" + (byLocation ? " (location's ground)" : ""));
			}
			CopperReach.Forget();
			RegrowthPlugin.Log.LogInfo($"Copper deposits in the new zone {zone.x},{zone.y}: raised {string.Join(", ", done)} more, for all of each to reach {margin:0.##} m over the deepest hole.");
		}

		private static Dictionary<Vector2s, List<Vector3>> Builds(HashSet<int> piecePrefabs)
		{
			Dictionary<Vector2s, List<Vector3>> builds = new Dictionary<Vector2s, List<Vector3>>();
			foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
			{
				if (zdo.GetLong(ZDOVars.s_creator, 0L) != 0L && piecePrefabs.Contains(zdo.GetPrefab()) && zdo.GetPosition().y < 3000f)
				{
					Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
					if (!builds.TryGetValue(zone, out List<Vector3> list))
					{
						builds[zone] = list = new List<Vector3>();
					}
					list.Add(zdo.GetPosition());
				}
			}
			return builds;
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
