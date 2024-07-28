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
    [Info("Boxed Player Spawn", "VisEntities", "1.0.0")]
    [Description(" ")]
    public class BoxedPlayerSpawn : RustPlugin
    {
        #region Fields

        private static BoxedPlayerSpawn _plugin;
        private static Configuration _config;
        private StoredData _storedData;

        private List<LegacyShelter> _spawnedLegacyShelters = new List<LegacyShelter>();

        private const string PREFAB_LEGACY_SHELTER = "assets/prefabs/building/legacy.shelter.wood/legacy.shelter.wood.deployed.prefab";
        private const int LAYER_ENTITIES = Layers.Mask.Deployed | Layers.Mask.Construction | Layers.Mask.Player_Server;
        private const int LAYER_TERRAIN = Layers.Mask.Terrain;
        private const int LAYER_WORLD = Layers.Mask.World;

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Maximum Attempts For Finding Shelter Position")]
            public int MaximumAttemptsForFindingShelterPosition { get; set; }

            [JsonProperty("Search Radius For Shelter Position Around Player")]
            public float SearchRadiusForShelterPositionAroundPlayer { get; set; }

            [JsonProperty("Nearby Entities Avoidance Radius")]
            public float NearbyEntitiesAvoidanceRadius { get; set; }

            [JsonProperty("Rocks Avoidance Radius")]
            public float RocksAvoidanceRadius { get; set; }

            [JsonProperty("Shelter Lifetime Seconds")]
            public float ShelterLifetimeSeconds { get; set; }
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

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                MaximumAttemptsForFindingShelterPosition = 5,
                SearchRadiusForShelterPositionAroundPlayer = 5f,
                NearbyEntitiesAvoidanceRadius = 6f,
                RocksAvoidanceRadius = 3f,
                ShelterLifetimeSeconds = 30f
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

                if (TryFindSuitableShelterPosition(spawnPoint.pos, _config.SearchRadiusForShelterPositionAroundPlayer, _config.MaximumAttemptsForFindingShelterPosition, out shelterPosition, out shelterRotation))
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

        #region Legacy Shelter Spawning and Setup

        private bool TryFindSuitableShelterPosition(Vector3 center, float searchRadius, int maximumAttempts, out Vector3 suitablePosition, out Quaternion suitableRotation)
        {
            for (int attempt = 0; attempt < maximumAttempts; attempt++)
            {
                Vector3 position = TerrainUtil.GetRandomPositionAround(center, searchRadius);
                if (TerrainUtil.OnTopology(position, TerrainTopology.Enum.Beach)
                    && !TerrainUtil.InsideRock(position, _config.RocksAvoidanceRadius)
                    && !TerrainUtil.HasEntityNearby(position, _config.NearbyEntitiesAvoidanceRadius, LAYER_ENTITIES))
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

            StartRemovalTimer(legacyShelter);
            LockLegacyShelterDoor(legacyShelter);
            _spawnedLegacyShelters.Add(legacyShelter);

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

        #endregion Legacy Shelter Spawning and Setup

        #region Legacy Shelter Cleanup
        
        private void StartRemovalTimer(LegacyShelter legacyShelter)
        {
            timer.Once(_config.ShelterLifetimeSeconds, () =>
            {
                if (legacyShelter != null && !legacyShelter.IsDestroyed)
                {
                    legacyShelter.Kill();
                }
            });
        }

        private void KillAllLegacyShelters()
        {
            if (_spawnedLegacyShelters != null)
            {
                foreach (var shelter in _spawnedLegacyShelters)
                {
                    if (shelter != null && !shelter.IsDestroyed)
                        shelter.Kill();
                }

                _spawnedLegacyShelters.Clear();
            }
        }

        #endregion Legacy Shelter Cleanup

        #region Helper Classes

        public static class TerrainUtil
        {
            public static bool OnTopology(Vector3 position, TerrainTopology.Enum mask)
            {
                return (TerrainMeta.TopologyMap.GetTopology(position) & (int)mask) != 0;
            }

            public static bool InsideRock(Vector3 position, float radius)
            {
                List<Collider> colliders = Pool.GetList<Collider>();
                Vis.Colliders(position, radius, colliders, LAYER_WORLD, QueryTriggerInteraction.Ignore);

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

                Pool.FreeList(ref colliders);
                return result;
            }

            public static bool HasEntityNearby(Vector3 position, float radius, LayerMask mask, string prefabName = null)
            {
                List<Collider> hitColliders = Pool.GetList<Collider>();
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

                Pool.FreeList(ref hitColliders);
                return hasEntityNearby;
            }

            public static Vector3 GetRandomPositionAround(Vector3 center, float radius, bool adjustToWaterHeight = false)
            {
                Vector3 randomDirection = Random.onUnitSphere;
                float randomDistance = Random.Range(0, radius);
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