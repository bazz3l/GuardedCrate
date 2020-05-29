using System.Collections.Generic;
using Oxide.Core.Plugins;
using Oxide.Core;
using Rust.Ai.HTN;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.1.6")]
    [Description("Spawns a crate guarded by scientists with custom loot.")]
    class GuardedCrate : RustPlugin
    {
        [PluginReference]
        Plugin Kits;

        #region Fields
        readonly int _layerMask = LayerMask.GetMask("Terrain", "World", "Default");
        readonly List<int> _blockedLayers = new List<int> {
            (int)Layer.Water,
            (int)Layer.Construction,
            (int)Layer.Trigger,
            (int)Layer.Prevent_Building,
            (int)Layer.Deployed,
            (int)Layer.Tree,
            (int)Layer.Clutter
        };

        const string _cratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        const string _markerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        const string _cargoPrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        const string _npcPrefab = "assets/prefabs/npc/scientist/htn/scientist_full_any.prefab";

        List<MonumentInfo> _monuments { get { return TerrainMeta.Path.Monuments; } }
        HashSet<HTNPlayer> _guards = new HashSet<HTNPlayer>();
        PluginConfig _config;
        MapMarkerGenericRadius _marker;
        HackableLockedCrate _crate;
        Timer _eventRepeatTimer;
        Timer _eventTimer;
        bool _eventActive;
        bool _wasLooted;
        Vector3 _eventPos;
        
        static GuardedCrate Instance;
        #endregion

        #region Config
        PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                EventTime = 3600f,
                EventLength = 1800f,
                NPCCount = 15,
                UseKit = true,
                NPCTypes = new List<NPCType> {
                    new NPCType("guard", 150f),
                    new NPCType("guard-heavy", 300f)
                }
            };
        }

        class PluginConfig
        {
            public int NPCCount;
            public float EventTime;
            public float EventLength;
            public bool UseKit;
            public List<NPCType> NPCTypes;
        }

        class NPCType
        {
            public string Kit;
            public float Health;
            public float Distance;

            public NPCType(string kit, float health = 150f, float distance = 100f)
            {
                Kit = kit;
                Health = health;
                Distance = distance;
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
            Instance = this;

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

            MessageAll($"<color=#DC143C>Guarded Loot</color>: ({player.displayName}) completed the event.");
        }

        object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            BasePlayer player = planner?.GetOwnerPlayer();
            if (player == null)
            {
                return null;
            }

            if (_eventActive && Vector3Ex.Distance2D(_eventPos, player.transform.position) <= 20f)
            {
                player.ChatMessage("<color=#DC143C>Guarded Loot</color>: You can't build here.");

                return false;
            }

            return null;
        }
        #endregion

        #region Core
        void StartEvent()
        {
            Vector3 position = RandomLocation();

            if (position == Vector3.zero || _eventActive)
            {
                return;
            }

            _eventActive = true;

            SpawnPlane(position);

            _eventTimer = timer.Once(_config.EventLength, () => StopEvent());
        }

        void StopEvent()
        {
            if (_crate != null && !_crate.IsDestroyed)
            {
                _crate?.Kill();
            }

            ResetEvent();
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
            _crate = null;
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

        IEnumerator<object> SpawnAI()
        {
            for (int i = 0; i < _config.NPCCount; i++)
            {
                Vector3 spawnLoc = RandomCircle(_eventPos, UnityEngine.Random.Range(10f, 20f), (360 / _config.NPCCount * i));
                Vector3 validPos = Vector3.zero;

                if (IsValidLocation(spawnLoc, false, out validPos))
                {
                    SpawnNPC(validPos, Quaternion.FromToRotation(Vector3.forward, _eventPos));                  
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

            NPCType npcType = _config.NPCTypes.GetRandom();
            if (npcType == null)
            {
                return;
            }
            
            npc.enableSaving = false;
            npc._aiDomain.MovementRadius = UnityEngine.Random.Range(50f, npcType.Distance);
            npc._aiDomain.Movement = HTNDomain.MovementRule.RestrainedMove;
            npc.displayName = "Guard";            
            npc.InitializeHealth(npcType.Health, npcType.Health);
            npc.Spawn();

            _guards.Add(npc);

            if (!_config.UseKit)
            {
                return;
            }

            npc.inventory.Strip();
                
            Interface.Oxide.CallHook("GiveKit", npc, npcType.Kit);                
        }

        void SpawnPlane(Vector3 position)
        {
            CargoPlane cargoplane = GameManager.server.CreateEntity(_cargoPrefab) as CargoPlane;
            if (cargoplane == null)
            {
                return;
            }

            cargoplane.InitDropPosition(position);
            cargoplane.Spawn();
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
            _crate.SetWasDropped();
            _crate.Spawn();
            _crate.gameObject.AddComponent<ParachuteComponent>();
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

        void SpawnEvent(Vector3 position)
        {
            _eventPos = position;

            SpawnMarker(_eventPos);
            SpawnCreate(_eventPos);

            timer.In(30f, () => SingletonComponent<ServerMgr>.Instance.StartCoroutine(SpawnAI()));

            MessageAll($"<color=#DC143C>Guarded Crate</color>: Guards with valuable cargo arriving at ({GetGrid(_eventPos)}) ETA 30 seconds! Prepare to attack or run for your life.");
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

                    Instance.SpawnEvent(_lastPosition);
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
                _parachute.SetParent(_entity);
                _parachute.transform.localPosition = new Vector3(0, 1f, 0);
                _parachute.Spawn();

                Rigidbody rb = _entity.GetComponent<Rigidbody>();
                rb.useGravity = true;
                rb.drag = 1.2f;
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

        Vector3 RandomLocation()
        {
            float wordSize = MapSize();

            int maxTries = 100;

            while (--maxTries > 0)
            {
                Vector3 randomPos = new Vector3(Core.Random.Range(-wordSize, wordSize), 200f, Core.Random.Range(-wordSize, wordSize));

                Vector3 position;

                if (!IsValidLocation(randomPos, true, out position))
                {
                    continue;
                }

                return position;
            }

            return Vector3.zero;
        }

        bool IsValidLocation(Vector3 location, bool hasPlayers, out Vector3 position)
        {
            RaycastHit hit;

            if (!Physics.Raycast(location + (Vector3.up * 250f), Vector3.down, out hit, Mathf.Infinity, _layerMask))
            {
                position = Vector3.zero;

                return false;
            }

            if (!IsValidPoint(hit.point, hasPlayers) || _blockedLayers.Contains(hit.collider.gameObject.layer))
            {
                position = Vector3.zero;

                return false;
            }

            position = hit.point;

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

        bool IsNearPlayer(Vector3 position, bool hasPlayers = false)
        {
            foreach(BasePlayer player in BasePlayer.activePlayerList)
            {
                if (hasPlayers && Vector3Ex.Distance2D(position, player.transform.position) <= 50f)
                {
                    return true;
                }
            }

            return false;
        }

        bool IsValidPoint(Vector3 position, bool hasPlayers)
        {
            if (IsNearMonument(position) || WaterLevel.Test(position) || IsNearPlayer(position, hasPlayers))
            {
                return false;
            }

            return true;
        }

        // Thanks to yetzt with fixed grid
        string GetGrid(Vector3 position)
        {
            char letter = 'A';

            float x = Mathf.Floor((position.x + (ConVar.Server.worldsize / 2)) / 146.3f) % 26;
            float z = Mathf.Floor(ConVar.Server.worldsize / 146.3f) - Mathf.Floor((position.z+(ConVar.Server.worldsize / 2)) / 146.3f);
            letter = (char)(((int)letter) + x);

            return $"{letter}{z}";
        }

        void MessageAll(string message)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                player.ChatMessage(message);
            }
        }
        #endregion
    }
}