using System;
using System.Collections;
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
		A piece reaches over the hole when one of its vertices stands over the floor at its own x and z. The floor between the ground's vertices
		is the terrain collider's (Heightmap.RebuildCollisionMesh: two triangles a square, split from x+1,z to x,z+1). The ground's vertices are
		worked out here as HeightmapBuilder.Build does it (a zone's four corner biomes, DUtils.SmoothStep and Lerp), in the frame that needs them:
		waiting for the game's builder thread cost a frame up to half a second.
		A location's TerrainModifiers are done again here as the game does them (level, then smooth, in TerrainModifier.SortByModifiers' order:
		sort order, then the distance from the world's centre, a location's own having no creation time) on the ground's vertices under the
		deposit, in the heightmap's own frame. The highest ground any of them can leave counts:
		- one that may not be there (under a RandomSpawn or its OffObject, a RandomObject or one of its objects, or a networked object) counts
		  both ways, which for the game's pointwise and rising level and smooth is the higher of the ground with and without it, in order;
		- a location's turn: its proxy's as the game sends it, and as the world's save keeps it (ZDO.Save rounds the Euler angles down to the half
		  degree: 225 comes back as 224.5); one not placed yet in each turn PlaceLocations can give it, both ways, standing on the ground where its
		  ray will meet it (the water's level for one snapped to it).
		Locations whose reach overlaps under a deposit are turned together, every pairing of their turns; the others each on their own.
		A location's prefab is read once, in the background (a frame each), for its modifiers; a deposit near one not read yet waits for it.
	*/
	internal static class CopperReach
	{
		public const string Whole = "rock4_copper", Fractured = "rock4_copper_frac";
		public const float DigDepth = 8f;
		public static readonly int WholeHash = Whole.GetStableHashCode(), FracturedHash = Fractured.GetStableHashCode();
		public static readonly int ProxyHash = "LocationProxy".GetStableHashCode();
		// How many zones round a deposit's own a location's modifiers are looked for in (the widest reach about 40 m).
		private const int LocationZones = 2;
		// The pairings of turns of locations reaching under one deposit together: more is not measured.
		private const int MaxPairings = 4096;
		private const int MaxCachedVertices = 1 << 18;

		public enum Result { Measured, Waiting, Failed }

		private sealed class Modifier
		{
			public Vector3 offset; // from the location's own position, before it is turned
			public bool level, square, smooth, maybe;
			public float levelRadius, smoothRadius, smoothPower, levelOffset, reach;
			public int sortOrder;
		}

		private struct Turn
		{
			public Vector3 position;
			public Quaternion rotation;
		}

		private sealed class NearLocation
		{
			public List<Modifier> modifiers;
			public readonly List<Turn> turns = new List<Turn>();
			public int minJ, maxJ, minI, maxI; // the grid's vertices it can reach in any turn
		}

		private struct Placed
		{
			public Vector3 at;
			public Modifier modifier;
		}

		private static List<Vector3[]> pieces;
		private static float wholeScale, fracturedScale;
		private static int width;
		private static float scale;
		private static bool failed;
		private static readonly Dictionary<Vector2s, Heightmap.Biome[]> corners = new Dictionary<Vector2s, Heightmap.Biome[]>();
		private static readonly Dictionary<long, float> heights = new Dictionary<long, float>();
		// A location's modifiers once its prefab was read; null when it could not be.
		private static readonly Dictionary<ZoneSystem.ZoneLocation, List<Modifier>> modifiers = new Dictionary<ZoneSystem.ZoneLocation, List<Modifier>>();
		private static readonly Queue<ZoneSystem.ZoneLocation> toRead = new Queue<ZoneSystem.ZoneLocation>();
		private static readonly HashSet<ZoneSystem.ZoneLocation> asked = new HashSet<ZoneSystem.ZoneLocation>();
		private static MonoBehaviour host;
		private static Coroutine reader;
		private static ZoneSystem.ZoneLocation reading;
		private static int readCount, readFailed;
		private static double readMilliseconds;
		// Each placed location's own position and turn (Euler angles as its ZDO keeps them), by its zone (its LocationProxy: one location a zone).
		private static Dictionary<Vector2s, (Vector3 position, Vector3 euler)> proxies = new Dictionary<Vector2s, (Vector3, Vector3)>();
		private static readonly List<Vector3> world = new List<Vector3>();
		private static readonly List<int> ends = new List<int>();
		private static readonly List<NearLocation> near = new List<NearLocation>();
		private static readonly List<Placed> placed = new List<Placed>();

		public static void Start(MonoBehaviour by)
		{
			host = by;
		}

		public static void NoteProxy(ZDO zdo)
		{
			proxies[ZoneSystem.GetZone(zdo.GetPosition())] = (zdo.GetPosition(), zdo.m_rotation);
		}

		// The proxies a pass found, for the deposits it queues: a zone generated again since is found anew.
		public static void SetProxies(Dictionary<Vector2s, (Vector3, Vector3)> found)
		{
			proxies = found;
		}

		// The ground worked out for one batch of deposits; worked out again when needed.
		public static void Forget()
		{
			corners.Clear();
			heights.Clear();
		}

		public static void Reset()
		{
			if (reader != null && host)
			{
				host.StopCoroutine(reader);
			}
			reader = null;
			if (reading != null)
			{
				reading.m_prefab.Release();
				reading = null;
			}
			toRead.Clear();
			asked.Clear();
			modifiers.Clear();
			proxies = new Dictionary<Vector2s, (Vector3, Vector3)>();
			Forget();
		}

		// Locations are read one at a time in the background; Busy while any is left.
		public static bool Busy => reader != null;

		public static string ReadReport()
		{
			return $"{readCount} location prefab(s) read for their ground ({readMilliseconds:0} ms of work{(readFailed > 0 ? $", {readFailed} could not be read" : "")})";
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
			if (template.m_scale != 1f || template.m_width % 2 != 0 || template.IsDistantLod)
			{
				RegrowthPlugin.Log.LogWarning($"CopperDeposits OverDeepestHole: the ground's vertices are not 1 m apart ({template.m_width} by {template.m_scale}{(template.IsDistantLod ? ", distant" : "")}); deposits stay where they are.");
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
			scale = template.m_scale;
			failed = false;
			return true;
		}

		/*
			need: how much higher than `up` under its place now the deposit must stand for each of its live pieces to reach `margin` over the
			deepest hole (negative: that much less would do). byLocation: a location's own ground counted. Waiting: a location near is still
			being read. Failed: nothing to measure, or a location near could not be read.
		*/
		public static Result Measure(ZDO zdo, float margin, float up, out float need, out bool byLocation)
		{
			need = 0f;
			byLocation = false;
			if (!Ready())
			{
				return Result.Failed;
			}
			Vector3 position = zdo.GetPosition();
			position.y -= up;
			Result nearby = Near(position);
			if (nearby != Result.Measured)
			{
				return nearby;
			}
			bool fractured = zdo.GetPrefab() == FracturedHash;
			Quaternion rotation = zdo.GetRotation();
			Vector3 size = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
			if (size == Vector3.zero)
			{
				size = Vector3.one * zdo.GetFloat(ZDOVars.s_scaleScalarHash, fractured ? fracturedScale : wholeScale);
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
					Vector3 w = position + rotation * Vector3.Scale(size, v);
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
				return Result.Failed;
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
			float[] floor = Levelled(raw, x0, z0, nx, nz, out byLocation);
			if (floor == null)
			{
				return Result.Failed;
			}
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
			return Result.Measured;
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

		// The ground as generated at a vertex (whole metres), as HeightmapBuilder.Build works it out for the zone holding it.
		private static float Vertex(int x, int z)
		{
			long key = ((long)x << 32) | (uint)z;
			if (heights.TryGetValue(key, out float height))
			{
				return height;
			}
			Vector2s zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
			Vector3 corner = ZoneSystem.GetZonePos(zone) + new Vector3((float)width * scale * -0.5f, 0f, (float)width * scale * -0.5f);
			WorldGenerator generator = WorldGenerator.instance;
			if (!corners.TryGetValue(zone, out Heightmap.Biome[] biome))
			{
				float farX = (float)((double)corner.x + (double)width * (double)scale), farZ = (float)((double)corner.z + (double)width * (double)scale);
				biome = new Heightmap.Biome[]
				{
					generator.GetBiome(corner.x, corner.z), generator.GetBiome(farX, corner.z), generator.GetBiome(corner.x, farZ), generator.GetBiome(farX, farZ),
				};
				corners[zone] = biome;
			}
			int l = Mathf.RoundToInt((x - corner.x) / scale), k = Mathf.RoundToInt((z - corner.z) / scale);
			float wx = (float)((double)corner.x + (double)l * (double)scale), wy = (float)((double)corner.z + (double)k * (double)scale);
			if (biome[1] == biome[0] && biome[2] == biome[0] && biome[3] == biome[0])
			{
				height = generator.GetBiomeHeight(biome[0], wx, wy, out _);
			}
			else
			{
				// The game works out all four; one biome twice gives the same height.
				float h0 = generator.GetBiomeHeight(biome[0], wx, wy, out _);
				float h1 = biome[1] == biome[0] ? h0 : generator.GetBiomeHeight(biome[1], wx, wy, out _);
				float h2 = biome[2] == biome[0] ? h0 : biome[2] == biome[1] ? h1 : generator.GetBiomeHeight(biome[2], wx, wy, out _);
				float h3 = biome[3] == biome[0] ? h0 : biome[3] == biome[1] ? h1 : biome[3] == biome[2] ? h2 : generator.GetBiomeHeight(biome[3], wx, wy, out _);
				float t = DUtils.SmoothStep(0f, 1f, (float)((double)k / (double)width));
				float t2 = DUtils.SmoothStep(0f, 1f, (float)((double)l / (double)width));
				height = DUtils.Lerp(DUtils.Lerp(h0, h1, t2), DUtils.Lerp(h2, h3, t2), t);
			}
			if (heights.Count >= MaxCachedVertices)
			{
				heights.Clear();
			}
			heights[key] = height;
			return height;
		}

		// The terrain collider's height in a square of the grid: the triangle (x,z) (x+1,z) (x,z+1) or (x+1,z+1) (x,z+1) (x+1,z).
		private static float Read(float[] grid, int x0, int z0, int nx, int nz, float x, float z)
		{
			float fx = x - x0, fz = z - z0;
			int j = Mathf.Clamp(Mathf.FloorToInt(fx), 0, nx - 2), i = Mathf.Clamp(Mathf.FloorToInt(fz), 0, nz - 2);
			float tx = fx - j, tz = fz - i;
			float h00 = grid[i * nx + j], h10 = grid[i * nx + j + 1], h01 = grid[(i + 1) * nx + j], h11 = grid[(i + 1) * nx + j + 1];
			return Triangle(h00, h10, h01, h11, tx, tz);
		}

		private static float Triangle(float h00, float h10, float h01, float h11, float tx, float tz)
		{
			return tx + tz <= 1f ? h00 + (h10 - h00) * tx + (h01 - h00) * tz : h11 + (h01 - h11) * (1f - tx) + (h10 - h11) * (1f - tz);
		}

		// The generated ground where a ray from above meets it.
		private static float Ground(float x, float z)
		{
			int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
			return Triangle(Vertex(x0, z0), Vertex(x0 + 1, z0), Vertex(x0, z0 + 1), Vertex(x0 + 1, z0 + 1), x - x0, z - z0);
		}

		/*
			The ground under the deposit once the locations near have levelled it: of every turn and every modifier there or not, the highest
			it can be at each vertex. Null when too many turns would have to be paired.
		*/
		private static float[] Levelled(float[] raw, int x0, int z0, int nx, int nz, out bool byLocation)
		{
			byLocation = false;
			// What each location can reach of the grid in any turn; one reaching none of it plays no part.
			for (int n = near.Count - 1; n >= 0; n--)
			{
				NearLocation location = near[n];
				location.minJ = location.minI = int.MaxValue;
				location.maxJ = location.maxI = int.MinValue;
				foreach (Turn turn in location.turns)
				{
					foreach (Modifier m in location.modifiers)
					{
						Vector3 at = turn.position + turn.rotation * m.offset;
						// One vertex more each way than the reach: the heightmap's own frame may round the centre the other way.
						int r = Mathf.CeilToInt(m.reach) + 1;
						int cx = Mathf.FloorToInt(at.x + 0.5f), cz = Mathf.FloorToInt(at.z + 0.5f);
						location.minJ = Math.Min(location.minJ, cx - r - x0);
						location.maxJ = Math.Max(location.maxJ, cx + r - x0);
						location.minI = Math.Min(location.minI, cz - r - z0);
						location.maxI = Math.Max(location.maxI, cz + r - z0);
					}
				}
				location.minJ = Math.Max(location.minJ, 0);
				location.maxJ = Math.Min(location.maxJ, nx - 1);
				location.minI = Math.Max(location.minI, 0);
				location.maxI = Math.Min(location.maxI, nz - 1);
				if (location.minJ > location.maxJ || location.minI > location.maxI)
				{
					near.RemoveAt(n);
				}
			}
			if (near.Count == 0)
			{
				return raw;
			}
			// Locations whose reach overlaps are turned together.
			int[] group = new int[near.Count];
			for (int a = 0; a < near.Count; a++)
			{
				group[a] = a;
			}
			int Root(int a)
			{
				while (group[a] != a)
				{
					a = group[a] = group[group[a]];
				}
				return a;
			}
			for (int a = 0; a < near.Count; a++)
			{
				for (int b = a + 1; b < near.Count; b++)
				{
					if (near[a].minJ <= near[b].maxJ && near[b].minJ <= near[a].maxJ && near[a].minI <= near[b].maxI && near[b].minI <= near[a].maxI)
					{
						group[Root(a)] = Root(b);
					}
				}
			}
			float[] best = (float[])raw.Clone();
			List<NearLocation> together = new List<NearLocation>();
			for (int g = 0; g < near.Count; g++)
			{
				if (Root(g) != g)
				{
					continue;
				}
				together.Clear();
				long pairings = 1;
				for (int a = 0; a < near.Count; a++)
				{
					if (Root(a) == g)
					{
						together.Add(near[a]);
						pairings *= near[a].turns.Count;
					}
				}
				if (pairings > MaxPairings)
				{
					return null;
				}
				float[] highest = null;
				for (long pairing = 0; pairing < pairings; pairing++)
				{
					placed.Clear();
					long rest = pairing;
					foreach (NearLocation location in together)
					{
						Turn turn = location.turns[(int)(rest % location.turns.Count)];
						rest /= location.turns.Count;
						foreach (Modifier m in location.modifiers)
						{
							placed.Add(new Placed { at = turn.position + turn.rotation * m.offset, modifier = m });
						}
					}
					// Heightmap.ApplyModifiers in TerrainModifier.GetAllInstances' order: by sort order, then (no creation time on a location's own) by the distance from the world's centre.
					placed.Sort((a, b) => a.modifier.sortOrder != b.modifier.sortOrder ? a.modifier.sortOrder.CompareTo(b.modifier.sortOrder) : a.at.sqrMagnitude.CompareTo(b.at.sqrMagnitude));
					float[] grid = (float[])raw.Clone();
					foreach (Placed p in placed)
					{
						Apply(grid, x0, z0, nx, nz, p);
					}
					if (highest == null)
					{
						highest = grid;
						continue;
					}
					for (int k = 0; k < highest.Length; k++)
					{
						highest[k] = Mathf.Max(highest[k], grid[k]);
					}
				}
				// The group's own vertices only: another group's are its own.
				foreach (NearLocation location in together)
				{
					for (int i = location.minI; i <= location.maxI; i++)
					{
						for (int j = location.minJ; j <= location.maxJ; j++)
						{
							best[i * nx + j] = highest[i * nx + j];
						}
					}
				}
			}
			for (int k = 0; k < best.Length; k++)
			{
				byLocation |= Mathf.Abs(best[k] - raw[k]) > 0.01f;
			}
			return best;
		}

		/*
			Heightmap.LevelTerrain, then SmoothTerrain2, for a location's own modifier, on every heightmap it reaches: each takes the vertex
			nearest its centre in its own frame (WorldToVertexMask for the level, WorldToVertex for the smooth). One that may not be there leaves
			the higher of the two.
		*/
		private static void Apply(float[] grid, int x0, int z0, int nx, int nz, Placed p)
		{
			Modifier m = p.modifier;
			// ApplyModifier: the modifier's position, raised by its offset.
			Vector3 worldPos = p.at + Vector3.up * m.levelOffset;
			float target = worldPos.y;
			int r = Mathf.CeilToInt(m.reach) + 1;
			int cx = Mathf.FloorToInt(worldPos.x + 0.5f), cz = Mathf.FloorToInt(worldPos.z + 0.5f);
			int half = width / 2, halfMask = (width + 1) / 2;
			Vector2s lastZone = new Vector2s(int.MinValue, int.MinValue);
			Vector2 levelCentre = Vector2.zero, smoothCentre = Vector2.zero;
			for (int i = Math.Max(0, cz - r - z0); i <= Math.Min(nz - 1, cz + r - z0); i++)
			{
				for (int j = Math.Max(0, cx - r - x0); j <= Math.Min(nx - 1, cx + r - x0); j++)
				{
					int x = x0 + j, z = z0 + i;
					Vector2s zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
					Vector3 centre = ZoneSystem.GetZonePos(zone);
					if (zone.x != lastZone.x || zone.y != lastZone.y)
					{
						lastZone = zone;
						Vector3 local = worldPos - centre;
						levelCentre = new Vector2(Mathf.FloorToInt(local.x / scale + 0.5f + (float)halfMask), Mathf.FloorToInt(local.z / scale + 0.5f + (float)halfMask));
						smoothCentre = new Vector2(Mathf.FloorToInt(local.x / scale + 0.5f) + half, Mathf.FloorToInt(local.z / scale + 0.5f) + half);
					}
					// The vertex in the zone's frame.
					Vector2 vertex = new Vector2(Mathf.RoundToInt(x - centre.x) + half, Mathf.RoundToInt(z - centre.z) + half);
					float h = grid[i * nx + j], changed = h;
					bool touched = false;
					if (m.level)
					{
						float radius = (float)((double)m.levelRadius / (double)scale);
						int n = Mathf.CeilToInt(radius);
						if (Mathf.Abs(vertex.x - levelCentre.x) <= n && Mathf.Abs(vertex.y - levelCentre.y) <= n && (m.square || !(Vector2.Distance(levelCentre, vertex) > radius)))
						{
							changed = target;
							touched = true;
						}
					}
					if (m.smooth)
					{
						float radius = (float)(double)(m.smoothRadius / scale);
						int n = Mathf.CeilToInt(radius);
						float d = Vector2.Distance(smoothCentre, vertex);
						if (Mathf.Abs(vertex.x - smoothCentre.x) <= n && Mathf.Abs(vertex.y - smoothCentre.y) <= n && !(d > radius))
						{
							float f = d / radius;
							f = m.smoothPower != 3f ? Mathf.Pow(f, m.smoothPower) : (float)((double)f * (double)f * (double)f);
							changed = DUtils.Lerp(changed, target, (float)(1.0 - (double)f));
							touched = true;
						}
					}
					if (touched)
					{
						grid[i * nx + j] = m.maybe ? Mathf.Max(h, changed) : changed;
					}
				}
			}
		}

		/*
			The locations whose zones lie within LocationZones of the deposit's and that move the ground, each with the turns it can stand in:
			Waiting while one of them is still being read (it is asked for here), Failed when one could not be.
		*/
		private static Result Near(Vector3 at)
		{
			near.Clear();
			bool waiting = false, unreadable = false;
			Vector2s zone = ZoneSystem.GetZone(at);
			for (int y = zone.y - LocationZones; y <= zone.y + LocationZones; y++)
			{
				for (int x = zone.x - LocationZones; x <= zone.x + LocationZones; x++)
				{
					Vector2s z = new Vector2s(x, y);
					if (!ZoneSystem.instance.m_locationInstances.TryGetValue(z, out ZoneSystem.LocationInstance instance) || instance.m_location == null)
					{
						continue;
					}
					if (!modifiers.TryGetValue(instance.m_location, out List<Modifier> list))
					{
						Ask(instance.m_location);
						waiting = true;
						continue;
					}
					if (list == null)
					{
						unreadable = true;
						continue;
					}
					if (list.Count == 0)
					{
						continue;
					}
					NearLocation location = new NearLocation { modifiers = list };
					if (instance.m_placed && proxies.TryGetValue(z, out var proxy))
					{
						AddTurns(location, proxy.position, proxy.euler);
					}
					else
					{
						// ZoneSystem.PlaceLocations: on the ground a ray from above meets, turned to the slope or at random by 22.5 degrees (the slope's
						// rounding can make it 360), or not at all.
						Vector3 p = instance.m_position;
						p.y = instance.m_location.m_snapToWater ? 30f : Ground(p.x, p.z);
						int steps = instance.m_location.m_slopeRotation ? 17 : instance.m_location.m_randomRotation ? 16 : 1;
						for (int s = 0; s < steps; s++)
						{
							// The proxy keeps what transform.rotation gives back as Euler angles; a client turns the location by those.
							AddTurns(location, p, Quaternion.Euler(0f, (float)s * 22.5f, 0f).eulerAngles);
						}
					}
					near.Add(location);
				}
			}
			return waiting ? Result.Waiting : unreadable ? Result.Failed : Result.Measured;
		}

		// As the game sends the turn, and as the world's save brings it back (ZPackage.WriteSmallRotation and ReadSmallRotation).
		private static void AddTurns(NearLocation location, Vector3 position, Vector3 euler)
		{
			location.turns.Add(new Turn { position = position, rotation = Quaternion.Euler(euler) });
			Vector3 saved = Saved(euler);
			if (saved != euler)
			{
				location.turns.Add(new Turn { position = position, rotation = Quaternion.Euler(saved) });
			}
		}

		private static Vector3 Saved(Vector3 euler)
		{
			if (euler.CloseToZero())
			{
				return Vector3.zero;
			}
			Vector3 doubled = euler * 2f;
			uint x = (uint)doubled.x, y = (uint)doubled.y, z = (uint)doubled.z;
			if ((x <= 1 || x >= 719) && (z <= 1 || z >= 719))
			{
				return new Vector3(0f, (float)(y & 0x7FFF) * 0.5f, 0f);
			}
			// Tilted (not a location the game placed): as it is.
			return euler;
		}

		private static void Ask(ZoneSystem.ZoneLocation location)
		{
			if (!asked.Add(location))
			{
				return;
			}
			toRead.Enqueue(location);
			if (reader == null && host)
			{
				reader = host.StartCoroutine(ReadLocations());
			}
		}

		// Every kind of location of the world, in the background: a zone generated now needs those near it read already.
		public static void AskAll()
		{
			if (ZoneSystem.instance == null)
			{
				return;
			}
			foreach (ZoneSystem.LocationInstance instance in ZoneSystem.instance.m_locationInstances.Values)
			{
				if (instance.m_location != null)
				{
					Ask(instance.m_location);
				}
			}
		}

		private static IEnumerator ReadLocations()
		{
			while (toRead.Count > 0)
			{
				ZoneSystem.ZoneLocation location = toRead.Dequeue();
				if (!location.m_prefab.IsValid)
				{
					modifiers[location] = new List<Modifier>();
					continue;
				}
				bool loaded = false;
				reading = location;
				IEnumerator load = LocationObjects.LoadAsync(location.m_prefab, ok => loaded = ok);
				while (load.MoveNext())
				{
					yield return null;
				}
				System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
				try
				{
					modifiers[location] = loaded ? Extract(location.m_prefab.Asset) : null;
				}
				catch (Exception e)
				{
					modifiers[location] = null;
					RegrowthPlugin.Log.LogWarning($"CopperDeposits OverDeepestHole: the ground of location {location.m_prefabName} could not be read: {e.Message}");
				}
				finally
				{
					location.m_prefab.Release();
					reading = null;
				}
				readMilliseconds += watch.Elapsed.TotalMilliseconds;
				readCount++;
				if (modifiers[location] == null)
				{
					readFailed++;
					RegrowthPlugin.Log.LogWarning($"CopperDeposits OverDeepestHole: location {location.m_prefabName} could not be loaded; the deposits near one are not measured.");
				}
				yield return null;
			}
			reader = null;
		}

		/*
			A location's own TerrainModifiers, as Heightmap.ApplyModifiers takes them: enabled, not a player's, on objects active in the prefab --
			or switched on by ZoneSystem.SpawnLocation, which randomizes the RandomSpawn and RandomObject on active objects (Utils.
			GetEnabledComponentsInChildren) before it puts the location down and resets them after: a RandomSpawn's OffObject comes on when it
			does not spawn, and any of a RandomObject's objects can be the one kept.
		*/
		private static List<Modifier> Extract(GameObject root)
		{
			List<Modifier> list = new List<Modifier>();
			if (!root)
			{
				return list;
			}
			HashSet<GameObject> randomized = new HashSet<GameObject>(), switched = new HashSet<GameObject>();
			foreach (RandomSpawn spawn in Utils.GetEnabledComponentsInChildren<RandomSpawn>(root))
			{
				randomized.Add(spawn.gameObject);
				if (spawn.m_OffObject)
				{
					switched.Add(spawn.m_OffObject);
				}
			}
			foreach (RandomObject random in Utils.GetEnabledComponentsInChildren<RandomObject>(root))
			{
				randomized.Add(random.gameObject);
				foreach (RandomObject.ObjectEntry entry in random.m_objects)
				{
					if (entry != null && entry.m_object)
					{
						switched.Add(entry.m_object);
					}
				}
			}
			foreach (TerrainModifier tm in root.GetComponentsInChildren<TerrainModifier>(true))
			{
				if (!tm.enabled || tm.m_playerModifiction || !(tm.m_level || tm.m_smooth))
				{
					continue;
				}
				bool there = true, maybe = false;
				for (Transform t = tm.transform; t != null; t = t.parent)
				{
					GameObject go = t.gameObject;
					bool canSwitch = switched.Contains(go);
					if (!go.activeSelf && !canSwitch)
					{
						there = false;
						break;
					}
					// On a networked object it comes and goes with its object.
					maybe |= canSwitch || randomized.Contains(go) || go.GetComponent<ZNetView>() != null;
					if (t == root.transform)
					{
						break;
					}
				}
				if (!there)
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
					reach = Mathf.Max(tm.m_level ? tm.m_levelRadius : 0f, tm.m_smooth ? tm.m_smoothRadius : 0f),
					maybe = maybe,
				});
			}
			return list;
		}
	}
}
