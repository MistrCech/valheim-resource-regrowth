# Changelog

## [0.5.1] - 2026-10-07

### Changed

- Renamed to Sarkastic.gg Resource Regrowth: the community moved from sarkastic.eu to sarkastic.gg.
  The DLL is now `SarkasticGG_Resource_Regrowth.dll`; delete `SarkasticEU_Resource_Regrowth.dll`
  from `BepInEx/plugins` when upgrading. The plugin GUID, the config file
  `sarkasticeu.resource_regrowth.cfg` and the state file are unchanged, and nothing else is.

## [0.5.0] - 2026-09-28

### Added

- `[LocationObjects]` (off by default): destroyed objects that a location or a village generated
  are put back where they stood -- `Prefabs` = `Beehive`, the wild beehives of abandoned houses, bear
  caves, ruined towers and meadow villages. Unlike the other categories this creates objects. The
  place is exact: every generated location leaves a proxy with its position, rotation and seed, and
  every village or camp generator keeps its buildings' positions and rotations; objects that spawn
  by chance are decided by replaying the game's roll from that seed. Checked on a copy of the live
  world: 169 hive places, 47 of them with a hive by the replay, all 29 standing hives on those
  places (0.00 m apart in height too) and none elsewhere. Of the 18 missing, 14 would be put back,
  3 lie near player builds and 1 has lost the house that held it -- such a hive is not put back, so
  nothing hangs in the air (pulling down another house for the test kept that one out too). The
  put-back hives survived a
  restart and the server instantiated them where the originals hang, 4.08 m above the floor under
  the roof. Same rules as the rest: `AfterHours` (48) since first seen missing, `IdleHours`,
  `PlayerBuildRadius`, and the zone must not be loaded.
- The first scan after a start reads the layouts of the world's locations and village buildings
  through the game's own asynchronous loader: about 0.3 s of work over 45 s, no frame over 100 ms
  (loading them directly cost 9.9 s and frames up to 440 ms). Later scans only read new ones.

## [0.4.0] - 2026-09-13

### Added

- `[Terrain]`: dug, raised, levelled and paved ground slowly returns to the land's own shape once
  the area has been left alone (`IdleHours`, then `AfterHours` after the zone's ground was first
  seen modified, then a step every `StepHours`). Each step divides the remaining height change
  by `Divider` and drops it below `MinDelta`; paint goes back to nothing once the height under it
  is back, paved and cultivated ground excepted. Ground around player base objects (the game's
  own PlayerBase areas: workbench, forge, beds, fires ...) and wards is left alone by the object's
  own radius, so a base that creatures broke protects nothing any more. The data is written in
  the game's format (`TerrainComp.Save`); verified on a local 1.0.12 server: all 38 modified
  zones of a copied live world round-trip byte-identically, and a step reduced e.g. 242 to 230
  modified vertices with 587 to 230 painted ones, 60 vertices near a base left untouched.
