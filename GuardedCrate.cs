using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using Rust;
using Facepunch;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.0.0")]
    [Description("Eliminate the scientits to gain high value loot")]
    class GuardedCrate : RustPlugin
    {
        #region Vars
        const string CratePrefab     = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        const string CargoPrefab     = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        const string ChutePrefab     = "assets/prefabs/misc/parachute/parachute.prefab";
        const string MarkerPrefab    = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        const string ScientistPrefab = "assets/prefabs/npc/scientist/scientist.prefab";

        const float HeightToRaycast           = 250f;
        const float RaycastDistance           = 500f;
        const float PlayerHeight              = 1.3f;
        const float DefaultCupboardZoneRadius = 3.0f;
        const int MaxSpawnTries               = 250;
        const float RadiusFromRT              = 3.0f;
        const float RadiusFromCupboardZone    = 3.0f;

        public static GuardedCrate ins;
        private BaseEntity mapMarker;
        private Vector3 eventPosition; 
        #endregion

        #region Config
        private PluginConfig config;

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(GetDefaultConfig(), true);
        }

        private class PluginConfig
        {
            public int NPCCount;
            public int NPCRadius;
            public int NPCRoamRadius;
            public float NPCTargetSpeed;
            public float NPCAgressionRange;
            public Dictionary<string, int> LootItems;
        }

        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                NPCCount          = 15,
                NPCRadius         = 20,
                NPCRoamRadius     = 20,
                NPCTargetSpeed    = 2.5f,
                NPCAgressionRange = 300f,
                LootItems         = new Dictionary<string, int> {
                   { "rifle.ak", 1 },
                   { "rifle.bold", 1 },
                   { "ammo.rifle", 1000 },
                   { "lmg.m249", 1 },
                   { "rifle.m39", 1 },
                   { "rocket.launcher", 1 },
                   { "ammo.rocket.basic", 15 },
                   { "explosive.satchel", 5 },
                   { "explosive.timed", 10 }
                }
            };
        }
        #endregion

        #region Data
        private StoredData storedData;

        class StoredData {
            public bool EventActive = false;
            public uint ContainerID = 0;
            public Dictionary<ulong, int> Players = new Dictionary<ulong, int>();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, storedData, true);
        #endregion

        #region Lang
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
               ["EventStart"]      = "Guarded crate event started at {0}",
               ["EventEnded"]      = "Guarded crate event ended.",
               ["EventComplete"]   = "{0}, completed the guarded crate event.",
               ["StatsList"]       = "Top:\n{0}",
               ["StatsPlayer"]     = "Events complete: {0}",
               ["StatsPlayerNone"] = "You have not completed any events"
            }, this);
        }
        #endregion

        #region Oxide
        private void Init()
        {
            config     = Config.ReadObject<PluginConfig>();
            storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            SaveData();
        }

        private void Unload() => CleanEvent();

        private void OnServerInitialized()
        {
            ins = this;
        }

        private void OnLootEntity(BasePlayer player, StorageContainer entity)
        {
            if (entity == null || entity.net.ID != storedData.ContainerID || !storedData.EventActive)
            {
                return;
            }

            if (storedData.Players.ContainsKey(player.userID))
            {
                storedData.Players[player.userID]++;
            }
            else
            {
                storedData.Players.Add(player.userID, 1);
            }

            CleanEvent();

            PrintToChat(Lang("EventComplete", null, player.displayName.ToString()));
        }
        #endregion
 
        #region Core
        private void StartEvent()
        {
            Vector3? spawnPos = GetSpawnPos();
            if (spawnPos == null)
            {
                return;
            }

            eventPosition = (Vector3)spawnPos;

            CreateEvent();
        }

        private void CleanEvent()
        {
            if (mapMarker != null && !mapMarker.IsDestroyed)
                mapMarker.Kill();

            foreach (var gameObj in UnityEngine.Object.FindObjectsOfType(typeof(GuardComponent)))
            {
                UnityEngine.Object.Destroy(gameObj);
            }

            storedData.EventActive = false;
            storedData.ContainerID = 0;
            SaveData();
        }

        public void CreateEvent()
        {
            SpawnCargoPlane();
            SpawnGuards();
            GenerateMapMarker();

            storedData.EventActive = true;
            SaveData();

            PrintToChat(Lang("EventStart", null, GridReference(eventPosition)));
        }

        private void GenerateMapMarker()
        {
            BaseEntity marker = GameManager.server?.CreateEntity(MarkerPrefab, eventPosition);
            if (marker == null)
            {
                return;
            }

            VendingMachineMapMarker customMarker = marker.GetComponent<VendingMachineMapMarker>();
            customMarker.markerShopName = "Guarded Crate Event";
            marker.Spawn();

            mapMarker = marker;
        }

        private void SpawnCargoPlane()
        {
            CargoPlane cargoplane = GameManager.server?.CreateEntity(CargoPrefab) as CargoPlane;
            if (cargoplane == null)
            {
                return;
            }

            cargoplane.InitDropPosition(eventPosition);
            cargoplane.Spawn();
            cargoplane.gameObject.AddComponent<CargoPlaneComponent>();
        }

        private void SpawnGuards()
        {
            for (int i = 0; i < config.NPCCount; i++)
            {
                Vector3 spawnPos = eventPosition + (UnityEngine.Random.onUnitSphere * config.NPCRadius);

                if (!IsValidPosition(ref spawnPos))
                {
                    continue;
                }

                Scientist npc = GameManager.server?.CreateEntity(ScientistPrefab, spawnPos) as Scientist;
                if (npc == null)
                {
                    continue;
                }

                npc.displayName = Get(npc.userID);
                npc.Spawn();
                npc.gameObject.AddComponent<GuardComponent>();
            }
        }

        private void SpawnHackableLockedCrate()
        {
            HackableLockedCrate crate = GameManager.server?.CreateEntity(CratePrefab, eventPosition + new Vector3(0, 250f, 0)) as HackableLockedCrate;
            if (crate == null)
            {
                return;
            }

            crate.Spawn();
            crate.gameObject.AddComponent<HackableLootCrateComponent>();
            crate.gameObject.AddComponent<ParachuteComponent>();
            crate.inventory.Clear();

            while (crate.inventory.itemList.Count > 0)
            {
                var item = crate.inventory.itemList[0];
                item.RemoveFromContainer();
                item.Remove(0f);
            }

            foreach (var loot in config.LootItems)
            {
                Item item = ItemManager?.CreateByName(loot.Key, loot.Value);
                if (item == null)
                {
                    continue;
                }

                item.MoveToContainer(crate.inventory);
            }

            storedData.ContainerID = crate.net.ID;
        }
        #endregion

        #region Helpers
        private static string Get(ulong v) => Facepunch.RandomUsernames.Get((int)(v % 2147483647uL));

        private Vector3? GetSpawnPos()
        {
            for (int i = 0; i < MaxSpawnTries; i++)
            {
                Vector3 randomPos = new Vector3(
                    UnityEngine.Random.Range(-TerrainMeta.Size.x / 2, TerrainMeta.Size.x / 2),
                    HeightToRaycast,
                    UnityEngine.Random.Range(-TerrainMeta.Size.z / 2, TerrainMeta.Size.z / 2)
                );

                if (IsValidPosition(ref randomPos))
                {
                    return randomPos;
                }
            }

            return null;
        }

        private bool IsValidPosition(ref Vector3 randomPos) // Credits: Egor_Blagov
        {
            RaycastHit hitInfo;
            if (Physics.Raycast(randomPos, Vector3.down, out hitInfo, RaycastDistance, Layers.Solid))
            {
                randomPos.y = hitInfo.point.y;
            } else {
                return false;
            }

            if (WaterLevel.Test(randomPos + new Vector3(0, PlayerHeight, 0)))
            {
                return false;
            }

            List<Collider> colliders = new List<Collider>();
            Vis.Colliders(randomPos, RadiusFromRT, colliders);
            if (colliders.Where(col => col.name.ToLower().Contains("prevent") && col.name.ToLower().Contains("building")).Count() > 0)
            {
                return false;
            }

            List<BaseEntity> entities = new List<BaseEntity>();            
            Vis.Entities(randomPos, RadiusFromRT, entities);
            if (entities.Where(ent => ent is BaseVehicle || ent is CargoShip || ent is BaseHelicopter || ent is BradleyAPC).Count() > 0)
            {
                return false;
            }
            
            List<BuildingPrivlidge> cupboards = new List<BuildingPrivlidge>();
            Vis.Entities(randomPos, DefaultCupboardZoneRadius + RadiusFromCupboardZone, cupboards);
            if (cupboards.Count > 0)
            {
                return false;
            }

            return true;
        }

        public string GridReference(Vector3 position) // Credit: Jake_Rich
        {
            Vector2 roundedPos = new Vector2(World.Size / 2 + position.x, World.Size / 2 - position.z);
            string grid = $"{NumberToLetter(Mathf.FloorToInt(roundedPos.x / 145))}{Mathf.FloorToInt(roundedPos.y / 145)}";
            return grid;
        }

        public string NumberToLetter(int num) // Credit: Jake_Rich
        {
            int num2 = Mathf.FloorToInt((float)(num / 26));
            int num3 = num % 26;
            string text = string.Empty;
            if (num2 > 0)
            {
                for (int i = 0; i < num2; i++)
                {
                    text += System.Convert.ToChar(65 + i);
                }
            }

            return text + System.Convert.ToChar(65 + num3).ToString();
        }

        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        #endregion

        #region Scripts
        public class CargoPlaneComponent : MonoBehaviour
        {
            public CargoPlane cargoplane;
            public bool dropped = false;

            void Start()
            {
                 cargoplane = GetComponent<CargoPlane>();
                 if (cargoplane == null)
                 {
                     return;
                 }

                 cargoplane.dropped = true;
            }

            void Update()
            {
                if (!cargoplane.isServer)
                {
                    return;
                }

                cargoplane.secondsTaken += Time.deltaTime;
                float t = Mathf.InverseLerp(0.0f, cargoplane.secondsToTake, cargoplane.secondsTaken);
                if (!dropped && (double) t >= 0.5)
                {
                    dropped = true;

                    ins.SpawnHackableLockedCrate();
                }
            }

            void OnDestroy()
            {
                Destroy(this);
            }
        }

        public class HackableLootCrateComponent : FacepunchBehaviour
        {
            public HackableLockedCrate crate;
            public BaseEntity parachute;
            public Vector3 spawnPoint;

            void Start()
            {
                 crate = GetComponent<HackableLockedCrate>();
                 if (crate == null)
                 {
                     return;
                 }

                 crate.StartHacking();
                 
                 spawnPoint = crate.transform.position;
            }

            void OnCollisionEnter(Collision col)
            {
                ins.Puts("Guarded crate landed");
            }

            void OnDestroy()
            {
                Destroy(this);
            }
        }

        public class GuardComponent : FacepunchBehaviour
        {
            public NPCPlayerApex npc;
            public Vector3 spawnPoint;
            public bool goingHome;
            public int roamRadius;

            void Start()
            {
                npc = GetComponent<NPCPlayerApex>();
                if (npc == null)
                {
                    return;
                }

                npc.utilityAiComponent.enabled = true;
                npc.Stats.AggressionRange      = ins.config.NPCAgressionRange;
                npc.Stats.VisionRange          = ins.config.NPCAgressionRange;
                npc.SpawnPosition              = npc.transform.position;
                npc.Destination                = npc.transform.position;
                
                spawnPoint = npc.transform.position;
                roamRadius = UnityEngine.Random.Range(0, ins.config.NPCRoamRadius);
            } 

            void Update()
            {
                if ((npc.GetFact(NPCPlayerApex.Facts.IsAggro)) == 0 && npc.GetNavAgent.isOnNavMesh)
                {
                    var distance = Vector3.Distance(npc.transform.position, spawnPoint);
                    if (!goingHome && distance > roamRadius)
                    {
                        goingHome = true;
                    }

                    if (goingHome && distance > roamRadius)
                    {
                        npc.CurrentBehaviour = BaseNpc.Behaviour.Wander;
                        npc.SetFact(NPCPlayerApex.Facts.Speed, (byte)NPCPlayerApex.SpeedEnum.Walk, true, true);
                        npc.TargetSpeed = ins.config.NPCTargetSpeed;
                        npc.GetNavAgent.SetDestination(spawnPoint);
                        npc.Destination = spawnPoint;
                    }
                    else 
                    {
                        goingHome = false;
                    }
                }
            }

            void OnCollisionEnter(Collision col)
            {
                //
            }

            void OnDestroy()
            {
                 if (npc != null && !npc.IsDestroyed)
                     npc.Kill();
            }
        }

        public class ParachuteComponent : FacepunchBehaviour
        {
            public BaseEntity parachute;
            public BaseEntity entity;

            void Start()
            {
                entity = this.GetComponent<BaseEntity>();
                if (entity == null)
                {
                    return;
                }

                parachute = GameManager.server?.CreateEntity(ChutePrefab, entity.transform.position) as BaseEntity;
                if (parachute == null)
                {
                    return;
                }

                parachute.Spawn();
                parachute.SetParent(entity);
                parachute.transform.localPosition = new Vector3(0, 1f, 0);

                Rigidbody rb = entity.GetComponent<Rigidbody>();
                rb.drag      = 2f;
                rb.AddForce(-transform.up * -1f);
                rb.useGravity = true;
            }

            void OnCollisionEnter(Collision col)
            {
                if (parachute != null && !parachute.IsDestroyed)
                    parachute.Kill();

                OnDestroy();
            }

            void OnDestroy()
            {
                Destroy(this);
            }
        }
        #endregion

        #region Chat Commands
        [ChatCommand("guarded")]
        private void GuardedCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            if (player.IsAdmin && !storedData.EventActive)
            {
                StartEvent();
            }                
        }

        [ChatCommand("guarded-end")]
        private void GuardedEndCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            if (player.IsAdmin && storedData.EventActive)
            {
                CleanEvent();
            }
        }

        [ChatCommand("guarded-top")]
        private void GuardedTopCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            Dictionary<string, int> playerStats = new Dictionary<string, int>();

            foreach(var p in storedData.Players)
            {
                var playerName = covalence.Players?.FindPlayerById(p.Key.ToString())?.Name;
                if (playerName != null)
                    playerStats.Add(playerName, p.Value);
            }

            var stats = playerStats.OrderByDescending(x => x.Value)
            .Select(x => x.Key + ": " + x.Value)
            .Take(5)
            .ToArray();

            PrintToChat(player, Lang("StatsList", player.userID.ToString(), string.Join("\n", stats)));
        }

        [ChatCommand("guarded-stats")]
        private void GuardedStatsCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            if (storedData.Players.ContainsKey(player.userID))
            {
                PrintToChat(player, Lang("StatsPlayer", player.userID.ToString(), storedData.Players[player.userID]));
                return;
            }

            PrintToChat(player, Lang("StatsPlayerNone", player.userID.ToString()));
        }
        #endregion
    }
}
