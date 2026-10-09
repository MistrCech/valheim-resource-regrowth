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

		- Surtling core stands (hide-when-picked pickables): ZDO "picked" back to false.
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
			public int depleted, waiting, blocked, unsupported, inUse, regrown, logged;
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

		private readonly MonoBehaviour host;
		private readonly Settings settings;
		private RegrowthState state;
		private readonly Dictionary<int, bool> corePrefabs = new Dictionary<int, bool>(); // prefab -> picked by default
		private readonly Dictionary<int, Container> chestPrefabs = new Dictionary<int, Container>();
		private readonly HashSet<int> spawnerPrefabs = new HashSet<int>();
		private readonly HashSet<int> generatorPrefabs = new HashSet<int>();
		private readonly HashSet<int> piecePrefabs = new HashSet<int>();
		private readonly HashSet<int> tombstonePrefabs = new HashSet<int>();
		private readonly int terrainPrefab = Terrain.CompilerPrefab.GetStableHashCode();
		private Dictionary<int, float> protectorPrefabs = new Dictionary<int, float>();
		private readonly Dictionary<int, string> prefabNames = new Dictionary<int, string>();
		private LocationObjects locationObjects;
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
			RegrowthPlugin.Log.LogInfo($"Terrain{(settings.TerrainEnabled.Value ? "" : " (off)")}: ground is protected around {protectorPrefabs.Count} kinds of base object and ward: {string.Join(", ", protectorNames)}");
			if (settings.LocationObjectsEnabled.Value)
			{
				locationObjects = new LocationObjects(SplitList(settings.LocationObjectsPrefabs.Value));
			}
			state = new RegrowthState(StatePath());
			DateTime now = Now();
			state.Load(now);
			passTimer = 60f;
			RegrowthPlugin.Log.LogInfo(
				$"Active{(settings.DryRun.Value ? " (dry run: nothing will be changed)" : "")}. "
				+ $"Surtling core stands: {corePrefabs.Count} prefab(s), treasure chests: {chestPrefabs.Count}, one-time spawners: {spawnerPrefabs.Count}, "
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
			HashSet<string> cores = SplitList(settings.CorePrefabs.Value);
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
				if (prefab.GetComponent<TombStone>())
				{
					tombstonePrefabs.Add(hash);
				}
				if (cores.Contains(prefab.name))
				{
					Pickable pickable = prefab.GetComponent<Pickable>();
					if (pickable && pickable.m_hideWhenPicked && pickable.m_respawnTimeMinutes <= 0f)
					{
						corePrefabs[hash] = pickable.m_defaultPicked;
					}
					else
					{
						RegrowthPlugin.Log.LogWarning($"{prefab.name} is not a non-respawning hide-when-picked pickable; ignored.");
					}
				}
				if (prefab.name.StartsWith("TreasureChest_", StringComparison.Ordinal) && !excludedChests.Contains(prefab.name))
				{
					Container container = prefab.GetComponent<Container>();
					if (container && !container.m_rootObjectOverride && container.m_defaultItems.m_drops.Count > 0)
					{
						chestPrefabs[hash] = container;
					}
					else
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
				Dictionary<Vector2s, List<ZDO>> standing = new Dictionary<Vector2s, List<ZDO>>();
				// Building pieces the world generated (no creator), which is what holds a location object up.
				Dictionary<Vector2s, List<ZDO>> worldPieces = new Dictionary<Vector2s, List<ZDO>>();
				// Dungeon generators (dungeon interiors, camps, villages): what an object inside a dungeon belongs to.
				Dictionary<Vector2s, List<ZDO>> generators = new Dictionary<Vector2s, List<ZDO>>();

				// 1. One pass over every object: note player builds and tombstones, collect candidates.
				for (int i = 0; i < zdos.Count; i++)
				{
					ZDO zdo = zdos[i];
					int prefab = zdo.GetPrefab();
					if (tombstonePrefabs.Contains(prefab)
						|| (piecePrefabs.Contains(prefab) && zdo.GetLong(ZDOVars.s_creator, 0L) != 0L))
					{
						AddBlocker(blockers, zdo.GetPosition());
					}
					else if (locationObjects != null && piecePrefabs.Contains(prefab))
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
					else if (locationObjects != null && locationObjects.IsWanted(prefab))
					{
						Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
						if (!standing.TryGetValue(zone, out List<ZDO> list))
						{
							standing[zone] = list = new List<ZDO>();
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
					foreach (LocationObjects.Slot slot in locationObjects.AllSlots())
					{
						try
						{
							DecideSlot(slot, now, dryRun, blockers, standing, worldPieces, tallies[Kind.LocationObject], stillDepleted, regrown);
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
				// The world changed and the clocks were reset; a crash before the next autosave would
				// lose the one and keep the other, so save now (the same call as the autosave).
				if ((regrown.Count > 0 || terrainStepped) && ZNet.instance.EnoughDiskSpaceAvailable(out bool _))
				{
					ZNet.instance.Save(sync: false, saveOtherPlayerProfiles: true, waitForNextFrame: true);
				}
				state.ForgetZonesSeenBefore(now.AddHours(-Math.Max(0f, settings.IdleHours.Value)));

				RegrowthPlugin.Log.LogInfo(
					$"Pass{(dryRun ? " (dry run)" : "")}: "
					+ string.Join("; ", tallies.Select(t =>
						t.Key == Kind.Terrain
							? $"terrain zones {t.Value.depleted} modified ({t.Value.waiting} waiting, {t.Value.blocked} all protected, {t.Value.inUse} in use, {t.Value.regrown} {(dryRun ? "would step" : "stepped")})"
							: t.Key == Kind.LocationObject
							? $"location objects {t.Value.depleted} missing ({t.Value.waiting} waiting, {t.Value.blocked} near player builds, {t.Value.unsupported} with what held them gone, {t.Value.inUse} in use, "
								+ $"{t.Value.regrown} {(dryRun ? "would put back" : "put back")})"
							: $"{Describe(t.Key)}s {t.Value.depleted} depleted ({t.Value.waiting} waiting, {t.Value.blocked} near player builds, {t.Value.inUse} in use, "
							+ $"{t.Value.regrown} {(dryRun ? "would regrow" : "regrown")})"))
					+ (locationObjects != null ? $" ({locationObjects.SourceCount} locations and rooms known{(loadedPrefabs > 0 ? $", {loadedPrefabs} prefab(s) read this pass" : "")})" : "")
					+ (settings.LocationsTogether.Value ? $"; {locationsRegrown} location(s) {(dryRun ? "would regrow" : "regrew")} as a whole" : "")
					+ $". Scanned {zdos.Count} objects: {work.ElapsedMilliseconds} ms of work spread over {frames} frames ({wall.ElapsedMilliseconds} ms wall)."
					+ (errors > 0 ? $" {errors} object(s) skipped after errors." : "")
					+ (regrown.Count > 0 || terrainStepped ? " World save requested." : ""));
				terrainStepped = false;
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
				double over = hours - AfterHours(member.kind);
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
			Vector3 position = zdo.GetPosition();
			double modified = Hours(state.DepletedSince(key, now), now);
			double idle = Hours(state.LastSeen(ZoneSystem.GetZone(position)), now);
			if (modified < settings.TerrainAfterHours.Value || idle < settings.IdleHours.Value)
			{
				tally.waiting++;
				return;
			}
			if (state.HasKey(stepKey))
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
			Terrain.Data result = Terrain.Step(data, position, near, settings, stats);
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
			(horizontally) of where the game put it. "In use" is its zone being loaded on the server: an
			object created there now would appear in front of whoever is near.
		*/
		private const float SlotTolerance = 1.5f;

		private void DecideSlot(LocationObjects.Slot slot, DateTime now, bool dryRun, Dictionary<Vector2s, List<Vector3>> blockers,
			Dictionary<Vector2s, List<ZDO>> standing, Dictionary<Vector2s, List<ZDO>> worldPieces, Tally tally, HashSet<string> stillDepleted, HashSet<string> regrown)
		{
			Vector2s zone = ZoneSystem.GetZone(slot.position);
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					if (!standing.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out List<ZDO> list))
					{
						continue;
					}
					foreach (ZDO zdo in list)
					{
						Vector3 p = zdo.GetPosition();
						float x = p.x - slot.position.x;
						float z = p.z - slot.position.z;
						if (zdo.GetPrefab() == slot.prefab && x * x + z * z <= SlotTolerance * SlotTolerance)
						{
							return;
						}
					}
				}
			}
			tally.depleted++;
			string key = string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:0.0}:{3:0.0}:{4:0.0}",
				Kind.LocationObject, Name(slot.prefab).Replace(' ', '_'), slot.position.x, slot.position.y, slot.position.z);
			stillDepleted.Add(key);
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
				return;
			}
			if (ZoneSystem.instance.m_zones.ContainsKey(zone))
			{
				tally.inUse++;
				return;
			}
			tally.regrown++;
			if (tally.logged < settings.LogDetailsPerPass.Value)
			{
				tally.logged++;
				RegrowthPlugin.Log.LogInfo(
					$"{(dryRun ? "Would put back" : "Put back")} {Name(slot.prefab)} of {slot.source} at {slot.position.x:0},{slot.position.y:0},{slot.position.z:0}: "
					+ $"missing {depleted:0.##} h, area idle {idle:0.##} h.");
			}
			if (!dryRun)
			{
				LocationObjects.Create(slot.prefab, slot.position, slot.rotation);
				regrown.Add(key);
			}
		}

		private float AfterHours(Kind kind)
		{
			switch (kind)
			{
				case Kind.Core: return settings.CoresAfterHours.Value;
				case Kind.Chest: return settings.ChestsAfterHours.Value;
				case Kind.LocationObject: return settings.LocationObjectsAfterHours.Value;
				default: return settings.SpawnersAfterHours.Value;
			}
		}

		private static string Describe(Kind kind)
		{
			switch (kind)
			{
				case Kind.Core: return "surtling core stand";
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
		private static bool IsRestockable(ZDO zdo)
		{
			return zdo.GetLong(ZDOVars.s_creator, 0L) == 0L && zdo.GetBool(ZDOVars.s_addedDefaultItems) && !zdo.GetBool(ZDOVars.s_cheated);
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
	}
}
