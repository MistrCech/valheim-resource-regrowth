using System.Collections.Generic;
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
	*/
	internal static class CopperDeposits
	{
		private const string Prefab = "rock4_copper";
		private const float BuildRadius = 20f;
		private static readonly int KeptHash = (RegrowthPlugin.GUID + ".copperkept").GetStableHashCode();
		private static readonly Dictionary<ZoneSystem.ZoneVegetation, float> gameOffsets = new Dictionary<ZoneSystem.ZoneVegetation, float>();

		/*
			True when the state changed and must be saved right away. When deposits moved, the world is saved at once too: until it is, a crash
			would leave them where they were while the state says they moved, and the next change would move them by the wrong height.
			nothingYet: the world has had no objects since the server started (a new one nobody has come to; a dedicated server generates zones
			only around players). Without objects otherwise, it is shutting down.
		*/
		public static bool Apply(Settings settings, RegrowthState state, HashSet<int> piecePrefabs, bool worldLoaded, bool nothingYet)
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
			Dictionary<Vector2s, List<Vector3>> builds = new Dictionary<Vector2s, List<Vector3>>();
			foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
			{
				if (zdo.GetPrefab() == prefab)
				{
					deposits.Add(zdo);
				}
				else if (zdo.GetLong(ZDOVars.s_creator, 0L) != 0L && piecePrefabs.Contains(zdo.GetPrefab()) && zdo.GetPosition().y < 3000f)
				{
					Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
					if (!builds.TryGetValue(zone, out List<Vector3> list))
					{
						builds[zone] = list = new List<Vector3>();
					}
					list.Add(zdo.GetPosition());
				}
			}
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
				zdo.SetOwner(ZDOMan.GetSessionID());
				ZNetView view = ZNetScene.instance.FindInstance(zdo);
				if (view)
				{
					view.transform.position = at;
				}
				zdo.SetPosition(at);
				moved++;
			}
			state.CopperRaised = want;
			RegrowthPlugin.Log.LogInfo($"Copper deposits: {want:0.##} m higher than the game puts them: {moved} moved by {delta:0.##} m, {kept} left where they are for a player's building within {BuildRadius:0} m. World save requested.");
			ZNet.instance.Save(sync: false, saveOtherPlayerProfiles: true, waitForNextFrame: true);
			return true;
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
}
