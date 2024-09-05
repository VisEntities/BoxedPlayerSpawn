/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Rust;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Boxed Player Spawn", "VisEntities", "1.1.1")]
    [Description("Spawns players inside a shelter when they join the server for the first time.")]
    public class BoxedPlayerSpawn : RustPlugin
    {
        #region Fields

        private static BoxedPlayerSpawn _plugin;
        private static Configuration _config;
        private StoredData _storedData;

        private System.Random _randomGenerator = new System.Random();

        private Dictionary<LegacyShelter, BoxStorage> _spawnedShelters = new Dictionary<LegacyShelter, BoxStorage>();

        private const string PREFAB_LEGACY_SHELTER = "assets/prefabs/building/legacy.shelter.wood/legacy.shelter.wood.deployed.prefab";
        private const string PREFAB_WOODEN_BOX = "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";
        
        private const int LAYER_ENTITIES = Layers.Mask.Deployed | Layers.Mask.Construction;
        private const int LAYER_TERRAIN = Layers.Mask.Terrain;

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Number Of Attempts To Find Shelter Position")]
            public int NumberOfAttemptsToFindShelterPosition { get; set; }

            [JsonProperty("Search Radius For Shelter Position Around Beach Spawn Point")]
            public float SearchRadiusForShelterPositionAroundBeachSpawnPoint { get; set; }

            [JsonProperty("Nearby Entities Avoidance Radius")]
            public float NearbyEntitiesAvoidanceRadius { get; set; }

            [JsonProperty("Rocks Avoidance Radius")]
            public float RocksAvoidanceRadius { get;    set; }

            [JsonProperty("Shelter Lifetime Seconds")]
            public float ShelterLifetimeSeconds { get; set; }

            [JsonProperty("Spawn Box Storage Inside Shelter")]
            public bool SpawnBoxStorageInsideShelter { get; set; }

            [JsonProperty("Items To Spawn Inside Box Storage")]
            public List<ItemInfo> ItemsToSpawnInsideBoxStorage { get; set; }
        }

        public class ItemInfo
        {
            [JsonProperty("Shortname")]
            public string Shortname { get; set; }

            [JsonProperty("Skin Id")]
            public ulong SkinId { get; set; }

            [JsonProperty("Minimum Amount")]
            public int MinimumAmount { get; set; }

            [JsonProperty("Maximum Amount")]
            public int MaximumAmount { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;


            if (string.Compare(_config.Version, "1.1.0") < 0)
            {
                _config.SpawnBoxStorageInsideShelter = defaultConfig.SpawnBoxStorageInsideShelter;
                _config.ItemsToSpawnInsideBoxStorage = defaultConfig.ItemsToSpawnInsideBoxStorage;
            }

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                NumberOfAttemptsToFindShelterPosition = 5,
                SearchRadiusForShelterPositionAroundBeachSpawnPoint = 5f,
                NearbyEntitiesAvoidanceRadius = 6f,
                RocksAvoidanceRadius = 5f,
                ShelterLifetimeSeconds = 30f,
                SpawnBoxStorageInsideShelter = true,
                ItemsToSpawnInsideBoxStorage = new List<ItemInfo>()
                {
                    new ItemInfo
                    {
                        Shortname = "wood",
                        SkinId = 0,
                        MinimumAmount = 100,
                        MaximumAmount = 200,
                    },
                    new ItemInfo
                    {
                        Shortname = "metal.fragments",
                        SkinId = 0,
                        MinimumAmount = 50,
                        MaximumAmount = 100,
                    },
                }
            };
        }

        #endregion Configuration

        #region Data Utility

        public class DataFileUtil
        {
            private const string FOLDER = "";

            public static string GetFilePath(string filename = null)
            {
                if (filename == null)
                    filename = _plugin.Name;

                return Path.Combine(FOLDER, filename);
            }

            public static string[] GetAllFilePaths()
            {
                string[] filePaths = Interface.Oxide.DataFileSystem.GetFiles(FOLDER);

                for (int i = 0; i < filePaths.Length; i++)
                {
                    // Remove the redundant '.json' from the filepath. This is necessary because the filepaths are returned with a double '.json'.
                    filePaths[i] = filePaths[i].Substring(0, filePaths[i].Length - 5);
                }

                return filePaths;
            }

            public static bool Exists(string filePath)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(filePath);
            }

            public static T Load<T>(string filePath) where T : class, new()
            {
                T data = Interface.Oxide.DataFileSystem.ReadObject<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static T LoadIfExists<T>(string filePath) where T : class, new()
            {
                if (Exists(filePath))
                    return Load<T>(filePath);
                else
                    return null;
            }

            public static T LoadOrCreate<T>(string filePath) where T : class, new()
            {
                T data = LoadIfExists<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static void Save<T>(string filePath, T data)
            {
                Interface.Oxide.DataFileSystem.WriteObject<T>(filePath, data);
            }

            public static void Delete(string filePath)
            {
                Interface.Oxide.DataFileSystem.DeleteDataFile(filePath);
            }
        }

        #endregion Data Utility

        #region Stored Data

        public class StoredData
        {
            [JsonProperty("Previously Connected Players")]
            public HashSet<ulong> PreviouslyConnectedPlayers { get; set; } = new HashSet<ulong>();
        }

        #endregion Stored Data

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            PermissionUtil.RegisterPermissions();
            _storedData = DataFileUtil.LoadOrCreate<StoredData>(DataFileUtil.GetFilePath());
        }

        private void Unload()
        {
            KillAllLegacyShelters();
            _config = null;
            _plugin = null;
        }

        private void OnNewSave()
        {
            DataFileUtil.LoadOrCreate<StoredData>(DataFileUtil.GetFilePath());
        }

        private object OnPlayerRespawn(BasePlayer player, BasePlayer.SpawnPoint spawnPoint)
        {
            if (player == null || PermissionUtil.HasPermission(player, PermissionUtil.IGNORE))
                return null;

            if (_storedData.PreviouslyConnectedPlayers.Contains(player.userID))
                return null;

            if (TerrainUtil.OnTopology(spawnPoint.pos, TerrainTopology.Enum.Beach))
            {
                Vector3 shelterPosition;
                Quaternion shelterRotation;

                if (TryFindSuitableShelterPosition(spawnPoint.pos, _config.SearchRadiusForShelterPositionAroundBeachSpawnPoint, _config.NumberOfAttemptsToFindShelterPosition, out shelterPosition, out shelterRotation))
                {
                    LegacyShelter legacyShelter = SpawnLegacyShelter(shelterPosition, shelterRotation, player);
                    if (legacyShelter != null)
                    {
                        spawnPoint.pos = shelterPosition;
                        _storedData.PreviouslyConnectedPlayers.Add(player.userID);
                        DataFileUtil.Save(DataFileUtil.GetFilePath(), _storedData);

                        return new BasePlayer.SpawnPoint
                        {
                            pos = spawnPoint.pos,
                            rot = spawnPoint.rot
                        };
                    }
                }
            }

            return null;
        }

        #endregion Oxide Hooks

        #region Shelter Spawning and Setup

        private bool TryFindSuitableShelterPosition(Vector3 center, float searchRadius, int maximumAttempts, out Vector3 suitablePosition, out Quaternion suitableRotation)
        {
            for (int attempt = 0; attempt < maximumAttempts; attempt++)
            {
                Vector3 position = TerrainUtil.GetRandomPositionAround(center, minimumRadius: 0f, maximumRadius: searchRadius);
                if (TerrainUtil.OnTopology(position, TerrainTopology.Enum.Beach)
                    && !TerrainUtil.InsideRock(position, _config.RocksAvoidanceRadius)
                    && !TerrainUtil.HasEntityNearby(position, _config.NearbyEntitiesAvoidanceRadius, LAYER_ENTITIES)
                    && !PlayerUtil.HasPlayerNearby(position, _config.NearbyEntitiesAvoidanceRadius))
                {
                    RaycastHit groundHit;
                    if (TerrainUtil.GetGroundInfo(position, out groundHit, 5f, LAYER_TERRAIN))
                    {
                        suitablePosition = groundHit.point;
                        suitableRotation = Quaternion.FromToRotation(Vector3.up, groundHit.normal);
                        return true;
                    }
                }
            }

            suitablePosition = Vector3.zero;
            suitableRotation = Quaternion.identity;
            return false;
        }

        private LegacyShelter SpawnLegacyShelter(Vector3 position, Quaternion rotation, BasePlayer player)
        {
            LegacyShelter legacyShelter = GameManager.server.CreateEntity(PREFAB_LEGACY_SHELTER, position, rotation) as LegacyShelter;
            if (legacyShelter == null)
                return null;

            legacyShelter.OnPlaced(player);
            legacyShelter.Spawn();

            StartRemovalTimer(legacyShelter, _config.ShelterLifetimeSeconds);
            LockLegacyShelterDoor(legacyShelter);

            BoxStorage woodenBox = null;
            if (_config.SpawnBoxStorageInsideShelter)
                woodenBox = SpawnBoxStorageInsideShelter(position);

            _spawnedShelters.Add(legacyShelter, woodenBox);

            return legacyShelter;
        }
        
        private void LockLegacyShelterDoor(LegacyShelter legacyShelter)
        {
            LegacyShelterDoor door = legacyShelter.GetChildDoor();
            if (door != null)
            {
                BaseLock lockEntity = door.GetSlot(BaseEntity.Slot.Lock) as BaseLock;
                if (lockEntity != null)
                {
                    lockEntity.SetFlag(BaseEntity.Flags.Locked, true);
                }
            }
        }

        #endregion Shelter Spawning and Setup

        #region Interior Wooden Box Spawning and Filling

        private BoxStorage SpawnBoxStorageInsideShelter(Vector3 shelterPosition)
        {
            RaycastHit groundHit;
            if (TerrainUtil.GetGroundInfo(shelterPosition, out groundHit, 2f, LAYER_TERRAIN))
            {
                Vector3 boxPosition = groundHit.point;
                Quaternion boxRotation = Quaternion.FromToRotation(Vector3.up, groundHit.normal);

                BoxStorage woodenBox = GameManager.server.CreateEntity(PREFAB_WOODEN_BOX, boxPosition, boxRotation) as BoxStorage;
                if (woodenBox != null)
                {
                    woodenBox.Spawn();
                    PopulateItems(woodenBox.inventory, _config.ItemsToSpawnInsideBoxStorage);
                    return woodenBox;
                }
            }

            return null;
        }

        private void PopulateItems(ItemContainer itemContainer, List<ItemInfo> items)
        {
            Shuffle(items);
            foreach (ItemInfo itemInfo in items)
            {
                var itemDefinition = ItemManager.FindItemDefinition(itemInfo.Shortname);
                if (itemDefinition != null)
                {
                    int amountToAdd = Random.Range(itemInfo.MinimumAmount, itemInfo.MaximumAmount + 1);
                    Item item = ItemManager.Create(itemDefinition, amountToAdd, itemInfo.SkinId);

                    if (!item.MoveToContainer(itemContainer))
                        item.Remove();
                }

                if (itemContainer.itemList.Count >= itemContainer.capacity)
                    break;
            }
        }

        #endregion Interior Wooden Box Spawning and Filling

        #region Helper Functions

        private static void Shuffle<T>(List<T> list)
        {
            int remainingItems = list.Count;

            while (remainingItems > 1)
            {
                remainingItems--;
                int randomIndex = _plugin._randomGenerator.Next(remainingItems + 1);

                T itemToSwap = list[randomIndex];
                list[randomIndex] = list[remainingItems];
                list[remainingItems] = itemToSwap;
            }
        }

        #endregion Helper Functions

        #region Shelters Removal

        private void StartRemovalTimer(LegacyShelter legacyShelter, float lifetimeSeconds)
        {
            timer.Once(lifetimeSeconds, () =>
            {
                if (legacyShelter != null)
                {
                    if (_spawnedShelters.TryGetValue(legacyShelter, out BoxStorage woodenBox))
                    {
                        if (woodenBox != null)
                            woodenBox.Kill();
                    }

                    legacyShelter.Kill();
                    _spawnedShelters.Remove(legacyShelter);
                }
            });
        }

        private void KillAllLegacyShelters()
        {
            foreach (var kvp in _spawnedShelters)
            {
                LegacyShelter shelter = kvp.Key;
                BoxStorage woodenBox = kvp.Value;

                if (shelter != null)
                    shelter.Kill();

                if (woodenBox != null)
                    woodenBox.Kill();
            }

            _spawnedShelters.Clear();
        }

        #endregion Shelters Removal

        #region Helper Classes

        public static class PlayerUtil
        {
            public static bool HasPlayerNearby(Vector3 position, float radius)
            {
                return BaseNetworkable.HasCloseConnections(position, radius);
            }
        }

        public static class TerrainUtil
        {
            public static bool OnTopology(Vector3 position, TerrainTopology.Enum topology)
            {
                return (TerrainMeta.TopologyMap.GetTopology(position) & (int)topology) != 0;
            }

            public static bool InsideRock(Vector3 position, float radius)
            {
                List<Collider> colliders = Pool.Get<List<Collider>>();
                Vis.Colliders(position, radius, colliders, Layers.Mask.World, QueryTriggerInteraction.Ignore);

                bool result = false;

                foreach (Collider collider in colliders)
                {
                    if (collider.name.Contains("rock", CompareOptions.OrdinalIgnoreCase)
                        || collider.name.Contains("cliff", CompareOptions.OrdinalIgnoreCase)
                        || collider.name.Contains("formation", CompareOptions.OrdinalIgnoreCase))
                    {
                        result = true;
                        break;
                    }
                }

                Pool.FreeUnmanaged(ref colliders);
                return result;
            }

            public static bool HasEntityNearby(Vector3 position, float radius, LayerMask mask, string prefabName = null)
            {
                List<Collider> hitColliders = Pool.Get<List<Collider>>();
                GamePhysics.OverlapSphere(position, radius, hitColliders, mask, QueryTriggerInteraction.Ignore);

                bool hasEntityNearby = false;
                foreach (Collider collider in hitColliders)
                {
                    BaseEntity entity = collider.gameObject.ToBaseEntity();
                    if (entity != null)
                    {
                        if (prefabName == null || entity.PrefabName == prefabName)
                        {
                            hasEntityNearby = true;
                            break;
                        }
                    }
                }

                Pool.FreeUnmanaged(ref hitColliders);
                return hasEntityNearby;
            }

            public static Vector3 GetRandomPositionAround(Vector3 center, float minimumRadius, float maximumRadius, bool adjustToWaterHeight = false)
            {
                Vector3 randomDirection = Random.onUnitSphere;
                float randomDistance = Random.Range(minimumRadius, maximumRadius);
                Vector3 randomPosition = center + randomDirection * randomDistance;

                if (adjustToWaterHeight)
                    randomPosition.y = TerrainMeta.WaterMap.GetHeight(randomPosition);
                else
                    randomPosition.y = TerrainMeta.HeightMap.GetHeight(randomPosition);

                return randomPosition;
            }

            public static bool GetGroundInfo(Vector3 startPosition, out RaycastHit raycastHit, float range, LayerMask mask)
            {
                return Physics.Linecast(startPosition + new Vector3(0.0f, range, 0.0f), startPosition - new Vector3(0.0f, range, 0.0f), out raycastHit, mask);
            }

            public static bool GetGroundInfo(Vector3 startPosition, out RaycastHit raycastHit, float range, LayerMask mask, Transform ignoreTransform = null)
            {
                startPosition.y += 0.25f;
                range += 0.25f;
                raycastHit = default;

                RaycastHit hit;
                if (!GamePhysics.Trace(new Ray(startPosition, Vector3.down), 0f, out hit, range, mask, QueryTriggerInteraction.UseGlobal, null))
                    return false;

                if (ignoreTransform != null && hit.collider != null
                    && (hit.collider.transform == ignoreTransform || hit.collider.transform.IsChildOf(ignoreTransform)))
                {
                    return GetGroundInfo(startPosition - new Vector3(0f, 0.01f, 0f), out raycastHit, range, mask, ignoreTransform);
                }

                raycastHit = hit;
                return true;
            }
        }

        #endregion Helper Classes

        #region Permissions

        private static class PermissionUtil
        {
            public const string IGNORE = "boxedplayerspawn.ignore";
            private static readonly List<string> _permissions = new List<string>
            {
                IGNORE,
            };
            
            public static void RegisterPermissions()
            {
                foreach (var permission in _permissions)
                {
                    _plugin.permission.RegisterPermission(permission, _plugin);
                }
            }

            public static bool HasPermission(BasePlayer player, string permissionName)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permissionName);
            }
        }

        #endregion Permissions
    }
}