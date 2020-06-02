using System.Collections.Generic;
using Oxide.Core.Plugins;
using Oxide.Core;
using Rust.Ai.HTN;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.1.8")]
    [Description("Spawns a crate guarded by scientists.")]
    class GuardedCrate : RustPlugin
    {
        [PluginReference]
        Plugin Kits;

        #region Fields
        const string _cratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        const string _chutePrefab = "assets/prefabs/misc/parachute/parachute.prefab";
        const string _markerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        const string _cargoPrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        const string _npcPrefab = "assets/prefabs/npc/scientist/htn/scientist_full_any.prefab";

        readonly int _allowedLayers = LayerMask.GetMask("Terrain", "World", "Default");
        readonly List<int> _blockedLayers = new List<int> {
            (int)Layer.Water,
            (int)Layer.Construction,
            (int)Layer.Trigger,
            (int)Layer.Prevent_Building,
            (int)Layer.Deployed,
            (int)Layer.Tree,
            (int)Layer.Clutter
        };

        List<MonumentInfo> _monuments { get { return TerrainMeta.Path.Monuments; } }
        EventManager _manager = new EventManager();
        PluginConfig _config;
        
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
            public float MinMovementRadius;
            public float MaxMovementRadius;

            public NPCType(string kit, float health = 150f, float minMovementRadius = 80f, float maxMovementRadius = 120f)
            {
                Kit = kit;
                Health = health;
                MinMovementRadius = minMovementRadius;
                MaxMovementRadius = maxMovementRadius;
            }
        }
        #endregion

        #region Oxide
        protected override void LoadDefaultConfig() => Config.WriteObject(GetDefaultConfig(), true);

        void OnServerInitialized()
        {
            _manager.StartEventTimer(_config.EventTime, _config.EventLength);
            _manager.StartEvent(_config.EventLength);
        }

        void Init()
        {
            Instance = this;

            _config = Config.ReadObject<PluginConfig>();
        }

        void Unload()
        {
            _manager.ResetEvent();
        }

        void OnLootEntity(BasePlayer player, HackableLockedCrate crate)
        {
            if (crate == null || !_manager.IsEventLootable(crate.net.ID)) return;

            _manager.SetLooted(true);
            _manager.ResetEvent();

            MessageAll($"<color=#DC143C>Guarded Loot</color>: ({player.displayName}) completed the event.");
        }

        object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            BasePlayer player = planner?.GetOwnerPlayer();
            if (player == null)
            {
                return null;
            }

            if (_manager.BuildBlocked(player.ServerPosition))
            {
                player.ChatMessage("<color=#DC143C>Guarded Loot</color>: You can't build here.");

                return false;
            }

            return null;
        }
        #endregion

        #region Core
        class EventManager
        {
            List<HTNPlayer> _guards = new List<HTNPlayer>();
            Vector3 _eventPos = Vector3.zero;
            MapMarkerGenericRadius _marker;
            HackableLockedCrate _crate;
            Timer _eventTimer;
            Timer _eventRepeatTimer;
            bool _wasLooted;
            bool _eventActive;
            bool _restainedMove;

            public void StartEventTimer(float eventTime, float eventDuaration)
            {
                _eventRepeatTimer?.Destroy();

                _eventRepeatTimer = Instance.timer.Every(eventTime, () => StartEvent(eventDuaration));
            }

            public void StartEvent(float eventDuaration)
            {
                if (IsEventActive()) return;

                SpawnPlane();

                _eventTimer = Instance.timer.Once(eventDuaration, () => ResetEvent());
            }

            public void ResetEvent()
            {
                DestroyCrate(_wasLooted);
                DestroyTimers();
                DestroyGuards();

                _eventActive = false;
                _wasLooted = false;

                StartEventTimer(Instance._config.EventTime, Instance._config.EventLength);
            }

            public void SetLooted(bool wasLooted)
            {
                _wasLooted = wasLooted;
            }

            public bool IsEventLootable(uint id)
            {
                return _crate != null && _crate.net.ID == id;
            }

            public bool IsEventActive()
            {
                return _eventActive;
            }

            public bool BuildBlocked(Vector3 position)
            {
                return _eventActive && Vector3Ex.Distance2D(_eventPos, position) <= 20f;
            }

            void DestroyGuards()
            {
                foreach (HTNPlayer npc in _guards)
                {
                    if (npc == null || npc.IsDestroyed) continue;

                    npc.Kill();
                }

                _guards.Clear();
            }

            void DestroyCrate(bool wasLooted = false)
            {
                if (_crate != null && !_crate.IsDestroyed && !wasLooted)
                {
                    _crate?.Kill();
                }

                if (_marker != null && !_marker.IsDestroyed)
                {
                    _marker?.Kill();
                }
                
                _crate = null;
                _marker = null;
            }

            void DestroyTimers()
            {
                _eventTimer?.Destroy();
                _eventRepeatTimer?.Destroy();
            }

            public void SpawnEvent(Vector3 position)
            {
                _eventPos = position;

                _eventActive = true;

                SpawnCreate();

                Instance.timer.In(30f, () => SingletonComponent<ServerMgr>.Instance.StartCoroutine(SpawnAI()));

                Instance.MessageAll($"<color=#DC143C>Guarded Crate</color>: Guards with valuable cargo arriving at ({Instance.GetGrid(_eventPos)}) ETA 30 seconds! Prepare to attack or run for your life.");
            }

            public void SpawnPlane()
            {
                CargoPlane cargoplane = GameManager.server.CreateEntity(_cargoPrefab) as CargoPlane;
                if (cargoplane == null) return;

                cargoplane.InitDropPosition(_eventPos);
                cargoplane.Spawn();
                cargoplane.gameObject.AddComponent<PlaneComponent>();
            }

            public void SpawnCreate()
            {
                _crate = GameManager.server.CreateEntity(_cratePrefab, _eventPos, Quaternion.identity) as HackableLockedCrate;
                if (_crate == null) return;

                _crate.enableSaving = false;
                _crate.SetWasDropped();
                _crate.Spawn();
                _crate.gameObject.AddComponent<ParachuteComponent>();

                _marker = GameManager.server.CreateEntity(_markerPrefab, _eventPos) as MapMarkerGenericRadius;
                if (_marker == null) return;

                _marker.enableSaving = false;
                _marker.alpha  = 0.8f;
                _marker.color1 = Color.red;
                _marker.color2 = Color.white;
                _marker.radius = 0.6f;
                _marker.Spawn();
                _marker.transform.localPosition = Vector3.zero;
                _marker.SendUpdate(true);
            }

            IEnumerator<object> SpawnAI()
            {
                for (int i = 0; i < Instance._config.NPCCount; i++)
                {
                    Vector3 spawnLoc = Instance.RandomCircle(_eventPos, UnityEngine.Random.Range(10f, 20f), (360 / Instance._config.NPCCount * i));
                    Vector3 validPos = Vector3.zero;

                    if (Instance.IsValidLocation(spawnLoc, false, out validPos))
                    {
                        SpawnNPC(Instance._config.NPCTypes.GetRandom(), validPos, Quaternion.FromToRotation(Vector3.forward, _eventPos));              
                    }

                    yield return new WaitForSeconds(0.5f);
                }

                yield return null;
            }

            public void SpawnNPC(NPCType npcType, Vector3 position, Quaternion rotation)
            {
                BaseEntity entity = GameManager.server.CreateEntity(_npcPrefab, position, rotation);
                if (entity == null) return;

                entity.enableSaving = false;
                entity.Spawn();

                HTNPlayer component = entity.GetComponent<HTNPlayer>();
                if (component == null) return;

                component._aiDomain.MovementRadius = UnityEngine.Random.Range(npcType.MinMovementRadius, npcType.MaxMovementRadius);
                component._aiDomain.NavAgent.speed = Mathf.Lerp(component._aiDomain.NavAgent.speed, 5f, 5f);
                component._aiDomain.Movement       = GetMovementRule();
                
                if (Instance._config.UseKit)
                {
                    Interface.Oxide.CallHook("GiveKit", component, npcType.Kit);
                }

                _guards.Add(component);
            }

            HTNDomain.MovementRule GetMovementRule()
            {
                return _restainedMove ? HTNDomain.MovementRule.RestrainedMove : HTNDomain.MovementRule.FreeMove;
            }
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

                    Instance._manager.SpawnEvent(_lastPosition);
                }
            }
        }

        class ParachuteComponent : FacepunchBehaviour
        {
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
                if (_parachute == null) return;

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

        bool IsValidLocation(Vector3 location, bool hasPlayers, out Vector3 position)
        {
            RaycastHit hit;

            if (!Physics.Raycast(location + (Vector3.up * 250f), Vector3.down, out hit, Mathf.Infinity, _allowedLayers))
            {
                position = Vector3.zero;

                return false;
            }

            if (!IsValidPoint(hit.point, hasPlayers) || _blockedLayers.Contains(hit.collider.gameObject.layer) || hit.collider.name.Contains("_rock"))
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