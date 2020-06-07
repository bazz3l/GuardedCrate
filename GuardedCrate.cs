using System.Collections.Generic;
using System;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using Oxide.Core;
using UnityEngine;
using Rust.Ai.HTN;
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
        const string _npcPrefab = "assets/prefabs/npc/scientist/htn/scientist_full_any.prefab";

        readonly int _blockedLayers = LayerMask.GetMask("Player (Server)", "Construction", "Deployed", "Clutter");
        readonly int _allowedLayers = LayerMask.GetMask("Terrain");
        readonly List<int> _blockedLayers = new List<int> {
            (int)Layer.Water,
            (int)Layer.Trigger,
            (int)Layer.Construction,
            (int)Layer.Prevent_Building,
            (int)Layer.Deployed,
            (int)Layer.Tree,
            (int)Layer.Clutter
        };

        List<MonumentInfo> _monuments { get { return TerrainMeta.Path.Monuments; } }
        SpawnFilter _filter = new SpawnFilter();
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
            public float ChaseDistance = 150f;

            [JsonProperty(PropertyName = "UseKit (should use kit)")]
            public bool UseKit = false;

            public GuardSetting(string name, string kit, float health = 100f, float minRoamRadius = 20f, float maxRoamRadius = 80f)
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
            _manager.ResetEvent();
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

        void OnEntityDeath(HTNPlayer npc, HitInfo info) => _manager.RemoveNPC(npc);
        #endregion

        #region Core
        class EventManager
        {
            List<GuardSetting> _guardSettings = new List<GuardSetting>();            
            List<HTNPlayer> _guards = new List<HTNPlayer>();
            Vector3 _eventPosition = Vector3.zero;
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

                _eventRepeatTimer = Instance.timer.Every(_eventTime, () => StartEvent(Instance.GetRandomPosition()));
            }

            public void StartEvent(Vector3 position)
            {
                if (position == Vector3.zero || IsEventActive()) return;

                SpawnPlane(position);

                _eventTimer = Instance.timer.Once(_eventDuration, () => ResetEvent());

                MessageAll("<color=#DC143C>Guarded Crate</color>: Prepare for coordinates.");
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
                return _eventActive && Vector3Ex.Distance2D(_eventPosition, position) <= 20f;
            }

            public void RemoveNPC(HTNPlayer npc)
            {
                if (!_guards.Contains(npc)) return;

                _guards.Remove(npc);

                if (IsEventActive()) return;

                ResetEvent(true);

                MessageAll($"<color=#DC143C>Guarded Crate</color>: Event completed, crate is now open loot up fast.");
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

                _crate = null;
            }

            void DestroyTimers()
            {
                _eventTimer?.Destroy();
                _eventRepeatTimer?.Destroy();
            }

            public void SpawnEvent(Vector3 position)
            {
                _eventPosition = position;

                _eventActive = true;

                SpawnCreate();

                Instance.timer.In(30f, () => SingletonComponent<ServerMgr>.Instance.StartCoroutine(SpawnAI()));

                MessageAll($"<color=#DC143C>Guarded Crate</color>: Guards with valuable cargo arriving at ({GetGrid(_eventPosition)}) ETA 30 seconds! Prepare to attack or run for your life.");
            }

            public void SpawnPlane(Vector3 position)
            {
                CargoPlane cargoplane = GameManager.server.CreateEntity(_cargoPrefab)?.GetComponent<CargoPlane>();
                if (cargoplane != null)
                {
                    cargoplane.InitDropPosition(position);
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
                MapMarkerGenericRadius marker = GameManager.server.CreateEntity(_markerPrefab, _eventPosition)?.GetComponent<MapMarkerGenericRadius>();
                if (marker != null)
                {
                    marker.enableSaving = false;
                    marker.alpha  = 0.8f;
                    marker.color1 = Color.red;
                    marker.color2 = Color.white;
                    marker.radius = 0.6f;
                    marker.Spawn();
                    marker.transform.localPosition = Vector3.zero;
                    marker.SendUpdate(true);
                }
                else
                {
                    marker.Kill(BaseEntity.DestroyMode.None);
                }

                _crate = GameManager.server.CreateEntity(_cratePrefab, _eventPosition, Quaternion.identity)?.GetComponent<HackableLockedCrate>();
                if (_crate != null)
                {
                    _crate.enableSaving = false;
                    _crate.SetWasDropped();
                    _crate.Spawn();
                    _crate.gameObject.AddComponent<ParachuteComponent>();

                    marker?.SetParent(_crate);
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

                HTNPlayer component = entity.GetComponent<HTNPlayer>();
                if (component != null)
                {
                    entity.enableSaving = false;
                    entity.Spawn();

                    component.startHealth = settings.Health;
                    component.InitializeHealth(component.startHealth, component.startHealth);
                    component.displayName = settings.Name;
                    component._aiDomain.MovementRadius = settings.GetRoamRange();
                    component._aiDomain.Movement = HTNDomain.MovementRule.FreeMove;

                    _guards.Add(component);

                    GiveKit(component, settings.Kit, settings.UseKit);
                }
                else
                {
                    entity.Kill(BaseNetworkable.DestroyMode.None);
                }
            }

            void GiveKit(HTNPlayer npc, string kit, bool give)
            {
                if (!give) return;

                npc.inventory.Strip();

                Interface.Oxide.CallHook("GiveKit", npc, kit);
            }

            public void TrySpawnNPC(int num)
            {
                Vector3 position = Instance.RandomCircle(_eventPosition, 10f, (360 / Instance._config.GuardMaxSpawn * num));

                position = Instance.GetValidLocation(position);

                if (position == Vector3.zero) return;

                SpawnNPC(GetRandomNPC(), position, Quaternion.FromToRotation(Vector3.forward, _eventPosition));
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
                _entity.GetComponent<Rigidbody>().drag = 1.2f;
            }

            void OnCollisionEnter(Collision col)
            {
                if (_parachute != null && !_parachute.IsDestroyed)
                {
                    _parachute?.Kill();
                    _parachute = null;
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

        Vector3 GetRandomPosition()
        {
            Vector3 position = Vector3.zero;

            float num = 100f;
            float x = TerrainMeta.Size.x / 3f;

            do
            {
                position = Vector3Ex.Range(-x, x);
            }
            while (_filter.GetFactor(position) == 0f && (num -= 1f) > 0f);

            position.y = 0f;

            return position;
        }

        Vector3 GetEventPosition()
        {
            Vector3 position = Vector3.zero;

            int maxTries = 200;

            do
            {
                position = GetValidLocation(GetRandomPosition());

                if (position == Vector3.zero) continue;

            } while(position == Vector3.zero && --maxTries > 0);

            return position;
        }

        Vector3 GetValidLocation(Vector3 position)
        {
            RaycastHit hit;

            if (position == Vector3.zero)
            {
                return Vector3.zero;
            }

            position.y += 250f;

            if (Physics.Raycast(position, Vector3.down, out hit, Mathf.Infinity, _allowedLayers, QueryTriggerInteraction.Ignore))
            {
                position = hit.point;

                if (_blockedLayers.Contains(hit.collider.gameObject.layer))
                {
                    return Vector3.zero;
                }

                if (IsLayerBlocked(position, 80f, _blockedLayers) || InMonumentBounds(position) || InOrOnRock(position, "rock_"))
                {
                    return Vector3.zero;
                }

                if (WaterLevel.Test(position))
                {
                    return Vector3.zero;
                }

                return position;
            }
            
            return Vector3.zero;
        }

        bool IsLayerBlocked(Vector3 position, float radius, int mask)
        {
            List<Collider> colliders = new List<Collider>();
            Vis.Colliders<Collider>(position, radius, colliders, mask, QueryTriggerInteraction.Ignore);

            colliders.RemoveAll(collider => (collider.ToBaseEntity()?.IsNpc ?? false) || !(collider.ToBaseEntity()?.OwnerID.IsSteamId() ?? true));

            bool blocked = colliders.Count > 0;

            colliders.Clear();
            colliders = null;

            return blocked;
        }

        bool IsRockTooLarge(Bounds bounds, float extents = 1.5f)
        {
            return bounds.extents.Max() > extents;
        }

        bool InOrOnRock(Vector3 position, string meshName, float radius = 2f)
        {
            bool flag = false;

            int hits = Physics.OverlapSphereNonAlloc(position, radius, Vis.colBuffer, Layers.Mask.World, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits; i++)
            {
                if (Vis.colBuffer[i].name.StartsWith(meshName) && IsRockTooLarge(Vis.colBuffer[i].bounds))
                {
                    flag = true;
                }

                Vis.colBuffer[i] = null;
            }
            
            return flag;
        }

        bool InMonumentBounds(Vector3 position)
        {
            foreach (MonumentInfo monument in _monuments)
            {
                if (monument.IsInBounds(position))
                {
                    return true;
                }
            }

            return false;
        }
        
        static string GetGrid(Vector3 position)
        {
            char letter = 'A';

            float x = Mathf.Floor((position.x + (ConVar.Server.worldsize / 2)) / 146.3f) % 26;
            float z = Mathf.Floor(ConVar.Server.worldsize / 146.3f) - Mathf.Floor((position.z+(ConVar.Server.worldsize / 2)) / 146.3f);
            letter = (char)(((int)letter) + x);

            return $"{letter}{z}";
        }

        static void MessageAll(string message)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                player.ChatMessage(message);
            }
        }
        #endregion
    }
}