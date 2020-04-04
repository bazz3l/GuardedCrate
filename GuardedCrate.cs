using Oxide.Core.Libraries.Covalence;
using System.Collections.Generic;
using Rust.Ai.HTN;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.0.7")]
    [Description("Spawns a crate guarded by scientists with custom loot.")]
    class GuardedCrate : CovalencePlugin
    {
        #region Fields
        static string cratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        static string cargoPrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        static string markerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        static string chutePrefab = "assets/prefabs/misc/parachute/parachute.prefab";    
        static string npcPrefab = "assets/prefabs/npc/scientist/htn/scientist_full_any.prefab";

        readonly int layerMask = LayerMask.GetMask("Terrain", "World", "Construction", "Deployed");
        readonly int worldMask = LayerMask.GetMask("World");

        HashSet<HTNPlayer> guards = new HashSet<HTNPlayer>();
        Timer eventRepeatTimer;
        Timer eventTimer;
        bool eventActive;
        bool wasLooted;
        Vector3 eventPosition;

        static MapMarkerGenericRadius marker;
        static HackableLockedCrate crate;

        static PluginConfig config;
        static GuardedCrate plugin;

        List<MonumentInfo> monuments
        {
            get { return TerrainMeta.Path.Monuments; }
        }

        float heightToRaycast
        {
            get { return TerrainMeta.HighestPoint.y + 250f; }
        }
        #endregion

        #region Config

        PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                eventTime = 3600f,
                eventLength = 720f,
                npcRoam = 80f,
                npcCount = 15,
                lootItems = new List<LootItem> {
                   new LootItem("rifle.ak", 1),
                   new LootItem("rifle.bold", 1),
                   new LootItem("ammo.rifle", 1000),
                   new LootItem("lmg.m249", 1),
                   new LootItem("rifle.m39", 1),
                   new LootItem("rocket.launcher", 1),
                   new LootItem("ammo.rocket.basic", 8),
                   new LootItem("explosive.satchel", 6),
                   new LootItem("explosive.timed", 4),
                   new LootItem("gunpowder", 1000),
                   new LootItem("metal.refined", 500),
                   new LootItem("leather", 600),
                   new LootItem("cloth", 600),
                   new LootItem("scrap", 1000),
                   new LootItem("sulfur", 5000),
                   new LootItem("stones", 10000),
                   new LootItem("lowgradefuel", 2000),
                   new LootItem("metal.fragments", 5000)
                }
            };
        }

        class PluginConfig
        {
            public float npcRoam;
            public int npcCount;
            public float eventTime;
            public float eventLength;
            public List<LootItem> lootItems;
        }

        class LootItem
        {
            public string shortname;
            public int amount;

            public LootItem(string Shortname, int Amount)
            {
                shortname = Shortname;
                amount    = Amount;
            }
        }
        #endregion

        #region Oxide
        protected override void LoadDefaultConfig() => Config.WriteObject(GetDefaultConfig(), true);

        void OnServerInitialized()
        {
            eventRepeatTimer = timer.Repeat(config.eventTime, 0, () => StartEvent());

            StartEvent();
        }

        void Init()
        {
            plugin = this;
            config = Config.ReadObject<PluginConfig>();
        }

        void Unload() => StopEvent();

        void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity == null || crate == null || entity.net.ID != crate.net.ID)
            {
                return;
            }

            wasLooted = true;

            ResetEvent();
        }
        #endregion

        #region Core
        void StartEvent()
        {
            if (eventActive)
            {
                return;
            }

            eventPosition = RandomLocation();

            if (eventPosition == Vector3.zero)
            {
                ResetEvent();
                return;
            }

            eventActive = true;

            SpawnCargoPlane();

            SingletonComponent<ServerMgr>.Instance.StartCoroutine(SpawnAI());

            eventTimer = timer.Once(config.eventLength, () => StopEvent());

            MessagePlayers(string.Format("<color=#DC143C>Guarded Loot</color>: fight for the high value loot ({0}).", GetGrid(eventPosition)));
        }

        void StopEvent()
        {
            if (crate != null && !crate.IsDestroyed)
            {
                crate?.Kill();
            }

            ResetEvent();

            MessagePlayers(string.Format("<color=#DC143C>Guarded Loot</color>: event ended."));
        }

        void ResetEvent()
        {
            eventActive = false;

            DestroyGuards();
            DestroyTimers();

            if (crate != null && !crate.IsDestroyed && !wasLooted)
            {
                crate?.Kill();
            }

            if (marker != null && !marker.IsDestroyed)
            {
                marker?.Kill();
            }

            wasLooted = false;
            marker = null;
            crate  = null;
        }

        void DestroyGuards()
        {
            foreach (HTNPlayer npc in guards)
            {
                if (npc == null || npc.IsDestroyed)
                {
                    continue;
                }

                npc.Kill();
            }

            guards.Clear();
        }

        void DestroyTimers()
        {
            if (eventRepeatTimer != null && !eventRepeatTimer.Destroyed)
            {
                eventRepeatTimer.Destroy();
            }

            if (eventTimer != null && !eventTimer.Destroyed)
            {
                eventTimer.Destroy();
            }

            eventRepeatTimer = timer.Repeat(config.eventTime, 0, () => StartEvent());
        }

        IEnumerator<object> SpawnAI() 
        {
            for (int i = 0; i < config.npcCount; i++)
            {
                Vector3 location = RandomCircle(eventPosition, 10f, (360 / config.npcCount * i));
                
                Vector3 position;

                if (!IsValidLocation(location, out position))
                {
                    continue;
                }

                SpawnNPC(position, Quaternion.FromToRotation(Vector3.forward, eventPosition));

                yield return new WaitForSeconds(0.5f);
            }

            yield return null;
        }

        void SpawnNPC(Vector3 position, Quaternion rotation)
        {
            HTNPlayer npc = GameManager.server.CreateEntity(npcPrefab, position, rotation) as HTNPlayer;
            if (npc == null)
            {
                return;
            }

            npc.enableSaving = false;
            npc._aiDomain.MovementRadius = config.npcRoam;
            npc._aiDomain.Movement = HTNDomain.MovementRule.RestrainedMove;
            npc.Spawn();

            guards.Add(npc);
        }

        void SpawnCreate(Vector3 position)
        {
            crate = GameManager.server.CreateEntity(cratePrefab, position, Quaternion.identity) as HackableLockedCrate;
            if (crate == null)
            {
                return;
            }
            
            crate.enableSaving = false;
            crate.Spawn();
            crate.gameObject.AddComponent<ParachuteComponent>();

            crate.inventory.itemList.Clear();

            SpawnCrateMarker();

            NextFrame(() => PopulateLoot());
        }

        void PopulateLoot()
        {
            if (config.lootItems.Count < 6)
            {
                return;
            }

            List<LootItem> items = new List<LootItem>();

            int counter = 0;

            while(counter < 4)
            {
                LootItem lootItem = config.lootItems[UnityEngine.Random.Range(0, config.lootItems.Count)];

                if (!items.Contains(lootItem))
                {
                    items.Add(lootItem);

                    counter++;
                }
            }

            foreach (LootItem item in items)
            {
                ItemManager.CreateByName(item.shortname, item.amount).MoveToContainer(crate.inventory);
            }
        }

        void SpawnCrateMarker()
        {
            marker = GameManager.server.CreateEntity(markerPrefab, crate.transform.position, crate.transform.rotation) as MapMarkerGenericRadius;
            if (marker == null)
            {
                return;
            }

            marker.enableSaving = false;
            marker.alpha = 0.8f;
            marker.color1 = ColorConverter(240, 12, 12);
            marker.color2 = ColorConverter(255, 255, 255);
            marker.radius = 0.6f;
            marker.Spawn();
            marker.SetParent(crate);
            marker.transform.localPosition = new Vector3(0f,0f,0f);
            marker.SendUpdate();
        }

        void SpawnCargoPlane()
        {
            CargoPlane cargoplane = GameManager.server.CreateEntity(cargoPrefab) as CargoPlane;
            if (cargoplane == null)
            {
                return;
            }

            cargoplane.InitDropPosition(eventPosition);
            cargoplane.Spawn();
            cargoplane.gameObject.AddComponent<PlaneComponent>();
        }

        class PlaneComponent : MonoBehaviour
        {
            CargoPlane plane;
            Vector3 lastPosition;
            bool hasDropped;

            void Awake()
            {
                plane = GetComponent<CargoPlane>();
                if (plane == null)
                {
                    Destroy(this);
                    return;
                }

                plane.dropped = true;
            }

            void Update()
            {
                if (plane == null || plane.IsDestroyed)
                {
                    Destroy(this);
                    return;
                }

                lastPosition = transform.position;

                float distance = Mathf.InverseLerp(0.0f, plane.secondsToTake, plane.secondsTaken);

                if (!hasDropped && (double) distance >= 0.5)
                {
                    hasDropped = true;

                    plugin.SpawnCreate(lastPosition);
                }
            }
        }

        class ParachuteComponent : FacepunchBehaviour
        {
            public BaseEntity parachute;
            public BaseEntity entity;

            void Awake()
            {
                entity = GetComponent<BaseEntity>();
                if (entity == null)
                {
                    Destroy(this);
                    return;
                }

                parachute = GameManager.server.CreateEntity(chutePrefab, entity.transform.position) as BaseEntity;
                if (parachute == null)
                {
                    return;
                }

                parachute.enableSaving = false;
                parachute.Spawn();
                parachute.SetParent(entity);
                parachute.transform.localPosition = new Vector3(0,1f,0);

                Rigidbody rb  = entity.GetComponent<Rigidbody>();
                rb.useGravity = true;
                rb.drag       = 2.5f;
            }

            void OnCollisionEnter(Collision col)
            {
                if (parachute == null || parachute.IsDestroyed)
                {
                    return;
                }

                parachute.Kill();

                Destroy(this);
            }
        }
        #endregion

        #region Helpers
        static Color ColorConverter(int r, int g, int b) => new Color(r/255f, g/255f, b/255f);

        float MapSize() => TerrainMeta.Size.x / 2;

        Vector3 RandomCircle(Vector3 center, float radius, float angle)
        {
            Vector3 pos = center;
            pos.x += radius * Mathf.Sin(angle * Mathf.Deg2Rad);
            pos.z += radius * Mathf.Cos(angle * Mathf.Deg2Rad);
            return pos;
        }

        Vector3 RandomLocation(int maxTries = 100)
        {
            float wordSize = MapSize() - 600f;

            for (int i = 0; i < maxTries; i++)
            {
                Vector3 location = new Vector3(Core.Random.Range(-wordSize, wordSize), 200f, Core.Random.Range(-wordSize, wordSize));

                Vector3 position;

                if (!IsValidLocation(location, out position))
                {
                    continue;
                }

                return position;
            }

            return Vector3.zero;
        }

        bool IsValidLocation(Vector3 location, out Vector3 position)
        {
            RaycastHit hit;
            if (Physics.Raycast(location + (Vector3.up * 250f), Vector3.down, out hit, Mathf.Infinity, layerMask))
            {
                Vector3 point = hit.point;

                if (!IsMonument(point) && !WaterLevel.Test(point))
                {
                    position = point;

                    return true;                    
                }
            }

            position = Vector3.zero;

            return false;
        }

        bool IsMonument(Vector3 position)
        {
            foreach(MonumentInfo mon in monuments)
            {
                if (mon.Bounds.Contains(position))
                {
                    return true;
                }
            }

            return false;
        }

        // Thanks to yetzt
        string GetGrid(Vector3 position)
        {
            char letter = 'A';
            float x = Mathf.Floor((position.x + (ConVar.Server.worldsize / 2)) / 146.3f) % 26;
            float z = (Mathf.Floor(ConVar.Server.worldsize / 146.3f) - 1) - Mathf.Floor((position.z + (ConVar.Server.worldsize / 2)) / 146.3f);
            letter = (char)(((int)letter) + x);
            return $"{letter}{z}";
        }

        void MessagePlayers(string message)
        {
            foreach (IPlayer player in covalence.Players.Connected)
            {
                if (player == null) continue;

                player.Message(message);
            }
        }
        #endregion
    }
}