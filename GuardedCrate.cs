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
    [Description("")]
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
        const int MaxSpawnTrys                = 150;
        const float RadiusFromRT              = 3.0f;
        const float RadiusFromCupboardZone    = 3.0f;

        public static GuardedCrate ins;
        public bool EventActive   = false;
        public uint ContainerID   = 0;
        public List<uint> NPCList = new List<uint>();
        public BaseEntity mapMarker;
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

        #region Lang
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
               ["EventStart"]    = "Guarded crate event started at {0}",
               ["EventEnded"]    = "Guarded crate event ended.",
               ["EventComplete"] = "{0}, compleated the guarded crate event."
            }, this);
        }
        #endregion

        #region Oxide
        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
        }

        private void OnServerInitialized()
        {
            ins = this;
        }

        private void OnLootEntity(BasePlayer player, StorageContainer entity)
        {
            if (entity == null)
            {
                return;
            }

            if (entity.net.ID != ContainerID || !EventActive)
            {
                return;
            }

            CleanEvent();

            PrintToChat(Lang("EventComplete", null, player.displayName.ToString()));
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
             if (container == null)
             {
                 return;
             }

             if (container.uid == ContainerID && container.itemList.Count == 0)
             {
                 container.Kill();
             }
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

            Vector3 position = (Vector3)spawnPos;

            CreateEvent(position);

            EventActive = true;

            PrintToChat(Lang("EventStart", null, GridReference(position)));
        }

        public void CleanEvent()
        {
            foreach(uint npcID in NPCList)
            {
               var npc = BaseNetworkable.serverEntities?.Find(npcID) as Scientist;
               if (npc != null)
                   npc.Kill();
            }

            NPCList.Clear();

            EventActive = false;

            if (mapMarker != null)
            {
                mapMarker.Kill();
            }

            PrintToChat(Lang("EventEnded", null));
        }

        public void CreateEvent(Vector3 position)
        {
            if (EventActive)
            {
                return;
            }

            HackableLockedCrate crate = GameManager.server?.CreateEntity(CratePrefab, position) as HackableLockedCrate;
            if (crate == null)
            {
                return;
            }

            crate.Spawn();
            crate.StartHacking();
            ContainerID = crate.net.ID;
            CreateLoot(crate);
            CreateMarker(position);
            CreateNPCs(position);
        }

        private void CreateLoot(HackableLockedCrate container)
        {
            if (container == null)
            {
                return;
            }

            container.inventory.Clear();

            NextFrame(() => {
                foreach(var lootItem in config.LootItems)
                {
                    Item item = ItemManager?.CreateByName(lootItem.Key, lootItem.Value);
                    if (item == null)
                    {
                        continue;
                    }

                    item.MoveToContainer(container.inventory);
                }
            });
        }

        private void CreateNPCs(Vector3 position)
        {
            for (int i = 0; i < config.NPCCount; i++)
            {
                Vector3 newPosition = position + (UnityEngine.Random.onUnitSphere * config.NPCRadius);

                if (!IsValidPosition(ref newPosition))
                {
                    continue;
                }

                var bot = GameManager.server?.CreateEntity(ScientistPrefab, newPosition) as Scientist;
                if (bot == null)
                {
                    continue;
                }

                bot.Spawn();
                bot.gameObject.AddComponent<Guard>();
            }
        }

        private void CreateMarker(Vector3 position)
        {
            BaseEntity marker = GameManager.server?.CreateEntity(MapMarkerPrefab, position);
            if (marker == null)
            {
                return;
            }

            VendingMachineMapMarker customMarker = marker.GetComponent<VendingMachineMapMarker>();
            customMarker.markerShopName = "Guarded Crate Event";
            marker.Spawn();

            mapMarker = marker;
        }
        #endregion

        #region Helpers
        private Vector3? GetSpawnPos()
        {
            for (int i = 0; i < MaxSpawnTrys; i++)
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
        public class Guard : MonoBehaviour
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

               var netID = npc.net.ID;
               if (netID != null && !ins.NPCList.Contains(netID))
                   ins.NPCList.Add(npc.net.ID);
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
               Destroy(this);
           }
        }
        #endregion

        #region Chat Commands
        [ChatCommand("guarded")]
        private void GuardedCommand(BasePlayer player, string command, string[] args)
        {
            if (player.IsAdmin && !EventActive)
                StartEvent();                
        }

        [ChatCommand("guarded-end")]
        private void GuardedEndCommand(BasePlayer player, string command, string[] args)
        {
            if (player.IsAdmin && EventActive)
                CleanEvent();
        }
        #endregion
    }
}
