using System.Collections.Generic;
using Rust.Ai.HTN;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.1.2")]
    [Description("Spawns a crate guarded by scientists with custom loot.")]
    public class GuardedCrate : RustPlugin
    {
        #region Fields
        public string cratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        public string cargoPrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        public string markerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        public string npcPrefab = "assets/prefabs/npc/scientist/htn/scientist_full_any.prefab";  
        public MapMarkerGenericRadius marker;
        public HackableLockedCrate crate;

        public static string chutePrefab = "assets/prefabs/misc/parachute/parachute.prefab";  
        public static GuardedCrate plugin;
        public static PluginConfig config;

        public readonly int layerMask = LayerMask.GetMask("Terrain", "World", "Construction", "Deployed");

        public HashSet<HTNPlayer> guards = new HashSet<HTNPlayer>();
        public Timer eventRepeatTimer;
        public Timer eventTimer;
        public bool eventActive;
        public bool wasLooted;
        public Vector3 eventPosition;

        List<MonumentInfo> monuments { get { return TerrainMeta.Path.Monuments; } }
        #endregion

        #region Config

        public PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                EventTime = 3600f,
                EventLength = 1800f,
                NPCRoam = 80f,
                NPCCount = 15,
                LootItemsMax = 4,
                LootItems = new List<LootItem> {
                    new LootItem("rifle.ak", 1, 1),
                    new LootItem("rifle.bold", 1, 1),
                    new LootItem("ammo.rifle", 1000, 1),
                    new LootItem("lmg.m249", 1, 0.6),
                    new LootItem("rifle.m39", 1, 1),
                    new LootItem("rocket.launcher", 1, 1),
                    new LootItem("ammo.rocket.basic", 8, 0.5),
                    new LootItem("explosive.satchel", 6, 0.7),
                    new LootItem("explosive.timed", 4, 0.5),
                    new LootItem("gunpowder", 1000, 1),
                    new LootItem("metal.refined", 500, 0.5),
                    new LootItem("leather", 600, 1),
                    new LootItem("cloth", 600, 1),
                    new LootItem("scrap", 1000, 1),
                    new LootItem("sulfur", 5000, 1),
                    new LootItem("stones", 10000, 1),
                    new LootItem("lowgradefuel", 2000, 1),
                    new LootItem("metal.fragments", 5000, 1)
                }
            };
        }

        public class PluginConfig
        {
            public float NPCRoam;
            public int NPCCount;
            public float EventTime;
            public float EventLength;
            public int LootItemsMax;
            public List<LootItem> LootItems;
        }

        public class LootItem
        {
            public string Shortname;
            public int Amount;
            public double Chance;

            public LootItem(string shortname, int amount, double chance = 1)
            {
                Shortname = shortname;
                Amount = amount;
                Chance = chance;
            }
        }
        #endregion

        #region Oxide
        protected override void LoadDefaultConfig() => Config.WriteObject(GetDefaultConfig(), true);

        private void OnServerInitialized()
        {
            eventRepeatTimer = timer.Repeat(config.EventTime, 0, () => StartEvent());

            StartEvent();
        }

        private void Init()
        {
            plugin = this;
            config = Config.ReadObject<PluginConfig>();
        }

        private void Unload() => StopEvent();

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
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
        private void StartEvent()
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

            eventTimer = timer.Once(config.EventLength, () => StopEvent());

            MessagePlayers("<color=#DC143C>Guarded Crate</color>: event started.");
        }

        private void StopEvent()
        {
            if (crate != null && !crate.IsDestroyed)
            {
                crate?.Kill();
            }

            ResetEvent();

            MessagePlayers("<color=#DC143C>Guarded Loot</color>: event ended.");
        }

        private void ResetEvent()
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

        private void DestroyGuards()
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

        private void DestroyTimers()
        {
            if (eventRepeatTimer != null && !eventRepeatTimer.Destroyed)
            {
                eventRepeatTimer.Destroy();
            }

            if (eventTimer != null && !eventTimer.Destroyed)
            {
                eventTimer.Destroy();
            }

            eventRepeatTimer = timer.Repeat(config.EventTime, 0, () => StartEvent());
        }

        private IEnumerator<object> SpawnAI() 
        {
            for (int i = 0; i < config.NPCCount; i++)
            {
                Vector3 location = RandomCircle(eventPosition, 10f, (360 / config.NPCCount * i));

                Vector3 pos;

                if (IsValidLocation(location, out pos))
                {
                    SpawnNPC(pos, Quaternion.FromToRotation(Vector3.forward, eventPosition));
                }

                yield return new WaitForSeconds(0.5f);
            }

            yield return null;
        }

        private void SpawnNPC(Vector3 position, Quaternion rotation)
        {
            HTNPlayer npc = GameManager.server.CreateEntity(npcPrefab, position, rotation) as HTNPlayer;
            if (npc == null)
            {
                return;
            }

            npc.enableSaving = false;
            npc._aiDomain.MovementRadius = config.NPCRoam;
            npc._aiDomain.Movement = HTNDomain.MovementRule.RestrainedMove;
            npc.Spawn();

            guards.Add(npc);
        }

        private void SpawnCreate(Vector3 position)
        {
            crate = GameManager.server.CreateEntity(cratePrefab, position, Quaternion.identity) as HackableLockedCrate;
            if (crate == null)
            {
                return;
            }

            crate.enableSaving = false;
            crate.Spawn();
            crate.gameObject.AddComponent<ParachuteComponent>();

            crate.inventory.Clear();
            crate.inventory.capacity = config.LootItemsMax;
            ItemManager.DoRemoves();
            
            CreateMarker();
            timer.In(3f, () => PopulateLoot());
            SingletonComponent<ServerMgr>.Instance.StartCoroutine(SpawnAI());

            MessagePlayers($"<color=#DC143C>Guarded Crate</color>: is landing at ({GetGrid(crate.transform.position)}).");
        }

        private void PopulateLoot()
        {
            if (config.LootItems.Count < config.LootItemsMax)
            {
                return;
            }

            List<LootItem> items = new List<LootItem>();

            int counter = 0;

            while(counter < config.LootItemsMax)
            {
                LootItem lootItem = config.LootItems.GetRandom();

                if (!items.Contains(lootItem))
                {
                    items.Add(lootItem);
                    counter++;
                }
            }

            foreach (LootItem item in items)
            {
                ItemManager.CreateByName(item.Shortname, item.Amount).MoveToContainer(crate.inventory);
            }
        }

        private void CreateMarker()
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
            marker.transform.localPosition = Vector3.zero;
            marker.SendUpdate();
        }

        private void SpawnCargoPlane()
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

        public class PlaneComponent : MonoBehaviour
        {
            private CargoPlane plane;
            private Vector3 lastPosition;
            private bool hasDropped;

            private void Awake()
            {
                plane = GetComponent<CargoPlane>();
                if (plane == null)
                {
                    Destroy(this);
                    return;
                }

                plane.dropped = true;
            }

            private void Update()
            {
                if (plane == null || plane.IsDestroyed)
                {
                    Destroy(this);
                    return;
                }

                lastPosition = transform.position;

                float distance = Mathf.InverseLerp(0.0f, plane.secondsToTake, plane.secondsTaken);
                if (!hasDropped && distance >= 0.5f)
                {
                    hasDropped = true;

                    plugin.SpawnCreate(lastPosition);
                }
            }
        }

        public class ParachuteComponent : FacepunchBehaviour
        {
            private BaseEntity parachute;
            private BaseEntity entity;

            private void Awake()
            {
                entity = GetComponent<BaseEntity>();
                if (entity == null)
                {
                    Destroy(this);
                    return;
                }

                parachute = GameManager.server.CreateEntity(chutePrefab, entity.transform.position);
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

            private void OnCollisionEnter(Collision col)
            {
                if (parachute != null && !parachute.IsDestroyed)
                {
                    parachute?.Kill();
                }

                Destroy(this);
            }
        }
        #endregion

        #region Helpers
        public static Color ColorConverter(int r, int g, int b) => new Color(r/255f, g/255f, b/255f);

        private float MapSize() => TerrainMeta.Size.x / 2;

        private Vector3 RandomCircle(Vector3 center, float radius, float angle)
        {
            Vector3 pos = center;
            pos.x += radius * Mathf.Sin(angle * Mathf.Deg2Rad);
            pos.z += radius * Mathf.Cos(angle * Mathf.Deg2Rad);
            return pos;
        }

        private Vector3 RandomLocation(int maxTries = 100)
        {
            float wordSize = MapSize() - 600f;

            for (int i = 0; i < maxTries; i++)
            {
                Vector3 location = new Vector3(Core.Random.Range(-wordSize, wordSize), 200f, Core.Random.Range(-wordSize, wordSize));

                Vector3 pos;

                if (!IsValidLocation(location, out pos)) continue;

                return pos;
            }

            return Vector3.zero;
        }

        private bool IsValidLocation(Vector3 location, out Vector3 position)
        {
            RaycastHit hit;

            if (Physics.Raycast(location + (Vector3.up * 250f), Vector3.down, out hit, Mathf.Infinity, layerMask))
            {
                if (IsValidPoint(hit.point))
                {
                    position = hit.point;

                    return true;
                }
            }

            position = Vector3.zero;

            return false;
        }

        private bool IsValidPoint(Vector3 point)
        {
            if (IsNearMonument(point) || IsNearPlayer(point) || WaterLevel.Test(point))
            {
                return false;
            }

            return true;
        }

       private bool IsNearMonument(Vector3 position)
        {
            foreach(MonumentInfo monument in monuments)
            {
                if (monument.Bounds.Contains(position))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsNearPlayer(Vector3 position)
        {
            foreach(BasePlayer player in BasePlayer.activePlayerList)
            {
                float distance = Vector3.Distance(position, player.transform.position);
                if (distance < 10)
                {
                    return true;
                }
            }

            return false;
        }

        // Thanks to yetzt
        private string GetGrid(Vector3 position)
        {
            char letter = 'A';
            float x = Mathf.Floor((position.x + (ConVar.Server.worldsize / 2)) / 146.3f) % 26;
            float z = (Mathf.Floor(ConVar.Server.worldsize / 146.3f) - 1) - Mathf.Floor((position.z + (ConVar.Server.worldsize / 2)) / 146.3f);
            letter = (char)(((int)letter) + x);

            return $"{letter}{z}";
        }

        private void MessagePlayers(string message)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                player.ChatMessage(message);
            }
        }
        #endregion
    }
}