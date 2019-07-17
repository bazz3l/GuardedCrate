using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using Rust;
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
        const string CratePrefab              = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        const string MapMarkerPrefab          = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        const string ScientistPrefab          = "assets/prefabs/npc/scientist/scientist.prefab";
        const float HeightToRaycast           = 250f;
        const float RaycastDistance           = 500f;
        const float PlayerHeight              = 1.3f;
        const float DefaultCupboardZoneRadius = 3.0f;
        const int MaxSpawnTries               = 150;
        const float RadiusFromRT              = 3.0f;
        const float RadiusFromCupboardZone    = 3.0f;

        public static GuardedCrate ins;
        private BaseEntity MapMarker; 
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
            public bool EventActive   = false;
            public uint ContainerID   = 0;
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
               ["EventComplete"]   = "{0}, compleated the guarded crate event.",
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

            CreateEvent((Vector3)spawnPos);
        }

        private void CleanEvent()
        { 
            if (MapMarker != null)
                MapMarker.Kill();

            foreach (var gameObj in UnityEngine.Object.FindObjectsOfType(typeof(EventGuard)))
            {
                UnityEngine.Object.Destroy(gameObj);
            }

            storedData.EventActive = false;
            storedData.ContainerID = 0;
            SaveData();

            PrintToChat(Lang("EventEnded", null));
        }

        public void CreateEvent(Vector3 position)
        {
            HackableLockedCrate crate = GameManager.server?.CreateEntity(CratePrefab, position) as HackableLockedCrate;
            if (crate == null)
            {
                return;
            }

            crate.Spawn();
            crate.gameObject.AddComponent<HackableLootCrate>();
            crate.inventory.Clear();

            NextFrame(() => {
                foreach(var lootItem in config.LootItems)
                {
                    Item item = ItemManager?.CreateByName(lootItem.Key, lootItem.Value);
                    if (item == null)
                    {
                        continue;
                    }

                    item.MoveToContainer(crate.inventory);
                }
            });

            BaseEntity marker = GameManager.server?.CreateEntity(MapMarkerPrefab, position);
            if (marker == null)
            {
                return;
            }

            VendingMachineMapMarker customMarker = marker.GetComponent<VendingMachineMapMarker>();
            customMarker.markerShopName = "Guarded Crate Event";
            marker.Spawn();

            MapMarker = marker;

            for (int i = 0; i < config.NPCCount; i++)
            {
                Vector3 newPosition = position + (UnityEngine.Random.onUnitSphere * config.NPCRadius);

                if (!IsValidPosition(ref newPosition))
                {
                    continue;
                }

                var npc = GameManager.server?.CreateEntity(ScientistPrefab, newPosition) as Scientist;
                if (npc == null)
                {
                    continue;
                }

                npc.Spawn();
                npc.gameObject.AddComponent<EventGuard>();
            }

            storedData.ContainerID = crate.net.ID;
            storedData.EventActive = true;
            SaveData();

            PrintToChat(Lang("EventStart", null, GridReference(position)));
        }
        #endregion

        #region Helpers
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

        public string GridReference(Vector3 position)
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
        public class HackableLootCrate : MonoBehaviour
        {
            public HackableLockedCrate crate;

            void Start()
            {
                 crate = GetComponent<HackableLockedCrate>();
                 if (crate == null)
                 {
                     return;
                 }

                 crate.StartHacking();
            }

            void OnDestroy()
            {
                Destroy(this);
            }
        }

        public class EventGuard : MonoBehaviour
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

               npc.Stats.AggressionRange      = ins.config.NPCAgressionRange;
               npc.utilityAiComponent.enabled = true;

               spawnPoint = npc.transform.position;
               roamRadius = UnityEngine.Random.Range(10, ins.config.NPCRoamRadius);
          } 

          void Update()
          {
               if (npc == null)
               {
                   return;
               }

               if (npc.GetNavAgent.isOnNavMesh)
               {
                   var distance = Vector3.Distance(npc.transform.position, spawnPoint);
                   if (!goingHome && distance > 0)
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
                   } else {
                      goingHome = false;
                   }
               }
           }

           void OnDestroy()
           {
               npc.Kill();

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

            //PrintToChat(player, Lang("StatsList", player.userID.ToString()));
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
