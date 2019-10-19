using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Rust;
using UnityEngine;
using Facepunch;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.0.0")]
    [Description("Eliminate the scientits to gain high value loot")]
    class GuardedCrate : RustPlugin
    {
        [PluginReference] Plugin Kits;

        #region Vars
        public const string CratePrefab     = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        public const string CargoPrefab     = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        public const string ChutePrefab     = "assets/prefabs/misc/parachute/parachute.prefab";
        public const string MarkerPrefab    = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        public const string ScientistPrefab = "assets/prefabs/npc/scientist/scientist.prefab";
        public const int MaxSpawnTries      = 250;

        public static GuardedCrate ins;
        private readonly int BlockedMask     = LayerMask.GetMask("Player (Server)", "Prevent Building", "Construction", "Deployed", "Trigger");
        private readonly int LayerMasks      = LayerMask.GetMask("Terrain", "World", "Default");
        private readonly List<string> Avoids = new List<string> { "ice", "rock" };
        private Vector3 eventPosition;

        private List<MonumentInfo> Monuments
        {
            get
            {
                return TerrainMeta.Path.Monuments;
            }
        }

        private float HeightToRaycast
        {
            get
            {
                return TerrainMeta.HighestPoint.y + 250f;
            }
        }
        #endregion

        #region Config
        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(GetDefaultConfig(), true);
        }

        private PluginConfig config;

        class PluginConfig
        {
            public float EventStartTime;
            public int NPCCount;
            public int NPCRadius;
            public float NPCHealth;
            public int NPCRoamRadius;
            public float NPCTargetSpeed;
            public float NPCAgressionRange;
            public float NPCVisionRange;
            public float NPCLongRange;
            public float NPCMediumRange;
            public Dictionary<string, int> LootItems;
        }

        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                EventStartTime    = 60f,
                NPCCount          = 40,
                NPCRadius         = 20,
                NPCHealth         = 150f,
                NPCRoamRadius     = 60,
                NPCTargetSpeed    = 0.5f,
                NPCAgressionRange = 250f, 
                NPCVisionRange    = 250f,
                NPCLongRange      = 250f,
                NPCMediumRange    = 100f,
                LootItems         = new Dictionary<string, int> {
                   { "rifle.ak", 1 },
                   { "rifle.bold", 1 },
                   { "ammo.rifle", 1000 },
                   { "lmg.m249", 1 },
                   { "rifle.m39", 1 },
                   { "rocket.launcher", 1 },
                   { "ammo.rocket.basic", 8 },
                   { "explosive.satchel", 6 },
                   { "explosive.timed", 4 }
                }
            };
        }
        #endregion

        #region Data
        private StoredData storedData;

        private class StoredData 
        {
            public Dictionary<ulong, int> Players = new Dictionary<ulong, int>();
            public List<uint> Bots  = new List<uint>();
            public bool EventActive = false;
            public uint CrateID     = 0;
            public uint MarkerID    = 0;
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, storedData, true);
        #endregion

        #region Oxide
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string> {
               ["EventStart"]      = "<color=#DC143C>Guarded Crate Event</color>: starting in 60s at {0}, armed guards stay clear or fight for your life.",
               ["EventActive"]     = "<color=#DC143C>Guarded Crate Event</color>: is now active in {0}.",
               ["EventEnded"]      = "<color=#DC143C>Guarded Crate Event</color>: has ended.",
               ["Leaderboard"]     = "<color=#DC143C>Guarded Crate Event</color>: Leaderboard\n{0}",
               ["EventComplete"]   = "<color=#DC143C>Guarded Crate Event</color>: {0}, completed the event.",
               ["Stats"]           = "<color=#DC143C>Guarded Crate Event</color>: {0}, events completed.",
               ["StatsNone"]       = "<color=#DC143C>Guarded Crate Event</color>: You have not completed any events."
            }, this);
        }

        private void OnServerInitialized()
        {
            ins = this;
        }

        private void Init()
        {
            config     = Config.ReadObject<PluginConfig>();
            storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            ResetEvent();
        }

        private void Unload()
        {
            ResetEvent();
        }

        private object OnNpcPlayerTarget(NPCPlayerApex npcPlayer, BaseEntity entity)
        {
            if (entity is NPCPlayerApex)
            {
                return false;
            }

            return null;
        }

        private void OnLootEntity(BasePlayer player, StorageContainer entity)
        {
            if (entity == null || entity.net == null || entity.net.ID != storedData.CrateID || !storedData.EventActive)
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

            PrintToChat(Lang("EventComplete", null, player.displayName));
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
            
            SpawnMarker();

            timer.Once(config.EventStartTime, () => ActivateEvent());

            PrintToChat(Lang("EventStart", null, GridReference(eventPosition)));

            Interface.Oxide.LogError("[Guarded Crate] Event Started.");
        }

        public void ActivateEvent()
        {
            SpawnCargoPlane();
            SingletonComponent<ServerMgr>.Instance.StartCoroutine(SpawnGuards());

            storedData.EventActive = true;
            SaveData();

            PrintToChat(Lang("EventActive", null, GridReference(eventPosition)));

            Interface.Oxide.LogError("[Guarded Crate] Event activated.");
        }

        private void CleanEvent()
        {
            BaseNetworkable marker = BaseNetworkable.serverEntities.Find(storedData.MarkerID);
            if (marker is MapMarkerGenericRadius)
            {
                marker?.Kill();
            }

            foreach(uint netID in storedData.Bots)
            {
                BaseNetworkable npc = BaseNetworkable.serverEntities.Find(netID);
                if (npc is NPCPlayerApex)
                {
                    npc?.Kill();
                }
            }

            storedData.EventActive = false;
            storedData.CrateID     = 0;
            storedData.MarkerID    = 0;
            storedData.Bots.Clear();
            SaveData();

            Interface.Oxide.LogError("[Guarded Crate] Event ended.");
        }

        private void ResetEvent()
        {
            BaseNetworkable crate = BaseNetworkable.serverEntities.Find(storedData.CrateID);
            if (crate is HackableLockedCrate)
            {
                crate?.Kill();
            }

            CleanEvent();

            Interface.Oxide.LogError("[Guarded Crate] Event Reset.");
        }

        private void SpawnCargoPlane()
        {
            CargoPlane cargoplane = GameManager.server.CreateEntity(CargoPrefab) as CargoPlane;
            if (cargoplane == null)
            {
                return;
            }

            cargoplane.InitDropPosition(eventPosition);
            cargoplane.Spawn();
            cargoplane.gameObject.AddComponent<CargoPlaneComponent>();

            Interface.Oxide.LogError("[Guarded Crate] Cargo plane spawned.");
        }

        private void SpawnMarker()
        {
            MapMarkerGenericRadius marker = GameManager.server.CreateEntity(MarkerPrefab, eventPosition) as MapMarkerGenericRadius;
            if (marker == null)
            {
                return;
            }

            marker.alpha  = 0.8f;
            marker.color1 = RGBColorConverter(240, 12, 12);
            marker.color2 = RGBColorConverter(255, 255, 255);
            marker.radius = 0.6f;
            marker.Spawn();
            marker.SendUpdate();

            storedData.MarkerID = marker.net.ID;
            SaveData();

            Interface.Oxide.LogError("[Guarded Crate] Marker spawned.");
        }

        private void SpawnCrate(Vector3 pos)
        {
            HackableLockedCrate crate = GameManager.server.CreateEntity(CratePrefab, pos) as HackableLockedCrate;
            if (crate == null)
            {
                return;
            }

            crate.Spawn();
            crate.gameObject.AddComponent<HackableCrateComponent>();
            crate.gameObject.AddComponent<ParachuteComponent>();
            crate.inventory.Clear();

            while (crate.inventory.itemList.Count > 0)
            {
                var item = crate.inventory.itemList[0];
                item.RemoveFromContainer();
                item.Remove(0f);
            }

            foreach (string lootKey in config.LootItems.Keys)
            {
                Item item = ItemManager.CreateByName(lootKey, config.LootItems[lootKey]);
                if (item == null) continue;

                item.MoveToContainer(crate.inventory);
            }

            storedData.CrateID = crate.net.ID;
            SaveData();
        }

        private void SpawnGuard(Vector3 position, float health = 150f, bool shouldChase = false)
        {
            NPCPlayerApex npc = GameManager.server.CreateEntity(ScientistPrefab, position) as NPCPlayerApex;
            if (npc == null)
            {
                return;
            }

            npc.displayName = BotName(npc.userID);
            npc._health     = health;
            npc._maxHealth  = npc._health;
            npc.Spawn();
            npc.inventory.Strip();
            npc.gameObject.AddComponent<GuardComponent>().shouldChase = shouldChase;

            Interface.Oxide.CallHook("GiveKit", npc, "guard");
            
            if (storedData.Bots.Contains(npc.net.ID))
            {
                return;
            }

            storedData.Bots.Add(npc.net.ID);
            SaveData();
        }

        IEnumerator SpawnGuards()
        {
            yield return new WaitForSeconds(0.5f);

            for (int i = 0; i < config.NPCCount; i++)
            {
                Vector3 position;

                if (!FindValidSpawn(eventPosition + (UnityEngine.Random.onUnitSphere * config.NPCRadius), 1, out position)) continue;

                SpawnGuard(position, config.NPCHealth, (i % 2 == 0) ? true : false);

                yield return new WaitForSeconds(0.5f);
            }

            yield break;
        }
        #endregion

        #region Scripts
        public class CargoPlaneComponent : MonoBehaviour
        {
            public CargoPlane plane;
            public bool dropped = false;

            void Start()
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
                plane.secondsTaken += Time.deltaTime;

                if (!dropped && (double) Mathf.InverseLerp(0.0f, plane.secondsToTake, plane.secondsTaken) >= 0.5)
                {
                    dropped = true;

                    ins.SpawnCrate(plane.transform.position);
                }
            }
        }

        public class HackableCrateComponent : MonoBehaviour
        {
            public HackableLockedCrate crate;

            void Start()
            {
                crate = GetComponent<HackableLockedCrate>();
                if (crate == null)
                {
                    Destroy(this);

                    return;
                }

                crate.StartHacking();                

                GetComponent<StorageContainer>().decayTimer = float.NegativeInfinity;
            }
        }

        public class GuardComponent : FacepunchBehaviour
        {
            public NPCPlayerApex npc;
            public Vector3 spawnPoint;
            public bool goingHome;
            public bool shouldChase;
            public int roamRadius;

            void Start()
            {
                npc = GetComponent<NPCPlayerApex>();
                if (npc == null)
                {
                    Destroy(this);
                    return;
                }

                npc.RadioEffect           = new GameObjectRef();
                npc.DeathEffect           = new GameObjectRef();
                npc.Stats.AggressionRange = ins.config.NPCAgressionRange;
                npc.Stats.VisionRange     = ins.config.NPCVisionRange;
                npc.Stats.LongRange       = ins.config.NPCLongRange;
                npc.Stats.MediumRange     = ins.config.NPCMediumRange;
                npc.Stats.DeaggroCooldown = 180f;
                npc.SpawnPosition         = npc.transform.position;
                npc.Destination           = npc.transform.position;
                npc.InitFacts();
                npc.SendNetworkUpdate();

                spawnPoint = npc.transform.position;
                roamRadius = UnityEngine.Random.Range(0,50);
            }

            void Update()
            {
                if (!npc.GetNavAgent.isOnNavMesh || (npc.GetFact(NPCPlayerApex.Facts.IsAggro)) == 1 && shouldChase)
                {
                    return;
                }

                ShouldRelocate();
            }

            void ShouldRelocate()
            {
                float distance = Vector3.Distance(npc.transform.position, spawnPoint);
                if (!goingHome && distance >= roamRadius)
                {
                    goingHome = true;
                }

                if (goingHome && distance >= roamRadius)
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

        public class ParachuteComponent : FacepunchBehaviour
        {
            public BaseEntity parachute;
            public BaseEntity entity;

            void Start()
            {
                entity = GetComponent<BaseEntity>();
                if (entity == null)
                {
                    Destroy(this);
                    return;
                }

                parachute = GameManager.server.CreateEntity(ChutePrefab, entity.transform.position) as BaseEntity;
                if (parachute == null)
                {
                    return;
                }

                parachute.Spawn();
                parachute.SetParent(entity);
                parachute.transform.localPosition = new Vector3(0, 1f, 0);

                Rigidbody rb = entity.GetComponent<Rigidbody>();
                rb.drag      = 1.5f;
                rb.AddForce(-transform.up * -1f);
                rb.useGravity = true;
            }

            void OnCollisionEnter(Collision col)
            {
                if (parachute != null && !parachute.IsDestroyed)
                    parachute.Kill();
            }
        }
        #endregion

        #region Chat Commands
        [ChatCommand("guarded-start")]
        private void StartCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin || storedData.EventActive)
            {
                return;
            }

            StartEvent();
        }

        [ChatCommand("guarded-end")]
        private void EndCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin || !storedData.EventActive)
            {
                return;
            }

            ResetEvent();
        }

        [ChatCommand("guarded-top")]
        private void TopCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            Dictionary<string, int> playerStats = new Dictionary<string, int>();

            foreach (KeyValuePair<ulong, int> item in storedData.Players)
            {
                BasePlayer foundPlayer = BasePlayer.FindByID(item.Key);
                if (foundPlayer != null)
                    playerStats.Add(foundPlayer.displayName, item.Value);
            }

            var stats = playerStats.OrderByDescending(x => x.Value)
            .Select(x => string.Format("{0}: {1}", x.Key, x.Value))
            .Take(5)
            .ToArray();

            player.ChatMessage(Lang("Leaderboard", player.UserIDString, string.Join("\n", stats)));
        }

        [ChatCommand("guarded-stats")]
        private void StatsCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            if (storedData.Players.ContainsKey(player.userID))
            {
                player.ChatMessage(Lang("Stats", player.UserIDString, storedData.Players[player.userID]));

                return;
            }

            player.ChatMessage(Lang("StatsNone", player.UserIDString));
        }
        #endregion

        #region Helpers
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);

        private string BotName(ulong v) => Facepunch.RandomUsernames.Get((int)(v % 2147483647uL));

        private Color RGBColorConverter(int r, int g, int b) => new Color(r/255f, g/255f, b/255f);        

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

        private Vector3? GetSpawnPos()
        {
            for (int i = 0; i < MaxSpawnTries; i++)
            {
                float posX = UnityEngine.Random.Range(-TerrainMeta.Size.x / 2, TerrainMeta.Size.x / 2);
                float posZ = UnityEngine.Random.Range(-TerrainMeta.Size.z / 2, TerrainMeta.Size.z / 2);
                Vector3 randomPos = new Vector3(posX, HeightToRaycast, posZ);

                Vector3 point;

                if (FindValidSpawn(randomPos, 1, out point))
                {
                    return point;
                }
            }

            return null;
        }

        private bool FindValidSpawn(Vector3 center, float range, out Vector3 result)
        {
            for (int i = 0; i < 50; i++)
            {
                Vector3 position = center + UnityEngine.Random.insideUnitSphere * range;

                RaycastHit hit;
                if (Physics.Raycast(new Vector3(position.x, position.y + 200f, position.z), Vector3.down, out hit, Mathf.Infinity, LayerMasks))
                {
                    Vector3 pos = hit.point;
                    if (!IsMonument(pos) && !WaterLevel.Test(pos) && !Avoids.Contains(hit.collider.gameObject.name))
                    {
                        result = pos;

                        return true;
                    }
                }
            }

            result = Vector3.zero;

            return false;
        }

        private bool IsMonument(Vector3 position)
        {
            foreach (MonumentInfo mon in Monuments)
            {
                if (mon.Bounds.Contains(position)) 
                {
                    return true;
                }
            }

            return false;
        }
        #endregion
    }
}
