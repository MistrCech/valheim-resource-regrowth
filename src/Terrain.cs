using System;
using System.Collections.Generic;
using UnityEngine;

namespace ResourceRegrowth
{
	/*
		Terrain regrowth: what players dug, raised, levelled and paved slowly returns to the
		land's own shape once the area has been left alone.

		Every zone the players have touched has a "_TerrainCompiler" object whose data (ZDO
		s_TCData, a compressed package written by TerrainComp.Save) holds, for each of the zone's
		65x65 heightmap vertices, whether it was modified and by how much the height was levelled
		and smoothed, and for each vertex whether it was painted and with what (dirt, cultivated,
		paved as RGB). A step divides every modification outside a protected area by Divider and
		drops it to zero once below MinDelta; paint outside protected areas goes back to nothing
		once the vertex's height is back, except paved and cultivated ground if kept. The data is
		written back in the same format; a loaded TerrainComp reloads it when the data revision
		changes (TerrainComp.CheckLoad) and every client rebuilds the ground from it.

		Protected: everything within the radius of a "player base" object -- the pieces the game
		itself treats as a base for creature spawning, i.e. those with a PlayerBase effect area
		(workbench, forge, ...) -- and of a ward. A base that is gone (the workbench broken by
		creatures) protects nothing any more, and the land returns.
	*/
	internal static class Terrain
	{
		public const string CompilerPrefab = "_TerrainCompiler";

		public class Data
		{
			public int version, operations;
			public Vector3 lastOpPoint;
			public float lastOpRadius;
			public bool[] modifiedHeight;
			public float[] levelDelta, smoothDelta;
			public bool[] modifiedPaint;
			public Color[] paintMask;
		}

		public struct Protector
		{
			public Vector3 position;
			public float radius;
		}

		public class Stats
		{
			public int modifiedBefore, modifiedAfter, paintedBefore, paintedAfter, protectedVertices;
		}

		public static Data Decode(byte[] compressed)
		{
			ZPackage pkg = new ZPackage(Utils.Decompress(compressed));
			Data d = new Data
			{
				version = pkg.ReadInt(),
				operations = pkg.ReadInt(),
				lastOpPoint = pkg.ReadVector3(),
				lastOpRadius = pkg.ReadSingle(),
			};
			int heights = pkg.ReadInt();
			d.modifiedHeight = new bool[heights];
			d.levelDelta = new float[heights];
			d.smoothDelta = new float[heights];
			for (int i = 0; i < heights; i++)
			{
				d.modifiedHeight[i] = pkg.ReadBool();
				if (d.modifiedHeight[i])
				{
					d.levelDelta[i] = pkg.ReadSingle();
					d.smoothDelta[i] = pkg.ReadSingle();
				}
			}
			int paints = pkg.ReadInt();
			d.modifiedPaint = new bool[paints];
			d.paintMask = new Color[paints];
			for (int i = 0; i < paints; i++)
			{
				d.modifiedPaint[i] = pkg.ReadBool();
				if (d.modifiedPaint[i])
				{
					d.paintMask[i] = new Color(pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle());
				}
			}
			return d;
		}

		public static byte[] Encode(Data d)
		{
			ZPackage pkg = new ZPackage();
			pkg.Write(d.version);
			pkg.Write(d.operations);
			pkg.Write(d.lastOpPoint);
			pkg.Write(d.lastOpRadius);
			pkg.Write(d.modifiedHeight.Length);
			for (int i = 0; i < d.modifiedHeight.Length; i++)
			{
				pkg.Write(d.modifiedHeight[i]);
				if (d.modifiedHeight[i])
				{
					pkg.Write(d.levelDelta[i]);
					pkg.Write(d.smoothDelta[i]);
				}
			}
			pkg.Write(d.modifiedPaint.Length);
			for (int i = 0; i < d.modifiedPaint.Length; i++)
			{
				pkg.Write(d.modifiedPaint[i]);
				if (d.modifiedPaint[i])
				{
					pkg.Write(d.paintMask[i].r);
					pkg.Write(d.paintMask[i].g);
					pkg.Write(d.paintMask[i].b);
					pkg.Write(d.paintMask[i].a);
				}
			}
			return Utils.Compress(pkg.GetArray());
		}

		// True when the zone has any modification left; cheap, stops at the first one.
		public static bool IsModified(byte[] compressed)
		{
			if (compressed == null)
			{
				return false;
			}
			try
			{
				ZPackage pkg = new ZPackage(Utils.Decompress(compressed));
				pkg.ReadInt();
				pkg.ReadInt();
				pkg.ReadVector3();
				pkg.ReadSingle();
				int heights = pkg.ReadInt();
				for (int i = 0; i < heights; i++)
				{
					if (pkg.ReadBool())
					{
						return true;
					}
				}
				int paints = pkg.ReadInt();
				for (int i = 0; i < paints; i++)
				{
					if (pkg.ReadBool())
					{
						return true;
					}
				}
				return false;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/*
			One step of decay for a zone. `center` is the compiler's position (the zone centre);
			vertex (x, y) of a 64 m zone lies at centre + (x - 32, y - 32), as Heightmap.CalcVertex
			has it. Returns the new data, or null when nothing changed.
		*/
		public static Data Step(Data d, Vector3 center, List<Protector> protectors, Settings s, Stats stats, bool all = false)
		{
			int width = Mathf.RoundToInt(Mathf.Sqrt(d.modifiedHeight.Length)) - 1;
			if ((width + 1) * (width + 1) != d.modifiedHeight.Length || width <= 0)
			{
				return null;
			}
			float divider = Mathf.Max(1.01f, s.TerrainDivider.Value);
			float minDelta = Mathf.Max(0f, s.TerrainMinDelta.Value);
			bool samePaintGrid = d.modifiedPaint.Length == d.modifiedHeight.Length;
			bool changed = false;
			float half = width * 0.5f;
			for (int i = 0; i < d.modifiedHeight.Length; i++)
			{
				if (d.modifiedHeight[i])
				{
					stats.modifiedBefore++;
				}
				if (samePaintGrid && d.modifiedPaint[i])
				{
					stats.paintedBefore++;
				}
				if (!d.modifiedHeight[i] && !(samePaintGrid && d.modifiedPaint[i]))
				{
					continue;
				}
				int x = i % (width + 1), y = i / (width + 1);
				Vector3 at = new Vector3(center.x - half + x, 0f, center.z - half + y);
				if (Protected(at, protectors))
				{
					stats.protectedVertices++;
					if (d.modifiedHeight[i]) stats.modifiedAfter++;
					if (samePaintGrid && d.modifiedPaint[i]) stats.paintedAfter++;
					continue;
				}
				if (d.modifiedHeight[i])
				{
					float level = all ? 0f : d.levelDelta[i] / divider;
					float smooth = all ? 0f : d.smoothDelta[i] / divider;
					if (Mathf.Abs(level) < minDelta) level = 0f;
					if (Mathf.Abs(smooth) < minDelta) smooth = 0f;
					if (level != d.levelDelta[i] || smooth != d.smoothDelta[i])
					{
						changed = true;
					}
					d.levelDelta[i] = level;
					d.smoothDelta[i] = smooth;
					d.modifiedHeight[i] = level != 0f || smooth != 0f;
					if (d.modifiedHeight[i])
					{
						stats.modifiedAfter++;
					}
				}
				if (samePaintGrid && d.modifiedPaint[i])
				{
					Color c = d.paintMask[i];
					bool keep = !all && ((s.TerrainKeepPaved.Value && c.b > 0.5f) || (s.TerrainKeepCultivated.Value && c.g > 0.5f));
					// Paint goes last: only once the ground under it is back to its own height.
					if (!keep && !d.modifiedHeight[i])
					{
						d.modifiedPaint[i] = false;
						d.paintMask[i] = Heightmap.m_paintMaskNothing;
						changed = true;
					}
					else
					{
						stats.paintedAfter++;
					}
				}
			}
			return changed ? d : null;
		}

		private static bool Protected(Vector3 at, List<Protector> protectors)
		{
			for (int i = 0; i < protectors.Count; i++)
			{
				float dx = protectors[i].position.x - at.x;
				float dz = protectors[i].position.z - at.z;
				float r = protectors[i].radius;
				if (dx * dx + dz * dz <= r * r)
				{
					return true;
				}
			}
			return false;
		}

		/*
			Which prefabs protect the ground around them, and how far: anything with a PlayerBase
			effect area (the game's own notion of a base: workbench, forge, stonecutter ...), by the
			area's collider radius, and wards by their own radius.
		*/
		public static Dictionary<int, float> ProtectorPrefabs(float multiplier, out List<string> names)
		{
			Dictionary<int, float> radii = new Dictionary<int, float>();
			names = new List<string>();
			foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
			{
				if (!prefab)
				{
					continue;
				}
				float radius = 0f;
				foreach (EffectArea area in prefab.GetComponentsInChildren<EffectArea>(true))
				{
					if ((area.m_type & EffectArea.Type.PlayerBase) == 0)
					{
						continue;
					}
					SphereCollider sphere = area.GetComponent<SphereCollider>();
					float r = sphere ? sphere.radius * Mathf.Max(area.transform.lossyScale.x, area.transform.lossyScale.z) : 10f;
					radius = Mathf.Max(radius, r);
				}
				PrivateArea ward = prefab.GetComponent<PrivateArea>();
				if (ward)
				{
					radius = Mathf.Max(radius, ward.m_radius);
				}
				if (radius > 0f)
				{
					radii[prefab.name.GetStableHashCode()] = radius * Mathf.Max(0f, multiplier);
					names.Add($"{prefab.name} {radius * multiplier:0} m");
				}
			}
			return radii;
		}
	}
}
