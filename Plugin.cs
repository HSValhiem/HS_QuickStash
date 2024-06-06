using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using HS;
using ServerSync;
using UnityEngine;

//TODO: Add Localization
//TODO: Add Stack Overflow when Container is filled mid stack

namespace HS_QuickStash
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private static readonly ConfigSync ConfigSync = new(MyPluginInfo.PLUGIN_GUID) { DisplayName = MyPluginInfo.PLUGIN_NAME, CurrentVersion = MyPluginInfo.PLUGIN_VERSION, MinimumRequiredVersion = "0.1.9" };

       //public new static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource(MyPluginInfo.PLUGIN_NAME);

        private static ConfigEntry<Toggle> _serverConfigLocked = null!;
        public static ConfigEntry<bool> ModEnabled = null!;
        private static ConfigEntry<bool> _overrideVersionCheck = null!;

        public static ConfigEntry<int> ContainerRange = null!;

        public static ConfigEntry<int> ExcludedSlots = null!;

        public static ConfigEntry<KeyboardShortcut> QuickStashHotkey = null!;

        public static List<Container> ContainersFastAccess = [];

        #region Config Boilerplate

        private new ConfigEntry<T> Config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
        {
            var configEntry = base.Config.Bind<T>(group, name, value, description);
            var syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;
            return configEntry;
        }

        private new ConfigEntry<T> Config<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
        {
            return Config(group, name, value, new ConfigDescription(description), synchronizedSetting);
        }

        private enum Toggle
        {
            On = 1,
            Off = 0
        }

        private enum FilterMode
        {
            Blacklist = 0,
            Whitelist = 1
        }

        #endregion

        private void Awake()
        {
            _serverConfigLocked = Config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can only be changed by server admins.");
            ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
            ModEnabled = Config("1 - General", "Mod Enabled", true, "");

            _overrideVersionCheck = Config("1 - General", "Override Version Check", true, new ConfigDescription("Set to True to override the Valheim version check and allow the mod to start even if an incorrect Valheim version is detected.", null, new ConfigurationManagerAttributes { Browsable = false }));

            ContainerRange = Config("2 - Settings", "Max Container Distance", 100, new ConfigDescription("The maximum range to stash items"));
            ExcludedSlots = Config("2 - Settings", "Excluded Inventory Slots", 8, new ConfigDescription("Slots in Inventory to not Stash from 1-8 on the first row"));
            QuickStashHotkey = Config("2 - Settings", "Quick Stash Keybind", new KeyboardShortcut(KeyCode.G), new ConfigDescription("Key to Press to Quickly Stash your Inventory to Nearby Chests"), false);

#if !DEBUG
        // Check if Plugin was Built for Current Version of Valheim
        if (!_overrideVersionCheck.Value && !VersionChecker.Check(Logger, Info, _overrideVersionCheck.Value, base.Config)) return;
#endif

            Harmony harmony = new(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

        }
    }
    public class ContainerData
    {
        public Container Container { get; }
        public Dictionary<string, int> ItemData { get; }

        public ContainerData(Container container)
        {
            Container = container;
            // Populate ItemData dictionary using item name as the key
            ItemData = container.GetInventory().GetAllItems()
                .GroupBy(item => item.m_shared.m_name)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.m_stack));
        }
    }

    [HarmonyPatch]
    class Patch
    {

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Container), "Awake")]
        private static void Patch_Container_Awake(Container __instance, ZNetView? ___m_nview)
        {
            // Add Container to FastAccess List
            if (___m_nview == null || ___m_nview.GetZDO() == null || ___m_nview.GetComponent<Piece>() == null) return;
            if (__instance.ToString().StartsWith("piece_chest") && !Plugin.ContainersFastAccess.Contains(__instance)) Plugin.ContainersFastAccess.Add(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Container), "OnDestroyed")]
        private static void Patch_Container_OnDestroyed(Container __instance) => Plugin.ContainersFastAccess.Remove(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Container), "GetHoverText")]
        public static string Patch_Container_GetHoverText(string __result) => __result + "\n[<color=yellow><b>" + Plugin.QuickStashHotkey.Value + "</b></color>] Quick Stash";


        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "Update")]
        private static void Patch_Player_Update(Player __instance)
        {
            if (!Plugin.ModEnabled.Value || !Input.GetKeyDown(Plugin.QuickStashHotkey.Value.MainKey)) return;

            // Retrieve the player's inventory and relevant configuration values
            var playerInventory = __instance.GetInventory();
            var excludedSlotsCount = Plugin.ExcludedSlots.Value;
            var allContainers = Plugin.ContainersFastAccess;

            // Create a dictionary mapping each slot in the player's inventory to its item
            var playerItemMap = Enumerable.Range(0, playerInventory.m_height)
                .SelectMany(y => Enumerable.Range(0, playerInventory.m_width)
                .Select(x => (new Vector2i(x, y), playerInventory.GetItemAt(x, y))))
                .ToDictionary(pair => pair.Item1, pair => pair.Item2);

            // Initialize collections to hold container data and empty containers
            var containerDataList = new List<ContainerData>();
            var emptyContainers = new List<Container>();

            // Populate container data and identify empty containers
            foreach (var container in allContainers)
            {
                var containerInventory = container.GetInventory();

                // Track empty containers
                if (containerInventory.NrOfItems() <= 0)
                {
                    emptyContainers.Add(container);
                    continue;
                }

                // Skip containers that are out of range
                if (Vector3.Distance(container.transform.position, __instance.transform.position) > Plugin.ContainerRange.Value) continue;

                // Add container data to the list
                containerDataList.Add(new ContainerData(container));
            }

            // Process each item in the player's inventory
            foreach (var playerItem in playerItemMap)
            {
                // Skip empty slots
                if (playerItem.Value == null) continue;

                // Skip excluded slots (e.g., hotbar or special slots)
                if (playerItem.Key.y == 0 && playerItem.Key.x <= excludedSlotsCount) continue;

                // Initialize a list to store containers with matching items and a variable for the best partial container
                var matchingItems = new List<ContainerData>();
                Container? bestPartialContainer = null;

                // Find containers that can store the current player item
                foreach (var containerData in containerDataList)
                {
                    var containerInventory = containerData.Container.GetInventory();

                    // Skip containers that cannot add the item
                    if (!containerInventory.CanAddItem(playerItem.Value)) continue;

                    // Track the container with the most empty slots as a potential candidate
                    if (bestPartialContainer == null || containerInventory.GetEmptySlots() > bestPartialContainer.GetInventory().GetEmptySlots())
                        bestPartialContainer = containerData.Container;

                    // Check if the item name exists in the container's item data
                    if (containerData.ItemData.ContainsKey(playerItem.Value.m_shared.m_name)) matchingItems.Add(containerData);
                }

                // Sort matching containers by item count in descending order
                var sortedMatchingItems = matchingItems.OrderByDescending(match => match.ItemData[playerItem.Value.m_shared.m_name]).ToList();

                // Try to add the item to the best matching container
                var added = sortedMatchingItems.Any(match => match.Container.GetInventory().AddItem(playerItem.Value));

                // TODO: Use container distance calculation
                // If unable to add to matching containers, try an empty container
                if (!added && emptyContainers.Any())
                {
                    emptyContainers.First().GetInventory().AddItem(playerItem.Value);
                    added = true;
                }

                // If unable to add to an empty container, try the best partial container
                if (!added && bestPartialContainer != null)
                {
                    bestPartialContainer.GetInventory().AddItem(playerItem.Value);
                    added = true;
                }

                // If still unable to store the item, notify the player
                if (!added)
                {
                    __instance?.Message(MessageHud.MessageType.Center, $"No storage available for {playerItem.Value.m_shared.m_name}");
                    continue;
                }

                // Remove the item from the player's inventory once it has been stored
                playerInventory.RemoveItem(playerItem.Value);
            }
        }
    }
}
