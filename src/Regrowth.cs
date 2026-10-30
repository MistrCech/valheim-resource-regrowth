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
		  Container.AddDefaultItems fills it -- what was left of the loot is replaced, what has been in
		  a player's bag stays (KeepPlayersItems). Vanilla would only
		  fill a chest on load when the loading machine already owns it, and ownership is handed out
		  on a slower timer than objects are created, so the plugin does not leave that to chance.
		  Whether a player took everything, some or nothing cannot be told from the chest, so a chest
		  counts as used once a player has been near it (or it is empty).
		- One-time creature spawners: once their creature is gone, the "spawned" connection is
		  cleared; vanilla then spawns again when the area is next loaded (CreatureSpawner).

		Everything a location holds regrows together (Locations.Together): a dungeon's interior
		with its entrance, a camp, a village, a ruin. Its clocks run per object as before, and as
		soon as one of them is due, every core taken, every creature killed, every chest and every
		location object missing in the location come back at once, and its doors close. A location
		regrows only when no player has been near any of it for IdleHours, none of it is within
		PlayerBuildRadius of a player-built piece or tombstone, and none of it is in use: no connected
		player owns it and the server does not have it loaded (Dedicated Simulation loads zones around
		players on the server). Changing an object that is loaded somewhere would not show until it is
		reloaded, or would act at once (a spawner).
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
		// Location objects decided in one frame, at most about (a source is decided whole in one).
		private const long SliceMilliseconds = 4;

		// The running regrowth with location objects on, for the WearNTear.Destroy patch.
		internal static Regrowth Active;
		// On a building piece it put back: when (UTC ticks). A piece that falls with it is told from one that stood there all along.
		internal static readonly int PutBackHash = (RegrowthPlugin.GUID + ".putback").GetStableHashCode();
		// On it too: its slot's state key. A piece that snaps to the ground no longer stands at its slot's height.
		internal static readonly int SlotKeyHash = (RegrowthPlugin.GUID + ".slot").GetStableHashCode();
		// On what was put back at its layout's height and has not yet been settled on the ground (Settle).
		internal static readonly int SettleHash = (RegrowthPlugin.GUID + ".settle").GetStableHashCode();
		// With it, when the layout has it stand above the ground or sunk into it (SnapToGround.m_offset).
		internal static readonly int SettleOffsetHash = (RegrowthPlugin.GUID + ".settleoffset").GetStableHashCode();

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
		private readonly HashSet<int> movingPrefabs = new HashSet<int>(); // what moves by itself (LocationObjects.MovesByItself): it is not on its layout's very spot
		private readonly HashSet<int> tombstonePrefabs = new HashSet<int>();
		private readonly HashSet<int> doorPrefabs = new HashSet<int>(); // what a player can open and close by hand: no key, not one that stays open
		private readonly int terrainPrefab = Terrain.CompilerPrefab.GetStableHashCode();
		private Dictionary<int, float> protectorPrefabs = new Dictionary<int, float>();
		private static readonly int WorkbenchHash = "piece_workbench".GetStableHashCode();
		private readonly Dictionary<int, string> prefabNames = new Dictionary<int, string>();
		private LocationObjects locationObjects;
		private ZoneReset zones;
		private float presenceTimer;
		private float passTimer;
		private Coroutine running;
		// The world has had objects since the start: one without any now is shutting down, not new (CopperDeposits.Apply).
		private bool worldSeen;

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
				// A chest never to be restocked does not come back as a location's part either: the game would fill it. Both lists as at the start.
				locationObjects = new LocationObjects(SplitList(settings.LocationObjectsPrefabs.Value), SplitList(settings.LocationObjectsLocations.Value),
					SplitList(settings.LocationObjectsLocationsExclude.Value).Concat(SplitList(settings.ChestsExclude.Value)));
			}
			state = new RegrowthState(StatePath());
			DateTime now = Now();
			state.Load(now);
			if (!state.PresenceKnownLoaded)
			{
				// Older versions kept a zone's presence for IdleHours only.
				state.PresenceKnownSince = now.AddHours(-Math.Max(0f, settings.IdleHours.Value));
			}
			worldSeen = WorldLoaded();
			if (CopperDeposits.Apply(settings, state, host, piecePrefabs, worldSeen, !worldSeen))
			{
				SaveState();
			}
			if (locationObjects != null)
			{
				Active = this;
				foreach ((int prefab, Vector3 position, Quaternion rotation) in state.Doors)
				{
					newDoors.Add((prefab, position, rotation));
				}
				ZDOMan.instance.m_onZDODestroyed += OnDestroyed;
				if (!RegrowthPlugin.DestroyPatched)
				{
					RegrowthPlugin.Log.LogWarning(RegrowthPlugin.DestroyPatchFailed
						? "LocationObjects: WearNTear.Destroy could not be patched at startup (see the error above); a piece put back that falls is put back again."
						: "LocationObjects: switched on after the server started; until a restart a piece put back that falls is put back again (the game method that tells is patched at startup only).");
				}
			}
			if (settings.CopperOverDeepestHole.Value >= 0f && !RegrowthPlugin.SpawnZonePatched)
			{
				RegrowthPlugin.Log.LogWarning(RegrowthPlugin.SpawnZonePatchFailed
					? "CopperDeposits OverDeepestHole: ZoneSystem.SpawnZone could not be patched at startup (see the error above); a deposit of a zone generated now is raised at the next pass, once no player is near."
					: "CopperDeposits OverDeepestHole: switched on after the server started; until a restart a deposit of a zone generated now is raised at the next pass, once no player is near.");
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
				+ $"location objects: {(locationObjects == null ? "off" : settings.LocationObjectsPrefabs.Value + (locationObjects.AnyListed ? $"; every breakable part of {settings.LocationObjectsLocations.Value}"
					+ (settings.LocationObjectsLocationsExclude.Value.Trim().Length > 0 ? $" but {settings.LocationObjectsLocationsExclude.Value}" : "") : "") + $"; {state.Doors.Count()} door(s) of dungeons known that went")}. "
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
			CopperDeposits.Stop();
			if (ZDOMan.instance != null)
			{
				ZDOMan.instance.m_onZDODestroyed -= OnDestroyed;
			}
			if (Active == this)
			{
				Active = null;
			}
			SaveState();
		}

		public void Tick(float dt)
		{
			CopperDeposits.Work();
			presenceTimer -= dt;
			if (presenceTimer <= 0f)
			{
				presenceTimer = PresenceIntervalSeconds;
				RecordPresence();
			}
			settleTimer -= dt;
			if (settleTimer <= 0f && unsettled.Count > 0)
			{
				settleTimer = SettleIntervalSeconds;
				Settle();
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
				if (prefabLists != PrefabLists())
				{
					BuildPrefabSets();
					RegrowthPlugin.Log.LogInfo($"The config's lists were read again: {corePrefabs.Count} kind(s) of pickable ([SurtlingCores] Prefabs), {chestPrefabs.Count} kind(s) of chest, {spawnerPrefabs.Count} kind(s) of one-time spawner.");
				}
				zones?.ReloadProtected();
				bool loaded = WorldLoaded();
				worldSeen |= loaded;
				if (CopperDeposits.Apply(settings, state, host, piecePrefabs, loaded, !worldSeen))
				{
					SaveState();
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

		// The config values the prefab sets were built from; they are built again before a pass when these changed (the config is
		// reloaded before every pass), so a list edited while the server runs counts from the next pass. [LocationObjects] Prefabs
		// and the switches of the parts still take a restart.
		private string prefabLists;

		private string PrefabLists()
		{
			return settings.CorePrefabs.Value + "|" + settings.ChestsExclude.Value + "|" + settings.SpawnersExclude.Value;
		}

		private void BuildPrefabSets()
		{
			corePrefabs.Clear();
			coreHours.Clear();
			chestPrefabs.Clear();
			spawnerPrefabs.Clear();
			generatorPrefabs.Clear();
			piecePrefabs.Clear();
			holderPrefabs.Clear();
			movingPrefabs.Clear();
			tombstonePrefabs.Clear();
			doorPrefabs.Clear();
			prefabNames.Clear();
			prefabLists = PrefabLists();
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
				if (LocationObjects.MovesByItself(prefab))
				{
					movingPrefabs.Add(hash);
				}
				if (prefab.GetComponent<TombStone>())
				{
					tombstonePrefabs.Add(hash);
				}
				Door door = prefab.GetComponent<Door>();
				if (door && !door.m_keyItem && !door.m_canNotBeClosed)
				{
					doorPrefabs.Add(hash);
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
				// Wall time includes the frames in between; work time is only what this pass itself took, and its worst frame is what a player can feel of it.
				System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
				System.Diagnostics.Stopwatch work = System.Diagnostics.Stopwatch.StartNew();
				int frames = 1;
				long worstFrame = 0L, frameStart = 0L;
				void EndFrame()
				{
					frames++;
					work.Stop();
					worstFrame = Math.Max(worstFrame, work.ElapsedMilliseconds - frameStart);
					frameStart = work.ElapsedMilliseconds;
				}
				List<ZDO> zdos = new List<ZDO>(ZDOMan.instance.m_objectsByID.Values);
				Dictionary<Vector2s, List<Vector3>> blockers = new Dictionary<Vector2s, List<Vector3>>();
				blockerPrefabs = new Dictionary<Vector2s, List<int>>();
				insideBlockers = new Dictionary<Vector2s, List<Vector3>>();
				insideBlockerPrefabs = new Dictionary<Vector2s, List<int>>();
				Dictionary<Vector2s, List<Terrain.Protector>> protectors = new Dictionary<Vector2s, List<Terrain.Protector>>();
				List<Candidate> candidates = new List<Candidate>();
				List<ZDO> sources = new List<ZDO>();
				// Doors of the world standing open: they close when their location comes back.
				List<(ZDO zdo, ZDOID uid, int prefab)> openDoors = new List<(ZDO, ZDOID, int)>();
				Standing standing = new Standing();
				List<ZDOID> stillUnsettled = new List<ZDOID>();
				// Fractured remains of wanted objects (a hit pile or rock leaves "<name>_frac"), by zone.
				Dictionary<Vector2s, List<ZDO>> remains = new Dictionary<Vector2s, List<ZDO>>();
				// What the world generated that can hold a location object up (LocationObjects.Holds, no creator), by zone.
				Dictionary<Vector2s, List<ZDO>> worldPieces = new Dictionary<Vector2s, List<ZDO>>();
				// For CopperDeposits: its deposits and the player's building pieces by zone.
				List<ZDOID> copperDeposits = new List<ZDOID>();
				Dictionary<Vector2s, List<Vector3>> copperBuilds = new Dictionary<Vector2s, List<Vector3>>();

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
						AddBlocker(blockers, zdo.GetPosition(), prefab);
						if (!tombstonePrefabs.Contains(prefab))
						{
							CopperDeposits.NoteBuild(copperBuilds, zdo.GetPosition());
						}
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
					if (CopperDeposits.IsDeposit(prefab))
					{
						copperDeposits.Add(zdo.m_uid);
					}
					if (generatorPrefabs.Contains(prefab))
					{
						NoteGenerator(zdo, prefab);
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
					if (doorPrefabs.Contains(prefab) && zdo.GetInt(ZDOVars.s_state) != 0 && zdo.GetLong(ZDOVars.s_creator, 0L) == 0L)
					{
						openDoors.Add((zdo, zdo.m_uid, prefab));
					}
					// Not in the chain above: a pickable can be in [SurtlingCores] Prefabs too (hanging_hairstrands). Up to 0.8.5 such a
					// one never counted as standing, so every place of it looked empty and got one more each time its hours were up.
					if (locationObjects != null && locationObjects.IsWanted(prefab))
					{
						Vector3 at = zdo.GetPosition();
						if (locationObjects.NearSlots(ZoneSystem.GetZone(at)))
						{
							standing.Add(zdo, prefab, at);
						}
						if (zdo.GetBool(SettleHash))
						{
							stillUnsettled.Add(zdo.m_uid);
						}
					}
					if (i % ScanBatch == ScanBatch - 1)
					{
						EndFrame();
						yield return null;
						work.Start();
					}
				}
				if (!WorldLoaded())
				{
					yield break;
				}
				CopperDeposits.Collected(copperDeposits, copperBuilds);
				// From the objects themselves, so what a restart forgot is found again.
				unsettled = stillUnsettled;
				// Where location objects stood: new locations and rooms only, at most one prefab read per frame.
				int loadedPrefabs = 0;
				if (locationObjects != null)
				{
					IEnumerator refresh = locationObjects.Refresh(sources, n => loadedPrefabs += n);
					while (refresh.MoveNext())
					{
						EndFrame();
						yield return null;
						work.Start();
					}
					if (!WorldLoaded())
					{
						yield break;
					}
					// After the layouts, so a door on a layout's own spot is known as its part, and fresh until a pass has collected what stands near it.
					foreach ((int prefab, Vector3 position, Quaternion rotation) in newDoors)
					{
						locationObjects.AddDoor(prefab, position, rotation);
					}
					newDoors.Clear();
				}

				// 2. Decide, over frames as the scan: what a pass does in one frame is what a player feels. Whatever was collected may be gone by the
				// time its turn comes, so a candidate is looked at again then, and what a group holds again when the group's does (DecideGroup).
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
				Dictionary<Vector2s, Group> groups = new Dictionary<Vector2s, Group>();
				Dictionary<Vector2s, Place> places = Places();
				bool together = settings.LocationsTogether.Value;
				System.Diagnostics.Stopwatch slice = System.Diagnostics.Stopwatch.StartNew();
				if (locationObjects != null)
				{
					remainsGone.Clear();
					extraGone.Clear();
					Tally tally = tallies[Kind.LocationObject];
					foreach (LocationObjects.Source source in locationObjects.Sources())
					{
						try
						{
							if (source.fresh)
							{
								// Read after this pass's scan: decided from the next pass, its clocks kept until then, and its location waits with it.
								foreach (LocationObjects.Slot slot in source.slots)
								{
									if (state.HasKey(slot.key))
									{
										stillDepleted.Add(slot.key);
									}
								}
								if (together)
								{
									GroupOf(source, groups, places).fresh = true;
								}
							}
							else
							{
								Group group = null;
								foreach (LocationObjects.Slot slot in source.slots)
								{
									if (!together)
									{
										DecideSlot(slot, now, dryRun, blockers, standing, remains, worldPieces, tally, stillDepleted, regrown);
									}
									else if (Missing(slot, now, dryRun, blockers, standing, worldPieces, tally, stillDepleted, out bool piece))
									{
										if (group == null)
										{
											group = GroupOf(source, groups, places);
										}
										group.missing.Add((slot, piece));
										group.dungeon |= slot.position.y > InteriorHeight;
									}
								}
							}
						}
						catch (Exception e)
						{
							if (errors++ == 0)
							{
								RegrowthPlugin.Log.LogWarning($"Skipped {source.name} at {source.position.x:0},{source.position.z:0}: {e}");
							}
						}
						if (slice.ElapsedMilliseconds >= SliceMilliseconds)
						{
							EndFrame();
							yield return null;
							work.Start();
							if (!WorldLoaded())
							{
								yield break;
							}
							slice.Reset();
							slice.Start();
						}
					}
				}
				s_waitingLogged = 0;
				foreach (Candidate candidate in candidates)
				{
					try
					{
						if (ZDOMan.instance.GetZDO(candidate.uid) == candidate.zdo && candidate.zdo.GetPrefab() == candidate.prefab)
						{
							if (candidate.kind == Kind.Terrain)
							{
								if (IsDepleted(candidate.kind, candidate.zdo, now))
								{
									DecideTerrain(candidate.zdo, now, dryRun, blockers, tallies[candidate.kind], stillDepleted);
								}
							}
							else if (GroupOf(candidate.zdo.GetPosition(), groups, places) is Group group)
							{
								group.members.Add(candidate);
							}
							else if (IsDepleted(candidate.kind, candidate.zdo, now))
							{
								// Of no location: alone, and only if there is anything to decide.
								Group lone = new Group { position = candidate.zdo.GetPosition(), dungeon = Inside(candidate.zdo.GetPosition()) };
								lone.members.Add(candidate);
								DecideGroup(lone, now, dryRun, blockers, remains, tallies, stillDepleted, regrown);
							}
						}
					}
					catch (Exception e)
					{
						if (errors++ == 0)
						{
							RegrowthPlugin.Log.LogWarning($"Skipped {Describe(candidate.kind)} {Name(candidate.prefab)}: {e}");
						}
					}
					if (slice.ElapsedMilliseconds >= SliceMilliseconds)
					{
						EndFrame();
						yield return null;
						work.Start();
						if (!WorldLoaded())
						{
							yield break;
						}
						slice.Reset();
						slice.Start();
					}
				}
				if (together)
				{
					foreach ((ZDO zdo, ZDOID uid, int prefab) door in openDoors)
					{
						GroupOf(door.zdo.GetPosition(), groups, places)?.doors.Add(door);
					}
				}
				int locationsRegrown = 0;
				foreach (Group group in groups.Values)
				{
					try
					{
						if (DecideGroup(group, now, dryRun, blockers, remains, tallies, stillDepleted, regrown))
						{
							locationsRegrown++;
						}
					}
					catch (Exception e)
					{
						if (errors++ == 0)
						{
							RegrowthPlugin.Log.LogWarning($"Skipped {group.name} at {group.position.x:0},{group.position.z:0}: {e}");
						}
					}
					if (slice.ElapsedMilliseconds >= SliceMilliseconds)
					{
						EndFrame();
						yield return null;
						work.Start();
						if (!WorldLoaded())
						{
							yield break;
						}
						slice.Reset();
						slice.Start();
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
						EndFrame();
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
				worstFrame = Math.Max(worstFrame, work.ElapsedMilliseconds - frameStart);

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
					+ (locationObjects != null ? $" ({locationObjects.SourceCount} locations and rooms known with {locationObjects.SlotCount} places"
						+ $"{(loadedPrefabs > 0 ? $", {loadedPrefabs} prefab(s) read this pass" : "")})" : "")
					+ (settings.LocationsTogether.Value ? $"; {locationsRegrown} location(s) {(dryRun ? "would regrow" : "regrew")} as a whole" : "")
					+ (zoneTally != null ? $"; zones {zoneTally.visited} visited ({zoneTally.waiting} waiting, {zoneTally.due} due, {zoneTally.blocked} kept{(zoneTally.why.Count > 0 ? ": " + string.Join(", ", zoneTally.why.OrderByDescending(w => w.Value).Select(w => $"{w.Value} {w.Key}")) : "")}, "
						+ $"{zoneTally.reset} {(settings.ZonesDryRun.Value || dryRun ? "would be generated again" : "generated again")}{(zoneTally.failed > 0 ? $", {zoneTally.failed} failed and undone" : "")})" : "")
					+ $". Scanned {zdos.Count} objects: {work.ElapsedMilliseconds} ms of work spread over {frames} frames ({wall.ElapsedMilliseconds} ms wall), {worstFrame} ms the worst of them."
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
			// A location or room of it was read in this pass: it waits for the next, or what stands of it would not be known yet.
			public bool fresh;
			public readonly List<Candidate> members = new List<Candidate>();
			// Its location objects missing, with whether each is a building piece.
			public readonly List<(LocationObjects.Slot slot, bool piece)> missing = new List<(LocationObjects.Slot, bool)>();
			public readonly List<(ZDO zdo, ZDOID uid, int prefab)> doors = new List<(ZDO, ZDOID, int)>();
		}

		private struct Place
		{
			public string name;
			public Vector3 position;
			public float radius;
		}

		// Every location the world has placed, by its zone: a zone holds one at most.
		private static Dictionary<Vector2s, Place> Places()
		{
			Dictionary<Vector2s, Place> places = new Dictionary<Vector2s, Place>();
			foreach (KeyValuePair<Vector2s, ZoneSystem.LocationInstance> entry in ZoneSystem.instance.m_locationInstances)
			{
				ZoneSystem.LocationInstance instance = entry.Value;
				if (instance.m_placed && instance.m_location != null)
				{
					places[entry.Key] = new Place { name = instance.m_location.m_prefabName, position = instance.m_position, radius = instance.m_location.m_exteriorRadius };
				}
			}
			return places;
		}

		// A dungeon's generator up inside, by zone (a location without one is a cave built whole); and the rooms of the camps and villages, the
		// dungeons on the surface, by the zone they are in, with what built them. What a generator built never changes, so each is read once.
		private readonly Dictionary<Vector2s, (int prefab, Vector3 position)> insideGenerators = new Dictionary<Vector2s, (int, Vector3)>();
		private readonly Dictionary<Vector2s, List<(Vector3 room, int prefab, Vector3 generator)>> campRooms = new Dictionary<Vector2s, List<(Vector3, int, Vector3)>>();
		private readonly HashSet<Vector3> campsRead = new HashSet<Vector3>();

		private void NoteGenerator(ZDO zdo, int prefab)
		{
			Vector3 position = zdo.GetPosition();
			if (Inside(position))
			{
				insideGenerators[ZoneSystem.GetZone(position)] = (prefab, position);
			}
			else if (!campsRead.Contains(position) && zdo.GetByteArray(ZDOVars.s_roomData, out byte[] data))
			{
				campsRead.Add(position);
				foreach ((int _, Vector3 room, Quaternion _) in LocationObjects.ReadRooms(data))
				{
					Vector2s zone = ZoneSystem.GetZone(room);
					if (!campRooms.TryGetValue(zone, out List<(Vector3, int, Vector3)> list))
					{
						campRooms[zone] = list = new List<(Vector3, int, Vector3)>();
					}
					list.Add((room, prefab, position));
				}
			}
		}

		/*
			Which location an object belongs to; null when none, or when each object goes on its own. Inside a dungeon (5000 m up) that is
			the location of the object's own zone: a dungeon's rooms lie within the zone of the generator that built them
			(DungeonGenerator.IsInsideDungeon), a cave the location is built with whole lies around its centre, and a zone holds one location.
			So it was for all 4283 chests, spawners and cores up there on the live world of 30 Oct 2026, where the nearest generator, the rule up to
			0.8.11, was another dungeon's for 21 of them and farther than it looked for 83, and the location nearest a generator another one for 4 of 302.
			On the surface it is the nearest location whose ground the object stands on, unless a room of a camp or village is nearer still: all
			1623 objects of a camp on the same world lay within CampRoomReach of a room, and 9 more stood there 1-3 m from a room and 15-24 m from the
			centre of a house or a tower beside the camp, whose radius took them in.
		*/
		private Group GroupOf(Vector3 position, Dictionary<Vector2s, Group> groups, Dictionary<Vector2s, Place> places)
		{
			if (!settings.LocationsTogether.Value)
			{
				return null;
			}
			Vector2s zone = ZoneSystem.GetZone(position);
			Group group;
			if (Inside(position))
			{
				if (places.TryGetValue(zone, out Place own))
				{
					group = GroupAt(zone, own.name, own.position, groups);
				}
				else if (insideGenerators.TryGetValue(zone, out (int prefab, Vector3 position) generator))
				{
					group = GroupAt(zone, Name(generator.prefab), generator.position, groups);
				}
				else
				{
					return null;
				}
				group.dungeon = true;
				return group;
			}
			bool any = NearestPlace(places, position, out Vector2s placeZone, out Place place, out float toPlace);
			if (NearestCampRoom(position, toPlace, out (Vector3 room, int prefab, Vector3 generator) camp))
			{
				Vector2s generatorZone = ZoneSystem.GetZone(camp.generator);
				return places.TryGetValue(generatorZone, out Place ofCamp) ? GroupAt(generatorZone, ofCamp.name, ofCamp.position, groups) : GroupAt(generatorZone, Name(camp.prefab), camp.generator, groups);
			}
			return any ? GroupAt(placeZone, place.name, place.position, groups) : null;
		}

		// The group of what a location or a room of its dungeon put there: the one its chests and creatures are in, from where the location
		// stands, or the generator that built the room.
		private Group GroupOf(LocationObjects.Source source, Dictionary<Vector2s, Group> groups, Dictionary<Vector2s, Place> places)
		{
			Vector2s zone = ZoneSystem.GetZone(source.anchor);
			return places.TryGetValue(zone, out Place place)
				? GroupAt(zone, place.name, place.position, groups)
				: GroupAt(zone, source.generator != 0 ? Name(source.generator) : source.name, source.anchor, groups);
		}

		private static Group GroupAt(Vector2s zone, string name, Vector3 position, Dictionary<Vector2s, Group> groups)
		{
			if (!groups.TryGetValue(zone, out Group group))
			{
				groups[zone] = group = new Group { name = name, position = position };
			}
			return group;
		}

		private int s_waitingLogged;

		// "3 surtling core stands (2 used up), 4 treasure chests (4 used), 9 creature spawners (5 used up)"
		private static string Summary(List<Candidate> members, List<Candidate> depleted, int missing)
		{
			return string.Join(", ", members.GroupBy(m => m.kind).Select(g =>
			{
				int used = depleted.Count(d => d.kind == g.Key);
				return $"{g.Count()} {Describe(g.Key)}{(g.Count() == 1 ? "" : "s")} ({used} {(g.Key == Kind.Chest ? "used" : "used up")})";
			}).Concat(missing > 0 ? new[] { $"{missing} location object{(missing == 1 ? "" : "s")} missing" } : new string[0]));
		}

		// The location whose ground the object stands on: within its radius and a little.
		private static bool NearestPlace(Dictionary<Vector2s, Place> places, Vector3 position, out Vector2s zone, out Place found, out float distance)
		{
			zone = default;
			found = default;
			distance = float.MaxValue;
			Vector2s at = ZoneSystem.GetZone(position);
			for (int dy = -2; dy <= 2; dy++)
			{
				for (int dx = -2; dx <= 2; dx++)
				{
					Vector2s near = new Vector2s(at.x + dx, at.y + dy);
					if (places.TryGetValue(near, out Place place))
					{
						float x = place.position.x - position.x;
						float z = place.position.z - position.z;
						float d = Mathf.Sqrt(x * x + z * z);
						if (d <= Mathf.Max(place.radius, 16f) + 8f && d < distance)
						{
							distance = d;
							found = place;
							zone = near;
						}
					}
				}
			}
			return distance < float.MaxValue;
		}

		private const float CampRoomReach = 8f;

		// The room of a camp or village the object stands by: the nearest within CampRoomReach, nearer than than.
		private bool NearestCampRoom(Vector3 position, float than, out (Vector3 room, int prefab, Vector3 generator) found)
		{
			found = default;
			float best = Mathf.Min(CampRoomReach, than);
			best *= best;
			bool any = false;
			Vector2s zone = ZoneSystem.GetZone(position);
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					if (campRooms.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out List<(Vector3 room, int prefab, Vector3 generator)> list))
					{
						foreach ((Vector3 room, int prefab, Vector3 generator) entry in list)
						{
							float x = entry.room.x - position.x;
							float z = entry.room.z - position.z;
							if (x * x + z * z < best)
							{
								best = x * x + z * z;
								found = entry;
								any = true;
							}
						}
					}
				}
			}
			return any;
		}

		/*
			A location as one: each used-up object and each location object missing keeps its own clock (state
			"depleted" entries, as before), and as soon as one of them has waited its AfterHours, everything
			used up or missing in the location comes back together with every chest in it -- if nobody has been
			near any of it for IdleHours, none of it is near a player's build and none of it is in use. Whatever
			held it back holds all of it back: nothing comes back while the rest does not. Only a dungeon has two
			sides: a build on the surface, a portal or a ladder by the entrance, keeps what is out there from
			coming back, and the interior comes back whole without it. The doors of the world standing open close
			with it, those a player can close by hand. Returns whether it regrew (or would have, in a dry run).
		*/
		private bool DecideGroup(Group group, DateTime now, bool dryRun, Dictionary<Vector2s, List<Vector3>> blockers, Dictionary<Vector2s, List<ZDO>> remains,
			Dictionary<Kind, Tally> tallies, HashSet<string> stillDepleted, HashSet<string> regrown)
		{
			// Looked at over frames: what has gone since is out of it, and its ZDO may be another object's by now.
			group.members.RemoveAll(m => ZDOMan.instance.GetZDO(m.uid) != m.zdo || m.zdo.GetPrefab() != m.prefab);
			List<Candidate> members = group.members;
			List<(LocationObjects.Slot slot, bool piece)> missing = group.missing;
			List<Candidate> depleted = members.Where(m => IsDepleted(m.kind, m.zdo, now)).ToList();
			if (depleted.Count == 0 && missing.Count == 0)
			{
				return false;
			}
			string apart = null;
			if (group.dungeon && (members.Any(m => !Inside(m.zdo.GetPosition())) || missing.Any(m => !Inside(m.slot.position)))
				&& HeldBy(members.Where(m => !Inside(m.zdo.GetPosition())), missing.Where(m => !Inside(m.slot.position)), blockers, out apart))
			{
				// Near a build on the surface, a portal by the entrance: what is out there stays as it is, its clocks running, and has no say in
				// when the interior comes back (the interior would be due for good otherwise). Nobody's build is touched by what comes back 5000 m up.
				foreach (Candidate member in depleted.Where(m => !Inside(m.zdo.GetPosition())))
				{
					string key = Key(member.kind, member.zdo);
					tallies[member.kind].depleted++;
					tallies[member.kind].blocked++;
					stillDepleted.Add(key);
					state.DepletedSince(key, now);
				}
				if (missing.Count > 0)
				{
					tallies[Kind.LocationObject].blocked += missing.Count(m => !Inside(m.slot.position));
				}
				members = members.Where(m => Inside(m.zdo.GetPosition())).ToList();
				depleted = depleted.Where(m => Inside(m.zdo.GetPosition())).ToList();
				missing = missing.Where(m => Inside(m.slot.position)).ToList();
				if (depleted.Count == 0 && missing.Count == 0)
				{
					return false;
				}
			}
			string due = null;
			double dueBy = double.MinValue;
			double soonest = double.MinValue;
			foreach (Candidate member in depleted)
			{
				string key = Key(member.kind, member.zdo);
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
			foreach ((LocationObjects.Slot slot, bool _) in missing)
			{
				double hours = Hours(state.DepletedSince(slot.key, now), now);
				double over = hours - settings.LocationObjectsAfterHours.Value;
				soonest = Math.Max(soonest, over);
				if (over >= 0 && over > dueBy)
				{
					dueBy = over;
					due = $"{Describe(Kind.LocationObject)} {Name(slot.prefab)} missing {hours:0.##} h";
				}
			}
			if (due == null)
			{
				dueBy = soonest;
			}
			Tally slots = missing.Count > 0 ? tallies[Kind.LocationObject] : null;
			double idle = double.MaxValue;
			foreach (Candidate member in members)
			{
				idle = Math.Min(idle, Hours(state.LastSeen(ZoneSystem.GetZone(member.zdo.GetPosition())), now));
			}
			foreach ((LocationObjects.Slot slot, bool _) in missing)
			{
				idle = Math.Min(idle, Hours(state.LastSeen(ZoneSystem.GetZone(slot.position)), now));
			}
			if (due == null || idle < settings.IdleHours.Value || group.fresh)
			{
				depleted.ForEach(member => tallies[member.kind].waiting++);
				if (slots != null)
				{
					slots.waiting += missing.Count;
				}
				if (dryRun && s_waitingLogged++ < settings.LogDetailsPerPass.Value)
				{
					RegrowthPlugin.Log.LogInfo(
						$"Waiting: {group.name ?? "a lone object"} at {group.position.x:0},{group.position.z:0}{(group.dungeon ? " (dungeon)" : "")}: {Summary(members, depleted, missing.Count)}; "
						+ (due != null ? $"due ({due})" : $"due in {-dueBy:0.#} h") + $", area idle {(idle > 1e6 ? "always" : idle.ToString("0.##") + " h")} (needs {settings.IdleHours.Value:0.#})"
						+ (group.fresh ? "; a part of it was read only in this pass, so it is decided from the next." : "."));
				}
				return false;
			}
			if (HeldBy(members, missing, blockers, out string held))
			{
				depleted.ForEach(member => tallies[member.kind].blocked++);
				if (slots != null)
				{
					slots.blocked += missing.Count;
				}
				// Told once a run: what keeps a whole location from coming back, for days maybe, is what the owner of a server wants to find.
				if (heldLogged.Add($"{group.name} {group.position.x:0.0},{group.position.z:0.0} {held}"))
				{
					RegrowthPlugin.Log.LogInfo($"Held: {group.name ?? "a lone object"} at {group.position.x:0},{group.position.z:0}{(group.dungeon ? " (dungeon)" : "")}: {Summary(members, depleted, missing.Count)}; due: {due}, area idle {idle:0.##} h; {held}.");
				}
				return false;
			}
			if (members.Any(member => InUse(member.zdo)) || missing.Any(m => ZoneSystem.instance.m_zones.ContainsKey(ZoneSystem.GetZone(m.slot.position))))
			{
				depleted.ForEach(member => tallies[member.kind].inUse++);
				if (slots != null)
				{
					slots.inUse += missing.Count;
				}
				return false;
			}
			Dictionary<Kind, int> counts = new Dictionary<Kind, int>();
			HashSet<ZDOID> used = new HashSet<ZDOID>(depleted.Select(m => m.uid));
			foreach (Candidate member in members)
			{
				// Every chest of the location is restocked, used or not: whether a player took anything cannot be told from it.
				if (!used.Contains(member.uid) && !(member.kind == Kind.Chest && IsRestockable(member.zdo, out _)))
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
			int cleared = 0;
			Dictionary<int, int> kinds = new Dictionary<int, int>();
			foreach ((LocationObjects.Slot slot, bool piece) in missing)
			{
				List<ZDO> leftovers = settings.LocationObjectsClearRemains.Value ? RemainsNear(remains, slot.position) : new List<ZDO>();
				cleared += leftovers.Count;
				slots.regrown++;
				kinds[slot.prefab] = kinds.TryGetValue(slot.prefab, out int count) ? count + 1 : 1;
				PutBack(slot, piece, now, dryRun, leftovers, regrown);
			}
			int closed = 0;
			foreach ((ZDO door, ZDOID uid, int prefab) in group.doors)
			{
				if (ZDOMan.instance.GetZDO(uid) != door || door.GetPrefab() != prefab)
				{
					continue;
				}
				Vector3 at = door.GetPosition();
				Vector2s zone = ZoneSystem.GetZone(at);
				// Not one near a player's build: that part of the place is theirs. Nor one somebody can be at, it may stand a zone away from the rest.
				if ((apart == null || Inside(at)) && !IsBlocked(blockers, at) && !InUse(door) && !ZoneSystem.instance.m_zones.ContainsKey(zone)
					&& Hours(state.LastSeen(zone), now) >= settings.IdleHours.Value)
				{
					closed++;
					if (!dryRun)
					{
						door.Set(ZDOVars.s_state, 0);
					}
				}
			}
			Tally first = tallies[depleted.Count > 0 ? depleted[0].kind : Kind.LocationObject];
			if (first.logged < settings.LogDetailsPerPass.Value)
			{
				first.logged++;
				string what = string.Join(", ", counts.Select(c => $"{c.Value} {Describe(c.Key)}{(c.Value == 1 ? "" : "s")}")
					.Concat(missing.Count > 0 ? new[] { $"{missing.Count} location object{(missing.Count == 1 ? "" : "s")} ("
						+ $"{string.Join(", ", kinds.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Value} {Name(k.Key)}"))}{(kinds.Count > 12 ? ", ..." : "")})" } : new string[0])
					.Concat(closed > 0 ? new[] { $"{closed} door{(closed == 1 ? "" : "s")} closed" } : new string[0]));
				RegrowthPlugin.Log.LogInfo(
					$"{(dryRun ? "Would regrow" : "Regrew")} {group.name ?? "a lone object"} at {group.position.x:0},{group.position.z:0}{(group.dungeon ? " (dungeon)" : "")}: {what}; "
					+ $"due: {due}, area idle {idle:0.##} h" + (cleared > 0 ? $"; {cleared} fractured remain(s) {(dryRun ? "would go" : "go")} with them" : "")
					+ (apart != null ? $"; on the surface stays what is near a player's build: {apart}." : "."));
			}
			return true;
		}

		// Groups logged as held, once a run.
		private readonly HashSet<string> heldLogged = new HashSet<string>();

		// The first of the group near a player's build or a tombstone, and which; what is there and not used up holds it back too, as what is missing does.
		private bool HeldBy(IEnumerable<Candidate> members, IEnumerable<(LocationObjects.Slot slot, bool piece)> missing, Dictionary<Vector2s, List<Vector3>> blockers, out string held)
		{
			foreach (Candidate member in members)
			{
				Vector3 position = member.zdo.GetPosition();
				if (BlockedBy(blockers, position, out Vector3 at, out int prefab))
				{
					held = $"{Describe(member.kind)} {Name(member.prefab)} at {position.x:0},{position.y:0},{position.z:0} is near {Name(prefab)} at {at.x:0},{at.y:0},{at.z:0}";
					return true;
				}
			}
			foreach ((LocationObjects.Slot slot, bool _) in missing)
			{
				if (BlockedBy(blockers, slot.position, out Vector3 at, out int prefab))
				{
					held = $"{Describe(Kind.LocationObject)} {Name(slot.prefab)} at {slot.position.x:0},{slot.position.y:0},{slot.position.z:0} is near {Name(prefab)} at {at.x:0},{at.y:0},{at.z:0}";
					return true;
				}
			}
			held = null;
			return false;
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
			A location object is missing when nothing of its prefab stands on its very spot, within
			PieceTolerance of where the game put it. What the game placed never moves, and of one kind it
			stands close: GoblinCamp2 on the live world (23 Oct 2026) has 215 pairs of one kind within 1.5 m
			among its 293 walls, poles and roofs, the nearest 0.2 m apart, so one wall standing hid its missing
			neighbours; of the crypts' gems and mud piles 218 of 469 and 126 of 703 have another of their kind
			that near (30 Oct 2026), and a gem taken beside one did not come back. Its height counts too (walls
			stand stacked 1 m apart), except for what the game puts on the ground after placing it (SnapToGround):
			the log benches around a village's campfire stand 0.11-0.19 m off their layout's height, and a test
			of that height put a second one beside each. Only what moves by itself (LocationObjects.MovesByItself:
			a rock that falls, a blob of tar afloat on its pit) is elsewhere when it is seen again: within
			SlotTolerance (horizontally) and SlotHeight of its height, for a dungeon's rooms lie 5000 m above their
			entrance, and a prop up there must not stand for one below. "In use" is its zone being loaded on the
			server: an object created there now would appear in front of whoever is near.
		*/
		private const float SlotTolerance = 1.5f;
		private const float PieceTolerance = 0.1f;
		private const float SlotHeight = 10f;

		/*
			Watched objects standing, by prefab and 2 m square, near the places watched (LocationObjects.NearSlots):
			a village's slots are hundreds of pieces among hundreds of others, a dvergr town's thousands. A chain of
			indices rather than a list per square: a pass collects up to as many objects as there are places.
		*/
		private class Standing
		{
			private const float Cell = 2f;
			private readonly Dictionary<long, int> first = new Dictionary<long, int>();
			private readonly List<ZDO> zdos = new List<ZDO>();
			private readonly List<int> next = new List<int>();

			public int Count => zdos.Count;

			private static long Key(int prefab, int x, int z)
			{
				return ((long)prefab << 32) | ((long)(x & 0xFFFF) << 16) | (long)(z & 0xFFFF);
			}

			public void Add(ZDO zdo, int prefab, Vector3 position)
			{
				long key = Key(prefab, Mathf.FloorToInt(position.x / Cell), Mathf.FloorToInt(position.z / Cell));
				next.Add(first.TryGetValue(key, out int head) ? head : -1);
				first[key] = zdos.Count;
				zdos.Add(zdo);
			}

			// What of the prefab stands in the position's square and the eight around it: everything within Cell of it, and some more.
			public void Near(int prefab, Vector3 position, List<ZDO> into)
			{
				into.Clear();
				int x = Mathf.FloorToInt(position.x / Cell);
				int z = Mathf.FloorToInt(position.z / Cell);
				for (int dz = -1; dz <= 1; dz++)
				{
					for (int dx = -1; dx <= 1; dx++)
					{
						if (first.TryGetValue(Key(prefab, x + dx, z + dz), out int i))
						{
							for (; i >= 0; i = next[i])
							{
								into.Add(zdos[i]);
							}
						}
					}
				}
			}
		}

		private readonly List<ZDO> near = new List<ZDO>();

		// Whether something of the slot's prefab stands on it; original: one the game made, not one put back. What stands on its very spot goes in onSpot.
		private bool Stands(LocationObjects.Slot slot, bool piece, Standing standing, out bool original, out List<ZDO> onSpot)
		{
			bool stands = false;
			original = false;
			onSpot = null;
			standing.Near(slot.prefab, slot.position, near);
			foreach (ZDO zdo in near)
			{
				Vector3 p = zdo.GetPosition();
				float x = p.x - slot.position.x;
				float z = p.z - slot.position.z;
				float y = Mathf.Abs(p.y - slot.position.y);
				bool here = piece || !movingPrefabs.Contains(slot.prefab)
					? x * x + z * z <= PieceTolerance * PieceTolerance && (slot.snaps || y <= PieceTolerance)
					: x * x + z * z <= SlotTolerance * SlotTolerance && y <= SlotHeight;
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
			return stands;
		}

		// Whether the slot has its object. One the game made standing again (its zone was generated again) clears a fall; extra ones on its very spot go.
		private bool Look(LocationObjects.Slot slot, bool piece, Standing standing, bool dryRun, Dictionary<Vector2s, List<Vector3>> blockers, Tally tally)
		{
			if (!Stands(slot, piece, standing, out bool original, out List<ZDO> onSpot))
			{
				return false;
			}
			// A put-back twin of another slot on the same spot does not count.
			if (original && state.ForgetFell(slot.key))
			{
				RegrowthPlugin.Log.LogInfo($"{Name(slot.prefab)} of {slot.source} at {slot.position.x:0},{slot.position.y:0},{slot.position.z:0}, which fell after being put back, stands in its place again; "
					+ "it is put back as before from now on.");
			}
			if (onSpot != null && onSpot.Count > 1)
			{
				RemoveExtra(slot, onSpot, dryRun, blockers, tally);
			}
			return true;
		}

		private void DecideSlot(LocationObjects.Slot slot, DateTime now, bool dryRun, Dictionary<Vector2s, List<Vector3>> blockers,
			Standing standing, Dictionary<Vector2s, List<ZDO>> remains, Dictionary<Vector2s, List<ZDO>> worldPieces,
			Tally tally, HashSet<string> stillDepleted, HashSet<string> regrown)
		{
			bool piece = holderPrefabs.Contains(slot.prefab);
			if (Look(slot, piece, standing, dryRun, blockers, tally))
			{
				return;
			}
			string key = slot.key;
			Vector2s zone = ZoneSystem.GetZone(slot.position);
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
			if (!Supported(slot, worldPieces))
			{
				tally.unsupported++;
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
			PutBack(slot, piece, now, dryRun, leftovers, regrown);
		}

		// Whether the slot's object is missing and can be put back. One put back that fell and one whose holder is gone stay away by themselves: they do not make the location due, nor hold it back.
		private bool Missing(LocationObjects.Slot slot, DateTime now, bool dryRun, Dictionary<Vector2s, List<Vector3>> blockers, Standing standing,
			Dictionary<Vector2s, List<ZDO>> worldPieces, Tally tally, HashSet<string> stillDepleted, out bool piece)
		{
			piece = holderPrefabs.Contains(slot.prefab);
			if (Look(slot, piece, standing, dryRun, blockers, tally))
			{
				return false;
			}
			tally.depleted++;
			stillDepleted.Add(slot.key);
			if (state.Fell(slot.key, out DateTime _))
			{
				tally.fell++;
				return false;
			}
			state.DepletedSince(slot.key, now);
			if (!Supported(slot, worldPieces))
			{
				tally.unsupported++;
				return false;
			}
			return true;
		}

		// Whether what held the slot's object up stands; one that does not is told once a run.
		private bool Supported(LocationObjects.Slot slot, Dictionary<Vector2s, List<ZDO>> worldPieces)
		{
			if (LocationObjects.Supported(slot, worldPieces))
			{
				return true;
			}
			if (unsupportedLogged.Add(slot.key))
			{
				RegrowthPlugin.Log.LogInfo($"Not putting back {Name(slot.prefab)} of {slot.source} at {slot.position.x:0},{slot.position.y:0},{slot.position.z:0}: what held it is gone "
					+ $"({string.Join(", ", slot.supports.Select(s => $"{Name(s.prefab)} at {s.position.x:0.0},{s.position.y:0.0},{s.position.z:0.0}"))}).");
			}
			return false;
		}

		private void PutBack(LocationObjects.Slot slot, bool piece, DateTime now, bool dryRun, List<ZDO> leftovers, HashSet<string> regrown)
		{
			if (dryRun)
			{
				return;
			}
			foreach (ZDO remainder in leftovers)
			{
				remainder.SetOwner(ZDOMan.GetSessionID());
				ZDOMan.instance.DestroyZDO(remainder);
				remainsGone.Add(remainder.m_uid);
			}
			ZDO made = LocationObjects.Create(slot.prefab, slot.position, slot.rotation, slot.scale);
			if (piece)
			{
				made.Set(PutBackHash, now.Ticks);
				made.Set(SlotKeyHash, slot.key);
			}
			// Filled now, as a gem is rolled when it is made: the game fills a chest only on a machine that owns it when it wakes, and one that woke on a player's game first would stay empty.
			if (chestPrefabs.TryGetValue(slot.prefab, out Container chest))
			{
				Refill(made, chest);
			}
			// Not inside a dungeon: there is no ground up there but the land 5000 m below. No room or cave of 1.0.20 has such an object.
			if (slot.snaps && !Inside(slot.position))
			{
				made.Set(SettleHash, true);
				if (slot.snapOffset != 0f)
				{
					made.Set(SettleOffsetHash, slot.snapOffset);
				}
				unsettled.Add(made.m_uid);
			}
			regrown.Add(slot.key);
		}

		/*
			The game settles what has a SnapToGround on the ground right after it has generated a location. Put back, such an object stands at the height of
			its layout: on a copy of the live world (30 Oct 2026) the bones by a shipwreck stand up to 4.1 m from it (27 of 122 over 1 m), the treasure there up to
			0.85 m. The world generator's height for the spot is further off still (up to 9 m): a location levels its ground. So each is settled as soon as the
			server has it loaded and the ground under it, here and not by SnapToGround.Snap: nearly all of them have the component on the layout's copy only (a
			test on 30 Oct found it on the prefab of just 4 kinds, none of them these), and what is made from the prefab has none. That takes Dedicated
			Simulation, which loads what is near players on the server; without it the object stays where it was put back. A player's game that had it loaded a
			moment before the server shows the new height when it next loads it.
		*/
		private const float SettleIntervalSeconds = 0.25f;
		private List<ZDOID> unsettled = new List<ZDOID>();
		private float settleTimer;

		private void Settle()
		{
			bool generated = false;
			for (int i = unsettled.Count - 1; i >= 0; i--)
			{
				ZDO zdo = ZDOMan.instance.GetZDO(unsettled[i]);
				// The scan that made this list takes many frames, and this may have settled the object meanwhile.
				if (zdo == null || !zdo.GetBool(SettleHash))
				{
					unsettled.RemoveAt(i);
					continue;
				}
				ZNetView view = ZNetScene.instance.FindInstance(zdo);
				if (!view)
				{
					continue;
				}
				// As SnapToGround.SnappAll does, for the ground to be there with what players did to it.
				if (!generated)
				{
					generated = true;
					Heightmap.ForceGenerateAll();
				}
				Vector3 at = view.transform.position;
				if (!ZoneSystem.instance.GetGroundHeight(at, out float ground))
				{
					continue;
				}
				unsettled.RemoveAt(i);
				zdo.Set(SettleHash, false);
				float from = at.y;
				at.y = ground + zdo.GetFloat(SettleOffsetHash);
				zdo.SetOwner(ZDOMan.GetSessionID());
				view.transform.position = at;
				zdo.SetPosition(at);
				RegrowthPlugin.Log.LogInfo($"{Name(zdo.GetPrefab())} put back at {at.x:0},{at.z:0} is settled on the ground: {from:0.00} -> {at.y:0.00}.");
			}
		}

		/*
			A building piece put back that fell by itself (WearNTear_Destroy_Patch) is not put
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
				RegrowthPlugin.Log.LogInfo($"{Name(prefab)} at {p.x:0},{p.y:0},{p.z:0}, put back {Hours(putBack, now):0.##} h ago, fell by itself: "
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
			int slotsThere = locationObjects.SlotsAt(slot.prefab, slot.position, SameSpot);
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

		// The fractured remains standing at a slot, in zones the server has not loaded; not one that is a location's own part on its place (a stump in its layout).
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
							&& !remainsGone.Contains(zdo.m_uid) && ZDOMan.instance.GetZDO(zdo.m_uid) == zdo && locationObjects.IsRemains(zdo.GetPrefab())
							&& !ZoneSystem.instance.m_zones.ContainsKey(ZoneSystem.GetZone(p))
							&& locationObjects.SlotsAt(zdo.GetPrefab(), p, SameSpot) == 0)
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

		private bool IsDepleted(Kind kind, ZDO zdo, DateTime now)
		{
			switch (kind)
			{
				case Kind.Core:
					return zdo.GetBool(ZDOVars.s_picked, corePrefabs[zdo.GetPrefab()]);
				case Kind.Chest:
					// Used: emptied, its loot gone and only a player's things left in it, or a player has been near it since it was last restocked (its clock then keeps running).
					return IsRestockable(zdo, out List<ItemDrop.ItemData> items)
						&& (items.Count == 0 || OnlyPlayersItems(zdo, items) || state.SeenSince(ZoneSystem.GetZone(zdo.GetPosition()), now.AddHours(-Math.Max(0f, settings.IdleHours.Value))) || state.HasKey(Key(kind, zdo)));
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
		// and chests from cheats are left alone. One that cannot be read is kept, to be safe.
		private static bool IsRestockable(ZDO zdo, out List<ItemDrop.ItemData> items)
		{
			items = null;
			return zdo.GetLong(ZDOVars.s_creator, 0L) == 0L && zdo.GetBool(ZDOVars.s_addedDefaultItems) && !zdo.GetBool(ZDOVars.s_cheated) && PlayersItems.TryRead(zdo, out items);
		}

		// Nothing of the world's left, only what players put in, and room for the loot: 4 of the 8 such chests on the live world (30 Oct 2026) had not been near a player
		// since. One with no room stays as it is, or its location would be due again after every restock, with nothing to bring back to it.
		private bool OnlyPlayersItems(ZDO zdo, List<ItemDrop.ItemData> items)
		{
			Container prefab = chestPrefabs[zdo.GetPrefab()];
			if (items.Any(item => !Theirs(prefab, item)))
			{
				return false;
			}
			return !settings.ChestsKeepPlayersItems.Value || items.Count < prefab.m_width * prefab.m_height;
		}

		/*
			A player's thing in a chest: what has been in their bag (ItemData.m_pickedUp), and a stack of the loot they put more of the same on, which
			stays the world's (Inventory.AddItem adds to the stack in the slot, and so does the game's "place stacks") -- told by holding more than the
			loot can roll. A few put on it, up to that, go with the loot.
		*/
		private static bool Theirs(Container prefab, ItemDrop.ItemData item)
		{
			if (item.m_pickedUp)
			{
				return true;
			}
			int most = MostLoot(prefab.m_defaultItems, item.m_shared.m_name);
			return most > 0 && item.m_stack > most;
		}

		/*
			The most of one item a chest's loot rolls (DropTable.GetDropListItems): its biggest stack in every draw, or each entry of it once when it is one
			of each. A stack is Random.Range(min, top + 1) (DropTable.AddItemToList); with a resource rate other than 1 it is Round(Random.Range(min, top + 1)
			* rate) instead, the float range's upper end included, at most the item's stack size (Game.ScaleDrops), unless the drop or its item type is not scaled.
		*/
		private static int MostLoot(DropTable table, string name)
		{
			int most = 0, all = 0;
			foreach (DropTable.DropData drop in table.m_drops)
			{
				ItemDrop itemDrop = drop.m_item ? drop.m_item.GetComponent<ItemDrop>() : null;
				if (itemDrop == null || itemDrop.m_itemData.m_shared.m_name != name)
				{
					continue;
				}
				ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;
				int stack = Mathf.Min(shared.m_maxStackSize, drop.m_stackMax);
				if (!drop.m_dontScale && Game.instance && Game.m_resourceRate != 1f && !Game.instance.m_nonScaledDropTypes.Contains(shared.m_itemType))
				{
					stack = Mathf.Min((int)Mathf.Max(1f, Mathf.Round((stack + 1) * Game.m_resourceRate)), shared.m_maxStackSize > 1 ? shared.m_maxStackSize : 10000);
				}
				most = Mathf.Max(most, stack);
				all += stack;
			}
			return table.m_oneOfEach ? all : most * Mathf.Max(0, table.m_dropMax);
		}

		// The doors of dungeons that went since the last pass (LocationObjects.AddDoor): it goes through the slots over many frames, and they join between passes.
		private readonly List<(int prefab, Vector3 position, Quaternion rotation)> newDoors = new List<(int, Vector3, Quaternion)>();
		// What a zone generated again took the place of: that door did not go.
		private readonly HashSet<ZDOID> replaced = new HashSet<ZDOID>();

		// The game calls this for everything destroyed, on the server also what a player's game destroyed.
		private void OnDestroyed(ZDO zdo)
		{
			try
			{
				if (locationObjects != null && locationObjects.IsGeneratorDoor(zdo.GetPrefab()) && !replaced.Remove(zdo.m_uid) && zdo.GetLong(ZDOVars.s_creator, 0L) == 0L
					&& locationObjects.SlotsAt(zdo.GetPrefab(), zdo.GetPosition(), 0.05f) == 0 && state.AddDoor(zdo.GetPrefab(), zdo.GetPosition(), zdo.GetRotation()))
				{
					newDoors.Add((zdo.GetPrefab(), zdo.GetPosition(), zdo.GetRotation()));
					// At once, as OnFell: the world can be saved without the door before the pass ends, and nothing else would know where it stood.
					SaveState();
				}
			}
			catch (Exception e)
			{
				RegrowthPlugin.Log.LogWarning($"Could not note a door that went: {e}");
			}
		}

		internal void Replacing(ZDO zdo)
		{
			if (locationObjects != null && locationObjects.IsGeneratorDoor(zdo.GetPrefab()))
			{
				replaced.Add(zdo.m_uid);
			}
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

		/*
			What Container.AddDefaultItems and Container.Save do, done on the ZDO directly. A player's things (Theirs) stay in their slots (KeepPlayersItems), each item of the
			loot takes a free slot and stacks with nothing of theirs: their stack never grows, and the next restock can tell the loot from it. On the live world of 30 Oct 2026, 8 of 1422 chests
			held such things, 14 stones, a torch, a stone axe, 17 bone fragments and some raw meat, and were never restocked for it. All 8 had free slots.
		*/
		private void Refill(ZDO zdo, Container prefab)
		{
			Inventory inventory = new Inventory(prefab.m_name, prefab.m_bkg, prefab.m_width, prefab.m_height);
			foreach (ItemDrop.ItemData item in prefab.m_defaultItems.GetDropListItems())
			{
				inventory.AddItem(item);
			}
			if (settings.ChestsKeepPlayersItems.Value && PlayersItems.TryRead(zdo, out List<ItemDrop.ItemData> items) && items.Any(item => Theirs(prefab, item)))
			{
				Inventory both = new Inventory(prefab.m_name, prefab.m_bkg, prefab.m_width, prefab.m_height);
				both.GetAllItems().AddRange(items.Where(item => Theirs(prefab, item)));
				foreach (ItemDrop.ItemData loot in inventory.GetAllItems())
				{
					if (!FreeSlot(both, prefab, out Vector2i slot))
					{
						break;
					}
					loot.m_gridPos = slot;
					both.GetAllItems().Add(loot);
				}
				inventory = both;
			}
			ZPackage package = new ZPackage();
			inventory.Save(package);
			zdo.Set(ZDOVars.s_items, package.GetArray());
			zdo.Set(ZDOVars.s_addedDefaultItems, true);
		}

		private static bool FreeSlot(Inventory inventory, Container prefab, out Vector2i slot)
		{
			for (int y = 0; y < prefab.m_height; y++)
			{
				for (int x = 0; x < prefab.m_width; x++)
				{
					if (inventory.GetItemAt(x, y) == null)
					{
						slot = new Vector2i(x, y);
						return true;
					}
				}
			}
			slot = default;
			return false;
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

		// What each blocker of the pass is, in the order of the blockers of its zone. Those up inside a dungeon again apart, for what is up there: a base on the surface over a crypt is thousands of pieces.
		private Dictionary<Vector2s, List<int>> blockerPrefabs = new Dictionary<Vector2s, List<int>>();
		private Dictionary<Vector2s, List<Vector3>> insideBlockers = new Dictionary<Vector2s, List<Vector3>>();
		private Dictionary<Vector2s, List<int>> insideBlockerPrefabs = new Dictionary<Vector2s, List<int>>();

		private void AddBlocker(Dictionary<Vector2s, List<Vector3>> blockers, Vector3 position, int prefab)
		{
			Add(blockers, blockerPrefabs, position, prefab);
			if (Inside(position))
			{
				Add(insideBlockers, insideBlockerPrefabs, position, prefab);
			}
		}

		private static void Add(Dictionary<Vector2s, List<Vector3>> positions, Dictionary<Vector2s, List<int>> prefabs, Vector3 position, int prefab)
		{
			Vector2s zone = ZoneSystem.GetZone(position);
			if (!positions.TryGetValue(zone, out List<Vector3> list))
			{
				list = new List<Vector3>();
				positions[zone] = list;
				prefabs[zone] = new List<int>();
			}
			list.Add(position);
			prefabs[zone].Add(prefab);
		}

		private bool IsBlocked(Dictionary<Vector2s, List<Vector3>> blockers, Vector3 position)
		{
			return BlockedBy(blockers, position, out Vector3 _, out int _);
		}

		private bool BlockedBy(Dictionary<Vector2s, List<Vector3>> blockers, Vector3 position, out Vector3 at, out int prefab)
		{
			at = default;
			prefab = 0;
			float radius = Mathf.Max(0f, settings.PlayerBuildRadius.Value);
			int zones = Mathf.CeilToInt(radius / 64f);
			bool inside = Inside(position);
			Dictionary<Vector2s, List<Vector3>> positions = inside ? insideBlockers : blockers;
			Dictionary<Vector2s, List<int>> prefabs = inside ? insideBlockerPrefabs : blockerPrefabs;
			Vector2s zone = ZoneSystem.GetZone(position);
			for (int dy = -zones; dy <= zones; dy++)
			{
				for (int dx = -zones; dx <= zones; dx++)
				{
					Vector2s near = new Vector2s(zone.x + dx, zone.y + dy);
					if (!positions.TryGetValue(near, out List<Vector3> list))
					{
						continue;
					}
					for (int i = 0; i < list.Count; i++)
					{
						Vector3 blocker = list[i];
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
							at = blocker;
							prefab = prefabs[near][i];
							return true;
						}
					}
				}
			}
			return false;
		}

		private const float InteriorHeight = 2500f;

		private static bool Inside(Vector3 position)
		{
			return position.y > InteriorHeight;
		}
	}

	/*
		A building piece the plugin put back that goes by itself (Regrowth.OnFell): the game destroys it
		from WearNTear.UpdateWear, with no hit, once what wears it took its last health -- too little
		support (UpdateSupport), or where it counts ash, lava, heavy snow or the wrong biome, which would
		take it again each time as well. Only where the server runs the piece: with Dedicated Simulation it
		owns what stands near players; in vanilla a player's game does, and the fall never passes through
		here. ApplyDamage writes the health (0 or less) before it calls Destroy; a player's hammer
		(RPC_Remove) calls it with the health as it was, so a piece taken down that way is no fall.
	*/
	[HarmonyPatch(typeof(WearNTear), "Destroy", typeof(HitData), typeof(bool))]
	internal static class WearNTear_Destroy_Patch
	{
		private static bool failed;

		static void Prefix(WearNTear __instance, HitData hitData)
		{
			if (Regrowth.Active == null || hitData != null || !__instance.m_nview || !__instance.m_nview.IsValid())
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
