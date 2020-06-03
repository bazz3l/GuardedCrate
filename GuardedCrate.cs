using System.Collections.Generic;
using System;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using Oxide.Core;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.1.9")]
    [Description("Spawns a crate guarded by scientists.")]
    class GuardedCrate : RustPlugin
    {
        [PluginReference] Plugin Kits;

        #region Fields
        const string _cratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        const string _chutePrefab = "assets/prefabs/misc/parachute/parachute.prefab";
        const string _markerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        const string _cargoPrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        const string _npcPrefab = "assets/prefabs/npc/scientist/scientist_gunner.prefab";

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
                EventDuration = 1800f,
                OpenCrate = true,
                GuardMaxSpawn = 10,            
                GuardSettings = new List<GuardSetting> {
                    new GuardSetting("Guard", "guard", 100f),
                    new GuardSetting("Heavy Guard", "guard-heavy", 300f)
                }
            };
        }

        class PluginConfig
        {
            [JsonProperty(PropertyName = "EventTime (how often the event should start)")]
            public float EventTime;

            [JsonProperty(PropertyName = "EventDuration (how long the event lasts)")]
            public float EventDuration;

            [JsonProperty(PropertyName = "OpenCrate (should crate open once guards are all eliminated)")]
            public bool OpenCrate;

            [JsonProperty(PropertyName = "GuardMaxSpawn (total number of guards to spawn)")]
            public int GuardMaxSpawn;

            [JsonProperty(PropertyName = "GuardSettings (min/max roam distance and kit name)")]
            public List<GuardSetting> GuardSettings;
        }

        class GuardSetting
        {
            [JsonProperty(PropertyName = "Name (npc display name)")]
            public string Name;

            [JsonProperty(PropertyName = "Kit (kit name)")]
            public string Kit;

            [JsonProperty(PropertyName = "Health (sets the health of npc)")]
            public float Health = 100f;

            [JsonProperty(PropertyName = "MinRoamRadius (min roam radius)")]
            public float MinRoamRadius;

            [JsonProperty(PropertyName = "MaxRoamRadius (max roam radius)")]
            public float MaxRoamRadius;

            [JsonProperty(PropertyName = "ChaseDistance (distance they attack and chase)")]
            public float ChaseDistance = 151f;

            [JsonProperty(PropertyName = "VisionRange (distance they are alerted)")]
            public float VisionRange = 153f;

            [JsonProperty(PropertyName = "MaxRange (max distance they will shoot)")]
            public float MaxRange = 150f;

            [JsonProperty(PropertyName = "UseKit (should use kit)")]
            public bool UseKit = false;

            public GuardSetting(string name, string kit, float health = 100f, float minRoamRadius = 30f, float maxRoamRadius = 80f)
            {
                Name = name;
                Kit = kit;
                Health = health;
                MinRoamRadius = minRoamRadius;
                MaxRoamRadius = maxRoamRadius;
            }

            public float GetRoamRange() => UnityEngine.Random.Range(MinRoamRadius, MaxRoamRadius);
        }
        #endregion

        #region Oxide
        protected override void LoadDefaultConfig() => Config.WriteObject(GetDefaultConfig(), true);

        void OnServerInitialized()
        {
            _manager = new EventManager(_config.EventTime, _config.EventDuration, _config.GuardSettings);
            _manager.StartEvent();
        }

        void Init()
        {
            Instance = this;

            _config = Config.ReadObject<PluginConfig>();
        }

        void Unload()
        {
            if (_manager == null) return;

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

        void OnEntityDeath(NPCPlayerApex npc, HitInfo info) => _manager.RemoveNPC(npc);
        #endregion

        #region Core
        class EventManager
        {
            List<GuardSetting> _guardSettings = new List<GuardSetting>();            
            List<NPCPlayerApex> _guards = new List<NPCPlayerApex>();
            Vector3 _eventPos = Vector3.zero;
            MapMarkerGenericRadius _marker;
            HackableLockedCrate _crate;
            Timer _eventRepeatTimer;
            Timer _eventTimer;
            bool _eventActive;
            bool _restainedMove;

            float _eventTime;
            float _eventDuration;

            public EventManager(float eventTime, float eventDuration, List<GuardSetting> guardSettings)
            {
                _eventTime = eventTime;
                _eventDuration = eventDuration;
                _guardSettings = guardSettings;
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

                _eventTimer = Instance.timer.Once(_eventDuration, () => ResetEvent());
            }

            public void ResetEvent(bool completed = false)
            {
                if (completed) OpenCrate();

                DestroyCrate(completed);
                DestroyTimers();
                DestroyGuards();

                _eventActive = false;

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

            public void RemoveNPC(NPCPlayerApex npc)
            {
                if (!_guards.Contains(npc)) return;

                _guards.Remove(npc);

                if (IsEventActive()) return;

                ResetEvent(true);

                Instance.MessageAll($"<color=#DC143C>Guarded Loot</color>: Event completed, crate is now open loot up fast.");
            }

            void OpenCrate()
            {
                if (_crate == null || !Instance._config.OpenCrate) return;

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
                foreach (NPCPlayerApex npc in _guards)
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
                CargoPlane cargoplane = GameManager.server.CreateEntity(_cargoPrefab)?.GetComponent<CargoPlane>();
                if (cargoplane != null)
                {
                    cargoplane.Spawn();
                    cargoplane.gameObject.AddComponent<PlaneComponent>();
                }
                else
                {
                    cargoplane.Kill(BaseEntity.DestroyMode.None);
                }
            }

            public void SpawnCreate()
            {
                _marker = GameManager.server.CreateEntity(_markerPrefab, _eventPos)?.GetComponent<MapMarkerGenericRadius>();
                if (_marker != null)
                {
                    _marker.enableSaving = false;
                    _marker.alpha  = 0.8f;
                    _marker.color1 = Color.red;
                    _marker.color2 = Color.white;
                    _marker.radius = 0.6f;
                    _marker.Spawn();
                    _marker.transform.localPosition = Vector3.zero;
                    _marker.SendUpdate(true);
                }
                else
                {
                    _marker.Kill(BaseEntity.DestroyMode.None);
                    _marker = null;
                }

                _crate = GameManager.server.CreateEntity(_cratePrefab, _eventPos, Quaternion.identity)?.GetComponent<HackableLockedCrate>();
                if (_crate != null)
                {
                    _crate.enableSaving = false;
                    _crate.SetWasDropped();
                    _crate.Spawn();
                    _crate.gameObject.AddComponent<ParachuteComponent>();                    
                }
                else
                {
                    _crate.Kill(BaseEntity.DestroyMode.None);
                    _crate = null;
                }
            }

            public void SpawnNPC(GuardSetting settings, Vector3 position, Quaternion rotation)
            {
                BaseEntity entity = GameManager.server.CreateEntity(_npcPrefab, position, rotation);

                NPCPlayerApex component = entity.GetComponent<NPCPlayerApex>();
                if (component != null)
                {
                    entity.enableSaving = false;
                    entity.Spawn();

                    component.CancelInvoke(component.EquipTest);
                    component.CancelInvoke(component.RadioChatter);
                    component.startHealth = settings.Health;
                    component.InitializeHealth(component.startHealth, component.startHealth);
                    component.RadioEffect           = new GameObjectRef();
                    component.CommunicationRadius   = 0;
                    component.displayName           = settings.Name;
                    component.Stats.AggressionRange = component.Stats.DeaggroRange = settings.ChaseDistance;
                    component.Stats.VisionRange     = settings.VisionRange;
                    component.Stats.LongRange       = settings.MaxRange;
                    component.Stats.MaxRoamRange    = settings.GetRoamRange();
                    component.Stats.Hostility       = 1;
                    component.Stats.Defensiveness   = 1;
                    component.InitFacts();
                    component.gameObject.AddComponent<GuardComponent>()?.Init(settings.GetRoamRange(), position);

                    _guards.Add(component);

                    Instance.timer.In(1f, () => GiveKit(component, settings.Kit, settings.UseKit));
                }
                else
                {
                    entity.Kill(BaseNetworkable.DestroyMode.None);
                }
            }

            void GiveKit(NPCPlayerApex npc, string kit, bool give)
            {
                if (!give) return;

                npc.inventory.Strip();

                Interface.Oxide.CallHook("GiveKit", npc, kit);
            }

            public void TrySpawnNPC(int num)
            {
                Vector3 position = Instance.RandomCircle(_eventPos, 10f, (360 / Instance._config.GuardMaxSpawn * num));

                if (Instance.IsValidLocation(position, out position))
                {
                    SpawnNPC(GetRandomNPC(), position, Quaternion.FromToRotation(Vector3.forward, _eventPos));
                }
            }

            IEnumerator<object> SpawnAI()
            {
                for (int i = 0; i < Instance._config.GuardMaxSpawn; i++)
                {
                    TrySpawnNPC(i);

                    yield return new WaitForSeconds(0.5f);
                }

                yield return null;
            }

            GuardSetting GetRandomNPC() => _guardSettings.GetRandom();
        }

        class GuardComponent : MonoBehaviour
        {
            NPCPlayerApex _npc;
            Vector3 _targetDestination;
            float _maxRoamDistance;

            public void Init(float maxRoamDistance, Vector3 targetDestination)
            {
                _maxRoamDistance    = maxRoamDistance;
                _targetDestination  = targetDestination;
                _npc.ServerPosition = targetDestination;
            }

            void Awake()
            {
                _npc = gameObject.GetComponent<NPCPlayerApex>();
                if (_npc == null)
                {
                    Destroy(this);
                    return;
                }

                Destroy(gameObject.GetComponent<Spawnable>());
            }

            void FixedUpdate() => ShouldRelocate();

            void OnDestroy()
            {
                if (_npc == null || _npc.IsDestroyed) return;

                _npc?.Kill();
            }

            void ShouldRelocate()
            {
                if (_npc == null || _npc.IsDestroyed) return;

                float distance = Vector3.Distance(transform.position, _targetDestination);

                bool moveback = distance >= 10 || distance >= _maxRoamDistance;

                if (_npc.AttackTarget == null && moveback || _npc.AttackTarget != null && moveback)
                {
                    if (_npc.GetNavAgent == null || !_npc.GetNavAgent.isOnNavMesh)
                        _npc.finalDestination = _targetDestination;
                    else
                        _npc.GetNavAgent.SetDestination(_targetDestination);

                    _npc.Destination = _targetDestination;
                    _npc.SetFact(NPCPlayerApex.Facts.Speed, moveback ? (byte)NPCPlayerApex.SpeedEnum.Sprint : (byte)NPCPlayerApex.SpeedEnum.Walk, true, true);
                }
            }
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
        [ConsoleCommand("gcreset")]
        void GGRespawn(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg?.Player();
            if (player != null)
            {
                arg.ReplyWith("You do not have permission to do that.");
                return;
            }

            _manager.ResetEvent();

            arg.ReplyWith("Event reset.");
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