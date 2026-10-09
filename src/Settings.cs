using BepInEx.Configuration;

namespace ResourceRegrowth
{
	/*
		All times are real hours: wall-clock time, counted whether or not anyone is online (and
		while the server is stopped). World time would not do here: on a dedicated server it only
		advances while a player is online.
	*/
	internal class Settings
	{
		private readonly ConfigFile file;

		public readonly ConfigEntry<bool> Enabled;
		public readonly ConfigEntry<bool> DryRun;
		public readonly ConfigEntry<float> CheckIntervalMinutes;
		public readonly ConfigEntry<int> LogDetailsPerPass;

		public readonly ConfigEntry<float> IdleHours;
		public readonly ConfigEntry<int> PresenceRadiusZones;
		public readonly ConfigEntry<float> PlayerBuildRadius;

		public readonly ConfigEntry<bool> LocationsTogether;

		public readonly ConfigEntry<bool> CoresEnabled;
		public readonly ConfigEntry<float> CoresAfterHours;
		public readonly ConfigEntry<string> CorePrefabs;

		public readonly ConfigEntry<bool> ChestsEnabled;
		public readonly ConfigEntry<float> ChestsAfterHours;
		public readonly ConfigEntry<string> ChestsExclude;

		public readonly ConfigEntry<bool> SpawnersEnabled;
		public readonly ConfigEntry<float> SpawnersAfterHours;
		public readonly ConfigEntry<string> SpawnersExclude;

		public readonly ConfigEntry<bool> LocationObjectsEnabled;
		public readonly ConfigEntry<float> LocationObjectsAfterHours;
		public readonly ConfigEntry<string> LocationObjectsPrefabs;

		public readonly ConfigEntry<bool> TerrainEnabled;
		public readonly ConfigEntry<float> TerrainAfterHours;
		public readonly ConfigEntry<float> TerrainStepHours;
		public readonly ConfigEntry<float> TerrainDivider;
		public readonly ConfigEntry<float> TerrainMinDelta;
		public readonly ConfigEntry<bool> TerrainKeepPaved;
		public readonly ConfigEntry<bool> TerrainKeepCultivated;
		public readonly ConfigEntry<float> TerrainProtectMultiplier;
		public readonly ConfigEntry<float> TerrainProtectPiecesRadius;

		public Settings(ConfigFile config)
		{
			file = config;
			Enabled = config.Bind("General", "Enabled", true, "Enable or disable the plugin.");
			DryRun = config.Bind("General", "DryRun", true,
				"Only log what would be regrown, change nothing. Leave on until the log looks right.");
			CheckIntervalMinutes = config.Bind("General", "CheckIntervalMinutes", 10f,
				"How often (real minutes) to look for things to regrow.");
			LogDetailsPerPass = config.Bind("General", "LogDetailsPerPass", 20,
				"How many individual objects to log per category and pass; the rest are only counted.");

			IdleHours = config.Bind("Safety", "IdleHours", 5f,
				"Nothing is regrown in an area a player has been near within this many real hours. Areas are only tracked from the plugin's first run.");
			PresenceRadiusZones = config.Bind("Safety", "PresenceRadiusZones", 2,
				"A player counts as near every zone (64 m square) within this many zones of their own.");
			PlayerBuildRadius = config.Bind("Safety", "PlayerBuildRadius", 48f,
				"Nothing within this many metres (horizontally) of a player-built piece or a tombstone is regrown.");

			LocationsTogether = config.Bind("Locations", "Together", true,
				"Everything a location holds regrows together -- a dungeon's interior with its entrance, a camp, a village, a ruin: as soon as one thing in it is due (each kind after its own AfterHours), every surtling core taken, every creature killed and every treasure chest in it come back at once. Off: each object on its own clock.");

			CoresEnabled = config.Bind("SurtlingCores", "Enabled", true,
				"Put taken surtling cores back on their stands (burial chambers and similar).");
			CoresAfterHours = config.Bind("SurtlingCores", "AfterHours", 10f,
				"Real hours after a core was taken (or after the plugin first saw it taken).");
			CorePrefabs = config.Bind("SurtlingCores", "Prefabs", "Pickable_SurtlingCoreStand",
				"Pickables that stay in place when taken (hide-when-picked) and should come back. Comma separated.");

			ChestsEnabled = config.Bind("TreasureChests", "Enabled", true,
				"Restock treasure chests (never player-built chests) with fresh loot from their own loot table; whatever was left in them is replaced.");
			ChestsAfterHours = config.Bind("TreasureChests", "AfterHours", 24f,
				"Real hours after a player was first near a chest (or it was seen empty) since it was last restocked. Whether anything was taken cannot be told from a chest, so being near it counts.");
			ChestsExclude = config.Bind("TreasureChests", "ExcludePrefabs", "",
				"Chest prefabs never to refill, e.g. TreasureChest_meadows_buried for dug-up treasure. Comma separated.");

			SpawnersEnabled = config.Bind("DungeonMonsters", "Enabled", true,
				"Let one-time creature spawners (dungeons, camps, caves) spawn again once their creature is dead.");
			SpawnersAfterHours = config.Bind("DungeonMonsters", "AfterHours", 24f,
				"Real hours after the plugin first saw the spawner's creature gone.");
			SpawnersExclude = config.Bind("DungeonMonsters", "ExcludePrefabs", "",
				"Spawner prefabs never to reset. Comma separated.");

			LocationObjectsEnabled = config.Bind("LocationObjects", "Enabled", false,
				"Put destroyed objects that a location or a village generated back where they stood -- wild beehives, for example. Unlike everything else here this creates objects, so it is off until switched on. Needs a restart.");
			LocationObjectsAfterHours = config.Bind("LocationObjects", "AfterHours", 48f,
				"Real hours after the plugin first saw the object missing.");
			LocationObjectsPrefabs = config.Bind("LocationObjects", "Prefabs", "Beehive",
				"Prefabs to put back. Only objects the world itself generated in a location or village room are ever recreated, never player-built ones. Comma separated. Needs a restart.");

			TerrainEnabled = config.Bind("Terrain", "Enabled", true,
				"Let dug, raised, levelled and paved ground slowly return to the land's own shape once the area has been left alone. Ground around a player base (anything the game counts as a base: workbench, forge, stonecutter ... and wards) is left as it is; when the base is gone, so is the protection.");
			TerrainAfterHours = config.Bind("Terrain", "AfterHours", 48f,
				"Real hours after the plugin first saw a zone's ground modified before the first step.");
			TerrainStepHours = config.Bind("Terrain", "StepHours", 24f,
				"Real hours between steps for one zone. Each step divides what is left by Divider, so with 1.7 a 4 m hole is 2.4 m after the first step, 1.4 after the second, 0.8, 0.5, 0.3, gone.");
			TerrainDivider = config.Bind("Terrain", "Divider", 1.7f,
				"Each step divides the remaining height change by this.");
			TerrainMinDelta = config.Bind("Terrain", "MinDelta", 0.2f,
				"A height change smaller than this (metres) is dropped to zero.");
			TerrainKeepPaved = config.Bind("Terrain", "KeepPaved", true,
				"Leave paved ground paved (roads, floors); only its height returns.");
			TerrainKeepCultivated = config.Bind("Terrain", "KeepCultivated", true,
				"Leave cultivated ground cultivated (fields).");
			TerrainProtectMultiplier = config.Bind("Terrain", "ProtectMultiplier", 1f,
				"The protected radius around a base object or ward is its own radius times this.");
			TerrainProtectPiecesRadius = config.Bind("Terrain", "ProtectPiecesRadius", 0f,
				"Also protect this many metres around any player-built piece or tombstone (0 = only bases and wards). The same list of pieces as PlayerBuildRadius uses.");
		}

		// Re-reads the file so DryRun and the delays can be changed without a restart.
		// (Enabled, prefab lists and exclusions still need one: they are applied when the plugin starts.)
		public void Reload()
		{
			try
			{
				file.Reload();
			}
			catch (System.Exception e)
			{
				RegrowthPlugin.Log.LogWarning($"Could not re-read the config, keeping the current settings: {e.Message}");
			}
		}
	}
}
