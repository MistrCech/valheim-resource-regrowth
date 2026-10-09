using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ResourceRegrowth
{
	/*
		Zone reset (off by default, [Zones]). A zone a player has been in is generated again by the game
		itself once nobody has been near it for Zones.IdleHours, as on its first generation: trees, rocks
		and ore deposits, bushes, pickables, the location with its buildings, chests, spawners and
		dungeon, and the ground. ZoneSystem.SpawnZone runs again in ghost mode, the way Dedicated
		Simulation generates the zones around players. Measured on a copy of a live world: a fresh world
		with the same seed generated 860 of 916 zones without a location exactly as they were, the rest
		with a few trees or rocks elsewhere (placement raycasts against whatever colliders are around).

		Nothing is deleted before the zone is generated again: ghost generation only sees colliders, and
		the objects of a zone nobody is near have none. Every object the game makes then takes the place
		of the old one of its prefab standing there (within half a metre), in the zone or over its edge --
		a location or dungeon reaching into the next zone, a cave whose interior lies next to another
		dungeon's, are matched object by object, not guessed. What is left over goes only when it is
		something the zone itself or life and felling there made: vegetation, stumps and logs, fractured
		rocks, wild creatures, birds and fish, items lying about, the ground's record, the zone's
		controller and proxy. Anything else left over stays: it may be a neighbour's.

		A location's rotation is not reproducible from the world seed: the game draws it from an unseeded
		random number (ZoneSystem.PlaceLocations). The proxy keeps it, so it is read before and given back
		to ZoneSystem.SpawnLocation -- the one game method this plugin patches, only with Zones on --
		rounded to the game's 22.5-degree step (the save truncates Euler angles to half a degree, and a
		dungeon's seed follows its generator's position to the metre). Tested: every location back at the
		same rotation and position, every dungeon with the same rooms.

		Never touched:
		- a zone with anything of a player's in it or within PlayerBuildRadius of it: pieces they built
		  (portals, beds, crops planted as pieces), tombstones, ships and carts, items a player has
		  carried (ItemData.m_pickedUp, also inside the world's chests and barrels), tamed creatures, the
		  world's item and armour stands holding something, cultivated ground, paved ground outside the
		  zone's location;
		- a zone where players have planted saplings (a grown tree has no mark of its planter);
		- a protected location (Zones.ProtectedLocations and every location the game places only once),
		  and a trader or anything within a protected location's reach in a neighbouring zone;
		- a zone the server has loaded, or next to one it has, or anything in it a connected player owns;
		- the first half hour after the server starts (presence is not known for the time it was off).

		If the game cannot generate the zone (it says not ready, or throws), nothing of it is deleted: the
		zone is marked generated again, its location placed, and whatever the attempt made is removed.

		Bookkeeping: a zone counts as visited when a player is in it or next to it (RecordPresence). At
		this feature's first run every zone already generated counts as visited, since what players did
		there before cannot be told. A zone's idle time runs from the latest of: the last time a player was
		near it, the time from which presence is known completely (RegrowthState.PresenceKnownSince), and
		this feature's first run.
	*/
	internal class ZoneReset
	{
		public class Tally
		{
			public int visited, waiting, blocked, due, reset, failed, logged;
			public readonly Dictionary<string, int> why = new Dictionary<string, int>();
		}

		// The zone reset of the running plugin, for the SpawnLocation patch.
		internal static ZoneReset Active;

		private const int ReadyFrames = 600;
		private static readonly TimeSpan Backoff = TimeSpan.FromHours(1);

		private readonly Settings settings;
		private readonly RegrowthState state;
		private readonly Func<int, string> name;
		private readonly Func<bool> worldLoaded;
		private readonly DateTime started;
		private readonly HashSet<int> shipPrefabs = new HashSet<int>();
		private readonly HashSet<int> itemPrefabs = new HashSet<int>();
		private readonly HashSet<int> characterPrefabs = new HashSet<int>();
		private readonly HashSet<int> traderPrefabs = new HashSet<int>();
		private readonly HashSet<int> itemStandPrefabs = new HashSet<int>();
		private readonly HashSet<int> armorStandPrefabs = new HashSet<int>();
		private readonly HashSet<int> containerPrefabs = new HashSet<int>();
		private readonly HashSet<int> plantPrefabs = new HashSet<int>();
		private readonly HashSet<int> vegetationPrefabs = new HashSet<int>();
		private readonly HashSet<int> debrisPrefabs = new HashSet<int>();
		// Birds and fish: life the zone spawns that is neither a character nor an item.
		private readonly HashSet<int> wildlifePrefabs = new HashSet<int>();
		private readonly Dictionary<int, string> kinds = new Dictionary<int, string>();
		private readonly HashSet<string> protectedNames;
		private readonly int playerPrefab = "Player".GetStableHashCode();
		private readonly int tombstonePrefab = "Player_tombstone".GetStableHashCode();
		private readonly int terrainPrefab = Terrain.CompilerPrefab.GetStableHashCode();
		private readonly int proxyPrefab;
		private readonly int zoneCtrlPrefab;
		// ArmorStand slot keys: "<i>_item" (ArmorStand.InitKeys).
		private static readonly int[] s_armorSlotKeys = Enumerable.Range(0, 16).Select(i => (i + "_item").GetStableHashCode()).ToArray();
		private bool broken;
		private bool announcedGrace;

		// Collected by the pass that runs it: what of players' lies where, and each zone's ground record.
		private readonly Dictionary<Vector2s, List<Vector3>> traces = new Dictionary<Vector2s, List<Vector3>>();
		private readonly Dictionary<Vector2s, ZDO> terrain = new Dictionary<Vector2s, ZDO>();
		// Zones that could not be done now (not ready, a neighbour loaded ...) wait a while before they are tried again.
		private readonly Dictionary<Vector2s, DateTime> retryAfter = new Dictionary<Vector2s, DateTime>();

		public ZoneReset(Settings settings, RegrowthState state, Func<int, string> name, Func<bool> worldLoaded)
		{
			this.settings = settings;
			this.state = state;
			this.name = name;
			this.worldLoaded = worldLoaded;
			started = DateTime.UtcNow;
			ZoneSystem zs = ZoneSystem.instance;
			proxyPrefab = zs.m_locationProxyPrefab.name.GetStableHashCode();
			zoneCtrlPrefab = zs.m_zoneCtrlPrefab.name.GetStableHashCode();
			foreach (ZoneSystem.ZoneVegetation vegetation in zs.m_vegetation)
			{
				if (vegetation != null && vegetation.m_prefab)
				{
					vegetationPrefabs.Add(vegetation.m_prefab.name.GetStableHashCode());
				}
			}
			foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
			{
				if (!prefab)
				{
					continue;
				}
				int hash = prefab.name.GetStableHashCode();
				if (prefab.GetComponent<Ship>() || prefab.GetComponent<Vagon>())
				{
					shipPrefabs.Add(hash);
				}
				if (prefab.GetComponent<ItemDrop>())
				{
					itemPrefabs.Add(hash);
				}
				if (prefab.GetComponent<Character>())
				{
					characterPrefabs.Add(hash);
				}
				if (prefab.GetComponentInChildren<Trader>(true))
				{
					traderPrefabs.Add(hash);
				}
				if (prefab.GetComponent<RandomFlyingBird>() || prefab.GetComponent<Fish>())
				{
					wildlifePrefabs.Add(hash);
				}
				if (prefab.GetComponent<ItemStand>())
				{
					itemStandPrefabs.Add(hash);
				}
				if (prefab.GetComponent<ArmorStand>())
				{
					armorStandPrefabs.Add(hash);
				}
				if (prefab.GetComponent<Container>())
				{
					containerPrefabs.Add(hash);
				}
				if (prefab.GetComponent<Plant>())
				{
					plantPrefabs.Add(hash);
				}
				// What felling leaves: stumps, logs, half logs.
				TreeBase tree = prefab.GetComponent<TreeBase>();
				if (tree)
				{
					if (tree.m_stubPrefab) debrisPrefabs.Add(tree.m_stubPrefab.name.GetStableHashCode());
					if (tree.m_logPrefab) debrisPrefabs.Add(tree.m_logPrefab.name.GetStableHashCode());
				}
				TreeLog log = prefab.GetComponent<TreeLog>();
				if (log)
				{
					debrisPrefabs.Add(hash);
					if (log.m_subLogPrefab) debrisPrefabs.Add(log.m_subLogPrefab.name.GetStableHashCode());
				}
				// What mining leaves: a rock or deposit hit once turns into its fractured self.
				Destructible destructible = prefab.GetComponent<Destructible>();
				if (destructible && destructible.m_spawnWhenDestroyed)
				{
					debrisPrefabs.Add(destructible.m_spawnWhenDestroyed.name.GetStableHashCode());
				}
				kinds[hash] = KindOf(prefab);
			}
			protectedNames = new HashSet<string>(settings.ZonesProtected.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
			foreach (ZoneSystem.ZoneLocation location in zs.m_locations)
			{
				if (location != null && location.m_enable && location.m_unique)
				{
					protectedNames.Add(location.m_prefabName);
				}
			}
			Active = this;
			RegrowthPlugin.Log.LogInfo($"Zones{(settings.ZonesDryRun.Value || settings.DryRun.Value ? " (dry run: nothing will be changed)" : "")}: a zone players have been in is generated again after {settings.ZonesIdleHours.Value:0.#} h with nobody near, "
				+ $"at most {settings.ZonesPerPass.Value} per pass, none in the first {settings.ZonesGraceMinutes.Value:0} minutes after a start; protected locations: {string.Join(", ", protectedNames.OrderBy(n => n, StringComparer.Ordinal))}. "
				+ $"Visited zones known: {state.VisitedCount}, zones with players' saplings: {state.PlantedCount}{(state.ZonesFirstRun.HasValue ? $", counting since {state.ZonesFirstRun.Value.ToLocalTime():yyyy-MM-dd HH:mm}" : "")}.");
		}

		private static string KindOf(GameObject prefab)
		{
			string n = prefab.name.ToLowerInvariant();
			if (prefab.GetComponent<TreeBase>()) return "trees";
			if (prefab.GetComponent<TreeLog>() || n.Contains("stub")) return "logs and stumps";
			if (prefab.GetComponent<MineRock>() || prefab.GetComponent<MineRock5>() || n.Contains("rock") || n.Contains("copper") || n.Contains("tin")
				|| n.Contains("silver") || n.Contains("obsidian") || n.Contains("mudpile") || n.Contains("marble") || n.Contains("flametal")) return "rocks and ore deposits";
			if (prefab.GetComponent<Pickable>()) return "pickables";
			if (prefab.GetComponent<Destructible>()) return "bushes and small plants";
			if (prefab.GetComponent<Character>()) return "creatures";
			if (prefab.GetComponent<ItemDrop>()) return "items lying about";
			if (prefab.GetComponent<WearNTear>()) return "buildings";
			return "other";
		}

		public void Stop()
		{
			if (Active == this)
			{
				Active = null;
			}
		}

		// Every 10 s for every player in the world: the zone they are in and the eight around it.
		public void MarkVisited(Vector2s zone, DateTime now)
		{
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					state.MarkVisited(new Vector2s(zone.x + dx, zone.y + dy), now);
				}
			}
		}

		// Called by the pass before it notes every object.
		public void Clear()
		{
			traces.Clear();
			terrain.Clear();
		}

		public void Note(ZDO zdo, int prefab)
		{
			if (prefab == terrainPrefab)
			{
				terrain[ZoneSystem.GetZone(zdo.GetPosition())] = zdo;
				return;
			}
			if (plantPrefabs.Contains(prefab) && zdo.GetLong(ZDOVars.s_creator, 0L) != 0L)
			{
				// A sapling or crop a player planted: once grown it carries nothing of its planter, so the zone is remembered.
				state.MarkPlanted(ZoneSystem.GetZone(zdo.GetPosition()));
			}
			// Chests are read only for the zones that are due (Collect): too dear for every object of every pass.
			if (IsPlayers(zdo, prefab, readContainers: false))
			{
				Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
				if (!traces.TryGetValue(zone, out List<Vector3> list))
				{
					traces[zone] = list = new List<Vector3>();
				}
				list.Add(zdo.GetPosition());
			}
		}

		/*
			Something a zone reset must never delete. Pieces players built and tombstones are the pass's
			own blockers already; this adds what of a player's is not a built piece.
		*/
		private bool IsPlayers(ZDO zdo, int prefab, bool readContainers)
		{
			if (zdo.GetLong(ZDOVars.s_creator, 0L) != 0L || prefab == tombstonePrefab || prefab == playerPrefab || shipPrefabs.Contains(prefab))
			{
				return true;
			}
			if (itemPrefabs.Contains(prefab))
			{
				return Carried(zdo);
			}
			if (characterPrefabs.Contains(prefab))
			{
				return zdo.GetBool(ZDOVars.s_tamed);
			}
			if (itemStandPrefabs.Contains(prefab))
			{
				return zdo.GetInt(ZDOVars.s_item) != 0;
			}
			if (armorStandPrefabs.Contains(prefab))
			{
				return s_armorSlotKeys.Any(key => zdo.GetInt(key) != 0);
			}
			if (readContainers && containerPrefabs.Contains(prefab))
			{
				return HoldsCarried(zdo);
			}
			return false;
		}

		/*
			ItemDrop.SaveToZDO: a version byte (109), then ItemData.Save -- durability (int), grid x, grid y,
			world level, flags (bit 0: it has been in a player's bag). An item the world made and nobody
			has loaded since has no data yet (ItemDrop.Start writes it): the world's.
		*/
		private static bool Carried(ZDO zdo)
		{
			byte[] data = zdo.GetByteArray(ZDOVars.s_itemData);
			if (data == null)
			{
				return false;
			}
			if (data.Length < 9 || data[0] != 109)
			{
				return true;
			}
			return (data[8] & 1) != 0;
		}

		// A chest, barrel or crate of the world with anything in it that has been in a player's bag: someone's storage.
		private static bool HoldsCarried(ZDO zdo)
		{
			byte[] items = zdo.GetByteArray(ZDOVars.s_items);
			if (items == null)
			{
				return false;
			}
			try
			{
				Inventory inventory = new Inventory("zone reset", null, 16, 16);
				inventory.Load(new ZPackage(items));
				return inventory.GetAllItems().Any(item => item.m_pickedUp);
			}
			catch (Exception)
			{
				// Unreadable: kept, to be safe.
				return true;
			}
		}

		public bool TakeRotation(Vector2s zone, int location, out Quaternion rotation) => state.TakeRotation(zone, location, out rotation);

		// When a player was last near the zone, as far as is known: never earlier than the time presence has been known from completely.
		private DateTime IdleSince(Vector2s zone)
		{
			DateTime since = state.TryLastSeen(zone, out DateTime seen) ? seen : DateTime.MinValue;
			if (since < state.PresenceKnownSince)
			{
				since = state.PresenceKnownSince;
			}
			if (state.ZonesFirstRun.HasValue && since < state.ZonesFirstRun.Value)
			{
				since = state.ZonesFirstRun.Value;
			}
			return since;
		}

		/*
			Decide every visited zone, then generate the due ones again, longest idle first, at most
			PerPass, trying at most three times as many, one per frame. blockers: the pass's player-built
			pieces and tombstones by zone.
		*/
		public IEnumerator Run(DateTime now, Dictionary<Vector2s, List<Vector3>> blockers, Tally tally)
		{
			if (broken)
			{
				Count(tally, "stopped after an error until restart");
				yield break;
			}
			ZoneSystem zs = ZoneSystem.instance;
			if (!state.ZonesFirstRun.HasValue)
			{
				state.ZonesFirstRun = now;
				foreach (Vector2s zone in zs.m_generatedZones)
				{
					state.MarkVisited(zone, now);
				}
				RegrowthPlugin.Log.LogInfo($"Zones: first run -- what players did before cannot be told, so all {state.VisitedCount} zones generated so far count as visited; none is generated again before {settings.ZonesIdleHours.Value:0.#} h from now.");
			}
			bool dryRun = settings.ZonesDryRun.Value || settings.DryRun.Value;
			double needed = Math.Max(0f, settings.ZonesIdleHours.Value);
			List<KeyValuePair<Vector2s, double>> due = new List<KeyValuePair<Vector2s, double>>();
			foreach (KeyValuePair<Vector2s, DateTime> visit in state.Visited.ToList())
			{
				Vector2s zone = visit.Key;
				if (!zs.IsZoneGenerated(zone))
				{
					continue;
				}
				tally.visited++;
				double idle = (now - IdleSince(zone)).TotalHours;
				if (idle < needed)
				{
					tally.waiting++;
					continue;
				}
				string why = Blocked(zone, blockers);
				if (why != null)
				{
					Count(tally, why);
					continue;
				}
				due.Add(new KeyValuePair<Vector2s, double>(zone, idle));
			}
			tally.due = due.Count;
			if ((now - started).TotalMinutes < settings.ZonesGraceMinutes.Value)
			{
				if (!announcedGrace && due.Count > 0)
				{
					announcedGrace = true;
					RegrowthPlugin.Log.LogInfo($"Zones: {due.Count} zone(s) due, but none is generated again in the first {settings.ZonesGraceMinutes.Value:0} minutes after a start.");
				}
				yield break;
			}
			int perPass = Math.Max(0, settings.ZonesPerPass.Value);
			int attempts = 0;
			foreach (KeyValuePair<Vector2s, double> zone in due.OrderByDescending(z => z.Value))
			{
				if (tally.reset >= perPass || attempts >= 3 * perPass)
				{
					break;
				}
				if (retryAfter.TryGetValue(zone.Key, out DateTime after) && now < after)
				{
					continue;
				}
				attempts++;
				IEnumerator one = Reset(zone.Key, zone.Value, dryRun, tally);
				while (one.MoveNext())
				{
					yield return null;
					if (!worldLoaded())
					{
						yield break;
					}
				}
				if (broken)
				{
					yield break;
				}
				yield return null;
				if (!worldLoaded())
				{
					yield break;
				}
			}
		}

		private static void Count(Tally tally, string why)
		{
			tally.blocked++;
			tally.why[why] = (tally.why.TryGetValue(why, out int n) ? n : 0) + 1;
		}

		private void Later(Vector2s zone, Tally tally, string why)
		{
			retryAfter[zone] = DateTime.UtcNow + Backoff;
			Count(tally, why);
		}

		// Why a zone must stay as it is, or null.
		private string Blocked(Vector2s zone, Dictionary<Vector2s, List<Vector3>> blockers)
		{
			ZoneSystem zs = ZoneSystem.instance;
			bool hasLocation = zs.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance) && instance.m_placed && instance.m_location != null;
			if (hasLocation && protectedNames.Contains(instance.m_location.m_prefabName))
			{
				return "protected location";
			}
			if (state.IsPlanted(zone))
			{
				return "planted by players";
			}
			if (zs.m_zones.ContainsKey(zone))
			{
				return "loaded";
			}
			float radius = Mathf.Max(0f, settings.PlayerBuildRadius.Value);
			if (Near(blockers, zone, radius) || Near(traces, zone, radius))
			{
				return "players' things in it or near";
			}
			if (terrain.TryGetValue(zone, out ZDO ground))
			{
				string paint = PlayersGround(ground, zone, hasLocation ? instance : default(ZoneSystem.LocationInstance), hasLocation);
				if (paint != null)
				{
					return paint;
				}
			}
			return null;
		}

		// Anything in `points` within `radius` of the zone's square (horizontally).
		private static bool Near(Dictionary<Vector2s, List<Vector3>> points, Vector2s zone, float radius)
		{
			Vector3 center = ZoneSystem.GetZonePos(zone);
			float half = ZoneSystem.c_ZoneSizeHalf;
			int ring = Mathf.CeilToInt(radius / ZoneSystem.c_ZoneSize);
			for (int dy = -ring; dy <= ring; dy++)
			{
				for (int dx = -ring; dx <= ring; dx++)
				{
					if (!points.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out List<Vector3> list))
					{
						continue;
					}
					foreach (Vector3 p in list)
					{
						float x = Mathf.Max(0f, Mathf.Abs(p.x - center.x) - half);
						float z = Mathf.Max(0f, Mathf.Abs(p.z - center.z) - half);
						if (x * x + z * z <= radius * radius)
						{
							return true;
						}
					}
				}
			}
			return false;
		}

		// Cultivated ground anywhere, or paved ground outside the zone's location: players' fields and roads.
		private static string PlayersGround(ZDO ground, Vector2s zone, ZoneSystem.LocationInstance instance, bool hasLocation)
		{
			byte[] bytes = ground.GetByteArray(ZDOVars.s_TCData);
			if (bytes == null)
			{
				return null;
			}
			Terrain.Data data;
			try
			{
				data = Terrain.Decode(bytes);
			}
			catch (Exception)
			{
				return "unreadable ground record";
			}
			int vertices = data.modifiedPaint.Length;
			int width = Mathf.RoundToInt(Mathf.Sqrt(vertices)) - 1;
			if (width <= 0 || (width + 1) * (width + 1) != vertices)
			{
				return "unreadable ground record";
			}
			Vector3 center = ZoneSystem.GetZonePos(zone);
			float half = width * 0.5f;
			float keep = hasLocation ? instance.m_location.m_exteriorRadius + 8f : -1f;
			for (int i = 0; i < vertices; i++)
			{
				if (!data.modifiedPaint[i])
				{
					continue;
				}
				Color c = data.paintMask[i];
				if (c.g > 0.5f)
				{
					return "cultivated ground";
				}
				if (c.b > 0.5f)
				{
					float x = center.x - half + i % (width + 1) - instance.m_position.x;
					float z = center.z - half + i / (width + 1) - instance.m_position.z;
					if (!hasLocation || x * x + z * z > keep * keep)
					{
						return "paved ground";
					}
				}
			}
			return null;
		}

		/*
			One zone. Cheap refusals first; then wait until the game could generate it (heightmap built,
			location prefab loaded -- SpawnZone's own two conditions); then, in a single frame, check again
			that nobody came near and nothing of a player's is there, mark the zone not generated and the
			location not placed, generate it, and let what the game made tell which old objects it replaces.
		*/
		private IEnumerator Reset(Vector2s zone, double idle, bool dryRun, Tally tally)
		{
			ZoneSystem zs = ZoneSystem.instance;
			Vector3 center = ZoneSystem.GetZonePos(zone);
			bool hasLocation = zs.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance) && instance.m_placed && instance.m_location != null;
			string where = $"zone {zone.x},{zone.y} ({(hasLocation ? instance.m_location.m_prefabName : "no location")})";
			List<ZDO> old = Collect(zone, hasLocation, out string refusal, out ZDO proxy);
			if (old == null)
			{
				Later(zone, tally, refusal);
				yield break;
			}
			if (dryRun)
			{
				tally.reset++;
				if (tally.logged++ < settings.LogDetailsPerPass.Value)
				{
					RegrowthPlugin.Log.LogInfo($"Zones: would generate {where} again, idle {idle:0.#} h: {old.Count} objects the world made there ({Kinds(old)}).");
				}
				yield break;
			}
			Heightmap template = zs.m_zonePrefab.GetComponentInChildren<Heightmap>();
			bool ready = false;
			for (int frame = 0; frame < ReadyFrames; frame++)
			{
				ready = HeightmapBuilder.instance.IsTerrainReady(center, template.m_width, template.m_scale, template.IsDistantLod, WorldGenerator.instance)
					&& (!hasLocation || zs.PokeCanSpawnLocation(instance.m_location, isFirstSpawn: true));
				if (ready)
				{
					break;
				}
				yield return null;
				if (!worldLoaded())
				{
					yield break;
				}
			}
			if (!ready)
			{
				Later(zone, tally, "terrain or location not ready");
				yield break;
			}
			// From here on, one frame: nothing can change in between.
			if (!zs.IsZoneGenerated(zone) || (DateTime.UtcNow - IdleSince(zone)).TotalHours < settings.ZonesIdleHours.Value)
			{
				Later(zone, tally, "a player came near");
				yield break;
			}
			old = Collect(zone, hasLocation, out refusal, out proxy);
			if (old == null)
			{
				Later(zone, tally, refusal);
				yield break;
			}
			int locationHash = hasLocation ? instance.m_location.m_prefabName.GetStableHashCode() : 0;
			if (hasLocation)
			{
				state.KeepRotation(zone, locationHash, PlacedRotation(proxy));
			}
			ZoneSystem.LocationInstance placed = instance;
			zs.m_generatedZones.Remove(zone);
			if (hasLocation)
			{
				instance.m_placed = false;
				zs.m_locationInstances[zone] = instance;
			}
			uint firstNew = ZDOMan.instance.m_nextUid;
			bool generated = false;
			GameObject root = null;
			Exception failure = null;
			try
			{
				generated = zs.SpawnZone(zone, ZoneSystem.SpawnMode.Ghost, out root);
			}
			catch (Exception e)
			{
				failure = e;
			}
			finally
			{
				if (ZNetView.m_ghostInit)
				{
					// A generation that threw between StartGhostInit and FinishGhostInit would leave every later object without an instance.
					ZNetView.FinishGhostInit();
				}
				if (root)
				{
					UnityEngine.Object.Destroy(root);
				}
			}
			List<ZDO> made = Made(firstNew);
			if (failure != null || !generated || !zs.IsZoneGenerated(zone))
			{
				// Nothing old was deleted: undo what the attempt made and put the zone back as it was.
				Undo(zone, made, placed, hasLocation, locationHash);
				tally.failed++;
				if (failure != null)
				{
					broken = true;
					RegrowthPlugin.Log.LogError($"Zones: generating {where} again failed and was undone ({made.Count} new objects removed); zone resets stop until restart: {failure}");
				}
				else
				{
					Later(zone, tally, "the game did not generate it");
				}
				yield break;
			}
			// Taken by SpawnLocation when the location was placed now; if it is still here, the location was not.
			if (hasLocation && state.TakeRotation(zone, locationHash, out Quaternion _))
			{
				RegrowthPlugin.Log.LogWarning($"Zones: {where} was generated again without its location being placed.");
			}
			try
			{
				HashSet<ZDO> gone = new HashSet<ZDO>();
				int replaced = Replace(zone, made, old, gone, out int beyond, out int refused);
				List<Vector3> guarded = ProtectedNear(zone);
				int cleared = 0, kept = 0;
				Dictionary<string, int> keptNames = new Dictionary<string, int>();
				foreach (ZDO zdo in old)
				{
					if (gone.Contains(zdo))
					{
						continue;
					}
					Vector3 p = zdo.GetPosition();
					int prefab = zdo.GetPrefab();
					if (OnlyTheZoneMakes(prefab, p.y > 3000f) && !traderPrefabs.Contains(prefab) && !guarded.Any(g => Horizontal(g, p) <= g.y))
					{
						Delete(zdo, gone);
						cleared++;
					}
					else
					{
						kept++;
						string n = name(prefab);
						keptNames[n] = (keptNames.TryGetValue(n, out int c) ? c : 0) + 1;
					}
				}
				state.ForgetVisit(zone);
				tally.reset++;
				if (tally.logged++ < settings.LogDetailsPerPass.Value)
				{
					RegrowthPlugin.Log.LogInfo($"Zones: generated {where} again, idle {idle:0.#} h: the game made {made.Count} objects; {replaced} replaced the old ones in their places"
						+ (beyond > 0 ? $" ({beyond} of them over the zone's edge)" : "") + (refused > 0 ? $", {refused} new ones removed again where the old one was in use" : "")
						+ $", {cleared} old ones left over went ({Kinds(old.Where(gone.Contains))})"
						+ (kept > 0 ? $", {kept} kept as maybe a neighbour's ({string.Join(", ", keptNames.OrderByDescending(k => k.Value).Take(4).Select(k => $"{k.Value} {k.Key}"))})" : "") + ".");
				}
			}
			catch (Exception e)
			{
				broken = true;
				state.ForgetVisit(zone);
				tally.reset++;
				RegrowthPlugin.Log.LogError($"Zones: {where} was generated again but sorting out its old objects failed; zone resets stop until restart: {e}");
			}
		}

		// Remove what a failed attempt made, and mark the zone generated and its location placed again; no old object was touched.
		private void Undo(Vector2s zone, List<ZDO> made, ZoneSystem.LocationInstance placed, bool hasLocation, int locationHash)
		{
			ZoneSystem zs = ZoneSystem.instance;
			HashSet<ZDO> gone = new HashSet<ZDO>();
			foreach (ZDO zdo in made)
			{
				Delete(zdo, gone);
			}
			foreach (GameObject spawned in zs.m_tempSpawnedObjects)
			{
				if (spawned)
				{
					UnityEngine.Object.Destroy(spawned);
				}
			}
			zs.m_tempSpawnedObjects.Clear();
			// ZoneSystem.SpawnLocation sets this for a location that wants randomly damaged buildings and clears it only at its end.
			WearNTear.m_randomInitialDamage = false;
			zs.m_generatedZones.Add(zone);
			if (hasLocation)
			{
				zs.m_locationInstances[zone] = placed;
				state.TakeRotation(zone, locationHash, out Quaternion _);
			}
		}

		/*
			The game turns a location only about the vertical, in steps of 22.5 degrees (PlaceLocations:
			Random.Range(0, 16) * 22.5, or the slope's direction rounded to 22.5). The proxy's saved
			rotation can be half a degree off that step (Euler angles truncated to 0.5 on save), and the
			dungeon's seed follows its generator's position to the metre, so the step is taken.
		*/
		private static Quaternion PlacedRotation(ZDO proxy)
		{
			float yaw = proxy.GetRotation().eulerAngles.y;
			return Quaternion.Euler(0f, Mathf.Round(yaw / 22.5f) * 22.5f, 0f);
		}

		private string Kinds(IEnumerable<ZDO> zdos)
		{
			return string.Join(", ", zdos.GroupBy(z => kinds.TryGetValue(z.GetPrefab(), out string k) ? k : "other")
				.OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"));
		}

		/*
			Left over after the zone was made again, and only ever made by the zone itself or by life and
			felling there. Inside a dungeon nothing grows: there only creatures and items lying about; a
			mushroom or a root left over may be a neighbouring dungeon's whose rooms reach in.
		*/
		private bool OnlyTheZoneMakes(int prefab, bool inside)
		{
			if (characterPrefabs.Contains(prefab) || itemPrefabs.Contains(prefab) || wildlifePrefabs.Contains(prefab))
			{
				return true;
			}
			return !inside && (vegetationPrefabs.Contains(prefab) || debrisPrefabs.Contains(prefab) || prefab == terrainPrefab || prefab == zoneCtrlPrefab || prefab == proxyPrefab);
		}

		/*
			Protected locations of the neighbouring zones whose reach (exterior radius and a margin) comes
			into this zone: nothing left over within it goes. As (x, radius, z) -- the radius in y.
		*/
		private List<Vector3> ProtectedNear(Vector2s zone)
		{
			List<Vector3> guarded = new List<Vector3>();
			ZoneSystem zs = ZoneSystem.instance;
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					if ((dx != 0 || dy != 0) && zs.m_locationInstances.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out ZoneSystem.LocationInstance near)
						&& near.m_placed && near.m_location != null && protectedNames.Contains(near.m_location.m_prefabName))
					{
						guarded.Add(new Vector3(near.m_position.x, Mathf.Max(near.m_location.m_exteriorRadius, near.m_location.m_interiorRadius) + 16f, near.m_position.z));
					}
				}
			}
			return guarded;
		}

		private static float Horizontal(Vector3 guard, Vector3 p)
		{
			float x = guard.x - p.x, z = guard.z - p.z;
			return Mathf.Sqrt(x * x + z * z);
		}

		private static void Delete(ZDO zdo, HashSet<ZDO> gone)
		{
			if (!gone.Add(zdo))
			{
				return;
			}
			zdo.SetOwner(ZDOMan.GetSessionID());
			ZDOMan.instance.DestroyZDO(zdo);
		}

		// What the game made now, by id: every object the server creates takes the next one.
		private static List<ZDO> Made(uint firstNew)
		{
			long server = ZDOMan.GetSessionID();
			List<ZDO> made = new List<ZDO>();
			for (uint id = firstNew; id < ZDOMan.instance.m_nextUid; id++)
			{
				ZDO zdo = ZDOMan.instance.GetZDO(new ZDOID(server, id));
				if (zdo != null)
				{
					made.Add(zdo);
				}
			}
			return made;
		}

		/*
			Each object made now replaces the old one of its prefab standing in its place, in this zone or a
			neighbouring one (a deleted object stays in its sector until the deletions go out at the end of
			the frame, so `gone` keeps track). Where the old one beyond the zone's edge is in use, or holds a
			player's things, the new one goes instead. Returns how many replaced; `beyond`: of them outside
			the zone; `refused`: new ones removed again.
		*/
		private int Replace(Vector2s zone, List<ZDO> made, List<ZDO> old, HashSet<ZDO> gone, out int beyond, out int refused)
		{
			beyond = 0;
			refused = 0;
			HashSet<ZDO> fresh = new HashSet<ZDO>(made);
			Dictionary<Vector2s, List<ZDO>> sectors = new Dictionary<Vector2s, List<ZDO>>();
			long server = ZDOMan.GetSessionID();
			int replaced = 0;
			foreach (ZDO zdo in made)
			{
				Vector3 p = zdo.GetPosition();
				Vector2s at = ZoneSystem.GetZone(p);
				bool here = at.x == zone.x && at.y == zone.y;
				if (!sectors.TryGetValue(at, out List<ZDO> there))
				{
					if (here)
					{
						there = old;
					}
					else
					{
						there = new List<ZDO>();
						ZDOMan.instance.FindSectorObjects(at, new SimulationDistance(0, 0), there);
					}
					sectors[at] = there;
				}
				int prefab = zdo.GetPrefab();
				bool inside = p.y > 3000f;
				foreach (ZDO candidate in there)
				{
					if (gone.Contains(candidate) || fresh.Contains(candidate) || candidate.GetPrefab() != prefab || !candidate.IsValid())
					{
						continue;
					}
					Vector3 q = candidate.GetPosition();
					float x = q.x - p.x, z = q.z - p.z, y = q.y - p.y;
					if (x * x + z * z > 0.25f || Mathf.Abs(y) > (inside ? 0.5f : 2f))
					{
						continue;
					}
					long owner = candidate.GetOwner();
					if (!here && (IsPlayers(candidate, prefab, readContainers: true) || (owner != 0L && owner != server && ZNet.instance.GetPeer(owner) != null)
						|| ZNetScene.instance.FindInstance(candidate)))
					{
						// The old one must stay: the new one would stand next to it.
						Delete(zdo, gone);
						refused++;
						break;
					}
					Delete(candidate, gone);
					replaced++;
					if (!here)
					{
						beyond++;
					}
					break;
				}
			}
			return replaced;
		}

		/*
			The zone's objects, or null with the reason when something must stop it: anything of a
			player's (also in the world's chests), anything loaded or owned by a connected player, the zone
			or a neighbouring one loaded (objects there may be replaced too), a location without its proxy
			(its rotation would be lost) or a proxy without its location.
		*/
		private List<ZDO> Collect(Vector2s zone, bool hasLocation, out string refusal, out ZDO proxy)
		{
			refusal = null;
			proxy = null;
			ZoneSystem zs = ZoneSystem.instance;
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					if (zs.m_zones.ContainsKey(new Vector2s(zone.x + dx, zone.y + dy)))
					{
						refusal = dx == 0 && dy == 0 ? "loaded" : "a neighbouring zone loaded";
						return null;
					}
				}
			}
			List<ZDO> objects = new List<ZDO>();
			ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), objects);
			long server = ZDOMan.GetSessionID();
			foreach (ZDO zdo in objects)
			{
				int prefab = zdo.GetPrefab();
				if (IsPlayers(zdo, prefab, readContainers: true))
				{
					refusal = "players' things in it";
					return null;
				}
				long owner = zdo.GetOwner();
				if ((owner != 0L && owner != server && ZNet.instance.GetPeer(owner) != null) || ZNetScene.instance.FindInstance(zdo))
				{
					refusal = "in use";
					return null;
				}
				if (prefab == proxyPrefab)
				{
					proxy = zdo;
				}
			}
			if (hasLocation != (proxy != null))
			{
				refusal = hasLocation ? "location without its proxy" : "proxy without a location";
				return null;
			}
			return objects;
		}
	}

	/*
		A location placed again by a zone reset is turned as it was the first time (ZoneReset). Only
		Full and Ghost placement; the proxy's own spawn (Client) already carries the rotation.
	*/
	[HarmonyPatch(typeof(ZoneSystem), "SpawnLocation")]
	internal static class ZoneSystem_SpawnLocation_Patch
	{
		static void Prefix(ZoneSystem.ZoneLocation location, Vector3 pos, ref Quaternion rot, ZoneSystem.SpawnMode mode)
		{
			if (mode == ZoneSystem.SpawnMode.Client || location == null || ZoneReset.Active == null)
			{
				return;
			}
			if (ZoneReset.Active.TakeRotation(ZoneSystem.GetZone(pos), location.m_prefabName.GetStableHashCode(), out Quaternion kept))
			{
				rot = kept;
			}
		}
	}
}
