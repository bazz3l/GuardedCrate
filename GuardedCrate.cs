using System.Collections.Generic;
using Oxide.Core.Plugins;
using Oxide.Core;
using Rust.Ai.HTN;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.1.4")]
    [Description("Spawns a crate guarded by scientists with custom loot.")]
    class GuardedCrate : RustPlugin
    {
        [PluginReference] Plugin Kits;

        #region Fields
        const string _cratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        const string _cargoPrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        const string _markerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        const string _npcPrefab = "assets/prefabs/npc/scientist/htn/scientist_full_any.prefab";

        readonly int _layerMask = LayerMask.GetMask("Terrain", "World", "Construction", "Deployed");

        List<MonumentInfo> _monuments { get { return TerrainMeta.Path.Monuments; } }
        HashSet<HTNPlayer> _guards = new HashSet<HTNPlayer>();
        PluginConfig _config;
        HackableLockedCrate _crate;
        MapMarkerGenericRadius _marker;
        Timer _eventRepeatTimer;
        Timer _eventTimer;
        bool _eventActive;
        bool _wasLooted;
        public static GuardedCrate plugin;
        #endregion

        #region Config
        PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                UseKit = false,
                KitName = "guard",
                EventTime = 3600f,
                EventLength = 1800f,
                NPCRoam = 150f,
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

        class PluginConfig
        {
            public bool UseKit;
            public string KitName;
            public float NPCRoam;
            public int NPCCount;
            public float EventTime;
            public float EventLength;
            public int LootItemsMax;
            public List<LootItem> LootItems;
        }

        class LootItem
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

        void OnServerInitialized()
        {
            _eventRepeatTimer = timer.Repeat(_config.EventTime, 0, () => StartEvent());

            StartEvent();
        }

        void Init()
        {
            plugin = this;
            _config = Config.ReadObject<PluginConfig>();
        }

        void Unload() => StopEvent();

        void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity == null || _crate == null || entity.net.ID != _crate.net.ID)
            {
                return;
            }

            _wasLooted = true;

            ResetEvent();

            MessagePlayers("<color=#DC143C>Guarded Loot</color>: event completed.");
        }
        #endregion

        #region Core
        void StartEvent()
        {
            if (_eventActive)
            {
                return;
            }

            Vector3 position = RandomLocation();

            if (position == Vector3.zero)
            {
                ResetEvent();

                return;
            }

            _eventActive = true;

            SpawnPlane(position);

            _eventTimer = timer.Once(_config.EventLength, () => StopEvent());

            MessagePlayers("<color=#DC143C>Guarded Crate</color>: event started.");
        }

        void StopEvent()
        {
            if (_crate != null && !_crate.IsDestroyed)
            {
                _crate?.Kill();
            }

            ResetEvent();

            MessagePlayers("<color=#DC143C>Guarded Loot</color>: event ended.");
        }

        void ResetEvent()
        {
            _eventActive = false;

            DestroyGuards();
            DestroyTimers();

            if (_crate != null && !_crate.IsDestroyed && !_wasLooted)
            {
                _crate?.Kill();
            }

            if (_marker != null && !_marker.IsDestroyed)
            {
                _marker?.Kill();
            }

            _wasLooted = false;
            _marker = null;
            _crate  = null;
        }

        void DestroyGuards()
        {
            foreach (HTNPlayer npc in _guards)
            {
                if (npc == null || npc.IsDestroyed)
                {
                    continue;
                }

                npc.Kill();
            }

            _guards.Clear();
        }

        void DestroyTimers()
        {
            if (_eventRepeatTimer != null && !_eventRepeatTimer.Destroyed)
            {
                _eventRepeatTimer.Destroy();
            }

            if (_eventTimer != null && !_eventTimer.Destroyed)
            {
                _eventTimer.Destroy();
            }

            _eventRepeatTimer = timer.Repeat(_config.EventTime, 0, () => StartEvent());
        }

        IEnumerator<object> SpawnAI(Vector3 position) 
        {
            for (int i = 0; i < _config.NPCCount; i++)
            {
                Vector3 spawnLocation = RandomCircle(position, 10f, (360 / _config.NPCCount * i));

                Vector3 validPosition;

                if (IsValidLocation(spawnLocation, out validPosition))
                {
                    SpawnNPC(validPosition, Quaternion.FromToRotation(Vector3.forward, position));
                }

                yield return new WaitForSeconds(0.5f);
            }

            yield return null;
        }

        void SpawnNPC(Vector3 position, Quaternion rotation)
        {
            HTNPlayer npc = GameManager.server.CreateEntity(_npcPrefab, position, rotation) as HTNPlayer;
            if (npc == null)
            {
                return;
            }

            npc.enableSaving = false;
            npc._aiDomain.MovementRadius = _config.NPCRoam;
            npc._aiDomain.Movement = HTNDomain.MovementRule.RestrainedMove;
            npc.Spawn();

            _guards.Add(npc);

            if (!_config.UseKit)
            {
                return;
            }

            npc.inventory.Strip();

            Interface.Oxide.CallHook("GiveKit", npc, _config.KitName);
        }

        void SpawnPlane(Vector3 position)
        {
            CargoPlane cargoplane = GameManager.server.CreateEntity(_cargoPrefab) as CargoPlane;
            if (cargoplane == null)
            {
                return;
            }

            cargoplane.Spawn();
            cargoplane.InitDropPosition(position);
            cargoplane.gameObject.AddComponent<PlaneComponent>();
        }

        void SpawnCreate(Vector3 position)
        {
            _crate = GameManager.server.CreateEntity(_cratePrefab, position, Quaternion.identity) as HackableLockedCrate;
            if (_crate == null)
            {
                return;
            }

            _crate.enableSaving = false;
            _crate.Spawn();
            _crate.gameObject.AddComponent<ParachuteComponent>();

            _crate.inventory.Clear();
            _crate.inventory.capacity = _config.LootItemsMax;
            ItemManager.DoRemoves();

            timer.Once(5f, () => PopulateLoot());
        }

        void SpawnMarker(Vector3 position)
        {
            _marker = GameManager.server.CreateEntity(_markerPrefab, position) as MapMarkerGenericRadius;
            if (_marker == null)
            {
                return;
            }

            _marker.enableSaving = false;
            _marker.alpha  = 0.8f;
            _marker.color1 = Color.red;
            _marker.color2 = Color.white;
            _marker.radius = 0.6f;
            _marker.Spawn();
            _marker.transform.localPosition = Vector3.zero;
            _marker.SendUpdate(true);
        }

        void PopulateLoot()
        {
            if (_crate == null || _config.LootItems.Count < _config.LootItemsMax)
            {
                return;
            }

            List<LootItem> items = new List<LootItem>();

            int counter = 0;

            while(counter < _config.LootItemsMax)
            {
                LootItem lootItem = _config.LootItems.GetRandom();

                if (!items.Contains(lootItem))
                {
                    items.Add(lootItem);

                    counter++;
                }
            }

            foreach (LootItem item in items)
            {
                ItemManager.CreateByName(item.Shortname, item.Amount)?.MoveToContainer(_crate.inventory);
            }
        }

        void SpawnEvent(Vector3 position)
        {
            SpawnMarker(position);

            SpawnCreate(position);

            SingletonComponent<ServerMgr>.Instance.StartCoroutine(SpawnAI(position));

            MessagePlayers($"<color=#DC143C>Guarded Crate</color>: crate landing fight for the loot, ({GetGrid(position)}).");
        }

        class PlaneComponent : MonoBehaviour
        {
            CargoPlane _plane;
            Vector3 _lastPosition;
            bool _hasDropped;

            void Awake()
            {
                _plane = GetComponent<CargoPlane>();
                if (_plane == null)
                {
                    Destroy(this);
                    return;
                }

                _plane.dropped = true;
            }

            void Update()
            {
                if (_plane == null || _plane.IsDestroyed)
                {
                    Destroy(this);
                    return;
                }

                _lastPosition = transform.position;

                float distance = Mathf.InverseLerp(0.0f, _plane.secondsToTake, _plane.secondsTaken);
                if (!_hasDropped && distance >= 0.5f)
                {
                    _hasDropped = true;

                    plugin.SpawnEvent(_lastPosition);
                }
            }
        }

        class ParachuteComponent : FacepunchBehaviour
        {
            const string _chutePrefab = "assets/prefabs/misc/parachute/parachute.prefab";
            BaseEntity _parachute;
            BaseEntity _entity;

            void Awake()
            {
                _entity = GetComponent<BaseEntity>();
                if (_entity == null)
                {
                    Destroy(this);
                    return;
                }

                _parachute = GameManager.server.CreateEntity(_chutePrefab, _entity.transform.position);
                if (_parachute == null)
                {
                    return;
                }

                _parachute.enableSaving = false;
                _parachute.Spawn();
                _parachute.SetParent(_entity);
                _parachute.transform.localPosition = new Vector3(0,1f,0);

                Rigidbody rb  = _entity.GetComponent<Rigidbody>();
                rb.useGravity = true;
                rb.drag       = 2.5f;
            }

            void OnCollisionEnter(Collision col)
            {
                if (_parachute != null && !_parachute.IsDestroyed)
                {
                    _parachute?.Kill();
                }

                Destroy(this);
            }
        }
        #endregion

        #region Helpers
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
            float wordSize = MapSize();

            for (int i = 0; i < maxTries; i++)
            {
                Vector3 randomLocation = new Vector3(Core.Random.Range(-wordSize, wordSize), 200f, Core.Random.Range(-wordSize, wordSize));

                Vector3 validPosition;

                if (!IsValidLocation(randomLocation, out validPosition))
                {
                    continue;
                }

                return validPosition;
            }

            return Vector3.zero;
        }

        bool IsValidLocation(Vector3 location, out Vector3 position)
        {
            RaycastHit hit;

            if (Physics.Raycast(location + (Vector3.up * 250f), Vector3.down, out hit, Mathf.Infinity, _layerMask))
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

        bool IsValidPoint(Vector3 point)
        {
            if (IsNearMonument(point) || IsNearPlayer(point) || WaterLevel.Test(point))
            {
                return false;
            }

            return true;
        }

        bool IsNearMonument(Vector3 position)
        {
            foreach(MonumentInfo monument in _monuments)
            {
                if (monument.Bounds.Contains(position))
                {
                    return true;
                }
            }

            return false;
        }

        bool IsNearPlayer(Vector3 position)
        {
            foreach(BasePlayer player in BasePlayer.activePlayerList)
            {
                if (Vector3.Distance(position, player.transform.position) < 50)
                {
                    return true;
                }
            }

            return false;
        }


        // Thanks to yetzt with fixed grid
        string GetGrid(Vector3 position)
        {
            char letter = 'A';

            float x = Mathf.Floor((position.x + (ConVar.Server.worldsize / 2)) / 146.3f) % 26;
            float z = Mathf.Floor(ConVar.Server.worldsize / 146.3f) - Mathf.Floor((position.z + (ConVar.Server.worldsize / 2)) / 146.3f);
            letter = (char)(((int)letter) + x);

            return $"{letter}{z}";
        }

        void MessagePlayers(string message)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                player.ChatMessage(message);
            }
        }
        #endregion
    }
}