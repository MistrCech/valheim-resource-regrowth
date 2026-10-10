using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace ResourceRegrowth
{
	/*
		Server-side only: runs on a dedicated server, changes world objects directly, and needs
		nothing on clients. Depends on BepInEx and nothing else. Patches one game method, and only
		with [Zones] switched on: ZoneSystem.SpawnLocation, to place a location of a zone generated
		again the way round it was (ZoneReset).
	*/
	[BepInPlugin(GUID, PluginName, PluginVersion)]
	public class RegrowthPlugin : BaseUnityPlugin
	{
		public const string GUID = "sarkasticeu.resource_regrowth";
		public const string PluginName = "Sarkastic.gg Resource Regrowth";
		public const string PluginVersion = "0.7.1";

		internal static ManualLogSource Log;
		// Whether ZoneSystem.SpawnLocation is patched for zone resets (only at startup, with Zones on).
		internal static bool SpawnLocationPatched;

		private Settings settings;
		private Regrowth regrowth;

		private void Awake()
		{
			Log = Logger;
			settings = new Settings(Config);
			if (settings.ZonesEnabled.Value)
			{
				new HarmonyLib.Harmony(GUID).CreateClassProcessor(typeof(ZoneSystem_SpawnLocation_Patch)).Patch();
				SpawnLocationPatched = true;
			}
		}

		private void Update()
		{
			bool ready = settings.Enabled.Value && WorldReady();
			if (regrowth == null && ready)
			{
				regrowth = new Regrowth(this, settings);
				regrowth.Start();
			}
			else if (regrowth != null && !ready)
			{
				regrowth.Stop();
				regrowth = null;
			}
			regrowth?.Tick(Time.unscaledDeltaTime);
		}

		private void OnDestroy()
		{
			regrowth?.Stop();
		}

		private static bool WorldReady()
		{
			return ZNet.instance && ZNet.instance.enabled && ZNet.instance.IsDedicated() && ZNet.instance.IsServer() && ZNet.World != null
				&& ZDOMan.instance != null && ZNetScene.instance && ObjectDB.instance
				&& ZoneSystem.instance && ZoneSystem.instance.LocationsGenerated;
		}
	}
}
