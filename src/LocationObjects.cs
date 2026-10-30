using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SoftReferenceableAssets;
using UnityEngine;

namespace ResourceRegrowth
{
	/*
		Objects a location or a camp room put into the world when it was generated -- wild beehives in
		abandoned houses, for example -- are deleted when destroyed, so unlike everything else here there
		is nothing left to change. Where each stood is still in the world, exactly:

		- every generated location leaves a LocationProxy object with the location's position, rotation,
		  name and seed (ZoneSystem.CreateLocationProxy). Object i of the location's prefab stood at
		  position + rotation * its position in the prefab (ZoneSystem.SpawnLocation);
		- camps and villages are rooms a DungeonGenerator placed; it keeps every room's hash, position and
		  rotation (DungeonGenerator.Save), and a room's objects stood where PlaceRoom put them.

		Some of those objects only spawn by chance (RandomSpawn, RandomObject). The game rolls them from a
		seed it also keeps -- the location's, or one made from the room's position -- so the roll is
		replayed the way the game made it, and only objects that really spawned count.

		Measured on a live world (26 Sep 2026): of 169 hive places the replay said 47 had a hive; all 29
		hives still standing were on those places, none anywhere else.

		This class finds the places and says which are empty. The prefab layouts and the replays are
		cached: a location's objects never move, and new locations only appear as the world is explored.

		What is watched: the prefabs listed (Prefabs), anywhere a location or room put them, and in the
		locations listed by name (Locations) every breakable part -- what has a WearNTear, Destructible,
		MineRock, MineRock5, TreeBase or TreeLog: the black marble of a dvergr tower, a fortress's walls,
		props, rocks, trees, nests (Breakable). A room counts with the location whose dungeon generator
		built it. What is missing comes back with the rest of its location ([Locations] Together), so that
		what held a piece up stands again with it.
	*/
	internal class LocationObjects
	{
		// One location or room as placed in the world, and the places of what it put there.
		public class Source
		{
			public string name;
			public Vector3 position;
			// Where the location it belongs to is looked for: its own position, a room's the generator's that built it.
			public Vector3 anchor;
			// A room: the prefab of that generator; a location: 0.
			public int generator;
			// Read in this pass: what stands of its kinds was not collected in this pass's scan, so it is decided from the next one.
			public bool fresh;
			public readonly List<Slot> slots = new List<Slot>();
		}

		public struct Slot
		{
			public int prefab;
			public Vector3 position;
			public Quaternion rotation;
			public string source;
			// The state's key (Key): made once here, not for every slot in every pass.
			public string key;
			// The game puts it on the ground after placing it (SnapToGround), at this height above it: only its x and z are the layout's.
			public bool snaps;
			public float snapOffset;
			// The size the location gave it, that the object keeps (ZNetView.m_syncInitialScale); zero: its prefab's.
			public Vector3 scale;
			// Building pieces of the same location or room that stood under or beside it: a hive hangs
			// under a roof, and with the house pulled down it would hang in the air. Empty when nothing
			// networked held it (a cave's rock is part of the location and cannot be removed).
			public List<(int prefab, Vector3 position)> supports;
		}

		private struct Template
		{
			public ZNetView view;
			public Vector3 local;
			public Quaternion rotation;
			public bool random;
			public bool snaps;
			public float snapOffset;
			public Vector3 scale;
			public List<ZNetView> supports;
		}

		private const float SupportRadius = 3f;

		// The prefabs listed (Prefabs).
		private readonly HashSet<int> wanted;
		// The prefabs of every place known, listed or a part of a listed location: what the scan collects as standing.
		private readonly HashSet<int> slotPrefabs = new HashSet<int>();
		// What the game leaves when a watched object is hit: its prefab's name and "_frac", what it breaks into, a tree's stump.
		private readonly HashSet<int> remains = new HashSet<int>();
		private readonly List<Regex> listedPatterns;
		private readonly HashSet<int> excluded;
		private readonly int proxyPrefab;
		private readonly Dictionary<int, DungeonGenerator> generatorPrefabs = new Dictionary<int, DungeonGenerator>();
		// What the generators place in the doorways between their rooms (DungeonGenerator.m_doorTypes).
		private readonly HashSet<int> generatorDoors = new HashSet<int>();
		private readonly Dictionary<int, ZoneSystem.ZoneLocation> locations = new Dictionary<int, ZoneSystem.ZoneLocation>();
		// Location name hash / room hash and whether it is a listed location's -> whether its prefab holds any watched object (checked once).
		private readonly Dictionary<int, bool> locationHasSlots = new Dictionary<int, bool>();
		private readonly Dictionary<(int, bool), bool> roomHasSlots = new Dictionary<(int, bool), bool>();
		private readonly Dictionary<int, bool> listedLocations = new Dictionary<int, bool>();
		// Prefab -> whether a listed location's part of it is watched (Breakable, not in LocationsExclude, a prefab the game knows).
		private readonly Dictionary<int, bool> breakable = new Dictionary<int, bool>();
		// Source key (location or room, by name and position) -> its slots, replay included.
		private readonly Dictionary<string, Source> slots = new Dictionary<string, Source>();
		private readonly List<Source> freshSources = new List<Source>();
		// The proxies and generators nothing new can come from any more: all they put there is known, or none of it is watched. A pass reads each of the others' rooms again.
		private readonly HashSet<ZDOID> settled = new HashSet<ZDOID>();
		// The zones within Near of a slot: only there does the scan collect what stands.
		private readonly HashSet<Vector2s> slotZones = new HashSet<Vector2s>();
		// The sources with a slot in each zone.
		private readonly Dictionary<Vector2s, List<Source>> sourcesByZone = new Dictionary<Vector2s, List<Source>>();
		private const float Near = 2f;

		public int SourceCount => slots.Count;
		public int SlotCount { get; private set; }
		public bool AnyListed => listedPatterns.Count > 0;

		public LocationObjects(IEnumerable<string> prefabNames, IEnumerable<string> locationPatterns, IEnumerable<string> excludeNames)
		{
			List<string> names = prefabNames.ToList();
			wanted = new HashSet<int>(names.Select(n => n.GetStableHashCode()));
			foreach (string name in names)
			{
				AddRemains(name);
			}
			listedPatterns = locationPatterns.Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant)).ToList();
			excluded = new HashSet<int>(excludeNames.Select(n => n.GetStableHashCode()));
			proxyPrefab = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
			foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
			{
				DungeonGenerator generator = prefab ? prefab.GetComponent<DungeonGenerator>() : null;
				if (generator)
				{
					generatorPrefabs[prefab.name.GetStableHashCode()] = generator;
					foreach (DungeonGenerator.DoorDef door in generator.m_doorTypes)
					{
						if (door.m_prefab)
						{
							generatorDoors.Add(door.m_prefab.name.GetStableHashCode());
						}
					}
				}
			}
			foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
			{
				if (location != null && location.m_enable && location.m_prefab.IsValid)
				{
					locations[location.m_prefab.Name.GetStableHashCode()] = location;
				}
			}
		}

		public bool IsGeneratorDoor(int prefab)
		{
			return generatorDoors.Contains(prefab);
		}

		/*
			A door of a dungeon that went. A generator draws its doors as it goes (DungeonGenerator.PlaceDoors: a door in about half the doorways, of a kind drawn too), in
			the middle of a stream of random numbers that only running all of it would repeat, and no layout says where one stood. So its place is learned as it goes, and
			from then on it is a slot like any other, as its location's. Not one on a layout's own slot, and not while a pass is deciding: this changes what it goes through.
		*/
		public void AddDoor(int prefab, Vector3 position, Quaternion rotation)
		{
			GameObject registered = ZNetScene.instance.GetPrefab(prefab);
			if (!registered)
			{
				return;
			}
			string key = Key(registered.name, position);
			string sourceKey = "door:" + key;
			if (slots.ContainsKey(sourceKey) || SlotsAt(prefab, position, 0.05f) > 0)
			{
				return;
			}
			// Its dungeon's location: through the room nearest to it that a generator drawing such doors built, as a room's slots find theirs. 2 of the
			// 1498 doors of the live world (30 Oct 2026) lay outside their dungeon's zone, past its edge.
			Source room = NearestRoom(prefab, position);
			ZoneSystem.LocationInstance instance = default;
			ZoneSystem.ZoneLocation location = room != null ? LocationAt(room.anchor) : InstanceAt(position, ref instance) ? instance.m_location : null;
			if (!Watched(registered, location != null && Listed(location)))
			{
				return;
			}
			string name = location != null ? location.m_prefabName : registered.name;
			Source source = room != null
				? new Source { name = name, position = position, anchor = room.anchor, generator = room.generator }
				: new Source { name = name, position = position, anchor = location != null ? instance.m_position : position };
			source.slots.Add(new Slot { prefab = prefab, position = position, rotation = rotation, source = name, key = key, scale = Vector3.zero, supports = new List<(int, Vector3)>() });
			Add(sourceKey, source);
		}

		private Source NearestRoom(int door, Vector3 position)
		{
			Source nearest = null;
			float best = 48f * 48f;
			foreach (Vector2s zone in ZonesAround(position, 48f))
			{
				if (!sourcesByZone.TryGetValue(zone, out List<Source> list))
				{
					continue;
				}
				foreach (Source source in list)
				{
					float distance = (source.position - position).sqrMagnitude;
					if (source.generator != 0 && distance < best && generatorPrefabs.TryGetValue(source.generator, out DungeonGenerator generator)
						&& generator.m_doorTypes.Any(type => type.m_prefab && type.m_prefab.name.GetStableHashCode() == door))
					{
						best = distance;
						nearest = source;
					}
				}
			}
			return nearest;
		}

		public bool IsSource(int prefab)
		{
			return prefab == proxyPrefab || generatorPrefabs.ContainsKey(prefab);
		}

		// Listed, or the kind of a part of a listed location.
		public bool IsWanted(int prefab)
		{
			return wanted.Contains(prefab) || slotPrefabs.Contains(prefab);
		}

		// Whether a slot is near the zone: what stands farther from every slot is not collected.
		public bool NearSlots(Vector2s zone)
		{
			return slotZones.Contains(zone);
		}

		// How many slots of the prefab lie within the radius (at most Near) of the position.
		public int SlotsAt(int prefab, Vector3 position, float radius)
		{
			int count = 0;
			foreach (Vector2s zone in ZonesAround(position, radius))
			{
				if (!sourcesByZone.TryGetValue(zone, out List<Source> list))
				{
					continue;
				}
				foreach (Source source in list)
				{
					foreach (Slot slot in source.slots)
					{
						if (slot.prefab == prefab && (slot.position - position).sqrMagnitude <= radius * radius && ZoneSystem.GetZone(slot.position) == zone)
						{
							count++;
						}
					}
				}
			}
			return count;
		}

		// The zones of the corners of a square around the position, each once.
		private static IEnumerable<Vector2s> ZonesAround(Vector3 position, float half)
		{
			Vector2s a = ZoneSystem.GetZone(position + new Vector3(-half, 0f, -half));
			Vector2s b = ZoneSystem.GetZone(position + new Vector3(half, 0f, half));
			for (int y = a.y; y <= b.y; y++)
			{
				for (int x = a.x; x <= b.x; x++)
				{
					yield return new Vector2s(x, y);
				}
			}
		}

		public IEnumerable<Source> Sources()
		{
			return slots.Values;
		}

		private void Add(string key, Source source)
		{
			source.fresh = true;
			freshSources.Add(source);
			slots[key] = source;
			SlotCount += source.slots.Count;
			foreach (Slot slot in source.slots)
			{
				GameObject prefab = slotPrefabs.Add(slot.prefab) ? ZNetScene.instance.GetPrefab(slot.prefab) : null;
				if (prefab)
				{
					AddRemains(prefab.name);
				}
				foreach (Vector2s zone in ZonesAround(slot.position, Near))
				{
					slotZones.Add(zone);
				}
				Vector2s own = ZoneSystem.GetZone(slot.position);
				if (!sourcesByZone.TryGetValue(own, out List<Source> list))
				{
					sourcesByZone[own] = list = new List<Source>();
				}
				if (list.Count == 0 || list[list.Count - 1] != source)
				{
					list.Add(source);
				}
			}
		}

		// What a watched object turns into when it breaks (a mud pile into its fractured pile, a rock into its pieces to mine) and a
		// felled tree's stump count as its remains; not an item it drops or an effect it plays.
		private void AddRemains(string name)
		{
			remains.Add((name + "_frac").GetStableHashCode());
			GameObject prefab = ZNetScene.instance.GetPrefab(name);
			if (!prefab)
			{
				return;
			}
			Destructible destructible = prefab.GetComponent<Destructible>();
			TreeBase tree = prefab.GetComponent<TreeBase>();
			foreach (GameObject left in new[] { destructible ? destructible.m_spawnWhenDestroyed : null, tree ? tree.m_stubPrefab : null })
			{
				if (left && left.GetComponent<ZNetView>() && !left.GetComponent<ItemDrop>() && !left.GetComponent<Character>())
				{
					remains.Add(left.name.GetStableHashCode());
				}
			}
		}

		/*
			A part of a location that players break: what has a WearNTear (pieces, also doors, fire pits, a treasure chest, the dvergr's ward),
			Destructible (props, rocks, nests), MineRock or MineRock5 (ore), TreeBase or TreeLog. Not what other parts bring back
			or what only goes when taken: a creature spawner ([DungeonMonsters]), a pickable (it regrows by itself, or [SurtlingCores]
			or Prefabs bring it back), an item lying about.
		*/
		private static bool Breakable(GameObject gameObject)
		{
			// A bird flying over has a Destructible too, and is never where the layout says; what goes by itself after a time (m_ttl) was never meant to last.
			Destructible destructible = gameObject.GetComponent<Destructible>();
			if (gameObject.GetComponent<ItemDrop>() || gameObject.GetComponent<Pickable>() || gameObject.GetComponent<PickableItem>() || gameObject.GetComponent<CreatureSpawner>()
				|| gameObject.GetComponent<RandomFlyingBird>() || (destructible && destructible.m_ttl > 0f))
			{
				return false;
			}
			return gameObject.GetComponent<WearNTear>() || destructible || gameObject.GetComponent<MineRock>()
				|| gameObject.GetComponent<MineRock5>() || gameObject.GetComponent<TreeBase>() || gameObject.GetComponent<TreeLog>();
		}

		// What does not stay where the game put it: what falls (StaticPhysics), what floats on water or tar (Floating, FloatingTerrain), any other body of physics.
		public static bool MovesByItself(GameObject gameObject)
		{
			Rigidbody body = gameObject.GetComponent<Rigidbody>();
			return gameObject.GetComponent<StaticPhysics>() || gameObject.GetComponent<Floating>() || gameObject.GetComponent<FloatingTerrain>() || (body && !body.isKinematic);
		}

		private bool Listed(ZoneSystem.ZoneLocation location)
		{
			int hash = location.m_prefab.Name.GetStableHashCode();
			if (!listedLocations.TryGetValue(hash, out bool listed))
			{
				listedLocations[hash] = listed = listedPatterns.Any(p => p.IsMatch(location.m_prefab.Name));
			}
			return listed;
		}

		// The location of a dungeon's own zone (a zone holds one location at most), for a door whose rooms are not known: a doorway lies on the edge of a room, and rooms lie within it.
		private static bool InstanceAt(Vector3 position, ref ZoneSystem.LocationInstance instance)
		{
			foreach (Vector3 at in new[] { position, position + new Vector3(0.5f, 0f, 0f), position - new Vector3(0.5f, 0f, 0f), position + new Vector3(0f, 0f, 0.5f), position - new Vector3(0f, 0f, 0.5f) })
			{
				if (ZoneSystem.instance.m_locationInstances.TryGetValue(ZoneSystem.GetZone(at), out instance) && instance.m_placed && instance.m_location != null)
				{
					return true;
				}
			}
			return false;
		}

		// The location whose dungeon generator stands at this spot: the one of its zone (a zone holds one location at most), else the
		// nearest within 64 m, as Regrowth finds a dungeon's location. Of 302 generators on the live world all had one in their zone, and
		// for 4 of them another stood nearer, across the zone's edge.
		private ZoneSystem.ZoneLocation LocationAt(Vector3 position)
		{
			ZoneSystem.ZoneLocation found = null;
			float best = 64f * 64f;
			Vector2s zone = ZoneSystem.GetZone(position);
			if (ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance own) && own.m_placed && own.m_location != null)
			{
				return own.m_location;
			}
			for (int dy = -2; dy <= 2; dy++)
			{
				for (int dx = -2; dx <= 2; dx++)
				{
					if (!ZoneSystem.instance.m_locationInstances.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out ZoneSystem.LocationInstance instance)
						|| !instance.m_placed || instance.m_location == null)
					{
						continue;
					}
					float x = instance.m_position.x - position.x;
					float z = instance.m_position.z - position.z;
					if (x * x + z * z <= best)
					{
						best = x * x + z * z;
						found = instance.m_location;
					}
				}
			}
			return found;
		}

		// The fractured remains of a wanted object (see Settings.LocationObjectsClearRemains).
		public bool IsRemains(int prefab)
		{
			return remains.Contains(prefab);
		}

		/*
			What can hold an object up: anything breakable the location itself built (WearNTear) -- the walls
			of a house a hive hangs in, but a dungeon's bench or training dummy beside a fire pit too, which
			are not buildable pieces. The same test picks the supports from a location's layout and finds
			them standing in the world; with two tests a support of a kind only one of them knew was never
			found, and the object waited for it forever.
		*/
		public static bool Holds(GameObject gameObject)
		{
			return gameObject.GetComponent<WearNTear>();
		}

		// Whether anything still stands where one of the slot's supports stood.
		public static bool Supported(Slot slot, Dictionary<Vector2s, List<ZDO>> pieces)
		{
			if (slot.supports == null || slot.supports.Count == 0)
			{
				return true;
			}
			foreach ((int prefab, Vector3 position) in slot.supports)
			{
				if (pieces.TryGetValue(ZoneSystem.GetZone(position), out List<ZDO> list))
				{
					foreach (ZDO zdo in list)
					{
						if (zdo.GetPrefab() == prefab && (zdo.GetPosition() - position).sqrMagnitude < 0.25f)
						{
							return true;
						}
					}
				}
			}
			return false;
		}

		/*
			Brings the slot cache up to date with the sources found in this pass. Prefabs are loaded one at
			a time through the game's own asynchronous loader, so the server does not stall on disk reads;
			a pass that finds nothing new costs a dictionary lookup per source. A prefab that does not load
			is skipped and tried again next pass.
		*/
		public IEnumerator Refresh(List<ZDO> sources, Action<int> onLoaded)
		{
			// What the last pass read is decided from this one.
			foreach (Source source in freshSources)
			{
				source.fresh = false;
			}
			freshSources.Clear();
			// Group new sources by the prefab they need, so each is loaded once; a room also by whether its location is listed.
			Dictionary<int, List<ZDO>> newProxies = new Dictionary<int, List<ZDO>>();
			Dictionary<(int, bool), List<(ZDO generator, Vector3 position, Quaternion rotation)>> newRooms = new Dictionary<(int, bool), List<(ZDO, Vector3, Quaternion)>>();
			foreach (ZDO source in sources)
			{
				if (settled.Contains(source.m_uid))
				{
					continue;
				}
				if (source.GetPrefab() == proxyPrefab)
				{
					int location = source.GetInt(ZDOVars.s_location);
					if ((locationHasSlots.TryGetValue(location, out bool has) && !has) || slots.ContainsKey(ProxyKey(source, location)))
					{
						settled.Add(source.m_uid);
						continue;
					}
					if (!newProxies.TryGetValue(location, out List<ZDO> list))
					{
						newProxies[location] = list = new List<ZDO>();
					}
					list.Add(source);
				}
				else if (source.GetByteArray(ZDOVars.s_roomData, out byte[] data))
				{
					ZoneSystem.ZoneLocation location = AnyListed ? LocationAt(source.GetPosition()) : null;
					bool listed = location != null && Listed(location);
					bool waiting = false;
					foreach ((int hash, Vector3 position, Quaternion rotation) in ReadRooms(data))
					{
						(int, bool) kind = (hash, listed);
						if ((roomHasSlots.TryGetValue(kind, out bool has) && !has) || slots.ContainsKey(RoomKey(hash, position)))
						{
							continue;
						}
						waiting = true;
						if (!newRooms.TryGetValue(kind, out var list))
						{
							newRooms[kind] = list = new List<(ZDO, Vector3, Quaternion)>();
						}
						list.Add((source, position, rotation));
					}
					if (!waiting)
					{
						settled.Add(source.m_uid);
					}
				}
			}
			foreach (KeyValuePair<int, List<ZDO>> entry in newProxies)
			{
				if (!locations.TryGetValue(entry.Key, out ZoneSystem.ZoneLocation location))
				{
					locationHasSlots[entry.Key] = false;
					continue;
				}
				bool loaded = false;
				IEnumerator load = LoadAsync(location.m_prefab, ok => loaded = ok);
				while (load.MoveNext())
				{
					yield return null;
				}
				try
				{
					if (loaded)
					{
						System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();
						ReplayLocation(location, Listed(location), entry.Value);
						onLoaded(1);
						if (took.ElapsedMilliseconds >= 30)
						{
							RegrowthPlugin.Log.LogInfo($"LocationObjects: reading the layout of {location.m_prefabName} for {entry.Value.Count} place(s) took {took.ElapsedMilliseconds} ms in one frame");
						}
					}
				}
				catch (Exception e)
				{
					// One layout that cannot be read must not end this pass, and the next, and every one after: its objects are left alone.
					locationHasSlots[entry.Key] = false;
					RegrowthPlugin.Log.LogWarning($"LocationObjects: the layout of {location.m_prefabName} could not be read; its objects are not put back: {e}");
				}
				finally
				{
					location.m_prefab.Release();
				}
				yield return null;
			}
			foreach (var entry in newRooms)
			{
				DungeonDB.RoomData room = DungeonDB.instance.GetRoom(entry.Key.Item1);
				if (room == null || !room.m_prefab.IsValid)
				{
					roomHasSlots[entry.Key] = false;
					continue;
				}
				bool loaded = false;
				IEnumerator load = LoadAsync(room.m_prefab, ok => loaded = ok);
				while (load.MoveNext())
				{
					yield return null;
				}
				try
				{
					if (loaded)
					{
						System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();
						ReplayRooms(entry.Key.Item1, entry.Key.Item2, room, entry.Value);
						onLoaded(1);
						if (took.ElapsedMilliseconds >= 30)
						{
							RegrowthPlugin.Log.LogInfo($"LocationObjects: reading the layout of the room {room.m_prefab.Name} for {entry.Value.Count} place(s) took {took.ElapsedMilliseconds} ms in one frame");
						}
					}
				}
				catch (Exception e)
				{
					roomHasSlots[entry.Key] = false;
					RegrowthPlugin.Log.LogWarning($"LocationObjects: the layout of the room {room.m_prefab.Name} could not be read; its objects are not put back: {e}");
				}
				finally
				{
					room.m_prefab.Release();
				}
				yield return null;
			}
		}

		private const float LoadTimeoutSeconds = 30f;

		// Holds a reference like Load does; the caller releases it whatever the outcome.
		internal static IEnumerator LoadAsync(SoftReference<GameObject> prefab, Action<bool> done)
		{
			bool finished = false;
			LoadResult result = LoadResult.Failed;
			prefab.LoadAsync((AssetID id, LoadResult r) => { finished = true; result = r; });
			float until = Time.realtimeSinceStartup + LoadTimeoutSeconds;
			while (!finished && Time.realtimeSinceStartup < until)
			{
				yield return null;
			}
			done(finished && result == LoadResult.Succeeded && prefab.Asset != null);
		}

		// The state's key of a slot: its prefab and spot. Slots of one prefab on one spot share it, and their clock.
		public static string Key(string prefabName, Vector3 position)
		{
			return string.Format(CultureInfo.InvariantCulture, "LocationObject:{0}:{1:0.0}:{2:0.0}:{3:0.0}", prefabName.Replace(' ', '_'), position.x, position.y, position.z);
		}

		/*
			The rotation the game gave a location: a whole number of 22.5-degree steps about y
			(ZoneSystem.PlaceLocations, also the slope-facing ones). The proxy keeps it only to the half degree
			once the world was saved -- ZDO.Save writes Euler angles doubled and truncated (WriteSmallRotation),
			and 225 degrees, read back from the quaternion a hair under, is saved as 224.5 -- which at 40 m from the
			location's centre is 0.35 m. On the live world of 23 Oct 2026, 113 of 1773 proxies were at 224.5, the
			rest on the step. One the game did not place so (another mod's: tilted, or more than a degree off a
			step) is taken as saved.
		*/
		public static Quaternion PlacedRotation(ZDO proxy)
		{
			Quaternion saved = proxy.GetRotation();
			Vector3 e = saved.eulerAngles;
			float step = Mathf.Round(e.y / 22.5f) * 22.5f;
			bool placed = Mathf.Abs(Mathf.DeltaAngle(e.y, step)) <= 1f && Mathf.Abs(Mathf.DeltaAngle(e.x, 0f)) <= 1f && Mathf.Abs(Mathf.DeltaAngle(e.z, 0f)) <= 1f;
			return placed ? Quaternion.Euler(0f, step, 0f) : saved;
		}

		private static string ProxyKey(ZDO proxy, int location)
		{
			Vector3 p = proxy.GetPosition();
			return $"L{location}:{p.x:0.0}:{p.y:0.0}:{p.z:0.0}";
		}

		private static string RoomKey(int hash, Vector3 p)
		{
			return $"R{hash}:{p.x:0.0}:{p.y:0.0}:{p.z:0.0}";
		}

		// DungeonGenerator.Save: count, then per room hash, position, rotation as Euler angles.
		public static List<(int, Vector3, Quaternion)> ReadRooms(byte[] data)
		{
			List<(int, Vector3, Quaternion)> rooms = new List<(int, Vector3, Quaternion)>();
			BinaryReader reader = new BinaryReader(new MemoryStream(data));
			int count = reader.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				int hash = reader.ReadInt32();
				Vector3 position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				Quaternion rotation = Quaternion.Euler(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				rooms.Add((hash, position, rotation));
			}
			return rooms;
		}

		private bool Watched(GameObject gameObject, bool listed)
		{
			int prefab = Utils.GetPrefabName(gameObject).GetStableHashCode();
			if (wanted.Contains(prefab))
			{
				return true;
			}
			if (!listed)
			{
				return false;
			}
			if (!breakable.TryGetValue(prefab, out bool watched))
			{
				GameObject registered = ZNetScene.instance.GetPrefab(prefab);
				breakable[prefab] = watched = registered && !excluded.Contains(prefab) && Breakable(registered);
			}
			return watched;
		}

		private List<Template> Templates(ZNetView[] views, bool listed)
		{
			List<ZNetView> watched = views.Where(view => Watched(view.gameObject, listed)).ToList();
			// Not another object put back here: two fire pits side by side, both broken, would each wait for the other forever. In a
			// listed location that is every piece but what LocationsExclude names.
			HashSet<ZNetView> isWatched = new HashSet<ZNetView>(watched);
			List<ZNetView> holders = views.Where(view => !isWatched.Contains(view) && Holds(view.gameObject)).ToList();
			List<Template> templates = new List<Template>();
			foreach (ZNetView view in watched)
			{
				Vector3 at = view.gameObject.transform.position;
				SnapToGround snap = view.GetComponent<SnapToGround>();
				templates.Add(new Template
				{
					view = view,
					local = at,
					rotation = view.gameObject.transform.rotation,
					random = view.GetComponentInParent<RandomSpawn>(true) != null || view.GetComponentInParent<RandomObject>(true) != null,
					// SnapToGround.SnappAll moves the object itself, after a location or a dungeon's rooms are placed.
					snaps = snap != null,
					snapOffset = snap ? snap.m_offset : 0f,
					// As ZNetView.SyncScale writes it for a new object. On a copy of the live world only roots and ice in caves and crypts, up to 2.93 times their prefab.
					scale = view.m_syncInitialScale && view.transform.localScale != Vector3.one ? view.transform.localScale : Vector3.zero,
					supports = holders.Where(other => new Vector2(other.transform.position.x - at.x, other.transform.position.z - at.z).magnitude <= SupportRadius
						&& other.transform.position.y <= at.y + 0.5f).ToList(),
				});
			}
			return templates;
		}

		// ZoneSystem.SpawnLocation, Full mode, without instantiating anything.
		private void ReplayLocation(ZoneSystem.ZoneLocation location, bool listed, List<ZDO> proxies)
		{
			int hash = location.m_prefab.Name.GetStableHashCode();
			GameObject asset = location.m_prefab.Asset;
			Vector3 rootPosition = asset.transform.position;
			Quaternion rootRotation = asset.transform.rotation;
			Location component = asset.GetComponent<Location>();
			Heightmap.Biome biome = component ? component.m_biome : Heightmap.Biome.None;
			UnityEngine.Random.State randomState = UnityEngine.Random.state;
			try
			{
				asset.transform.position = Vector3.zero;
				asset.transform.rotation = Quaternion.identity;
				ZNetView[] views = Utils.GetEnabledComponentsInChildren<ZNetView>(asset);
				List<Template> templates = Templates(views, listed);
				locationHasSlots[hash] = templates.Count > 0;
				if (templates.Count == 0)
				{
					return;
				}
				RandomSpawn[] randomSpawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(asset);
				RandomObject[] randomObjects = Utils.GetEnabledComponentsInChildren<RandomObject>(asset);
				string name = location.m_prefab.Name;
				foreach (ZDO proxy in proxies)
				{
					Vector3 position = proxy.GetPosition();
					Quaternion rotation = PlacedRotation(proxy);
					bool[] spawned = new bool[templates.Count];
					List<ZNetView>[] supportsSpawned = new List<ZNetView>[templates.Count];
					try
					{
						foreach (RandomSpawn randomSpawn in randomSpawns)
						{
							randomSpawn.Prepare();
						}
						UnityEngine.Random.InitState(proxy.GetInt(ZDOVars.s_seed));
						foreach (RandomSpawn randomSpawn in randomSpawns)
						{
							randomSpawn.Randomize(position + rotation * randomSpawn.gameObject.transform.position, component);
						}
						foreach (RandomObject randomObject in randomObjects)
						{
							randomObject.Randomize(position + rotation * randomObject.gameObject.transform.position, component);
						}
						for (int i = 0; i < templates.Count; i++)
						{
							spawned[i] = templates[i].view.gameObject.activeSelf;
							supportsSpawned[i] = templates[i].supports.Where(v => v.gameObject.activeSelf).ToList();
						}
					}
					finally
					{
						Restore(randomSpawns, randomObjects, views);
						if (component)
						{
							component.m_biome = biome;
						}
					}
					Source source = new Source { name = name, position = position, anchor = position };
					for (int i = 0; i < templates.Count; i++)
					{
						if (spawned[i])
						{
							string prefabName = Utils.GetPrefabName(templates[i].view.gameObject);
							Vector3 at = position + rotation * templates[i].local;
							source.slots.Add(new Slot
							{
								prefab = prefabName.GetStableHashCode(),
								position = at,
								rotation = rotation * templates[i].rotation,
								source = name,
								key = Key(prefabName, at),
								snaps = templates[i].snaps,
								snapOffset = templates[i].snapOffset,
								scale = templates[i].scale,
								supports = supportsSpawned[i].Count == 0 ? null
									: supportsSpawned[i].Select(v => (Utils.GetPrefabName(v.gameObject).GetStableHashCode(), position + rotation * v.transform.position)).ToList(),
							});
						}
					}
					Add(ProxyKey(proxy, hash), source);
				}
			}
			finally
			{
				asset.transform.position = rootPosition;
				asset.transform.rotation = rootRotation;
				UnityEngine.Random.state = randomState;
			}
		}

		// DungeonGenerator.PlaceRoom, Full mode, without instantiating anything.
		private void ReplayRooms(int hash, bool listed, DungeonDB.RoomData room, List<(ZDO generator, Vector3 position, Quaternion rotation)> placed)
		{
			GameObject asset = room.m_prefab.Asset;
			UnityEngine.Random.State randomState = UnityEngine.Random.state;
			try
			{
				ZNetView[] views = Utils.GetEnabledComponentsInChildren<ZNetView>(asset);
				List<Template> templates = Templates(views, listed);
				roomHasSlots[(hash, listed)] = templates.Count > 0;
				if (templates.Count == 0)
				{
					return;
				}
				RandomSpawn[] randomSpawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(asset);
				RandomObject[] randomObjects = Utils.GetEnabledComponentsInChildren<RandomObject>(asset);
				Room component = asset.GetComponent<Room>();
				Vector3 origin = component.transform.position;
				Quaternion inverse = Quaternion.Inverse(component.transform.rotation);
				string name = room.m_prefab.Name;
				foreach ((ZDO generatorZdo, Vector3 position, Quaternion rotation) in placed)
				{
					if (!generatorPrefabs.TryGetValue(generatorZdo.GetPrefab(), out DungeonGenerator generator))
					{
						continue;
					}
					Vector3 seedPosition = generator.m_useCustomInteriorTransform ? position - generatorZdo.GetPosition() : position;
					int seed = (int)seedPosition.x * 4271 + (int)seedPosition.y * 9187 + (int)seedPosition.z * 2134;
					if (generator.m_addBaseSeedToRandomSpawn)
					{
						seed += GeneratorSeed(generatorZdo.GetPosition());
					}
					bool[] spawned = new bool[templates.Count];
					List<ZNetView>[] supportsSpawned = new List<ZNetView>[templates.Count];
					try
					{
						foreach (RandomSpawn randomSpawn in randomSpawns)
						{
							randomSpawn.Prepare();
						}
						UnityEngine.Random.InitState(seed);
						foreach (RandomSpawn randomSpawn in randomSpawns)
						{
							randomSpawn.Randomize(position + rotation * (inverse * (randomSpawn.gameObject.transform.position - origin)), null, generator);
						}
						foreach (RandomObject randomObject in randomObjects)
						{
							randomObject.Randomize(position + rotation * (inverse * (randomObject.gameObject.transform.position - origin)), null, generator);
						}
						for (int i = 0; i < templates.Count; i++)
						{
							spawned[i] = templates[i].view.gameObject.activeSelf;
							supportsSpawned[i] = templates[i].supports.Where(v => v.gameObject.activeSelf).ToList();
						}
					}
					finally
					{
						Restore(randomSpawns, randomObjects, views);
					}
					Source source = new Source { name = name, position = position, anchor = generatorZdo.GetPosition(), generator = generatorZdo.GetPrefab() };
					for (int i = 0; i < templates.Count; i++)
					{
						if (spawned[i])
						{
							string prefabName = Utils.GetPrefabName(templates[i].view.gameObject);
							Vector3 at = position + rotation * (inverse * (templates[i].local - origin));
							source.slots.Add(new Slot
							{
								prefab = prefabName.GetStableHashCode(),
								position = at,
								rotation = rotation * (inverse * templates[i].rotation),
								source = name,
								key = Key(prefabName, at),
								snaps = templates[i].snaps,
								snapOffset = templates[i].snapOffset,
								scale = templates[i].scale,
								supports = supportsSpawned[i].Count == 0 ? null
									: supportsSpawned[i].Select(v => (Utils.GetPrefabName(v.gameObject).GetStableHashCode(), position + rotation * (inverse * (v.transform.position - origin)))).ToList(),
							});
						}
					}
					Add(RoomKey(hash, position), source);
				}
			}
			finally
			{
				UnityEngine.Random.state = randomState;
			}
		}

		// DungeonGenerator.GetSeed for a generator placed at this position.
		private static int GeneratorSeed(Vector3 position)
		{
			Vector2s zone = ZoneSystem.GetZone(position);
			return WorldGenerator.instance.GetSeed() + zone.x * 4271 + zone.y * -7187
				+ (int)position.x * -4271 + (int)position.y * 9187 + (int)position.z * -2134;
		}

		// As the game leaves a prefab after spawning from it.
		private static void Restore(RandomSpawn[] randomSpawns, RandomObject[] randomObjects, ZNetView[] views)
		{
			foreach (RandomSpawn randomSpawn in randomSpawns)
			{
				randomSpawn.Reset();
			}
			foreach (RandomObject randomObject in randomObjects)
			{
				randomObject.Reset();
			}
			foreach (ZNetView view in views)
			{
				view.gameObject.SetActive(true);
			}
		}

		// What ZNetView.Awake does for a new object, without instantiating it: the game creates the
		// object from this when a player next loads the area.
		public static ZDO Create(int prefabHash, Vector3 position, Quaternion rotation, Vector3 scale)
		{
			GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
			ZNetView view = prefab.GetComponent<ZNetView>();
			ZDO zdo = ZDOMan.instance.CreateNewZDO(position, prefabHash);
			zdo.Persistent = view.m_persistent;
			zdo.Type = view.m_type;
			zdo.Distant = view.m_distant;
			zdo.SetPrefab(prefabHash);
			zdo.SetRotation(rotation);
			if (scale != Vector3.zero)
			{
				zdo.Set(ZDOVars.s_scaleHash, scale);
			}
			RollItem(prefab, zdo);
			// As ZoneSystem.SpawnLocation and DungeonGenerator.PlaceRoom mark what they build: in the Deep North it starts under snow.
			if (prefab.GetComponent<WearNTear>())
			{
				zdo.Set(ZDOVars.s_preSnow, true);
			}
			// Saved worlds only rewrite chunks marked as changed; SetRotation marks it only when the
			// rotation differs from the default.
			ZDOMan.instance.SetDirtySector(zdo);
			return zdo;
		}

		/*
			PickableItem.SetupRandomPrefab, done on the ZDO. The game rolls what a gem lying about is the first time the object wakes, and
			only where the object is owned; a player's game that wakes it before it owns it finds nothing rolled and shows nothing, and
			nothing can be picked. So it is rolled when it is made, as the world's first object is by whoever generates the zone.
		*/
		private static void RollItem(GameObject prefab, ZDO zdo)
		{
			PickableItem pickable = prefab.GetComponent<PickableItem>();
			if (!pickable || pickable.m_itemPrefab || pickable.m_randomItemPrefabs.Length == 0)
			{
				return;
			}
			PickableItem.RandomItem item = pickable.m_randomItemPrefabs[UnityEngine.Random.Range(0, pickable.m_randomItemPrefabs.Length)];
			zdo.Set(ZDOVars.s_itemPrefab, ObjectDB.instance.GetPrefabHash(item.m_itemPrefab.gameObject));
			zdo.Set(ZDOVars.s_itemStack, Game.instance.ScaleDrops(item.m_itemPrefab.m_itemData, item.m_stackMin, item.m_stackMax + 1));
		}
	}
}
