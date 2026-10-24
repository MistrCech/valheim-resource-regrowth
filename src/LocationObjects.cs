using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
	*/
	internal class LocationObjects
	{
		public struct Slot
		{
			public int prefab;
			public Vector3 position;
			public Quaternion rotation;
			public string source;
			// The state's key (Key): made once here, not for every slot in every pass.
			public string key;
			// The game puts it on the ground after placing it (SnapToGround): only its x and z are the layout's.
			public bool snaps;
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
			public List<ZNetView> supports;
		}

		private const float SupportRadius = 3f;

		private readonly HashSet<int> wanted;
		// What the game leaves when a wanted object is hit: its prefab's name and "_frac".
		private readonly HashSet<int> remains;
		private readonly int proxyPrefab;
		private readonly Dictionary<int, DungeonGenerator> generatorPrefabs = new Dictionary<int, DungeonGenerator>();
		private readonly Dictionary<int, ZoneSystem.ZoneLocation> locations = new Dictionary<int, ZoneSystem.ZoneLocation>();
		// Location name hash / room hash -> whether its prefab holds any wanted object (checked once).
		private readonly Dictionary<int, bool> locationHasSlots = new Dictionary<int, bool>();
		private readonly Dictionary<int, bool> roomHasSlots = new Dictionary<int, bool>();
		// Source key (location or room, by name and position) -> its slots, replay included.
		private readonly Dictionary<string, List<Slot>> slots = new Dictionary<string, List<Slot>>();

		public int SourceCount => slots.Count;

		public LocationObjects(IEnumerable<string> prefabNames)
		{
			List<string> names = prefabNames.ToList();
			wanted = new HashSet<int>(names.Select(n => n.GetStableHashCode()));
			remains = new HashSet<int>(names.Select(n => (n + "_frac").GetStableHashCode()));
			// What a wanted object turns into when it breaks (a mud pile into its fractured pile) counts as its remains too; not an item it
			// drops or an effect it plays.
			foreach (string name in names)
			{
				GameObject prefab = ZNetScene.instance.GetPrefab(name);
				Destructible destructible = prefab ? prefab.GetComponent<Destructible>() : null;
				GameObject left = destructible ? destructible.m_spawnWhenDestroyed : null;
				if (left && left.GetComponent<ZNetView>() && !left.GetComponent<ItemDrop>())
				{
					remains.Add(left.name.GetStableHashCode());
				}
			}
			proxyPrefab = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
			foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
			{
				DungeonGenerator generator = prefab ? prefab.GetComponent<DungeonGenerator>() : null;
				if (generator)
				{
					generatorPrefabs[prefab.name.GetStableHashCode()] = generator;
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

		public bool IsSource(int prefab)
		{
			return prefab == proxyPrefab || generatorPrefabs.ContainsKey(prefab);
		}

		public bool IsWanted(int prefab)
		{
			return wanted.Contains(prefab);
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
			// Group new sources by the prefab they need, so each is loaded once.
			Dictionary<int, List<ZDO>> newProxies = new Dictionary<int, List<ZDO>>();
			Dictionary<int, List<(ZDO generator, Vector3 position, Quaternion rotation)>> newRooms = new Dictionary<int, List<(ZDO, Vector3, Quaternion)>>();
			foreach (ZDO source in sources)
			{
				if (source.GetPrefab() == proxyPrefab)
				{
					int location = source.GetInt(ZDOVars.s_location);
					if ((locationHasSlots.TryGetValue(location, out bool has) && !has) || slots.ContainsKey(ProxyKey(source, location)))
					{
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
					foreach ((int hash, Vector3 position, Quaternion rotation) in ReadRooms(data))
					{
						if ((roomHasSlots.TryGetValue(hash, out bool has) && !has) || slots.ContainsKey(RoomKey(hash, position)))
						{
							continue;
						}
						if (!newRooms.TryGetValue(hash, out var list))
						{
							newRooms[hash] = list = new List<(ZDO, Vector3, Quaternion)>();
						}
						list.Add((source, position, rotation));
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
						ReplayLocation(location, entry.Value);
						onLoaded(1);
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
				DungeonDB.RoomData room = DungeonDB.instance.GetRoom(entry.Key);
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
						ReplayRooms(entry.Key, room, entry.Value);
						onLoaded(1);
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
		private static IEnumerator LoadAsync(SoftReference<GameObject> prefab, Action<bool> done)
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

		public IEnumerable<Slot> AllSlots()
		{
			return slots.Values.SelectMany(list => list);
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
		private static List<(int, Vector3, Quaternion)> ReadRooms(byte[] data)
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

		private List<Template> Templates(GameObject asset, ZNetView[] views)
		{
			List<Template> templates = new List<Template>();
			foreach (ZNetView view in views)
			{
				if (wanted.Contains(Utils.GetPrefabName(view.gameObject).GetStableHashCode()))
				{
					Vector3 at = view.gameObject.transform.position;
					templates.Add(new Template
					{
						view = view,
						local = at,
						rotation = view.gameObject.transform.rotation,
						random = view.GetComponentInParent<RandomSpawn>(true) != null || view.GetComponentInParent<RandomObject>(true) != null,
						// SnapToGround.SnappAll moves the object itself, after a location or a dungeon's rooms are placed.
						snaps = view.GetComponent<SnapToGround>() != null,
						// Not another object put back here: two fire pits side by side, both broken, would each wait for the other forever.
						supports = views.Where(other => other != view && Holds(other.gameObject)
							&& !wanted.Contains(Utils.GetPrefabName(other.gameObject).GetStableHashCode())
							&& new Vector2(other.transform.position.x - at.x, other.transform.position.z - at.z).magnitude <= SupportRadius
							&& other.transform.position.y <= at.y + 0.5f).ToList(),
					});
				}
			}
			return templates;
		}

		// ZoneSystem.SpawnLocation, Full mode, without instantiating anything.
		private void ReplayLocation(ZoneSystem.ZoneLocation location, List<ZDO> proxies)
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
				List<Template> templates = Templates(asset, views);
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
					List<Slot> list = new List<Slot>();
					for (int i = 0; i < templates.Count; i++)
					{
						if (spawned[i])
						{
							string prefabName = Utils.GetPrefabName(templates[i].view.gameObject);
							Vector3 at = position + rotation * templates[i].local;
							list.Add(new Slot
							{
								prefab = prefabName.GetStableHashCode(),
								position = at,
								rotation = rotation * templates[i].rotation,
								source = name,
								key = Key(prefabName, at),
								snaps = templates[i].snaps,
								supports = supportsSpawned[i].Select(v => (Utils.GetPrefabName(v.gameObject).GetStableHashCode(), position + rotation * v.transform.position)).ToList(),
							});
						}
					}
					slots[ProxyKey(proxy, hash)] = list;
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
		private void ReplayRooms(int hash, DungeonDB.RoomData room, List<(ZDO generator, Vector3 position, Quaternion rotation)> placed)
		{
			GameObject asset = room.m_prefab.Asset;
			UnityEngine.Random.State randomState = UnityEngine.Random.state;
			try
			{
				ZNetView[] views = Utils.GetEnabledComponentsInChildren<ZNetView>(asset);
				List<Template> templates = Templates(asset, views);
				roomHasSlots[hash] = templates.Count > 0;
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
					List<Slot> list = new List<Slot>();
					for (int i = 0; i < templates.Count; i++)
					{
						if (spawned[i])
						{
							string prefabName = Utils.GetPrefabName(templates[i].view.gameObject);
							Vector3 at = position + rotation * (inverse * (templates[i].local - origin));
							list.Add(new Slot
							{
								prefab = prefabName.GetStableHashCode(),
								position = at,
								rotation = rotation * (inverse * templates[i].rotation),
								source = name,
								key = Key(prefabName, at),
								snaps = templates[i].snaps,
								supports = supportsSpawned[i].Select(v => (Utils.GetPrefabName(v.gameObject).GetStableHashCode(), position + rotation * (inverse * (v.transform.position - origin)))).ToList(),
							});
						}
					}
					slots[RoomKey(hash, position)] = list;
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
		public static ZDO Create(int prefabHash, Vector3 position, Quaternion rotation)
		{
			GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
			ZNetView view = prefab.GetComponent<ZNetView>();
			ZDO zdo = ZDOMan.instance.CreateNewZDO(position, prefabHash);
			zdo.Persistent = view.m_persistent;
			zdo.Type = view.m_type;
			zdo.Distant = view.m_distant;
			zdo.SetPrefab(prefabHash);
			zdo.SetRotation(rotation);
			// Saved worlds only rewrite chunks marked as changed; SetRotation marks it only when the
			// rotation differs from the default.
			ZDOMan.instance.SetDirtySector(zdo);
			return zdo;
		}
	}
}
