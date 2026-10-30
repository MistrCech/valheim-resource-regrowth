using System;
using System.Collections.Generic;
using System.Linq;

namespace ResourceRegrowth
{
	// What of a player's an object of the world holds: an item remembers having been in a player's bag (ItemData.m_pickedUp).
	internal static class PlayersItems
	{
		// A chest, barrel or crate of the world with anything in it that has been in a player's bag: someone's storage. Unreadable: kept, to be safe.
		public static bool InContainer(ZDO zdo)
		{
			return !TryRead(zdo, out List<ItemDrop.ItemData> items) || items.Any(item => item.m_pickedUp);
		}

		// What a chest, barrel or crate of the world holds; false if it cannot be read.
		public static bool TryRead(ZDO zdo, out List<ItemDrop.ItemData> items)
		{
			items = new List<ItemDrop.ItemData>();
			byte[] data = zdo.GetByteArray(ZDOVars.s_items);
			if (data == null)
			{
				return true;
			}
			try
			{
				Inventory inventory = new Inventory("regrowth", null, 16, 16);
				inventory.Load(new ZPackage(data));
				items = inventory.GetAllItems();
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/*
			An item lying about. ItemDrop.SaveToZDO: a version byte (109), then ItemData.Save -- durability
			(int), grid x, grid y, world level, flags (bit 0: it has been in a player's bag). An item the
			world made and nobody has loaded since has no data yet (ItemDrop.Start writes it): the world's.
		*/
		public static bool Carried(ZDO zdo)
		{
			byte[] data = zdo.GetByteArray(ZDOVars.s_itemData);
			if (data == null)
			{
				return false;
			}
			if (data.Length < 9 || data[0] != 109)
			{
				return true;
			}
			return (data[8] & 1) != 0;
		}
	}
}
