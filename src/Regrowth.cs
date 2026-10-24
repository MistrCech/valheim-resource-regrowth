using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ResourceRegrowth
{
	/*
		Soft regrowth: most objects handled here stay in the world when they are used up, so
		regrowing them only changes their data. The exception is location objects (LocationObjects.cs):
		destroyed objects a location or village generated, which are created again where they stood.
		That is off unless switched on.

		- Surtling core stands and the other hide-when-picked pickables listed (the totem on a Fuling
		  totem pole): ZDO "picked" back to false.
		- Treasure chests: restocked from the chest's own loot table, the way
		  Container.AddDefaultItems fills it -- whatever was left in it is replaced. Vanilla would only
		  fill a chest on load when the loading machine already owns it, and ownership is handed out
		  on a slower timer than objects are created, so the plugin does not leave that to chance.
		  Whether a player took everything, some or nothing cannot be told from the chest, so a chest
		  counts as used once a player has been near it (or it is empty).
		- One-time creature spawners: once their creature is gone, the "spawned" connection is
		  cleared; vanilla then spawns again when the area is next loaded (CreatureSpawner).

		Everything a location holds regrows together (Locations.Together): a dungeon's interior
		with its entrance, a camp, a village, a ruin. Its clocks run per object as before, and as
		soon as one of them is due, every core taken, every creature killed and every chest in the
		location come back at once. A location regrows only when no player has been near any of it
		for IdleHours, none of it is within PlayerBuildRadius of a player-built piece or tombstone,
		and none of it is in use: no connected player owns it and the server does not have it loaded
		(Dedicated Simulation loads zones around players on the server). Changing an object that is
		loaded somewhere would not show until it is reloaded, or would act at once (a spawner).
	*/
	internal class Regrowth
	{
		private enum Kind { Core, Chest, Spawner, Terrain, LocationObject }

		private class Tally
		{
			public int depleted, waiting, blocked, unsupported, fell, inUse, regrown, logged, extra;
		}

		// ZDO instances are pooled and reused after an object is destroyed, so a candidate keeps
		// the id and prefab it had when collected and is dropped if either no longer matches.
		private struct Candidate
		{
			public Kind kind;
			public ZDO zdo;
			public ZDOID uid;
			public int prefab;
		}

		private const float PresenceIntervalSeconds = 10f;
		private const int ScanBatch = 4096;

		// The running regrowth with location objects on, for the WearNTear.Destroy patch.
		internal static Regrowth Active;
		// On a building piece it put back: when (UTC ticks). A piece that falls with it is told from one that stood there all along.
		internal static readonly int PutBackHash = (RegrowthPlugin.GUID + ".putback").GetStableHashCode();
		// On it too: its slot's state key. A piece that snaps to the ground no longer stands at its slot's height.
		internal static readonly int SlotKeyHash = (RegrowthPlugin.GUID + ".slot").GetStableHashCode();

		private readonly MonoBehaviour host;
		private readonly Settings settings;
		private RegrowthState state;
		private readonly Dictionary<int, bool> corePrefabs = new Dictionary<int, bool>(); // prefab -> picked by default
		private readonly Dictionary<int, float> coreHours = new Dictionary<int, float>(); // prefab -> its own AfterHours ("name:hours")
		private readonly Dictionary<int, Container> chestPrefabs = new Dictionary<int, Container>();
		private readonly HashSet<int> spawnerPrefabs = new HashSet<int>();
		private readonly HashSet<int> generatorPrefabs = new HashSet<int>();
		private readonly HashSet<int> piecePrefabs = new HashSet<int>();
		private readonly HashSet<int> holderPrefabs = new HashSet<int>(); // what can hold a location object up (LocationObjects.Holds)
		private readonly HashSet<int> tombstonePrefabs = new HashSet<int>();
		private readonly int terrainPrefab = Terrain.CompilerPrefab.GetStableHashCode();
		private Dictionary<int, float> protectorPrefabs = new Dictionary<int, float>();
		private static readonly int WorkbenchHash = "piece_workbench".GetStableHashCode();
		private readonly Dictionary<int, string> prefabNames = new Dictionary<int, string>();
		private LocationObjects locationObjects;
		private ZoneReset zones;
		private float presenceTimer;
		private float passTimer;
		private Coroutine running;

		public Regrowth(MonoBehaviour host, Settings settings)
		{
			this.host = host;
			this.settings = settings;
		}

		public void Start()
		{
			BuildPrefabSets();
			protectorPrefabs = Terrain.ProtectorPrefabs(settings.TerrainProtectMultiplier.Value, out List<string> protectorNames);
			RegrowthPlugin.Log.LogInfo($"Terrain{(settings.TerrainEnabled.Value ? "" : " (off)")}: ground is protected around {protectorPrefabs.Count} kinds of base object and ward: {string.Join(", ", protectorNames)}; a workbench keeps the ground within {settings.TerrainWorkbenchRadius.Value:0} m at least (Terrain WorkbenchRadius)");
			if (settings.LocationObjectsEnabled.Value)
			{
				locationObjects = new LocationObjects(SplitList(settings.LocationObjectsPrefabs.Value));
			}
			state = new RegrowthState(StatePath());
			DateTime now = Now();
			state.Load(now);
			if (!state.PresenceKnownLoaded)
			{
				// Older versions kept a zone's presence for IdleHours only.
				state.PresenceKnownSince = now.AddHours(-Math.Max(0f, settings.IdleHours.Value));
			}
			if (locationObjects != null)
			{
				Active = this;
				if (!RegrowthPlugin.DestroyPatched)
				{
					RegrowthPlugin.Log.LogWarning(RegrowthPlugin.DestroyPatchFailed
						? "LocationObjects: WearNTear.Destroy could not be patched at startup (see the error above); a piece put back that falls is put back again."
						: "LocationObjects: switched on after the server started; until a restart a piece put back that falls is put back again (the game method that tells is patched at startup only).");
				}
			}
			if (settings.ZonesEnabled.Value)
			{
				if (RegrowthPlugin.SpawnLocationPatched)
				{
					zones = new ZoneReset(settings, state, Name, WorldLoaded);
				}
				else
				{
					RegrowthPlugin.Log.LogWarning(RegrowthPlugin.SpawnLocationPatchFailed
						? "Zones: ZoneSystem.SpawnLocation could not be patched at startup (see the error above); no zone is generated again."
						: "Zones: switched on after the server started; they need a restart (the game method that keeps a location's rotation is patched at startup only).");
				}
			}
			passTimer = 60f;
			RegrowthPlugin.Log.LogInfo(
				$"Active{(settings.DryRun.Value ? " (dry run: nothing will be changed)" : "")}. "
				+ $"Pickables: {string.Join(", ", corePrefabs.Keys.Select(h => Name(h) + (coreHours.TryGetValue(h, out float own) ? $" ({own:0.#} h)" : "")))}, treasure chests: {chestPrefabs.Count}, one-time spawners: {spawnerPrefabs.Count}, "
				+ $"location objects: {(locationObjects != null ? settings.LocationObjectsPrefabs.Value : "off")}. "
				+ $"Tracking since {state.FirstRun.ToLocalTime():yyyy-MM-dd HH:mm} ({Hours(state.FirstRun, now):0.#} h ago). State: {state.FilePath}");
		}

		public void Stop()
		{
			if (running != null)
			{
				host.StopCoroutine(running);
				running = null;
			}
			zones?.Stop();
			if (Active == this)
			{
				Active = null;
			}
			SaveState();
		}

		public void Tick(float dt)
		{
			presenceTimer -= dt;
			if (presenceTimer <= 0f)
			{
				presenceTimer = PresenceIntervalSeconds;
				RecordPresence();
			}
			passTimer -= dt;
			if (passTimer <= 0f && running == null)
			{
				bool wasDryRun = settings.DryRun.Value;
				settings.Reload();
				if (settings.DryRun.Value != wasDryRun)
				{
					RegrowthPlugin.Log.LogInfo(settings.DryRun.Value ? "Dry run switched on: nothing will be changed." : "Dry run switched off: regrowth is live.");
				}
				passTimer = Mathf.Max(1f, settings.CheckIntervalMinutes.Value) * 60f;
				running = host.StartCoroutine(Pass());
			}
		}

		private static DateTime Now()
		{
			return DateTime.UtcNow;
		}

		private static double Hours(DateTime from, DateTime to)
		{
			return (to - from).TotalHours;
		}

		private static string StatePath()
		{
			World world = ZNet.World;
			return Path.Combine(SaveSystem.GetWorldsSaveRootPath(world.m_fileSource), world.m_name + ".regrowth.txt");
		}

		private void SaveState()
		{
			try
			{
				state?.Save();
			}
			catch (Exception e)
			{
				RegrowthPlugin.Log.LogWarning($"Could not save state to {state.FilePath}: {e.Message}");
			}
		}

		private void BuildPrefabSets()
		{
			// "name" or "name:hours" -- a prefab with its own hours instead of AfterHours.
			Dictionary<string, float> cores = new Dictionary<string, float>();
			foreach (string entry in SplitList(settings.CorePrefabs.Value))
			{
				int colon = entry.IndexOf(':');
				string name = colon < 0 ? entry : entry.Substring(0, colon).Trim();
				float hours = float.NaN;
				if (colon >= 0 && (!float.TryParse(entry.Substring(colon + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hours) || hours < 0f))
				{
					RegrowthPlugin.Log.LogWarning($"[SurtlingCores] Prefabs: \"{entry}\" has no hours after the colon; {name} takes AfterHours.");
					hours = float.NaN;
				}
				cores[name] = hours;
			}
			HashSet<string> excludedChests = SplitList(settings.ChestsExclude.Value);
			HashSet<string> excludedSpawners = SplitList(settings.SpawnersExclude.Value);
			foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
			{
				if (!prefab)
				{
					continue;
				}
				int hash = prefab.name.GetStableHashCode();
				prefabNames[hash] = prefab.name;
				if (prefab.GetComponent<Piece>())
				{
					piecePrefabs.Add(hash);
				}
				if (LocationObjects.Holds(prefab))
				{
					holderPrefabs.Add(hash);
				}
				if (prefab.GetComponent<TombStone>())
				{
					tombstonePrefabs.Add(hash);
				}
				if (cores.TryGetValue(prefab.name, out float ownHours))
				{
					Pickable pickable = prefab.GetComponent<Pickable>();
					if (pickable && pickable.m_hideWhenPicked && pickable.m_respawnTimeMinutes <= 0f)
					{
						corePrefabs[hash] = pickable.m_defaultPicked;
						if (!float.IsNaN(ownHours))
						{
							coreHours[hash] = ownHours;
						}
					}
					else
					{
						RegrowthPlugin.Log.LogWarning($"{prefab.name} is not a non-respawning hide-when-picked pickable; ignored.");
					}
				}
				if (!excludedChests.Contains(prefab.name))
				{
					// A treasure chest by its name, or any other container of the world that comes with a loot table of its own -- a shipwreck's chest, a Deep North granary,
					// a chest of Morkhalla. What the players build has no loot table (and a Piece component is no sign of it: the treasure chests have one too).
					Container container = prefab.GetComponent<Container>();
					if (container && !container.m_rootObjectOverride && container.m_defaultItems.m_drops.Count > 0)
					{
						chestPrefabs[hash] = container;
					}
					else if (prefab.name.StartsWith("TreasureChest_", StringComparison.Ordinal))
					{
						RegrowthPlugin.Log.LogInfo($"{prefab.name} has no container with a loot table of its own; ignored.");
					}
				}
				if (prefab.GetComponent<DungeonGenerator>())
				{
					generatorPrefabs.Add(hash);
				}
				CreatureSpawner spawner = prefab.GetComponent<CreatureSpawner>();
				if (spawner && spawner.m_respawnTimeMinuts <= 0f && !excludedSpawners.Contains(prefab.name))
				{
					spawnerPrefabs.Add(hash);
				}
			}
		}

		private static HashSet<string> SplitList(string value)
		{
			return new HashSet<string>(value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
		}

		private void RecordPresence()
		{
			DateTime now = Now();
			int radius = Mathf.Max(0, settings.PresenceRadiusZones.Value);
			foreach (ZNetPeer peer in ZNet.instance.GetPeers())
			{
				// Only players in the world: a peer still connecting or loading has no real position yet.
				if (!peer.IsReady() || peer.m_characterID.IsNone())
				{
					continue;
				}
				Vector2s zone = ZoneSystem.GetZone(peer.GetRefPos());
				zones?.MarkVisited(zone, now);
				for (int dy = -radius; dy <= radius; dy++)
				{
					for (int dx = -radius; dx <= radius; dx++)
					{
						state.MarkSeen(new Vector2s(zone.x + dx, zone.y + dy), now);
					}
				}
			}
		}

		// On shutdown ZDOMan empties its objects before the plugin is told; a pass must then not
		// conclude that nothing is depleted any more and throw the clocks away.
		private static bool WorldLoaded()
		{
			return ZNet.instance && ZNet.instance.enabled && ZDOMan.instance != null && ZDOMan.instance.m_objectsByID.Count > 0;
		}

		private IEnumerator Pass()
		{
			// Whatever goes wrong inside, the next pass must still be able to start.
			try
			{
				DateTime now = Now();
				bool dryRun = settings.DryRun.Value;
				// Wall time includes the frames in between; work time is only what this pass itself took.
				System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
				System.Diagnostics.Stopwatch work = System.Diagnostics.Stopwatch.StartNew();
				int frames = 1;
				List<ZDO> zdos = new List<ZDO>(ZDOMan.instance.m_objectsByID.Values);
				Dictionary<Vector2s, List<Vector3>> blockers = new Dictionary<Vector2s, List<Vector3>>();
				Dictionary<Vector2s, List<Terrain.Protector>> protectors = new Dictionary<Vector2s, List<Terrain.Protector>>();
				List<Candidate> candidates = new List<Candidate>();
				List<ZDO> sources = new List<ZDO>();
				// Wanted objects standing, by zone and prefab: a village's slots are hundreds of pieces among hundreds of others.
				Dictionary<(Vector2s, int), List<ZDO>> standing = new Dictionary<(Vector2s, int), List<ZDO>>();
				// Fractured remains of wanted objects (a hit pile or rock leaves "<name>_frac"), by zone.
				Dictionary<Vector2s, List<ZDO>> remains = new Dictionary<Vector2s, List<ZDO>>();
				// What the world generated that can hold a location object up (LocationObjects.Holds, no creator), by zone.
				Dictionary<Vector2s, List<ZDO>> worldPieces = new Dictionary<Vector2s, List<ZDO>>();
				// Dungeon generators (dungeon interiors, camps, villages): what an object inside a dungeon belongs to.
				Dictionary<Vector2s, List<ZDO>> generators = new Dictionary<Vector2s, List<ZDO>>();

				// 1. One pass over every object: note player builds and tombstones, collect candidates.
				zones?.Clear();
				for (int i = 0; i < zdos.Count; i++)
				{
					ZDO zdo = zdos[i];
					int prefab = zdo.GetPrefab();
					zones?.Note(zdo, prefab);
					if (tombstonePrefabs.Contains(prefab)
						|| (piecePrefabs.Contains(prefab) && zdo.GetLong(ZDOVars.s_creator, 0L) != 0L))
					{
						AddBlocker(blockers, zdo.GetPosition());
					}
					else if (locationObjects != null && holderPrefabs.Contains(prefab))
					{
						Vector2s pieceZone = ZoneSystem.GetZone(zdo.GetPosition());
						if (!worldPieces.TryGetValue(pieceZone, out List<ZDO> pieceList))
						{
							worldPieces[pieceZone] = pieceList = new List<ZDO>();
						}
						pieceList.Add(zdo);
					}
					if (generatorPrefabs.Contains(prefab))
					{
						Vector2s generatorZone = ZoneSystem.GetZone(zdo.GetPosition());
						if (!generators.TryGetValue(generatorZone, out List<ZDO> generatorList))
						{
							generators[generatorZone] = generatorList = new List<ZDO>();
						}
						generatorList.Add(zdo);
					}
					if (protectorPrefabs.TryGetValue(prefab, out float protectRadius) && zdo.GetLong(ZDOVars.s_creator, 0L) != 0L && zdo.GetBool(ZDOVars.s_enabled, true))
					{
						// A workbench keeps the ground around it as far as a zone is kept from being generated again, not only as far as its own radius.
						if (prefab == WorkbenchHash)
						{
							protectRadius = Mathf.Max(protectRadius, settings.TerrainWorkbenchRadius.Value);
						}
						AddProtector(protectors, zdo.GetPosition(), protectRadius);
					}
					if (settings.CoresEnabled.Value && corePrefabs.ContainsKey(prefab))
					{
						candidates.Add(new Candidate { kind = Kind.Core, zdo = zdo, uid = zdo.m_uid, prefab = prefab });
					}
					else if (settings.ChestsEnabled.Value && chestPrefabs.ContainsKey(prefab))
					{
						candidates.Add(new Candidate { kind = Kind.Chest, zdo = zdo, uid = zdo.m_uid, prefab = prefab });
					}
					else if (settings.SpawnersEnabled.Value && spawnerPrefabs.Contains(prefab))
					{
						candidates.Add(new Candidate { kind = Kind.Spawner, zdo = zdo, uid = zdo.m_uid, prefab = prefab });
					}
					else if (settings.TerrainEnabled.Value && prefab == terrainPrefab)
					{
						candidates.Add(new Candidate { kind = Kind.Terrain, zdo = zdo, uid = zdo.m_uid, prefab = prefab });
					}
					else if (locationObjects != null && locationObjects.IsSource(prefab))
					{
						sources.Add(zdo);
					}
					else if (locationObjects != null && settings.LocationObjectsClearRemains.Value && locationObjects.IsRemains(prefab))
					{
						Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
						if (!remains.TryGetValue(zone, out List<ZDO> list))
						{
							remains[zone] = list = new List<ZDO>();
						}
						list.Add(zdo);
					}
					// Not in the chain above: a pickable can be in [SurtlingCores] Prefabs too (hanging_hairstrands). Up to 0.8.5 such a
					// one never counted as standing, so every place of it looked empty and got one more each time its hours were up.
					if (locationObjects != null && locationObjects.IsWanted(prefab))
					{
						(Vector2s, int) at = (ZoneSystem.GetZone(zdo.GetPosition()), prefab);
						if (!standing.TryGetValue(at, out List<ZDO> list))
						{
							standing[at] = list = new List<ZDO>();
						}
						list.Add(zdo);
					}
					if (i % ScanBatch == ScanBatch - 1)
					{
						frames++;
						work.Stop();
						yield return null;
						work.Start();
					}
				}
				if (!WorldLoaded())
				{
					yield break;
				}
				// Where location objects stood: new locations and rooms only, at most one prefab read per frame.
				int loadedPrefabs = 0;
				if (locationObjects != null)
				{
					IEnumerator refresh = locationObjects.Refresh(sources, n => loadedPrefabs += n);
					while (refresh.MoveNext())
					{
						frames++;
						work.Stop();
						yield return null;
						work.Start();
					}
					if (!WorldLoaded())
					{
						yield break;
					}
				}

				// 2. Decide for each candidate, all in this frame.
				Dictionary<Kind, Tally> tallies = new Dictionary<Kind, Tally>
				{
					{ Kind.Core, new Tally() }, { Kind.Chest, new Tally() }, { Kind.Spawner, new Tally() }, { Kind.Terrain, new Tally() },
				};
				if (locationObjects != null)
				{
					tallies[Kind.LocationObject] = new Tally();
				}
				this.protectors = protectors;
				HashSet<string> stillDepleted = new HashSet<string>();
				HashSet<string> regrown = new HashSet<string>();
				int errors = 0;
				Dictionary<string, Group> groups = new Dictionary<string, Group>();
				Dictionary<Vector2s, List<Place>> places = Places();
				foreach (Candidate candidate in candidates)
				{
					try
					{
						if (ZDOMan.instance.GetZDO(candidate.uid) != candidate.zdo || candidate.zdo.GetPrefab() != candidate.prefab)
						{
							continue;
						}
						if (candidate.kind == Kind.Terrain)
						{
							if (IsDepleted(candidate.kind, candidate.zdo, null, now))
							{
								DecideTerrain(candidate.zdo, now, dryRun, blockers, tallies[candidate.kind], stillDepleted);
							}
							continue;
						}
						GroupOf(candidate, groups, places, generators).members.Add(candidate);
					}
					catch (Exception e)
					{
						if (errors++ == 0)
						{
							RegrowthPlugin.Log.LogWarning($"Skipped {Describe(candidate.kind)} {Name(candidate.prefab)}: {e}");
						}
					}
				}
				int locationsRegrown = 0;
				s_waitingLogged = 0;
				foreach (Group group in groups.Values)
				{
					try
					{
						if (DecideGroup(group, now, dryRun, blockers, tallies, stillDepleted, regrown) && group.name != null)
						{
							locationsRegrown++;
						}
					}
					catch (Exception e)
					{
						if (errors++ == 0)
						{
							RegrowthPlugin.Log.LogWarning($"Skipped {group.name ?? "an object"} at {group.position.x:0},{group.position.z:0}: {e}");
						}
					}
				}
				if (locationObjects != null)
				{
					remainsGone.Clear();
					extraGone.Clear();
					foreach (LocationObjects.Slot slot in locationObjects.AllSlots())
					{
						try
						{
							DecideSlot(slot, now, dryRun, blockers, standing, remains, worldPieces, tallies[Kind.LocationObject], stillDepleted, regrown);
						}
						catch (Exception e)
						{
							if (errors++ == 0)
							{
								RegrowthPlugin.Log.LogWarning($"Skipped {Describe(Kind.LocationObject)} {Name(slot.prefab)} at {slot.position.x:0},{slot.position.z:0}: {e}");
							}
						}
					}
				}
				// Forgotten only now: objects that share a key (same prefab and spot) share the clock too.
				foreach (string key in regrown)
				{
					state.Forget(key);
					stillDepleted.Remove(key);
				}
				state.KeepOnly(stillDepleted);
				// 3. Zones generated again as the world first made them, one per frame.
				ZoneReset.Tally zoneTally = null;
				if (zones != null && WorldLoaded())
				{
					zoneTally = new ZoneReset.Tally();
					IEnumerator reset = zones.Run(now, blockers, zoneTally);
					while (reset.MoveNext())
					{
						frames++;
						work.Stop();
						yield return null;
						work.Start();
					}
					if (!WorldLoaded())
					{
						yield break;
					}
				}
				bool zonesChanged = zoneTally != null && zoneTally.reset > 0 && !settings.ZonesDryRun.Value && !dryRun;
				bool extraRemoved = locationObjects != null && tallies[Kind.LocationObject].extra > 0 && !dryRun;
				// The world changed and the clocks were reset; a crash before the next autosave would
				// lose the one and keep the other, so save now (the same call as the autosave).
				if ((regrown.Count > 0 || terrainStepped || zonesChanged || extraRemoved) && ZNet.instance.EnoughDiskSpaceAvailable(out bool _))
				{
					ZNet.instance.Save(sync: false, saveOtherPlayerProfiles: true, waitForNextFrame: true);
				}
				// Kept for the longer of the idle times (zones' too, also while they are off, so that switching them on finds the history);
				// what is forgotten moves the time from which presence is known completely.
				DateTime cutoff = now.AddHours(-Math.Max(Math.Max(0f, settings.IdleHours.Value), Math.Max(0f, settings.ZonesIdleHours.Value)));
				state.ForgetZonesSeenBefore(cutoff);
				if (cutoff > state.PresenceKnownSince)
				{
					state.PresenceKnownSince = cutoff;
				}

				RegrowthPlugin.Log.LogInfo(
					$"Pass{(dryRun ? " (dry run)" : "")}: "
					+ string.Join("; ", tallies.Select(t =>
						t.Key == Kind.Terrain
							? $"terrain zones {t.Value.depleted} modified ({t.Value.waiting} waiting, {t.Value.blocked} all protected, {t.Value.inUse} in use, {t.Value.regrown} {(dryRun ? "would step" : "stepped")})"
							: t.Key == Kind.LocationObject
							? $"location objects {t.Value.depleted} missing ({t.Value.waiting} waiting, {t.Value.blocked} near player builds, {t.Value.unsupported} with what held them gone, "
								+ $"{(t.Value.fell > 0 ? $"{t.Value.fell} fell after being put back, " : "")}{t.Value.inUse} in use, "
								+ $"{t.Value.regrown} {(dryRun ? "would put back" : "put back")}{(t.Value.extra > 0 ? $", {t.Value.extra} extra {(dryRun ? "would go" : "gone")}" : "")})"
							: $"{Describe(t.Key)}s {t.Value.depleted} depleted ({t.Value.waiting} waiting, {t.Value.blocked} near player builds, {t.Value.inUse} in use, "
							+ $"{t.Value.regrown} {(dryRun ? "would regrow" : "regrown")})"))
					+ (locationObjects != null ? $" ({locationObjects.SourceCount} locations and rooms known{(loadedPrefabs > 0 ? $", {loadedPrefabs} prefab(s) read this pass" : "")})" : "")
					+ (settings.LocationsTogether.Value ? $"; {locationsRegrown} location(s) {(dryRun ? "would regrow" : "regrew")} as a whole" : "")
					+ (zoneTally != null ? $"; zones {zoneTally.visited} visited ({zoneTally.waiting} waiting, {zoneTally.due} due, {zoneTally.blocked} kept{(zoneTally.why.Count > 0 ? ": " + string.Join(", ", zoneTally.why.OrderByDescending(w => w.Value).Select(w => $"{w.Value} {w.Key}")) : "")}, "
						+ $"{zoneTally.reset} {(settings.ZonesDryRun.Value || dryRun ? "would be generated again" : "generated again")}{(zoneTally.failed > 0 ? $", {zoneTally.failed} failed and undone" : "")})" : "")
					+ $". Scanned {zdos.Count} objects: {work.ElapsedMilliseconds} ms of work spread over {frames} frames ({wall.ElapsedMilliseconds} ms wall)."
					+ (errors > 0 ? $" {errors} object(s) skipped after errors." : "")
					+ (regrown.Count > 0 || terrainStepped || zonesChanged || extraRemoved ? " World save requested." : ""));
				terrainStepped = false;
				if (zones != null && zones.RefreshDone)
				{
					zones.RefreshDone = false;
					settings.ZonesRefreshNow.Value = false;
					RegrowthPlugin.Log.LogInfo("Zones: RefreshNow is done and switched off in the config; the zones and the ground it left follow the usual clocks again.");
				}
				SaveState();
			}
			finally
			{
				running = null;
			}
		}

		private Dictionary<Vector2s, List<Terrain.Protector>> protectors = new Dictionary<Vector2s, List<Terrain.Protector>>();

		// What one location holds (or a lone object), decided as one.
		private class Group
		{
			public string name;
			public Vector3 position;
			public bool dungeon;
			public readonly List<Candidate> members = new List<Candidate>();
		}

		private struct Place
		{
			public Vector2s zone;
			public string name;
			public Vector3 position;
			public float radius, interior;
		}

		// Every location the world has placed, by the zone its centre is in.
		private static Dictionary<Vector2s, List<Place>> Places()
		{
			Dictionary<Vector2s, List<Place>> places = new Dictionary<Vector2s, List<Place>>();
			foreach (KeyValuePair<Vector2s, ZoneSystem.LocationInstance> entry in ZoneSystem.instance.m_locationInstances)
			{
				ZoneSystem.LocationInstance instance = entry.Value;
				if (!instance.m_placed || instance.m_location == null)
				{
					continue;
				}
				Vector2s zone = ZoneSystem.GetZone(instance.m_position);
				if (!places.TryGetValue(zone, out List<Place> list))
				{
					places[zone] = list = new List<Place>();
				}
				list.Add(new Place
				{
					zone = entry.Key, name = instance.m_location.m_prefabName, position = instance.m_position,
					radius = instance.m_location.m_exteriorRadius, interior = instance.m_location.m_interiorRadius,
				});
			}
			return places;
		}

		/*
			Which location an object belongs to. Inside a dungeon (5000 m up) that is the dungeon its
			nearest dungeon generator built (rooms lie within about 60 m of it), and the location at the
			same spot on the surface, so a dungeon's interior and its entrance are one; an interior the
			location is built with whole, without a generator (troll and bear caves), is the location
			whose interior radius covers it. On the surface it is the nearest location whose ground the
			object stands on. An object of no location is a group of its own.
		*/
		private Group GroupOf(Candidate candidate, Dictionary<string, Group> groups, Dictionary<Vector2s, List<Place>> places, Dictionary<Vector2s, List<ZDO>> generators)
		{
			Vector3 position = candidate.zdo.GetPosition();
			bool inside = position.y > 3000f;
			string key = null, name = null;
			Vector3 at = position;
			if (settings.LocationsTogether.Value)
			{
				ZDO generator = inside ? Nearest(generators, position, 96f) : null;
				{
					Vector3 anchor = generator != null ? generator.GetPosition() : position;
					if (NearestPlace(places, anchor, generator != null ? Reach.Generator : inside ? Reach.Interior : Reach.Surface, out Place place))
					{
						key = $"location {place.zone.x},{place.zone.y}";
						name = place.name;
						at = place.position;
					}
					else if (generator != null)
					{
						key = $"dungeon {generator.m_uid}";
						name = Name(generator.GetPrefab());
						at = anchor;
					}
				}
			}
			if (key == null)
			{
				key = Key(candidate.kind, candidate.zdo);
			}
			if (!groups.TryGetValue(key, out Group group))
			{
				groups[key] = group = new Group { name = name, position = at };
			}
			group.dungeon |= inside;
			return group;
		}

		private int s_waitingLogged;

		// "3 surtling core stands (2 used up), 4 treasure chests (4 used), 9 creature spawners (5 used up)"
		private static string Summary(List<Candidate> members, List<Candidate> depleted)
		{
			return string.Join(", ", members.GroupBy(m => m.kind).Select(g =>
			{
				int used = depleted.Count(d => d.kind == g.Key);
				return $"{g.Count()} {Describe(g.Key)}{(g.Count() == 1 ? "" : "s")} ({used} {(g.Key == Kind.Chest ? "used" : "used up")})";
			}));
		}

		private static ZDO Nearest(Dictionary<Vector2s, List<ZDO>> byZone, Vector3 position, float within)
		{
			ZDO best = null;
			float bestDistance = within * within;
			Vector2s zone = ZoneSystem.GetZone(position);
			int zones = Mathf.CeilToInt(within / 64f);
			for (int dy = -zones; dy <= zones; dy++)
			{
				for (int dx = -zones; dx <= zones; dx++)
				{
					if (!byZone.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out List<ZDO> list))
					{
						continue;
					}
					foreach (ZDO zdo in list)
					{
						float distance = (zdo.GetPosition() - position).sqrMagnitude;
						if (distance <= bestDistance)
						{
							bestDistance = distance;
							best = zdo;
						}
					}
				}
			}
			return best;
		}

		private enum Reach { Surface, Interior, Generator }

		// A dungeon generator stands above its location's centre; anything else within its location's (interior) radius and a little.
		private static bool NearestPlace(Dictionary<Vector2s, List<Place>> places, Vector3 position, Reach reach, out Place found)
		{
			found = default;
			bool any = false;
			float bestDistance = float.MaxValue;
			Vector2s zone = ZoneSystem.GetZone(position);
			for (int dy = -2; dy <= 2; dy++)
			{
				for (int dx = -2; dx <= 2; dx++)
				{
					if (!places.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out List<Place> list))
					{
						continue;
					}
					foreach (Place place in list)
					{
						float x = place.position.x - position.x;
						float z = place.position.z - position.z;
						float distance = Mathf.Sqrt(x * x + z * z);
						float within = reach == Reach.Generator ? 64f
							: reach == Reach.Interior ? (place.interior > 0f ? place.interior + 16f : -1f)
							: Mathf.Max(place.radius, 16f) + 8f;
						if (distance <= within && distance < bestDistance)
						{
							bestDistance = distance;
							found = place;
							any = true;
						}
					}
				}
			}
			return any;
		}

		/*
			A location as one: each used-up object keeps its own clock (state "depleted" entries, as
			before), and as soon as one of them has waited its AfterHours, everything used up in the
			location comes back together with every chest in it -- if nobody has been near any of it
			for IdleHours, none of it is near a player's build and none of it is in use. Returns
			whether it regrew (or would have, in a dry run).
		*/
		private bool DecideGroup(Group group, DateTime now, bool dryRun, Dictionary<Vector2s, List<Vector3>> blockers,
			Dictionary<Kind, Tally> tallies, HashSet<string> stillDepleted, HashSet<string> regrown)
		{
			List<Candidate> depleted = new List<Candidate>();
			string due = null;
			double dueBy = double.MinValue;
			double soonest = double.MinValue;
			foreach (Candidate member in group.members)
			{
				string key = Key(member.kind, member.zdo);
				if (!IsDepleted(member.kind, member.zdo, key, now))
				{
					continue;
				}
				depleted.Add(member);
				tallies[member.kind].depleted++;
				stillDepleted.Add(key);
				double hours = Hours(state.DepletedSince(key, now), now);
				double over = hours - AfterHours(member.kind, member.prefab);
				soonest = Math.Max(soonest, over);
				if (over >= 0 && over > dueBy)
				{
					dueBy = over;
					due = $"{Describe(member.kind)} {Name(member.prefab)} {(member.kind == Kind.Chest ? "used" : "used up")} {hours:0.##} h ago";
				}
			}
			if (depleted.Count == 0)
			{
				return false;
			}
			if (due == null)
			{
				dueBy = soonest;
			}
			double idle = double.MaxValue;
			foreach (Candidate member in group.members)
			{
				idle = Math.Min(idle, Hours(state.LastSeen(ZoneSystem.GetZone(member.zdo.GetPosition())), now));
			}
			if (due == null || idle < settings.IdleHours.Value)
			{
				depleted.ForEach(member => tallies[member.kind].waiting++);
				if (dryRun && s_waitingLogged++ < settings.LogDetailsPerPass.Value)
				{
					RegrowthPlugin.Log.LogInfo(
						$"Waiting: {group.name ?? "a lone object"} at {group.position.x:0},{group.position.z:0}{(group.dungeon ? " (dungeon)" : "")}: {Summary(group.members, depleted)}; "
						+ (due != null ? $"due ({due})" : $"due in {-dueBy:0.#} h") + $", area idle {(idle > 1e6 ? "always" : idle.ToString("0.##") + " h")} (needs {settings.IdleHours.Value:0.#}).");
				}
				return false;
			}
			if (group.members.Any(member => IsBlocked(blockers, member.zdo.GetPosition())))
			{
				depleted.ForEach(member => tallies[member.kind].blocked++);
				return false;
			}
			if (group.members.Any(member => InUse(member.zdo)))
			{
				depleted.ForEach(member => tallies[member.kind].inUse++);
				return false;
			}
			Dictionary<Kind, int> counts = new Dictionary<Kind, int>();
			foreach (Candidate member in group.members)
			{
				// Every chest of the location is restocked, used or not: what a player left in it cannot be told apart.
				if (!depleted.Contains(member) && !(member.kind == Kind.Chest && IsRestockable(member.zdo)))
				{
					continue;
				}
				tallies[member.kind].regrown++;
				counts[member.kind] = (counts.TryGetValue(member.kind, out int n) ? n : 0) + 1;
				if (!dryRun)
				{
					Apply(member.kind, member.zdo);
					regrown.Add(Key(member.kind, member.zdo));
				}
			}
			Tally first = tallies[depleted[0].kind];
			if (first.logged < settings.LogDetailsPerPass.Value)
			{
				first.logged++;
				string what = string.Join(", ", counts.Select(c => $"{c.Value} {Describe(c.Key)}{(c.Value == 1 ? "" : "s")}"));
				RegrowthPlugin.Log.LogInfo(
					$"{(dryRun ? "Would regrow" : "Regrew")} {group.name ?? "a lone object"} at {group.position.x:0},{group.position.z:0}{(group.dungeon ? " (dungeon)" : "")}: {what}; "
					+ $"due: {due}, area idle {idle:0.##} h.");
			}
			return true;
		}

		/*
			ZDO ids are handed out afresh on every world load (ZDO.Load), so they cannot identify an
			object across restarts. Everything handled here stays where the world put it, so prefab and
			position do.
		*/
		private string Key(Kind kind, ZDO zdo)
		{
			Vector3 p = zdo.GetPosition();
			return string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:0.0}:{3:0.0}:{4:0.0}",
				kind, Name(zdo.GetPrefab()).Replace(' ', '_'), p.x, p.y, p.z);
		}

		/*
			Terrain has its own clocks: AfterHours since the zone's ground was first seen modified, then
			StepHours between steps ("step" entries in the state). A zone is never "regrown" as a whole;
			it is stepped while anything is left, and forgotten once nothing is.
		*/
		private void DecideTerrain(ZDO zdo, DateTime now, bool dryRun, Dictionary<Vector2s, List<Vector3>> blockers, Tally tally, HashSet<string> stillDepleted)
		{
			tally.depleted++;
			string key = Key(Kind.Terrain, zdo);
			string stepKey = "step" + key;
			stillDepleted.Add(key);
			// The time of the last step is kept whatever this pass decides: it used to be dropped whenever the zone was not idle, so that the first
			// pass after the players had left stepped the ground again at once, however short a time ago the last step was (StepHours).
			if (state.HasKey(stepKey))
			{
				stillDepleted.Add(stepKey);
			}
			Vector3 position = zdo.GetPosition();
			double modified = Hours(state.DepletedSince(key, now), now);
			double idle = Hours(state.LastSeen(ZoneSystem.GetZone(position)), now);
			// RefreshNow: every modified zone nobody has been near for half an hour is stepped back at once, without the clocks.
			bool refresh = settings.ZonesRefreshNow.Value && settings.ZonesEnabled.Value && !settings.ZonesDryRun.Value && !dryRun;
			if (refresh ? idle < 0.5 : (modified < settings.TerrainAfterHours.Value || idle < settings.IdleHours.Value))
			{
				tally.waiting++;
				return;
			}
			if (!refresh && state.HasKey(stepKey))
			{
				stillDepleted.Add(stepKey);
				if (Hours(state.DepletedSince(stepKey, now), now) < settings.TerrainStepHours.Value)
				{
					tally.waiting++;
					return;
				}
			}
			if (InUse(zdo))
			{
				tally.inUse++;
				return;
			}
			List<Terrain.Protector> near = ProtectorsNear(position);
			float pieces = settings.TerrainProtectPiecesRadius.Value;
			if (pieces > 0f)
			{
				foreach (Vector3 blocker in BlockersNear(blockers, position, pieces + 64f))
				{
					near.Add(new Terrain.Protector { position = blocker, radius = pieces });
				}
			}
			Terrain.Data data = Terrain.Decode(zdo.GetByteArray(ZDOVars.s_TCData));
			Terrain.Stats stats = new Terrain.Stats();
			Terrain.Data result = Terrain.Step(data, position, near, settings, stats, all: refresh);
			if (result == null)
			{
				// Everything left is protected (or already zero).
				tally.blocked++;
				return;
			}
			tally.regrown++;
			if (tally.logged < settings.LogDetailsPerPass.Value)
			{
				tally.logged++;
				RegrowthPlugin.Log.LogInfo(
					$"{(dryRun ? "Would step" : "Stepped")} terrain of zone {position.x:0},{position.z:0}: height changes {stats.modifiedBefore} -> {stats.modifiedAfter} vertices, "
					+ $"paint {stats.paintedBefore} -> {stats.paintedAfter}, {stats.protectedVertices} protected; modified {modified:0.#} h ago, area idle {idle:0.##} h.");
			}
			if (!dryRun)
			{
				zdo.Set(ZDOVars.s_TCData, Terrain.Encode(result));
				state.Forget(stepKey);
				state.DepletedSince(stepKey, now);
				stillDepleted.Add(stepKey);
				terrainStepped = true;
			}
		}

		private bool terrainStepped;

		private List<Terrain.Protector> ProtectorsNear(Vector3 position)
		{
			List<Terrain.Protector> list = new List<Terrain.Protector>();
			Vector2s zone = ZoneSystem.GetZone(position);
			// A zone is 64 m; protectors up to two zones away can reach into it (wards 32 m, multiplied).
			for (int dy = -2; dy <= 2; dy++)
			{
				for (int dx = -2; dx <= 2; dx++)
				{
					if (protectors.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out List<Terrain.Protector> found))
					{
						list.AddRange(found);
					}
				}
			}
			return list;
		}

		private static IEnumerable<Vector3> BlockersNear(Dictionary<Vector2s, List<Vector3>> blockers, Vector3 position, float radius)
		{
			int zones = Mathf.CeilToInt(radius / 64f);
			Vector2s zone = ZoneSystem.GetZone(position);
			for (int dy = -zones; dy <= zones; dy++)
			{
				for (int dx = -zones; dx <= zones; dx++)
				{
					if (blockers.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out List<Vector3> list))
					{
						foreach (Vector3 blocker in list)
						{
							yield return blocker;
						}
					}
				}
			}
		}

		private static void AddProtector(Dictionary<Vector2s, List<Terrain.Protector>> protectors, Vector3 position, float radius)
		{
			Vector2s zone = ZoneSystem.GetZone(position);
			if (!protectors.TryGetValue(zone, out List<Terrain.Protector> list))
			{
				list = new List<Terrain.Protector>();
				protectors[zone] = list;
			}
			list.Add(new Terrain.Protector { position = position, radius = radius });
		}

		/*
			A location object is missing when nothing of its prefab stands within SlotTolerance
			(horizontally) of where the game put it -- a building piece (what can hold another up,
			LocationObjects.Holds) only on its very spot, within PieceTolerance. Pieces never move, and in a
			village they stand close: GoblinCamp2 on the live world (23 Oct 2026) has 215 pairs of one kind
			within 1.5 m among its 293 walls, poles and roofs, the nearest 0.2 m apart, so one wall standing
			hid its missing neighbours. Its height counts too (walls stand stacked 1 m apart), except for a
			piece the game puts on the ground after placing it (SnapToGround): the log benches around the
			village's campfire stand 0.11-0.19 m off their layout's height, and a test of that height put a
			second one beside each. "In use" is its zone being loaded on the server: an object created there
			now would appear in front of whoever is near.
		*/
		private const float SlotTolerance = 1.5f;
		private const float PieceTolerance = 0.1f;

		private void DecideSlot(LocationObjects.Slot slot, DateTime now, bool dryRun, Dictionary<Vector2s, List<Vector3>> blockers,
			Dictionary<(Vector2s, int), List<ZDO>> standing, Dictionary<Vector2s, List<ZDO>> remains, Dictionary<Vector2s, List<ZDO>> worldPieces,
			Tally tally, HashSet<string> stillDepleted, HashSet<string> regrown)
		{
			Vector2s zone = ZoneSystem.GetZone(slot.position);
			bool piece = holderPrefabs.Contains(slot.prefab);
			bool stands = false;
			bool original = false;
			List<ZDO> onSpot = null;
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					if (!standing.TryGetValue((new Vector2s(zone.x + dx, zone.y + dy), slot.prefab), out List<ZDO> list))
					{
						continue;
					}
					foreach (ZDO zdo in list)
					{
						Vector3 p = zdo.GetPosition();
						float x = p.x - slot.position.x;
						float z = p.z - slot.position.z;
						bool here = piece
							? x * x + z * z <= PieceTolerance * PieceTolerance && (slot.snaps || Mathf.Abs(p.y - slot.position.y) <= PieceTolerance)
							: x * x + z * z <= SlotTolerance * SlotTolerance;
						// Collected over many frames: what fell since is gone, and its ZDO may be another object's by now.
						// What fell this frame is still known until the destroy list goes out at the end of it.
						if (here && zdo.GetPrefab() == slot.prefab && ZDOMan.instance.GetZDO(zdo.m_uid) == zdo
							&& !ZDOMan.instance.m_destroySendList.Contains(zdo.m_uid))
						{
							stands = true;
							original |= zdo.GetLong(PutBackHash) == 0L;
							if ((p - slot.position).sqrMagnitude <= SameSpot * SameSpot && !extraGone.Contains(zdo.m_uid))
							{
								if (onSpot == null)
								{
									onSpot = new List<ZDO>();
								}
								onSpot.Add(zdo);
							}
						}
					}
				}
			}
			string key = slot.key;
			if (stands)
			{
				// One the game made stands again (its zone was generated again): from now on it is put back as before.
				// A put-back twin of another slot on the same spot does not count.
				if (original && state.ForgetFell(key))
				{
					RegrowthPlugin.Log.LogInfo($"{Name(slot.prefab)} of {slot.source} at {slot.position.x:0},{slot.position.y:0},{slot.position.z:0}, which fell after being put back, stands in its place again; "
						+ "it is put back as before from now on.");
				}
				if (onSpot != null && onSpot.Count > 1)
				{
					RemoveExtra(slot, onSpot, dryRun, blockers, tally);
				}
				return;
			}
			tally.depleted++;
			stillDepleted.Add(key);
			if (state.Fell(key, out DateTime _))
			{
				tally.fell++;
				return;
			}
			double depleted = Hours(state.DepletedSince(key, now), now);
			double idle = Hours(state.LastSeen(zone), now);
			if (depleted < settings.LocationObjectsAfterHours.Value || idle < settings.IdleHours.Value)
			{
				tally.waiting++;
				return;
			}
			if (IsBlocked(blockers, slot.position))
			{
				tally.blocked++;
				return;
			}
			if (!LocationObjects.Supported(slot, worldPieces))
			{
				tally.unsupported++;
				if (unsupportedLogged.Add(key))
				{
					RegrowthPlugin.Log.LogInfo($"Not putting back {Name(slot.prefab)} of {slot.source} at {slot.position.x:0},{slot.position.y:0},{slot.position.z:0}: what held it is gone "
						+ $"({string.Join(", ", slot.supports.Select(s => $"{Name(s.prefab)} at {s.position.x:0.0},{s.position.y:0.0},{s.position.z:0.0}"))}).");
				}
				return;
			}
			if (ZoneSystem.instance.m_zones.ContainsKey(zone))
			{
				tally.inUse++;
				return;
			}
			// What the hit left of it (the pieces still to mine) goes with it, or the pile would stand twice.
			List<ZDO> leftovers = settings.LocationObjectsClearRemains.Value ? RemainsNear(remains, slot.position) : new List<ZDO>();
			tally.regrown++;
			if (tally.logged < settings.LogDetailsPerPass.Value)
			{
				tally.logged++;
				RegrowthPlugin.Log.LogInfo(
					$"{(dryRun ? "Would put back" : "Put back")} {Name(slot.prefab)} of {slot.source} at {slot.position.x:0},{slot.position.y:0},{slot.position.z:0}: "
					+ $"missing {depleted:0.##} h, area idle {idle:0.##} h" + (leftovers.Count > 0 ? $"; {leftovers.Count} fractured remain(s) {(dryRun ? "would go" : "go")} with it." : "."));
			}
			if (!dryRun)
			{
				foreach (ZDO remainder in leftovers)
				{
					remainder.SetOwner(ZDOMan.GetSessionID());
					ZDOMan.instance.DestroyZDO(remainder);
					remainsGone.Add(remainder.m_uid);
				}
				ZDO made = LocationObjects.Create(slot.prefab, slot.position, slot.rotation);
				if (piece)
				{
					made.Set(PutBackHash, now.Ticks);
					made.Set(SlotKeyHash, key);
				}
				regrown.Add(key);
			}
		}

		/*
			A building piece put back that fell for lack of support (WearNTear_Destroy_Patch) is not put
			back again until its slot has had a piece standing on it once more -- in practice when [Zones]
			generates its zone again, location and all. Otherwise it would come back every AfterHours and
			fall at the next visit, its wood dropped each time: on a copy of the live world (23 Oct 2026)
			3 of the 14 pieces put back in GoblinCamp2, a banner and two walls of its watchtower 38-41 m
			up, fell within 90 s of a player coming near.
		*/
		internal void OnFell(ZDO zdo)
		{
			int prefab = zdo.GetPrefab();
			string key = zdo.GetString(SlotKeyHash);
			if (locationObjects == null || !locationObjects.IsWanted(prefab) || string.IsNullOrEmpty(key))
			{
				return;
			}
			Vector3 p = zdo.GetPosition();
			DateTime now = Now();
			if (state.MarkFell(key, now))
			{
				DateTime putBack = new DateTime(zdo.GetLong(PutBackHash), DateTimeKind.Utc);
				RegrowthPlugin.Log.LogInfo($"{Name(prefab)} at {p.x:0},{p.y:0},{p.z:0}, put back {Hours(putBack, now):0.##} h ago, fell for lack of support: "
					+ "not put back again before something stands in its place once more (its zone generated again).");
				// At once, not at the end of the pass: the world may be saved without the piece before a crash.
				SaveState();
			}
		}

		private const float RemainsRadius = 1.5f;
		// Remains destroyed in this pass (a destroyed object is only gone once the destroy list is sent).
		private readonly HashSet<ZDOID> remainsGone = new HashSet<ZDOID>();
		// Slots already logged as held by something gone, once per run.
		private readonly HashSet<string> unsupportedLogged = new HashSet<string>();

		/*
			More objects on a slot's very spot (within 5 cm) than the location's layout has there: one was put
			back while one stood. Up to 0.8.5 a prefab also in [SurtlingCores] Prefabs never counted as standing
			(see the scan), and on the live world of 17 Oct 2026 hanging_hairstrands stood up to six on a spot
			(513 on 149 spots). The extra ones go -- one not picked stays, if there is one -- where putting back
			could happen too: not near a player's build, in a zone the server has not loaded, owned by nobody
			connected.
		*/
		private const float SameSpot = 0.05f;
		// Extra objects destroyed in this pass (gone only once the destroy list is sent).
		private readonly HashSet<ZDOID> extraGone = new HashSet<ZDOID>();

		private void RemoveExtra(LocationObjects.Slot slot, List<ZDO> onSpot, bool dryRun, Dictionary<Vector2s, List<Vector3>> blockers, Tally tally)
		{
			int slotsThere = locationObjects.AllSlots().Count(other => other.prefab == slot.prefab && (other.position - slot.position).sqrMagnitude <= SameSpot * SameSpot);
			if (onSpot.Count <= slotsThere || IsBlocked(blockers, slot.position) || ZoneSystem.instance.m_zones.ContainsKey(ZoneSystem.GetZone(slot.position))
				|| onSpot.Any(InUse))
			{
				return;
			}
			// Collected over many frames: an object destroyed since may have been reused for another.
			// What stays: not picked, and one the game made before one put back.
			List<ZDO> extra = onSpot.Where(zdo => ZDOMan.instance.GetZDO(zdo.m_uid) == zdo).OrderBy(zdo => zdo.GetBool(ZDOVars.s_picked))
				.ThenBy(zdo => zdo.GetLong(PutBackHash) != 0L).Skip(slotsThere).ToList();
			if (extra.Count == 0)
			{
				return;
			}
			tally.extra += extra.Count;
			if (tally.logged < settings.LogDetailsPerPass.Value)
			{
				tally.logged++;
				RegrowthPlugin.Log.LogInfo($"{(dryRun ? "Would remove" : "Removed")} {extra.Count} extra {Name(slot.prefab)} of {slot.source} at "
					+ $"{slot.position.x:0},{slot.position.y:0},{slot.position.z:0}: {onSpot.Count} stood on a spot the layout has {slotsThere} for.");
			}
			if (!dryRun)
			{
				foreach (ZDO zdo in extra)
				{
					zdo.SetOwner(ZDOMan.GetSessionID());
					ZDOMan.instance.DestroyZDO(zdo);
					extraGone.Add(zdo.m_uid);
				}
			}
		}

		// The fractured remains standing at a slot, in zones the server has not loaded.
		private List<ZDO> RemainsNear(Dictionary<Vector2s, List<ZDO>> remains, Vector3 position)
		{
			List<ZDO> found = new List<ZDO>();
			Vector2s zone = ZoneSystem.GetZone(position);
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					if (!remains.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out List<ZDO> list))
					{
						continue;
					}
					foreach (ZDO zdo in list)
					{
						Vector3 p = zdo.GetPosition();
						float x = p.x - position.x;
						float z = p.z - position.z;
						if (x * x + z * z <= RemainsRadius * RemainsRadius && Mathf.Abs(p.y - position.y) < 2f
							&& !remainsGone.Contains(zdo.m_uid) && !ZoneSystem.instance.m_zones.ContainsKey(ZoneSystem.GetZone(p)))
						{
							found.Add(zdo);
						}
					}
				}
			}
			return found;
		}

		private float AfterHours(Kind kind, int prefab)
		{
			switch (kind)
			{
				case Kind.Core: return coreHours.TryGetValue(prefab, out float hours) ? hours : settings.CoresAfterHours.Value;
				case Kind.Chest: return settings.ChestsAfterHours.Value;
				case Kind.LocationObject: return settings.LocationObjectsAfterHours.Value;
				default: return settings.SpawnersAfterHours.Value;
			}
		}

		private static string Describe(Kind kind)
		{
			switch (kind)
			{
				case Kind.Core: return "pickable";
				case Kind.Chest: return "treasure chest";
				case Kind.Terrain: return "terrain zone";
				case Kind.LocationObject: return "location object";
				default: return "creature spawner";
			}
		}

		private string Name(int prefab)
		{
			return prefabNames.TryGetValue(prefab, out string name) ? name : prefab.ToString();
		}

		private bool IsDepleted(Kind kind, ZDO zdo, string key, DateTime now)
		{
			switch (kind)
			{
				case Kind.Core:
					return zdo.GetBool(ZDOVars.s_picked, corePrefabs[zdo.GetPrefab()]);
				case Kind.Chest:
					// Used: emptied, or a player has been near it since it was last restocked (its clock then keeps running).
					return IsRestockable(zdo)
						&& (IsEmpty(zdo) || state.HasKey(key) || state.SeenSince(ZoneSystem.GetZone(zdo.GetPosition()), now.AddHours(-Math.Max(0f, settings.IdleHours.Value))));
				case Kind.Terrain:
					return Terrain.IsModified(zdo.GetByteArray(ZDOVars.s_TCData));
				default:
					ZDOConnection connection = zdo.GetConnection();
					return connection != null
						&& connection.m_type == ZDOExtraData.ConnectionType.Spawned
						&& ZDOMan.instance.GetZDO(connection.m_target) == null;
			}
		}

		// Player-built chests are never touched, a chest vanilla has not filled yet has nothing to restock,
		// and chests from cheats are left alone.
		// A chest holding anything that has been in a player's bag is someone's storage: never restocked.
		private static bool IsRestockable(ZDO zdo)
		{
			return zdo.GetLong(ZDOVars.s_creator, 0L) == 0L && zdo.GetBool(ZDOVars.s_addedDefaultItems) && !zdo.GetBool(ZDOVars.s_cheated) && !PlayersItems.InContainer(zdo);
		}

		private static bool IsEmpty(ZDO zdo)
		{
			// Container.Save stores the inventory as a byte array (Inventory.Save): version, then item count.
			byte[] items = zdo.GetByteArray(ZDOVars.s_items);
			if (items == null)
			{
				return true;
			}
			ZPackage package = new ZPackage(items);
			int version = package.ReadInt();
			int count = version >= (int)Version.Item.Smaller ? package.ReadUShort() : package.ReadInt();
			return count == 0;
		}

		private void Apply(Kind kind, ZDO zdo)
		{
			switch (kind)
			{
				case Kind.Core:
					zdo.Set(ZDOVars.s_picked, false);
					break;
				case Kind.Chest:
					Refill(zdo, chestPrefabs[zdo.GetPrefab()]);
					break;
				default:
					zdo.SetConnection(ZDOExtraData.ConnectionType.None, ZDOID.None);
					break;
			}
		}

		// What Container.AddDefaultItems and Container.Save do, done on the ZDO directly.
		private static void Refill(ZDO zdo, Container prefab)
		{
			Inventory inventory = new Inventory(prefab.m_name, prefab.m_bkg, prefab.m_width, prefab.m_height);
			foreach (ItemDrop.ItemData item in prefab.m_defaultItems.GetDropListItems())
			{
				inventory.AddItem(item);
			}
			ZPackage package = new ZPackage();
			inventory.Save(package);
			zdo.Set(ZDOVars.s_items, package.GetArray());
			zdo.Set(ZDOVars.s_addedDefaultItems, true);
		}

		private static bool InUse(ZDO zdo)
		{
			if (ZNetScene.instance.FindInstance(zdo))
			{
				return true;
			}
			long owner = zdo.GetOwner();
			return owner != 0L && owner != ZDOMan.GetSessionID() && ZNet.instance.GetPeer(owner) != null;
		}

		private static void AddBlocker(Dictionary<Vector2s, List<Vector3>> blockers, Vector3 position)
		{
			Vector2s zone = ZoneSystem.GetZone(position);
			if (!blockers.TryGetValue(zone, out List<Vector3> list))
			{
				list = new List<Vector3>();
				blockers[zone] = list;
			}
			list.Add(position);
		}

		private bool IsBlocked(Dictionary<Vector2s, List<Vector3>> blockers, Vector3 position)
		{
			float radius = Mathf.Max(0f, settings.PlayerBuildRadius.Value);
			int zones = Mathf.CeilToInt(radius / 64f);
			bool inside = position.y > InteriorHeight;
			Vector2s zone = ZoneSystem.GetZone(position);
			for (int dy = -zones; dy <= zones; dy++)
			{
				for (int dx = -zones; dx <= zones; dx++)
				{
					if (!blockers.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out List<Vector3> list))
					{
						continue;
					}
					foreach (Vector3 blocker in list)
					{
						// A dungeon's interior lies 5000 m up: a base on the surface above it is not touched by what
						// comes back inside, and a tombstone down there is not touched by what comes back on the surface.
						if ((blocker.y > InteriorHeight) != inside)
						{
							continue;
						}
						float x = blocker.x - position.x;
						float z = blocker.z - position.z;
						if (x * x + z * z <= radius * radius)
						{
							return true;
						}
					}
				}
			}
			return false;
		}

		private const float InteriorHeight = 2500f;
	}

	/*
		A building piece the plugin put back that falls for lack of support (Regrowth.OnFell): the game
		destroys it from WearNTear.UpdateWear, with no hit, once UpdateSupport found too little. Only where
		the server runs the piece: with Dedicated Simulation it owns what stands near players; in vanilla a
		player's game does, and the fall never passes through here. ApplyDamage writes the health (0 or
		less) before it calls Destroy; a player's hammer (RPC_Remove) calls it with the health as it was, so
		a piece taken down that way -- standing unsupported only with NoBuildingFall -- is no fall.
	*/
	[HarmonyPatch(typeof(WearNTear), "Destroy", typeof(HitData), typeof(bool))]
	internal static class WearNTear_Destroy_Patch
	{
		private static bool failed;

		static void Prefix(WearNTear __instance, HitData hitData)
		{
			if (Regrowth.Active == null || hitData != null || !__instance.m_noSupportWear || !__instance.m_nview || !__instance.m_nview.IsValid()
				|| __instance.HaveSupport())
			{
				return;
			}
			try
			{
				ZDO zdo = __instance.m_nview.GetZDO();
				if (zdo.GetLong(Regrowth.PutBackHash) != 0L && zdo.GetFloat(ZDOVars.s_health, __instance.m_health) <= 0f)
				{
					Regrowth.Active.OnFell(zdo);
				}
			}
			catch (Exception e)
			{
				// The game's own destroy must go on whatever happens here.
				if (!failed)
				{
					failed = true;
					RegrowthPlugin.Log.LogWarning($"Could not note a fallen piece: {e}");
				}
			}
		}
	}
}
