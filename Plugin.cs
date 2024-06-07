using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
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
        private static readonly ConfigSync ConfigSync =
            new(MyPluginInfo.PLUGIN_GUID) { DisplayName = MyPluginInfo.PLUGIN_NAME, CurrentVersion = MyPluginInfo.PLUGIN_VERSION, MinimumRequiredVersion = "0.0.2" };

        public new static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource(MyPluginInfo.PLUGIN_NAME);

        private static ConfigEntry<Toggle> _serverConfigLocked = null!;
        public static ConfigEntry<bool> ModEnabled = null!;
        private static ConfigEntry<bool> _overrideVersionCheck = null!;

        public static ConfigEntry<int> ContainerRange = null!;
        public static ConfigEntry<int> ExcludedSlots = null!;

        public static ConfigEntry<bool> PrioritizeChestInUse = null!;

        public static ConfigEntry<bool> HighlightChests = null!;
        public static ConfigEntry<Color> HighlightColor = null!;
        public static ConfigEntry<int> HighlightTime = null!;

        public static ConfigEntry<KeyboardShortcut> QuickStashHotkey = null!;

        public static ConfigEntry<bool> DebugLogging = null!;

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
            ExcludedSlots = Config("2 - Settings", "Excluded Inventory Slots", 8, new ConfigDescription("Exclude slots from the top left corner of the inventory's first row when storing items"));
            QuickStashHotkey = Config("2 - Settings", "Quick Stash Keybind", new KeyboardShortcut(KeyCode.G), new ConfigDescription("Key to Press to Quickly Stash your Inventory to Nearby Chests"), false);
            HighlightChests = Config("2 - Settings", "Highlight Target Chests", true, new ConfigDescription("Highlights chests that items are sorted to"));
            PrioritizeChestInUse = Config("2 - Settings", "Prioritize Chest in Use", true, new ConfigDescription("If a matching item is not found when stashing, try to add item to chest that is open"));
            HighlightColor = Config("2 - Settings", "Highlight Chest Color", Color.green, new ConfigDescription("Color to Highlight chests"));
            HighlightTime = Config("2 - Settings", "Highlight Chest Time", 3, new ConfigDescription("Time in seconds to Highlight chests"));
            DebugLogging = Config("2 - Settings", "Enable Debug Logging", true, new ConfigDescription("Log Items that are stashed and extra debug info to Log"));
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
        public Container? Container { get; }
        public Dictionary<string, int> ItemData { get; }

        private List<KeyValuePair<Renderer, Material[]>>? _chestHighlightMaterials;

        public ContainerData(Container container)
        {
            Container = container;
            // Populate ItemData dictionary using item name as the key
            ItemData = container.GetInventory().GetAllItems()
                .GroupBy(item => item.m_shared.m_name)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.m_stack));
        }

        public async void DisableHighlight()
        {
            await Task.Delay(Plugin.HighlightTime.Value * 1000);
            SetChestHighlight(false);
        }

        public void SetChestHighlight(bool enabled)
        {
            if (Container == null) return;
            if ((enabled && _chestHighlightMaterials != null) || (!enabled && _chestHighlightMaterials == null)) return;
            var renderers = Container.m_piece.GetComponentsInChildren<Renderer>();

            if (enabled)
            {
                _chestHighlightMaterials = renderers.Select(renderer => new KeyValuePair<Renderer, Material[]>(renderer, renderer.sharedMaterials)).ToList();
                renderers.ToList().ForEach(renderer =>
                    renderer.materials.ToList().ForEach(material =>
                    {
                        if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Plugin.HighlightColor.Value * Plugin.HighlightColor.Value.a);
                        material.color = Plugin.HighlightColor.Value;
                    })
                );
                DisableHighlight();
            }
            else
            {
                if (_chestHighlightMaterials == null) return;
                _chestHighlightMaterials.Where(kvp => kvp.Key != null).ToList().ForEach(kvp => kvp.Key.materials = kvp.Value);
                _chestHighlightMaterials = null;
            }
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

            // Make sure that we are not in a menu
            if (TextInput.IsVisible() || Settings.instance != null || Menu.IsVisible() || Console.IsVisible() || Minimap.IsOpen() || Game.IsPaused() ||
                PlayerCustomizaton.IsBarberGuiVisible() || Chat.instance != null && Chat.instance.IsChatDialogWindowVisible() || StoreGui.IsVisible() || Hud.InRadial() ||
                InventoryGui.IsVisible() &&
                (InventoryGui.instance.IsSkillsPanelOpen || InventoryGui.instance.IsTrophisPanelOpen || InventoryGui.instance.IsTextPanelOpen))
                return;

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
            var emptyContainers = new List<ContainerData>();

            // Populate container data and identify empty containers
            foreach (var container in allContainers)
            {
                var containerInventory = container.GetInventory();

                // Track empty containers
                if (containerInventory.NrOfItems() <= 0)
                {
                    emptyContainers.Add(new ContainerData(container));
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

                // Skip excluded slots
                if (playerItem.Key.y == 0 && playerItem.Key.x <= excludedSlotsCount) continue;

                // Skip Equipped Items
                if (playerItem.Value.m_equipped) continue;

                // Initialize a list to store containers with matching items and a variable for the best partial container
                var matchingItems = new List<ContainerData>();

                // Track best partial container
                ContainerData? bestPartialContainer = null;

                // Track container that is in Use by the Player
                ContainerData? containerInUse = null;

                // Find containers that can store the current player item
                foreach (var containerData in containerDataList)
                {
                    if (containerData.Container == null) continue;
                    var containerInventory = containerData.Container.GetInventory();

                    // Skip containers that cannot add the item
                    if (!containerInventory.CanAddItem(playerItem.Value)) continue;

                    // Track the container with the most empty slots as a potential candidate
                    if (bestPartialContainer == null || bestPartialContainer.Container != null && containerInventory.GetEmptySlots() > bestPartialContainer.Container.GetInventory().GetEmptySlots())
                        bestPartialContainer = containerData;

                    // Check if the item name exists in the container's item data
                    if (containerData.ItemData.ContainsKey(playerItem.Value.m_shared.m_name)) matchingItems.Add(containerData);

                    // Check if Container is in use by the player and track it
                    if (containerData.Container.IsInUse()) containerInUse = containerData;
                }

                // Sort matching containers by item count in descending order
                var sortedMatchingItems = matchingItems.OrderByDescending(match => match.ItemData[playerItem.Value.m_shared.m_name]).ToList();

                // Try to add the item to the best matching container
                ContainerData? targetContainer = null;
                foreach (var match in sortedMatchingItems)
                {
                    if (match.Container == null) continue;

                    if (match.Container.GetInventory().AddItem(playerItem.Value))
                    {
                        targetContainer = match;
                        break;
                    }
                }

                // If unable to add to matching containers, try the container that the player is currently using
                if (targetContainer == null && containerInUse != null && containerInUse.Container != null && Plugin.PrioritizeChestInUse.Value)
                    targetContainer = containerInUse.Container.GetInventory().AddItem(playerItem.Value) ? containerInUse : null;

                // TODO: Add Config Option to determine how to handle full matched containers
                // If unable to add to matching containers, try an empty container starting from the closest
                if (targetContainer == null && emptyContainers.Any())
                {
                    var closestContainer = emptyContainers
                        .Where(emptyContainer => emptyContainer?.Container != null)
                        .OrderBy(emptyContainer => emptyContainer.Container != null ? Vector3.Distance(emptyContainer.Container.transform.position, __instance!.transform.position) : float.MaxValue)
                        .FirstOrDefault();

                    targetContainer = closestContainer?.Container?.GetInventory().AddItem(playerItem.Value) == true ? closestContainer : null;
                }
                // If unable to add to an empty container, try the best partial container
                if (targetContainer == null && bestPartialContainer != null)
                    targetContainer = bestPartialContainer.Container?.GetInventory().AddItem(playerItem.Value) == true ? bestPartialContainer : null;

                // If still unable to store the item, notify the player
                if (targetContainer == null)
                {
                    __instance?.Message(MessageHud.MessageType.Center, "No available storage to stash items");
                    if (Plugin.DebugLogging.Value) Plugin.Logger.LogInfo($"Quick Stash halted on Item: {Localization.instance.Localize(playerItem.Value.m_shared.m_name)}");
                    continue;
                }

                if (targetContainer.Container == null) continue;

                // Remove the item from the player's inventory once it has been stored
                playerInventory.RemoveItem(playerItem.Value);

                // Log Debug Info
                if (Plugin.DebugLogging.Value)
                    Plugin.Logger.LogInfo($"Added (Item: {Localization.instance.Localize(playerItem.Value.m_shared.m_name)}) Count: {playerItem.Value.m_stack} to" + 
                                          $" Container: Name: {targetContainer.Container.m_name} Distance: {Vector3.Distance(targetContainer.Container.transform.position, __instance!.transform.position)}");

                // Temporary highlight the chest
                if (Plugin.HighlightChests.Value) targetContainer.SetChestHighlight(true);
            }
        }
    }
}
