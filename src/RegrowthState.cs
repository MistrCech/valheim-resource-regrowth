using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ResourceRegrowth
{
	/*
		What the plugin remembers between restarts, in a text file next to the world save:
		- when a player was last near each zone;
		- since when each depleted object has been seen depleted.

		Times are real time in UTC. Both clocks start at the plugin's first run on a world, so
		nothing regrows until IdleHours / AfterHours have passed since installing it -- the plugin
		cannot know what happened before.
	*/
	internal class RegrowthState
	{
		private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

		private readonly string path;
		private readonly Dictionary<Vector2s, DateTime> lastSeen = new Dictionary<Vector2s, DateTime>();
		private readonly Dictionary<string, DateTime> depletedSince = new Dictionary<string, DateTime>();
		// Zone reset: zones a player has been in since they were last generated, and since when.
		private readonly Dictionary<Vector2s, DateTime> visited = new Dictionary<Vector2s, DateTime>();
		// Zone reset: a location's rotation to give it again when its zone is next generated.
		private readonly Dictionary<Vector2s, KeyValuePair<int, Quaternion>> keepRotation = new Dictionary<Vector2s, KeyValuePair<int, Quaternion>>();

		public DateTime? ZonesFirstRun { get; set; }
		// Zone reset: zones where players planted saplings (once grown, a tree has no mark of its planter).
		private readonly HashSet<Vector2s> planted = new HashSet<Vector2s>();
		/*
			Since when presence is known completely: zone entries are forgotten after a while, and a zone
			without one was not seen after this. Only moves forward; older versions kept IdleHours.
		*/
		public DateTime PresenceKnownSince { get; set; }
		public bool PresenceKnownLoaded { get; private set; }

		public DateTime FirstRun { get; private set; }
		public string FilePath => path;

		public RegrowthState(string path)
		{
			this.path = path;
		}

		public void Load(DateTime now)
		{
			FirstRun = now;
			string file = path;
			if (!File.Exists(file))
			{
				// Save replaces the file by delete + move; a stop between the two leaves only the new copy.
				file = path + ".tmp";
				if (!File.Exists(file))
				{
					return;
				}
			}
			foreach (string raw in File.ReadAllLines(file))
			{
				string[] f = raw.Split(' ');
				try
				{
					switch (f[0])
					{
						case "firstrun":
							FirstRun = Parse(f[1]);
							break;
						case "zone":
							lastSeen[new Vector2s(int.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture))] = Parse(f[3]);
							break;
						case "depleted":
							depletedSince[f[1]] = Parse(f[2]);
							break;
						case "visited":
							visited[new Vector2s(int.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture))] = Parse(f[3]);
							break;
						case "zonesfirstrun":
							ZonesFirstRun = Parse(f[1]);
							break;
						case "presencesince":
							PresenceKnownSince = Parse(f[1]);
							PresenceKnownLoaded = true;
							break;
						case "planted":
							planted.Add(new Vector2s(int.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture)));
							break;
						case "keeprotation":
							keepRotation[new Vector2s(int.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture))] = new KeyValuePair<int, Quaternion>(
								int.Parse(f[3], CultureInfo.InvariantCulture),
								new Quaternion(float.Parse(f[4], CultureInfo.InvariantCulture), float.Parse(f[5], CultureInfo.InvariantCulture),
									float.Parse(f[6], CultureInfo.InvariantCulture), float.Parse(f[7], CultureInfo.InvariantCulture)));
							break;
					}
				}
				catch (Exception)
				{
					// A damaged line only costs that entry's clock, which then starts again.
				}
			}
			ClampToNow(now);
		}

		/*
			A time in the future (the system clock was set back, or the file was edited) would keep
			objects waiting until the clock catches up, so it counts from now instead.
		*/
		private void ClampToNow(DateTime now)
		{
			if (FirstRun > now)
			{
				FirstRun = now;
			}
			foreach (Vector2s zone in new List<Vector2s>(lastSeen.Keys))
			{
				if (lastSeen[zone] > now)
				{
					lastSeen[zone] = now;
				}
			}
			foreach (string key in new List<string>(depletedSince.Keys))
			{
				if (depletedSince[key] > now)
				{
					depletedSince[key] = now;
				}
			}
			if (ZonesFirstRun.HasValue && ZonesFirstRun.Value > now)
			{
				ZonesFirstRun = now;
			}
			if (PresenceKnownSince > now)
			{
				PresenceKnownSince = now;
			}
			foreach (Vector2s zone in new List<Vector2s>(visited.Keys))
			{
				if (visited[zone] > now)
				{
					visited[zone] = now;
				}
			}
		}

		public void Save()
		{
			List<string> lines = new List<string>
			{
				"# Sarkastic.gg Resource Regrowth state. Real time, UTC. Edit only while the server is stopped.",
				"firstrun " + Format(FirstRun),
			};
			foreach (KeyValuePair<Vector2s, DateTime> zone in lastSeen)
			{
				lines.Add($"zone {zone.Key.x} {zone.Key.y} {Format(zone.Value)}");
			}
			foreach (KeyValuePair<string, DateTime> entry in depletedSince)
			{
				lines.Add($"depleted {entry.Key} {Format(entry.Value)}");
			}
			if (ZonesFirstRun.HasValue)
			{
				lines.Add("zonesfirstrun " + Format(ZonesFirstRun.Value));
			}
			lines.Add("presencesince " + Format(PresenceKnownSince));
			foreach (Vector2s zone in planted)
			{
				lines.Add($"planted {zone.x} {zone.y}");
			}
			foreach (KeyValuePair<Vector2s, DateTime> zone in visited)
			{
				lines.Add($"visited {zone.Key.x} {zone.Key.y} {Format(zone.Value)}");
			}
			foreach (KeyValuePair<Vector2s, KeyValuePair<int, Quaternion>> entry in keepRotation)
			{
				Quaternion q = entry.Value.Value;
				lines.Add(string.Format(CultureInfo.InvariantCulture, "keeprotation {0} {1} {2} {3:R} {4:R} {5:R} {6:R}", entry.Key.x, entry.Key.y, entry.Value.Key, q.x, q.y, q.z, q.w));
			}
			string temp = path + ".tmp";
			File.WriteAllLines(temp, lines);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			File.Move(temp, path);
		}

		public void MarkSeen(Vector2s zone, DateTime now)
		{
			if (!lastSeen.TryGetValue(zone, out DateTime previous) || previous < now)
			{
				lastSeen[zone] = now;
			}
		}

		// Whether a player has been near the zone since the given time (zones are only remembered for IdleHours).
		public bool SeenSince(Vector2s zone, DateTime since)
		{
			return lastSeen.TryGetValue(zone, out DateTime time) && time >= since;
		}

		// Zone reset bookkeeping.
		public void MarkVisited(Vector2s zone, DateTime now)
		{
			if (!visited.ContainsKey(zone))
			{
				visited[zone] = now;
			}
		}

		public IEnumerable<KeyValuePair<Vector2s, DateTime>> Visited => visited;
		public void MarkPlanted(Vector2s zone) => planted.Add(zone);
		public bool IsPlanted(Vector2s zone) => planted.Contains(zone);
		public int PlantedCount => planted.Count;

		public bool TryLastSeen(Vector2s zone, out DateTime time) => lastSeen.TryGetValue(zone, out time);
		public int VisitedCount => visited.Count;
		public void ForgetVisit(Vector2s zone) => visited.Remove(zone);

		public void KeepRotation(Vector2s zone, int location, Quaternion rotation) => keepRotation[zone] = new KeyValuePair<int, Quaternion>(location, rotation);

		public bool TakeRotation(Vector2s zone, int location, out Quaternion rotation)
		{
			rotation = Quaternion.identity;
			if (!keepRotation.TryGetValue(zone, out KeyValuePair<int, Quaternion> kept) || kept.Key != location)
			{
				return false;
			}
			keepRotation.Remove(zone);
			rotation = kept.Value;
			return true;
		}

		public DateTime LastSeen(Vector2s zone)
		{
			return lastSeen.TryGetValue(zone, out DateTime time) ? time : FirstRun;
		}

		// Returns since when the object has been depleted, recording now if this is the first sighting.
		public DateTime DepletedSince(string key, DateTime now)
		{
			if (!depletedSince.TryGetValue(key, out DateTime since))
			{
				since = now;
				depletedSince[key] = since;
			}
			return since;
		}

		public void Forget(string key)
		{
			depletedSince.Remove(key);
		}

		public bool HasKey(string key)
		{
			return depletedSince.ContainsKey(key);
		}

		// A zone last seen before the cutoff counts as idle either way (LastSeen falls back to FirstRun, earlier still).
		public void ForgetZonesSeenBefore(DateTime cutoff)
		{
			foreach (Vector2s zone in new List<Vector2s>(lastSeen.Keys))
			{
				if (lastSeen[zone] < cutoff)
				{
					lastSeen.Remove(zone);
				}
			}
		}

		// Drops entries for objects that are no longer depleted or no longer exist.
		public void KeepOnly(HashSet<string> keys)
		{
			List<string> stale = new List<string>();
			foreach (string key in depletedSince.Keys)
			{
				if (!keys.Contains(key))
				{
					stale.Add(key);
				}
			}
			foreach (string key in stale)
			{
				depletedSince.Remove(key);
			}
		}

		private static DateTime Parse(string s)
		{
			return DateTime.ParseExact(s, TimeFormat, CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
		}

		private static string Format(DateTime time)
		{
			return time.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
		}
	}
}
