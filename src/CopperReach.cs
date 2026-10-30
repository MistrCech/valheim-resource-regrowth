using System;
using System.Collections.Generic;
using UnityEngine;

namespace ResourceRegrowth
{
	/*
		How much higher a copper deposit must stand for every piece of it to reach over the deepest hole a player can dig around it
		([CopperDeposits] OverDeepestHole). From the game (1.0.20):
		- TerrainComp.ApplyToHeightmap keeps every vertex of the ground within 8 m of its height before players changed anything: the ground as
		  HeightmapBuilder generates it with the locations' own TerrainModifiers applied (Heightmap.ApplyModifiers copies the heights for
		  TerrainComp after them). A pickaxe (digg_v3) takes 0.5 m a hit.
		- A swing hits only what each of its rays meets first (Attack.DoMeleeAttack), and a pickaxe's swing whose topmost hit is ground digs
		  instead: a piece wholly under the ground cannot be hit.
		- rock4_copper is a Destructible that the first hit swaps for rock4_copper_frac (MineRock5) at its position, rotation and scale; the 130
		  pieces are mesh colliders, 13 609 vertices in all.
		A piece reaches over the hole when one of its vertices stands over the floor at its own x and z; between the ground's vertices the floor
		is read bilinearly. A location's TerrainModifiers are done again here as the game does them (level, then smooth; in the game's order) on
		the ground's vertices under the deposit. One that may not be there (under a RandomSpawn, or a networked object) counts both ways, the
		higher floor winning; a location not placed yet counts in each of the 16 turns it can be placed with.
	*/
	internal static class CopperReach
	{
		public const string Whole = "rock4_copper", Fractured = "rock4_copper_frac";
		public const float DigDepth = 8f;
		public static readonly int WholeHash = Whole.GetStableHashCode(), FracturedHash = Fractured.GetStableHashCode();
		public static readonly int ProxyHash = "LocationProxy".GetStableHashCode();
		// How many zones round a deposit's own a location's modifiers are looked for in (the widest reach about 40 m).
		private const int LocationZones = 2;

		private sealed class Modifier
		{
			public Vector3 offset; // from the location's own position, before it is turned
			public bool level, square, smooth, maybe;
			public float levelRadius, smoothRadius, smoothPower, levelOffset;
			public int sortOrder;
		}

		private struct Placed
		{
			public Vector3 at;
			public Modifier modifier;
		}

		private static List<Vector3[]> pieces;
		private static float wholeScale, fracturedScale;
		private static int width;
		private static bool failed;
		private static readonly Dictionary<Vector2s, float[]> ground = new Dictionary<Vector2s, float[]>();
		private static readonly Dictionary<ZoneSystem.ZoneLocation, List<Modifier>> modifiers = new Dictionary<ZoneSystem.ZoneLocation, List<Modifier>>();
		// Each placed location's own position and turn, by its zone (its LocationProxy: one location a zone).
		private static readonly Dictionary<Vector2s, (Vector3 position, Quaternion rotation)> proxies = new Dictionary<Vector2s, (Vector3, Quaternion)>();
		private static readonly List<Vector3> world = new List<Vector3>();
		private static readonly List<int> ends = new List<int>();

		public static void NoteProxy(ZDO zdo)
		{
			proxies[ZoneSystem.GetZone(zdo.GetPosition())] = (zdo.GetPosition(), zdo.GetRotation());
		}

		// The ground built for one batch of deposits; built again when needed.
		public static void Forget()
		{
			ground.Clear();
		}

		public static void Reset()
		{
			ground.Clear();
			proxies.Clear();
			modifiers.Clear();
		}

		public static bool Ready()
		{
			if (pieces != null)
			{
				return true;
			}
			if (failed)
			{
				return false;
			}
			failed = true;
			GameObject whole = ZNetScene.instance ? ZNetScene.instance.GetPrefab(Whole) : null;
			GameObject fractured = ZNetScene.instance ? ZNetScene.instance.GetPrefab(Fractured) : null;
			Heightmap template = ZoneSystem.instance ? ZoneSystem.instance.m_zonePrefab.GetComponentInChildren<Heightmap>() : null;
			if (!whole || !fractured || !template)
			{
				RegrowthPlugin.Log.LogWarning($"CopperDeposits OverDeepestHole: no {(!whole ? Whole : !fractured ? Fractured : "heightmap")} in the game; deposits stay where they are.");
				return false;
			}
			if (template.m_scale != 1f || template.m_width % 2 != 0)
			{
				RegrowthPlugin.Log.LogWarning($"CopperDeposits OverDeepestHole: the ground's vertices are not 1 m apart ({template.m_width} by {template.m_scale}); deposits stay where they are.");
				return false;
			}
			// MineRock5.Awake takes its pieces as GetComponentsInChildren<Collider>(): the same list, in the same order as the health it keeps.
			List<Vector3[]> list = new List<Vector3[]>();
			Matrix4x4 toRoot = fractured.transform.worldToLocalMatrix;
			foreach (Collider collider in fractured.GetComponentsInChildren<Collider>())
			{
				MeshCollider mesh = collider as MeshCollider;
				if (!mesh || !mesh.sharedMesh || !mesh.sharedMesh.isReadable)
				{
					RegrowthPlugin.Log.LogWarning($"CopperDeposits OverDeepestHole: piece {list.Count} of {Fractured} has no mesh to read; deposits stay where they are.");
					return false;
				}
				Vector3[] vertices = mesh.sharedMesh.vertices;
				for (int i = 0; i < vertices.Length; i++)
				{
					vertices[i] = toRoot.MultiplyPoint3x4(collider.transform.TransformPoint(vertices[i]));
				}
				list.Add(vertices);
			}
			pieces = list;
			wholeScale = whole.transform.localScale.x;
			fracturedScale = fractured.transform.localScale.x;
			width = template.m_width;
			failed = false;
			return true;
		}

		/*
			need: how much higher than `up` under its place now the deposit must stand for each of its live pieces to reach `margin` over the
			deepest hole (negative: that much less would do). byLocation: a location's own ground counted. False when there is nothing to measure.
		*/
		public static bool Measure(ZDO zdo, float margin, float up, out float need, out bool byLocation)
		{
			need = 0f;
			byLocation = false;
			if (!Ready())
			{
				return false;
			}
			bool fractured = zdo.GetPrefab() == FracturedHash;
			Vector3 position = zdo.GetPosition();
			position.y -= up;
			Quaternion rotation = zdo.GetRotation();
			Vector3 scale = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
			if (scale == Vector3.zero)
			{
				scale = Vector3.one * zdo.GetFloat(ZDOVars.s_scaleScalarHash, fractured ? fracturedScale : wholeScale);
			}
			float[] health = fractured ? Health(zdo) : null;
			world.Clear();
			ends.Clear();
			float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
			for (int a = 0; a < pieces.Count; a++)
			{
				if (health != null && a < health.Length && health[a] <= 0f)
				{
					continue;
				}
				foreach (Vector3 v in pieces[a])
				{
					Vector3 w = position + rotation * Vector3.Scale(scale, v);
					world.Add(w);
					minX = Mathf.Min(minX, w.x);
					maxX = Mathf.Max(maxX, w.x);
					minZ = Mathf.Min(minZ, w.z);
					maxZ = Mathf.Max(maxZ, w.z);
				}
				ends.Add(world.Count);
			}
			if (world.Count == 0)
			{
				return false;
			}
			// The ground's vertices under the deposit, one more each way.
			int x0 = Mathf.FloorToInt(minX) - 1, z0 = Mathf.FloorToInt(minZ) - 1;
			int nx = Mathf.CeilToInt(maxX) + 2 - x0, nz = Mathf.CeilToInt(maxZ) + 2 - z0;
			float[] raw = new float[nx * nz];
			for (int i = 0; i < nz; i++)
			{
				for (int j = 0; j < nx; j++)
				{
					raw[i * nx + j] = Vertex(x0 + j, z0 + i);
				}
			}
			float[] floor = Levelled(raw, x0, z0, nx, nz, position, out byLocation);
			need = float.MinValue;
			int start = 0;
			foreach (int end in ends)
			{
				float over = float.MinValue;
				for (int k = start; k < end; k++)
				{
					Vector3 w = world[k];
					over = Mathf.Max(over, w.y - (Read(floor, x0, z0, nx, nz, w.x, w.z) - DigDepth));
				}
				need = Mathf.Max(need, margin - over);
				start = end;
			}
			return true;
		}

		private static float[] Health(ZDO zdo)
		{
			string text = zdo.GetString(ZDOVars.s_health);
			if (text.Length == 0)
			{
				return null;
			}
			ZPackage package = new ZPackage(Convert.FromBase64String(text));
			float[] health = new float[package.ReadInt()];
			for (int i = 0; i < health.Length; i++)
			{
				health[i] = package.ReadSingle();
			}
			return health;
		}

		// The ground as generated at a vertex (whole metres): the zone's own heights, biomes blended as HeightmapBuilder does it.
		private static float Vertex(int x, int z)
		{
			Vector2s zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
			Vector3 centre = ZoneSystem.GetZonePos(zone);
			if (!ground.TryGetValue(zone, out float[] heights))
			{
				Heightmap template = ZoneSystem.instance.m_zonePrefab.GetComponentInChildren<Heightmap>();
				heights = HeightmapBuilder.instance.RequestTerrainSync(centre, width, 1f, template.IsDistantLod, WorldGenerator.instance).m_baseHeights.ToArray();
				ground[zone] = heights;
			}
			int half = width / 2;
			int j = Mathf.Clamp(x - (Mathf.RoundToInt(centre.x) - half), 0, width), i = Mathf.Clamp(z - (Mathf.RoundToInt(centre.z) - half), 0, width);
			return heights[i * (width + 1) + j];
		}

		private static float Read(float[] grid, int x0, int z0, int nx, int nz, float x, float z)
		{
			float fx = x - x0, fz = z - z0;
			int j = Mathf.Clamp(Mathf.FloorToInt(fx), 0, nx - 2), i = Mathf.Clamp(Mathf.FloorToInt(fz), 0, nz - 2);
			float tx = fx - j, tz = fz - i;
			return Mathf.Lerp(Mathf.Lerp(grid[i * nx + j], grid[i * nx + j + 1], tx), Mathf.Lerp(grid[(i + 1) * nx + j], grid[(i + 1) * nx + j + 1], tx), tz);
		}

		/*
			The ground under the deposit once the locations near have levelled it. A modifier that may not be there counts with and without, and a
			location not placed yet in each of its 16 turns: of all of them, the highest ground at each vertex.
		*/
		private static float[] Levelled(float[] raw, int x0, int z0, int nx, int nz, Vector3 at, out bool byLocation)
		{
			byLocation = false;
			List<(ZoneSystem.ZoneLocation location, Vector3 position, Quaternion? rotation)> near = Near(at);
			if (near.Count == 0)
			{
				return raw;
			}
			bool turns = false, maybes = false;
			foreach (var (location, _, rotation) in near)
			{
				foreach (Modifier m in Modifiers(location))
				{
					turns |= rotation == null && (m.offset.x != 0f || m.offset.z != 0f);
					maybes |= m.maybe;
				}
			}
			float[] best = null;
			for (int turn = 0; turn < (turns ? 16 : 1); turn++)
			{
				for (int with = maybes ? 0 : 1; with <= 1; with++)
				{
					List<Placed> all = new List<Placed>();
					foreach (var (location, position, rotation) in near)
					{
						Quaternion r = rotation ?? Quaternion.Euler(0f, turn * 22.5f, 0f);
						foreach (Modifier m in Modifiers(location))
						{
							if (!m.maybe || with == 1)
							{
								all.Add(new Placed { at = position + r * m.offset, modifier = m });
							}
						}
					}
					// Heightmap.ApplyModifiers in TerrainModifier.GetAllInstances' order: by sort order, then (no creation time on a location's own) by the distance from the world's centre.
					all.Sort((a, b) => a.modifier.sortOrder != b.modifier.sortOrder ? a.modifier.sortOrder.CompareTo(b.modifier.sortOrder) : a.at.sqrMagnitude.CompareTo(b.at.sqrMagnitude));
					float[] grid = (float[])raw.Clone();
					foreach (Placed p in all)
					{
						Apply(grid, x0, z0, nx, nz, p);
					}
					if (best == null)
					{
						best = grid;
						continue;
					}
					for (int k = 0; k < best.Length; k++)
					{
						best[k] = Mathf.Max(best[k], grid[k]);
					}
				}
			}
			for (int k = 0; k < best.Length; k++)
			{
				byLocation |= Mathf.Abs(best[k] - raw[k]) > 0.01f;
			}
			return best;
		}

		// Heightmap.LevelTerrain, then SmoothTerrain2, for a location's own modifier: the centre is the nearest vertex.
		private static void Apply(float[] grid, int x0, int z0, int nx, int nz, Placed p)
		{
			Modifier m = p.modifier;
			float target = p.at.y + m.levelOffset;
			int cx = Mathf.FloorToInt(p.at.x + 0.5f), cz = Mathf.FloorToInt(p.at.z + 0.5f);
			if (m.level)
			{
				float r = m.levelRadius;
				int n = Mathf.CeilToInt(r);
				for (int i = Mathf.Max(0, cz - n - z0); i <= Mathf.Min(nz - 1, cz + n - z0); i++)
				{
					for (int j = Mathf.Max(0, cx - n - x0); j <= Mathf.Min(nx - 1, cx + n - x0); j++)
					{
						if (m.square || !(Vector2.Distance(new Vector2(cx, cz), new Vector2(x0 + j, z0 + i)) > r))
						{
							grid[i * nx + j] = target;
						}
					}
				}
			}
			if (m.smooth)
			{
				float r = m.smoothRadius;
				int n = Mathf.CeilToInt(r);
				for (int i = Mathf.Max(0, cz - n - z0); i <= Mathf.Min(nz - 1, cz + n - z0); i++)
				{
					for (int j = Mathf.Max(0, cx - n - x0); j <= Mathf.Min(nx - 1, cx + n - x0); j++)
					{
						float d = Vector2.Distance(new Vector2(cx, cz), new Vector2(x0 + j, z0 + i));
						if (d > r)
						{
							continue;
						}
						float f = d / r;
						f = m.smoothPower != 3f ? Mathf.Pow(f, m.smoothPower) : (float)((double)f * f * f);
						float h = grid[i * nx + j];
						grid[i * nx + j] = h + (target - h) * (float)(1.0 - f);
					}
				}
			}
		}

		// The locations whose zones lie within LocationZones of the deposit's, with where each stands: its proxy's place and turn, or for one not
		// placed yet the ground where it will be put (the water's level for one snapped to it) and no turn.
		private static List<(ZoneSystem.ZoneLocation, Vector3, Quaternion?)> Near(Vector3 at)
		{
			List<(ZoneSystem.ZoneLocation, Vector3, Quaternion?)> near = new List<(ZoneSystem.ZoneLocation, Vector3, Quaternion?)>();
			Vector2s zone = ZoneSystem.GetZone(at);
			for (int y = zone.y - LocationZones; y <= zone.y + LocationZones; y++)
			{
				for (int x = zone.x - LocationZones; x <= zone.x + LocationZones; x++)
				{
					Vector2s z = new Vector2s(x, y);
					if (!ZoneSystem.instance.m_locationInstances.TryGetValue(z, out ZoneSystem.LocationInstance instance) || instance.m_location == null || Modifiers(instance.m_location).Count == 0)
					{
						continue;
					}
					if (instance.m_placed && proxies.TryGetValue(z, out var proxy))
					{
						near.Add((instance.m_location, proxy.position, proxy.rotation));
						continue;
					}
					Vector3 p = instance.m_position;
					p.y = instance.m_location.m_snapToWater ? 30f : Ground(p.x, p.z);
					near.Add((instance.m_location, p, null));
				}
			}
			return near;
		}

		private static float Ground(float x, float z)
		{
			int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
			float tx = x - x0, tz = z - z0;
			return Mathf.Lerp(Mathf.Lerp(Vertex(x0, z0), Vertex(x0 + 1, z0), tx), Mathf.Lerp(Vertex(x0, z0 + 1), Vertex(x0 + 1, z0 + 1), tx), tz);
		}

		// A location's own TerrainModifiers, as Heightmap.ApplyModifiers takes them: enabled ones on active objects, not a player's.
		private static List<Modifier> Modifiers(ZoneSystem.ZoneLocation location)
		{
			if (modifiers.TryGetValue(location, out List<Modifier> list))
			{
				return list;
			}
			list = new List<Modifier>();
			modifiers[location] = list;
			try
			{
				location.m_prefab.Load();
			}
			catch (Exception e)
			{
				RegrowthPlugin.Log.LogWarning($"CopperDeposits OverDeepestHole: could not load location {location.m_prefabName}, its ground counts as generated: {e.Message}");
				return list;
			}
			try
			{
				GameObject root = location.m_prefab.Asset;
				if (!root)
				{
					return list;
				}
				foreach (TerrainModifier tm in root.GetComponentsInChildren<TerrainModifier>(true))
				{
					if (!tm.enabled || tm.m_playerModifiction || !Active(tm.transform, root.transform) || !(tm.m_level || tm.m_smooth))
					{
						continue;
					}
					list.Add(new Modifier
					{
						offset = Quaternion.Inverse(root.transform.rotation) * (tm.transform.position - root.transform.position),
						level = tm.m_level,
						square = tm.m_square,
						smooth = tm.m_smooth,
						levelRadius = tm.m_levelRadius,
						smoothRadius = tm.m_smoothRadius,
						smoothPower = tm.m_smoothPower,
						levelOffset = tm.m_levelOffset,
						sortOrder = tm.m_sortOrder,
						// Under a RandomSpawn it may not be spawned; on a networked object it comes and goes with its object.
						maybe = tm.GetComponentInParent<RandomSpawn>(true) != null || tm.GetComponentInParent<ZNetView>(true) != null,
					});
				}
			}
			finally
			{
				location.m_prefab.Release();
			}
			return list;
		}

		private static bool Active(Transform t, Transform root)
		{
			for (; t != null; t = t.parent)
			{
				if (!t.gameObject.activeSelf)
				{
					return false;
				}
				if (t == root)
				{
					break;
				}
			}
			return true;
		}
	}
}
