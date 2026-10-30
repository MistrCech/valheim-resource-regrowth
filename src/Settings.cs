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
		public readonly ConfigEntry<bool> ChestsKeepPlayersItems;
		public readonly ConfigEntry<float> CopperRaiseMeters;
		public readonly ConfigEntry<float> CopperOverDeepestHole;

		public readonly ConfigEntry<bool> SpawnersEnabled;
		public readonly ConfigEntry<float> SpawnersAfterHours;
		public readonly ConfigEntry<string> SpawnersExclude;

		public readonly ConfigEntry<bool> LocationObjectsEnabled;
		public readonly ConfigEntry<float> LocationObjectsAfterHours;
		public readonly ConfigEntry<string> LocationObjectsPrefabs;
		public readonly ConfigEntry<string> LocationObjectsLocations;
		public readonly ConfigEntry<string> LocationObjectsLocationsExclude;
		public readonly ConfigEntry<bool> LocationObjectsClearRemains;

		public readonly ConfigEntry<bool> ZonesEnabled;
		public readonly ConfigEntry<bool> ZonesDryRun;
		public readonly ConfigEntry<float> ZonesIdleHours;
		public readonly ConfigEntry<int> ZonesPerPass;
		public readonly ConfigEntry<float> ZonesGraceMinutes;
		public readonly ConfigEntry<string> ZonesProtected;
		public readonly ConfigEntry<bool> ZonesRefreshNow;

		public readonly ConfigEntry<bool> TerrainEnabled;
		public readonly ConfigEntry<float> TerrainAfterHours;
		public readonly ConfigEntry<float> TerrainStepHours;
		public readonly ConfigEntry<float> TerrainDivider;
		public readonly ConfigEntry<float> TerrainMinDelta;
		public readonly ConfigEntry<bool> TerrainKeepPaved;
		public readonly ConfigEntry<bool> TerrainKeepCultivated;
		public readonly ConfigEntry<float> TerrainProtectMultiplier;
		public readonly ConfigEntry<float> TerrainProtectPiecesRadius;
		public readonly ConfigEntry<float> TerrainWorkbenchRadius;

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
				"Nothing within this many metres (horizontally) of a player-built piece or a tombstone is regrown; with Locations.Together nothing of a location that has any part so near. The surface and a dungeon's interior 5000 m above it do not count for each other.");

			LocationsTogether = config.Bind("Locations", "Together", true,
				"Everything a location holds regrows together -- a dungeon's interior with its entrance, a camp, a village, a ruin: as soon as one thing in it is due (each kind after its own AfterHours), every surtling core taken, every creature killed, every treasure chest and every location object missing in it come back at once, and the doors in it that a player can close by hand close. A player's build or tombstone near any of it holds all of it back; only a dungeon has two sides, and a build by its entrance keeps back what is on the surface while the interior comes back whole. Off: each object on its own clock.");

			CoresEnabled = config.Bind("SurtlingCores", "Enabled", true,
				"Put taken surtling cores back on their stands (burial chambers and similar).");
			CoresAfterHours = config.Bind("SurtlingCores", "AfterHours", 10f,
				"Real hours after a core was taken (or after the plugin first saw it taken).");
			CorePrefabs = config.Bind("SurtlingCores", "Prefabs", "Pickable_SurtlingCoreStand",
				"Pickables that stay in place when taken (hide-when-picked) and should come back. Comma separated. A prefab may carry its own hours after a colon instead of AfterHours: goblin_totempole:24 is the totem on a Fuling totem pole (villages, stone towers), back with the village's creatures and chests rather than sooner.");

			ChestsEnabled = config.Bind("TreasureChests", "Enabled", true,
				"Restock treasure chests and the other containers the world comes with a loot table of their own for -- a shipwreck's chest, a Deep North granary, a chest of Morkhalla (never player-built chests) -- with fresh loot from that table; whatever was left in them is replaced.");
			ChestsAfterHours = config.Bind("TreasureChests", "AfterHours", 24f,
				"Real hours after a player was first near a chest (or it was seen empty) since it was last restocked. Whether anything was taken cannot be told from a chest, so being near it counts.");
			ChestsExclude = config.Bind("TreasureChests", "ExcludePrefabs", "",
				"Chest prefabs never to refill, e.g. TreasureChest_meadows_buried for dug-up treasure or shipwreck_karve_chest for the chests of wrecks. Comma separated.");
			ChestsKeepPlayersItems = config.Bind("TreasureChests", "KeepPlayersItems", true,
				"What a player left in a chest of the world (an item remembers having been in a bag) stays in its slot when the chest is restocked, and the loot goes in the free slots. False: it is replaced with the rest of what is in the chest, and gone.");

			SpawnersEnabled = config.Bind("DungeonMonsters", "Enabled", true,
				"Let one-time creature spawners (dungeons, camps, caves) spawn again once their creature is dead.");
			SpawnersAfterHours = config.Bind("DungeonMonsters", "AfterHours", 24f,
				"Real hours after the plugin first saw the spawner's creature gone.");
			SpawnersExclude = config.Bind("DungeonMonsters", "ExcludePrefabs", "",
				"Spawner prefabs never to reset. Comma separated.");

			LocationObjectsEnabled = config.Bind("LocationObjects", "Enabled", false,
				"Put destroyed objects that a location or a village generated back where they stood -- wild beehives, for example. Unlike everything else here this creates objects, so it is off until switched on. Needs a restart.");
			LocationObjectsAfterHours = config.Bind("LocationObjects", "AfterHours", 48f,
				"Real hours after the plugin first saw the object missing. With Locations.Together this is the longest it waits: it comes back with the rest of its location, whatever made that due.");
			LocationObjectsPrefabs = config.Bind("LocationObjects", "Prefabs", "Beehive",
				"Prefabs to put back. Only objects the world itself generated in a location or village room are ever recreated, never player-built ones. Comma separated. Needs a restart. A muddy scrap pile in a sunken crypt is `mudpile2`. Building pieces of a camp or village (walls, poles, roofs) can go here too; one that falls for lack of support after being put back waits until its zone is generated again ([Zones]).");
			LocationObjectsLocations = config.Bind("LocationObjects", "Locations", "",
				"Locations whose every breakable part is put back, without listing prefabs: building pieces (black marble, stave walls, the walls of a fortress), props, rocks and ore, trees and nests -- also in the rooms of their dungeon or village. "
				+ "Not chests ([TreasureChests] fills them), creature spawners ([DungeonMonsters]), pickables (one that goes when picked can go in Prefabs) or items lying about. "
				+ "The parts of one location come back with the rest of it ([Locations] Together), so a building stands whole. "
				+ "Comma separated location names as the game has them; * matches any run of characters (Mistlands_*, Charred*). Needs a restart.");
			LocationObjectsLocationsExclude = config.Bind("LocationObjects", "LocationsExclude", "",
				"Prefabs that Locations never puts back, e.g. a nest (Spawner_CharredStone). Comma separated. Needs a restart.");
			LocationObjectsClearRemains = config.Bind("LocationObjects", "ClearRemains", true,
				"When an object is put back, what is left of it goes: the fractured remains the game spawns when a pile or rock is hit (the prefab's name and \"_frac\", or what it turns into when it breaks), or a felled tree's stump, within 1.5 m of its place. Without this the pile would stand twice, once whole and once as the pieces still to mine.");

			CopperRaiseMeters = config.Bind("CopperDeposits", "RaiseMeters", 0f,
				new ConfigDescription("Metres higher than the game puts copper deposits, in zones still to be generated and on the deposits that stand (not one broken open or within 20 m of a player's building). The game sinks them so far that a median of 46 of a deposit's 130 pieces reach over the ground, the worst 18; 4 m: 88, the worst 62, and the rock up to 6 m tall. Back to 0 puts them back. Read at every pass.",
					new AcceptableValueRange<float>(0f, 8f)));
			CopperOverDeepestHole = config.Bind("CopperDeposits", "OverDeepestHole", -1f,
				new ConfigDescription("Metres every piece of a copper deposit reaches over the deepest hole a pickaxe can dig around it (8 m under the ground as the game made it, a location's levelling included): each deposit stands that much higher than RaiseMeters puts it, by what it needs itself, so a player who digs it out leaves no copper in the ground. The game sinks the rock (17 m tall) so deep that even 4 m higher most deposits keep pieces nobody can reach, most on slopes; 0.5 is enough for a swing. A deposit within 200 m of a player waits for a later pass; one within 20 m of a player's building stays. -1: off, and what it raised goes back down. Read at every pass; new zones need it on at startup.",
					new AcceptableValueRange<float>(-1f, 4f)));

			ZonesEnabled = config.Bind("Zones", "Enabled", false,
				"Generate a zone players have been in again, as the world first made it, once nobody has been near it for IdleHours: trees, rocks, ore deposits, bushes, pickables, the location with its buildings, chests, creatures and dungeon, and the ground -- as if no player had ever been there. Never a zone with a workbench in it or within PlayerBuildRadius (a base), never a protected location. Players' things elsewhere stay where they are (pieces, tombstones, ships, carts, items they carried, tamed creatures, chests and stands holding something of theirs); fields, roads and trees they planted go with the reset unless a workbench is near. Unlike the rest this deletes and creates objects, so it is off until switched on. Needs a restart.");
			ZonesDryRun = config.Bind("Zones", "DryRun", true,
				"Only log which zones would be generated again and what they hold; change nothing. General.DryRun does the same for everything.");
			ZonesIdleHours = config.Bind("Zones", "IdleHours", 72f,
				"Real hours nobody may have been near a zone (PresenceRadiusZones) before it is generated again, counted from this feature's first run at the earliest.");
			ZonesPerPass = config.Bind("Zones", "PerPass", 8,
				"Most zones generated again per pass (every CheckIntervalMinutes); each takes a few tens of milliseconds of one frame.");
			ZonesGraceMinutes = config.Bind("Zones", "GraceMinutes", 30f,
				"Minutes after a start before any zone is generated again: who was near while the server was off is not known, and players coming back need time to get near their places.");
			ZonesProtected = config.Bind("Zones", "ProtectedLocations",
				"StartTemple,Eikthyrnir,GDKing,Bonemass,Dragonqueen,GoblinKing,Mistlands_DvergrBossEntrance1,FaderLocation,Vendor_BlackForest,Hildir_camp,BogWitch_Camp,Hildir_crypt,Hildir_cave,Hildir_plainsfortress",
				"Locations whose zone is never generated again, besides every location the game places only once. Comma separated. Needs a restart.");

			ZonesRefreshNow = config.Bind("Zones", "RefreshNow", false,
				"One-off, switches itself off in the config when done: in the first pass after a start generate again every visited zone nobody has been near for the last 30 minutes (not only those idle for IdleHours), "
				+ "without the PerPass limit and without GraceMinutes, and bring the ground of every modified zone back to the land's own shape at once, paint too, whatever KeepPaved and KeepCultivated say. "
				+ "What a workbench or a ward protects, protected locations and the 4 m around players' pieces stay as they are. Needs Zones.Enabled and no DryRun.");

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
			TerrainWorkbenchRadius = config.Bind("Terrain", "WorkbenchRadius", 48f,
				"Ground within this many metres of a player's workbench stays as it is, whatever the workbench's own radius (10 m): the distance that keeps a zone from being generated again (Safety PlayerBuildRadius), so a ditch, a moat or a levelled yard dug around a base is kept with it. 0 = a workbench protects only its own radius. Read at every pass.");
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
