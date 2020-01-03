using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Oxide.Core.Configuration;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Plugins;
using Oxide.Core;
using UnityEngine;
using Facepunch;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.0.2")]
    [Description("Eliminate the scientits to gain high value loot")]
    class GuardedCrate : RustPlugin
    {
        [PluginReference] Plugin Kits;

        #region Fields
        public const string CratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        public const string CargoPrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        public const string ChutePrefab = "assets/prefabs/misc/parachute/parachute.prefab";
        public const string MarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        public const string ScientistPrefab = "assets/prefabs/npc/scientist/scientist.prefab";
        public const int MaxSpawnTries = 300;

        public HashSet<NPCPlayerApex> npcGuards = new HashSet<NPCPlayerApex>();
        public static GuardedCrate plugin;
        private Vector3 eventPosition;
        private bool eventActive;
        private uint eventCrate;

        private List<MonumentInfo> Monuments
        {
            get { return TerrainMeta.Path.Monuments; }
        }

        private float HeightToRaycast
        {
            get { return TerrainMeta.HighestPoint.y + 250f; }
        }
        #endregion

        #region Config
        public PluginConfig config;

        protected override void LoadDefaultConfig() => Config.WriteObject(GetDefaultConfig(), true);

        public class PluginConfig
        {
            public float EventStartTime;
            public int GuardMaxSpawn;
            public int GuardMaxRoam;
            public float GuardAggressionRange;
            public float GuardAgressionRange;
            public float GuardDeaggroRange;
            public float GuardVisionRange;
            public float GuardLongRange;
            public string GuardKit;
            public Dictionary<string, int> LootItems;
        }

        public PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                EventStartTime       = 30f,
                GuardMaxSpawn        = 20,
                GuardMaxRoam         = 20,
                GuardAggressionRange = 101f,
                GuardVisionRange     = 102f,
                GuardDeaggroRange    = 104f,
                GuardLongRange       = 100f,
                GuardKit             = "guard",
                LootItems            = new Dictionary<string, int> {
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
            public Dictionary<ulong, PlayerStatistic> Statistics = new Dictionary<ulong, PlayerStatistic>();
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
               ["EventComplete"]   = "<color=#DC143C>Guarded Crate Event</color>: {0}, completed the event.",
               ["TopPlayers"]      = "<color=#DC143C>Guarded Crate Event</color>: Top Players\n{0}",
               ["TopItem"]         = "<color=#DC143C>{0}</color>:\nEvents Complete {1}, Guards Killed: {2}.",
               ["Statistics"]      = "<color=#DC143C>Guarded Crate Event</color>: {0}"
            }, this);
        }

        private void OnServerInitialized()
        {
            plugin = this;
        }

        private void Init()
        {
            config     = Config.ReadObject<PluginConfig>();
            storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            ResetEvent();
        }

        private void Unload() => ResetEvent();

        private void OnPlayerDie(NPCPlayerApex npc, HitInfo info)
        {
            if (npc == null || !npcGuards.Contains(npc)) return;

            BasePlayer player = info?.Initiator as BasePlayer;
            if (player == null) return;

            GetPlayerStatistics(player).GuardsKilled++;
        }

        private void OnLootEntity(BasePlayer player, StorageContainer entity)
        {
            if (entity == null || entity.net.ID != eventCrate || !eventActive) return;

            GetPlayerStatistics(player).EventsCompleted++;

            ResetEvent();

            PrintToChat(Lang("EventComplete", null, player.displayName));
        }
        #endregion
 
        #region Core
        private void StartEvent()
        {
            if (eventActive) return;

            Vector3 spawnPos = GetRamdomSpawn();

            if (spawnPos == Vector3.zero) return;
            eventPosition = spawnPos;
            eventActive   = true;

            SpawnCargoPlane();

            SingletonComponent<ServerMgr>.Instance.StartCoroutine(SpawnGuards());

            PrintToChat(Lang("EventActive", null, GridReference(eventPosition)));

            Interface.Oxide.LogError("[Guarded Crate] Event ended.");
        }

        private void ResetEvent()
        {
            foreach(NPCPlayerApex npc in npcGuards)
            {
                if (npc == null || npc.IsDestroyed) continue;
                npc?.KillMessage();
            }

            eventActive = false;

            npcGuards.Clear();

            SaveData();

            Interface.Oxide.LogError("[Guarded Crate] Event ended.");
        }

        private void SpawnCargoPlane()
        {
            CargoPlane cargoplane = GameManager.server.CreateEntity(CargoPrefab) as CargoPlane;
            cargoplane.InitDropPosition(eventPosition);
            cargoplane.Spawn();
            cargoplane.gameObject.AddComponent<CargoPlaneComponent>();
        }

        private void SpawnCrateMarker(HackableLockedCrate crate)
        {
            MapMarkerGenericRadius marker = GameManager.server.CreateEntity(MarkerPrefab, crate.transform.position, crate.transform.rotation) as MapMarkerGenericRadius;
            marker.alpha  = 0.8f;
            marker.color1 = RGBColorConverter(240, 12, 12);
            marker.color2 = RGBColorConverter(255, 255, 255);
            marker.radius = 0.6f;
            marker.Spawn();
            marker.SetParent(crate);
            marker.transform.localPosition = new Vector3(0f,0f,0f);
            marker.SendUpdate();
        }

        private void SpawnCrate(Vector3 position)
        {
            HackableLockedCrate crate = GameManager.server.CreateEntity(CratePrefab, position) as HackableLockedCrate;
            crate.Spawn();
            crate.StartHacking();
            crate.gameObject.AddComponent<ParachuteComponent>();

            SpawnCrateMarker(crate);

            eventCrate = crate.net.ID;
        }

        private void SpawnGuard(Vector3 position, bool shouldChase = false)
        {
            NPCPlayerApex npc = GameManager.server.CreateEntity(ScientistPrefab, position) as NPCPlayerApex;
            npc.Spawn();
            npc.gameObject.AddComponent<GuardComponent>().shouldChase = shouldChase;

            npcGuards.Add(npc);
        }

        IEnumerator SpawnGuards()
        {
            yield return new WaitForSeconds(0.75f);

            for (int i = 0; i < config.GuardMaxSpawn; i++)
            {
                Vector3 pos;

                if (!FindValidSpawn(eventPosition + (UnityEngine.Random.onUnitSphere * config.GuardMaxRoam), 1, out pos)) continue;

                SpawnGuard(pos, (i % 2 == 0) ? true : false);

                yield return new WaitForSeconds(0.75f);
            }

            yield break;
        }
        #endregion

        #region Scripts
        class CargoPlaneComponent : MonoBehaviour
        {
            private CargoPlane plane;
            private bool dropped;

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
                if (plane == null) return;

                plane.secondsTaken += Time.deltaTime;

                if (!dropped && (double) Mathf.InverseLerp(0.0f, plane.secondsToTake, plane.secondsTaken) >= 0.5)
                {
                    dropped = true;

                    plugin.SpawnCrate(plane.transform.position);
                }
            }
        }

        class GuardComponent : FacepunchBehaviour
        {
            private NPCPlayerApex npc;
            private int spawnRoamRadius;
            private Vector3 spawnPoint;
            public bool shouldChase; 
            private bool goingBack;

            void Awake()
            {
                npc = GetComponent<NPCPlayerApex>();
                if (npc == null)
                {
                    Destroy(this);

                    return;
                }

                spawnRoamRadius = UnityEngine.Random.Range(5, plugin.config.GuardMaxRoam);
                spawnPoint      = transform.position;

                npc.RadioEffect           = new GameObjectRef();
                npc.DeathEffect           = new GameObjectRef();
                npc.SpawnPosition         = spawnPoint;
                npc.Destination           = spawnPoint;
                npc.Stats.AggressionRange = plugin.config.GuardAggressionRange;
                npc.Stats.VisionRange     = plugin.config.GuardVisionRange;
                npc.Stats.DeaggroRange    = plugin.config.GuardDeaggroRange;
                npc.Stats.LongRange       = plugin.config.GuardLongRange;
                npc.Stats.Hostility       = 1f;
                npc.Stats.Defensiveness   = 1f;
                npc.Stats.OnlyAggroMarkedTargets = false;
                npc.InitFacts();

                npc.inventory.Strip();

                Interface.Oxide.CallHook("GiveKit", npc, plugin.config.GuardKit);
            }

            void Update()
            {
                if (npc == null || !npc.IsNavRunning()) return;
                if (shouldChase&& (CurrentDistance() <= plugin.config.GuardMaxRoam) && (npc.GetFact(NPCPlayerApex.Facts.IsAggro) == (byte) 1)) return;

                ShouldRelocate();
            }

            void ShouldRelocate()
            {
                float distance = CurrentDistance();
                if (!goingBack && distance >= plugin.config.GuardMaxRoam)
                {
                    goingBack = true;
                }

                if (goingBack && distance >= plugin.config.GuardMaxRoam)
                {
                    npc.CurrentBehaviour = BaseNpc.Behaviour.Wander;
                    npc.SetFact(NPCPlayerApex.Facts.Speed, (byte)NPCPlayerApex.SpeedEnum.Walk, true, true);
                    npc.GetNavAgent.SetDestination(spawnPoint);
                    npc.Destination = spawnPoint;
                }
                else
                {
                    goingBack = false;
                }
            }

            float CurrentDistance()
            {
                return Vector3.Distance(transform.position, spawnPoint);
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

                parachute = GameManager.server.CreateEntity(ChutePrefab, entity.transform.position) as BaseEntity;
                parachute.Spawn();
                parachute.SetParent(entity);
                parachute.transform.localPosition = new Vector3(0, 1f, 0);

                Rigidbody rb  = entity.GetComponent<Rigidbody>();
                rb.useGravity = true;
                rb.drag       = 1.5f;
            }

            void OnCollisionEnter(Collision col)
            {
                if (parachute == null) return;
                parachute.Kill();
                parachute = (BaseEntity) null;
            }
        }
        #endregion

        #region Statistics
        private PlayerStatistic GetPlayerStatistics(BasePlayer player)
        {
            if (!storedData.Statistics.ContainsKey(player.userID))
                storedData.Statistics.Add(player.userID, new PlayerStatistic(player.displayName));

            return storedData.Statistics[player.userID];
        }

        private class PlayerStatistic
        {
            public string PlayerName;
            public int GuardsKilled    = 0;
            public int EventsCompleted = 0;

            public PlayerStatistic(string playerName)
            {
                PlayerName = playerName;
            }

            public override string ToString()
            {
                return string.Format("Events Completed: {0}, Guards Killed: {1}", EventsCompleted, GuardsKilled);
            }
        }
        #endregion

        #region Commands
        [ChatCommand("gstart")]
        private void StartCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin) return;

            StartEvent();
        }

        [ChatCommand("gend")]
        private void EndCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin) return;

            ResetEvent();
        }

        [ChatCommand("gstats")]
        private void StatsCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            player.ChatMessage(Lang("Statistics", player.UserIDString, GetPlayerStatistics(player).ToString()));
        }

        [ChatCommand("gtop")]
        private void TopCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            List<string> TopPlayers = storedData.Statistics
            .OrderByDescending(x => x.Value.EventsCompleted)
            .Select(s => Lang("TopItem", player.UserIDString, s.Value.PlayerName, s.Value.EventsCompleted, s.Value.GuardsKilled))
            .Take(10)
            .ToList();

            player.ChatMessage(Lang("TopPlayers", player.UserIDString, string.Join("\n", TopPlayers)));
        }
        #endregion

        #region Helpers
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);

        private Color RGBColorConverter(int r, int g, int b) => new Color(r/255f, g/255f, b/255f);        

        private Vector3 GetRamdomSpawn()
        {
            for (int i = 0; i < MaxSpawnTries; i++)
            {
                Vector3 pos;
                float posX = UnityEngine.Random.Range(-TerrainMeta.Size.x / 2, TerrainMeta.Size.x / 2);
                float posZ = UnityEngine.Random.Range(-TerrainMeta.Size.z / 2, TerrainMeta.Size.z / 2);

                if (FindValidSpawn(new Vector3(posX, HeightToRaycast, posX), 1, out pos))
                    return pos;
            }

            return Vector3.zero;
        }

        private bool FindValidSpawn(Vector3 position, float range, out Vector3 foundPoint)
        {
            for (int i = 0; i < 50; i++)
            {
                RaycastHit hit;
                if (Physics.Raycast(position, Vector3.down, out hit, Mathf.Infinity, LayerMask.GetMask("Terrain", "World", "Default")))
                {
                    Vector3 point = hit.point;
                    if (IsMonument(point) || WaterLevel.Test(point)) continue;

                    foundPoint = point;

                    return true;
                }
            }

            foundPoint = Vector3.zero;

            return false;
        }

        private bool IsMonument(Vector3 position)
        {
            foreach(MonumentInfo mon in Monuments)
            {
                if (mon.Bounds.Contains(position))
                    return true;
            }

            return false;
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
        #endregion
    }
}
