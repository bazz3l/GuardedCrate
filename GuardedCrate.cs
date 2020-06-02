using System.Collections.Generic;
using System;
using Oxide.Core.Plugins;
using Oxide.Core;
using Rust.Ai.HTN;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.1.9")]
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
        EventManager _manager;
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
            public string KitName;
            public float MinMovementRadius;
            public float MaxMovementRadius;

            public NPCType(string kitName, float minMovementRadius = 120f, float maxMovementRadius = 150f)
            {
                KitName = kitName;
                MinMovementRadius = minMovementRadius;
                MaxMovementRadius = maxMovementRadius;
            }
        }
        #endregion

        #region Oxide
        protected override void LoadDefaultConfig() => Config.WriteObject(GetDefaultConfig(), true);

        void OnServerInitialized()
        {
            _manager = new EventManager(_config.EventTime, _config.EventLength, _config.NPCTypes);
            _manager.StartEventTimer();
            _manager.StartEvent();
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

        object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            BasePlayer player = planner.GetOwnerPlayer();
            if (player == null)
            {
                return null;
            }

            if (_manager.IsEventActive() && _manager.IsBuildBlocked(player.ServerPosition))
            {
                player.ChatMessage("<color=#DC143C>Guarded Loot</color>: Event active in this area building blocked.");
                return false;
            }

            return null;
        }

        object CanLootEntity(BasePlayer player, HackableLockedCrate crate)
        {
            if (_manager.IsEventActive() && _manager.IsEventLootable(crate.net.ID))
            {
                player.ChatMessage("<color=#DC143C>Guarded Loot</color>: All guards must be eliminated.");
                return false;
            }

            return null;
        }

        void OnEntityDeath(HTNPlayer npc, HitInfo info) => _manager.RemoveNPC(npc);
        #endregion

        #region Core
        class EventManager
        {
            List<HTNPlayer> _guards = new List<HTNPlayer>();
            List<NPCType> _npcTypes = new List<NPCType>();
            Vector3 _eventPos = Vector3.zero;
            MapMarkerGenericRadius _marker;
            HackableLockedCrate _crate;
            Timer _eventRepeatTimer;          
            Timer _eventTimer;
            bool _eventActive;            
            bool _wasLooted;
            bool _restainedMove;

            float _eventTime;
            float _eventLength;

            public EventManager(float eventTime, float eventLength, List<NPCType> npcTypes)
            {
                _eventTime = eventTime;
                _eventLength = eventLength;
                _npcTypes = npcTypes;
            }

            public void StartEventTimer()
            {
                _eventRepeatTimer?.Destroy();

                _eventRepeatTimer = Instance.timer.Every(_eventTime, () => StartEvent());
            }

            public void StartEvent()
            {
                if (IsEventActive()) return;

                SpawnPlane();

                _eventTimer = Instance.timer.Once(_eventLength, () => ResetEvent());
            }

            public void ResetEvent(bool completed = false)
            {
                DestroyCrate(completed);
                DestroyTimers();
                DestroyGuards();

                _eventActive = false;
                _wasLooted = false;

                StartEventTimer();
            }

            public bool IsEventLootable(uint id)
            {
                return _crate != null && _crate.net.ID == id;
            }

            public bool IsEventActive()
            {
                return _eventActive && _guards.Count > 0;
            }

            public bool IsBuildBlocked(Vector3 position)
            {
                return _eventActive && Vector3Ex.Distance2D(_eventPos, position) <= 20f;
            }

            public void RemoveNPC(HTNPlayer npc)
            {
                if (!_guards.Contains(npc)) return;

                _guards.Remove(npc);

                if (IsEventActive()) return;

                OpenCrate();

                ResetEvent(true);

                Instance.MessageAll($"<color=#DC143C>Guarded Loot</color>: Event completed, crate is now open loot up fast.");
            }

            void OpenCrate()
            {
                if (_crate == null) return;

                if (!_crate.IsBeingHacked())
                {
                    _crate.StartHacking();
                }

                if (!_crate.IsFullyHacked())
                {
                    _crate.RefreshDecay();
                    _crate.SetFlag(BaseEntity.Flags.Reserved2, true, false, true);
                    _crate.isLootable = true;
                    _crate.CancelInvoke(new Action(_crate.HackProgress));
                }
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

            void DestroyCrate(bool completed = false)
            {
                if (_crate != null && !_crate.IsDestroyed && !completed)
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
                CargoPlane component  = GameManager.server.CreateEntity(_cargoPrefab) as CargoPlane;
                if (component == null) return;

                //component.InitDropPosition(_eventPos);
                component.Spawn();
                component.gameObject.AddComponent<PlaneComponent>();
            }

            public void SpawnCreate()
            {
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

                _crate = GameManager.server.CreateEntity(_cratePrefab, _eventPos, Quaternion.identity) as HackableLockedCrate;
                if (_crate == null) return;

                _crate.enableSaving = false;
                _crate.SetWasDropped();
                _crate.Spawn();
                _crate.gameObject.AddComponent<ParachuteComponent>();
            }

            IEnumerator<object> SpawnAI()
            {
                for (int i = 0; i < Instance._config.NPCCount; i++)
                {
                    TrySpawnNPC(i);

                    yield return new WaitForSeconds(0.5f);
                }

                yield return null;
            }

            public void SpawnNPC(NPCType npcType, Vector3 position, Quaternion rotation)
            {
                HTNPlayer component = GameManager.server.CreateEntity(_npcPrefab, position, rotation) as HTNPlayer;
                if (component == null) return;

                component.enableSaving = false;
                component.Spawn();
                component._aiDomain.MovementRadius = UnityEngine.Random.Range(npcType.MinMovementRadius, npcType.MaxMovementRadius);
                component._aiDomain.Movement = GetMovementRule();
                
                if (Instance._config.UseKit)
                {
                    Interface.Oxide.CallHook("GiveKit", component, npcType.KitName);
                }

                _guards.Add(component);
            }

            public void TrySpawnNPC(int num)
            {
                Vector3 spawnPosition;

                for (int i = 0; i < 10; i++)
                {
                    Vector3 position = Instance.RandomCircle(_eventPos, 10f, (360 / Instance._config.NPCCount * num));

                    if (Instance.IsValidLocation(position, out spawnPosition))
                    {
                        SpawnNPC(GetRandomNPC(), spawnPosition, Quaternion.FromToRotation(Vector3.forward, _eventPos));
                        
                        return;
                    }
                }
            }

            HTNDomain.MovementRule GetMovementRule() => HTNDomain.MovementRule.FreeMove;
            NPCType GetRandomNPC() => _npcTypes.GetRandom();
        }

        class PlaneComponent : MonoBehaviour
        {
            Vector3 _lastPosition;
            CargoPlane _plane;
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
                if (rb == null) return;

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

        #region Commands
        [ChatCommand("ggrespawn")]
        void GGRespawn(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            if (_manager.IsEventActive()) _manager.ResetEvent();

            _manager.StartEvent();

            player.ChatMessage("<color=#DC143C>Guarded Crate</color>: Event reset.");
        }
        #endregion

        #region Helpers
        Vector3 RandomCircle(Vector3 center, float radius, float angle)
        {
            Vector3 pos = center;
            pos.x += radius * Mathf.Sin(angle * Mathf.Deg2Rad);
            pos.z += radius * Mathf.Cos(angle * Mathf.Deg2Rad);
            return pos;
        }

        bool IsValidLocation(Vector3 location, out Vector3 position)
        {
            RaycastHit hit;

            if (!Physics.Raycast(location + (Vector3.up * 250f), Vector3.down, out hit, Mathf.Infinity, _allowedLayers))
            {
                position = Vector3.zero;
                return false;
            }

            if (_blockedLayers.Contains(hit.collider.gameObject.layer) || hit.collider.name.Contains("rock"))
            {
                position = Vector3.zero;
                return false;
            }

            position = hit.point;
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