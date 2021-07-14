using System.Collections.Generic;
using System.Collections;
using System;
using Newtonsoft.Json;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Rust;
using UnityEngine;
using UnityEngine.AI;
using VLB;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.5.0")]
    [Description("Spawns hackable crate events at random locations guarded by scientists.")]
    public class GuardedCrate : CovalencePlugin
    {
        #region Fields

        private const string USE_PERM = "guardedcrate.use";

        private const string CRATE_PREFAB = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        private const string MARKER_PREFAB = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string CHUTE_PREFAB = "assets/prefabs/misc/parachute/parachute.prefab";
        private const string NPC_PREFAB = "assets/prefabs/npc/scientist/scientist.prefab";
        private const string PLANE_PREFAB = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";

        private readonly List<int> _blockedLayers = new List<int>
        {
            (int) Layer.Water, (int) Layer.Construction, (int) Layer.Trigger, (int) Layer.Prevent_Building,
            (int) Layer.Deployed, (int) Layer.Tree, (int) Layer.Clutter
        };

        private readonly int _obstructionLayer = LayerMask.GetMask("Player (Server)", "Construction", "Deployed");
        private readonly int _heightLayer = LayerMask.GetMask("Terrain", "World", "Default", "Construction", "Deployed", "Clutter");
        private readonly Dictionary<BaseEntity, CrateEvent> _entities = new Dictionary<BaseEntity, CrateEvent>();
        private readonly HashSet<CrateEvent> _events = new HashSet<CrateEvent>();
        private readonly List<Monument> _monuments = new List<Monument>();
        private readonly SpawnFilter _filter = new SpawnFilter();
        private PluginConfig _config;
        private PluginData _stored;

        #endregion

        #region Config

        protected override void LoadDefaultConfig() => _config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<PluginConfig>();

                if (_config == null)
                {
                    throw new Exception();
                }

                if (_config.ToDictionary().Keys
                    .SequenceEqual(Config.ToDictionary(x => x.Key, x => x.Value).Keys)) return;

                PrintWarning("Loaded updated config.");

                SaveConfig();
            }
            catch
            {
                PrintError("The configuration file contains an error and has been replaced with a default config.\n" +
                           "The error configuration file was saved in the .jsonError extension");

                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        private class PluginConfig : SerializableConfiguration
        {
            [JsonProperty("EnableAutoStart (enables events to spawn automatically)")]
            public bool EnableAutoStart = true;

            [JsonProperty("EventDuration (time between event spawns)")]
            public float EventDuration = 1800f;

            [JsonProperty("Command (command name)")]
            public string[] Command = {"gc"};
        }

        private class SerializableConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() =>
                JsonConvert.DeserializeObject<Dictionary<string, object>>(ToJson());
        }

        #endregion
        
        #region Storage

        private class PluginData
        {
            public readonly List<EventSetting> Events = new List<EventSetting>();

            public static PluginData LoadData()
            {
                PluginData data;

                try
                {
                    data = Interface.Oxide.DataFileSystem.ReadObject<PluginData>("GuardedCrate");

                    if (data == null)
                    {
                        throw new JsonException();
                    }
                }
                catch (Exception e)
                {
                    data = new PluginData();
                }

                return data;
            }

            public void Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject("GuardedCrate", this);
            }

            public EventSetting FindEvent(string name)
            {
                return Events.FirstOrDefault(x => x.EventName.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }

        private class EventSetting
        {
            [JsonProperty("EventDuration (duration the event will be active for)")]
            public float EventDuration;

            [JsonProperty("EventName (event name)")]
            public string EventName;

            [JsonProperty("AutoHack (enables auto hacking of crates when an event is finished)")]
            public bool AutoHack = true;

            [JsonProperty("AutoHackSeconds (countdown for crate to unlock in seconds)")]
            public float AutoHackSeconds = 60f;

            [JsonProperty("UseKits (use custom kits plugin)")]
            public bool UseKits = false;

            [JsonProperty("Kits (custom kits)")] public List<string> Kits = new List<string>();

            [JsonProperty("NpcName (custom name)")]
            public string NpcName;

            [JsonProperty("NpcCount (number of guards to spawn)")]
            public int NpcCount;

            [JsonProperty("NpcHealth (health guards spawn with)")]
            public float NpcHealth;

            [JsonProperty("NpcRadius (max distance guards will roam)")]
            public float NpcRadius;

            [JsonProperty("NpcAggression (max aggression distance guards will target)")]
            public float NpcAggression;

            [JsonProperty("MarkerColor (marker color)")]
            public string MarkerColor;

            [JsonProperty("MarkerBorderColor (marker border color)")]
            public string MarkerBorderColor;

            [JsonProperty("MarkerOpacity (marker opacity)")]
            public float MarkerOpacity = 1f;

            [JsonProperty("UseLoot (use custom loot table)")]
            public bool UseLoot = false;

            [JsonProperty("MaxLootItems (max items to spawn in crate)")]
            public int MaxLootItems = 6;

            [JsonProperty("CustomLoot (items to spawn in crate)")]
            public List<LootItem> CustomLoot = new List<LootItem>();
        }

        private class LootItem
        {
            public string Shortname;
            public int MinAmount;
            public int MaxAmount;

            public Item CreateItem() => ItemManager.CreateByName(Shortname, Random.Range(MinAmount, MaxAmount));
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"InvalidSyntax", "/gc start <name>\nedit <name> loot|duration\n/gc stop"},
                {"Permission", "No permission"},
                {"CreateEvent", "<color=#DC143C>Guarded Crate</color>: New event starting stand by."},
                {"CleanEvents", "<color=#DC143C>Guarded Crate</color>: Cleaning up events."},
                {
                    "EventStarted",
                    "<color=#DC143C>Guarded Crate</color>: <color=#EDDf45>{0}</color>, event started at <color=#EDDf45>{1}</color>, eliminate the guards before they leave in <color=#EDDf45>{2}</color>."
                },
                {
                    "EventEnded",
                    "<color=#DC143C>Guarded Crate</color>: The event ended at the location <color=#EDDf45>{0}</color>, <color=#EDDf45>{1}</color> cleared the event!"
                },
                {
                    "EventClear",
                    "<color=#DC143C>Guarded Crate</color>: The event ended at <color=#EDDf45>{0}</color>; You were not fast enough; better luck next time!"
                },
            }, this);
        }

        #endregion

        #region Oxide

        private void OnServerInitialized()
        {
            AddCovalenceCommand(_config.Command, nameof(GCCommand), USE_PERM);

            LoadMonuments();

            if (_config.EnableAutoStart)
            {
                timer.Every(_config.EventDuration, () => StartEvent());
            }

            timer.Every(30f, RefreshEvents);
        }

        private void Init()
        {
            _stored = PluginData.LoadData();

            RegisterDefaults();
        }

        private void Unload()
        {
            StopEvents();
        }

        private void OnEntityDeath(NPCPlayerApex npc, HitInfo hitInfo) =>
            FindEntityEvent(npc)
                ?.OnNPCDeath(npc, hitInfo?.InitiatorPlayer);

        private void OnEntityKill(NPCPlayerApex npc) =>
            FindEntityEvent(npc)
                ?.OnNPCDeath(npc, null);

        private void OnCrateLanded(HackableLockedCrate crate) =>
            FindEntityEvent(crate)
                ?.StartRoutine();

        private object CanHackCrate(BasePlayer player, HackableLockedCrate crate)
        {
            return FindEntityEvent(crate)
                ?.OnCanHackCrate();
        }

        #endregion

        #region Core

        #region Register Defaults

        private void RegisterDefaults()
        {
            if (_stored.Events == null || _stored.Events.Count != 0)
            {
                return;
            }

            _stored.Events.Add(new EventSetting
            {
                EventDuration = 800f,
                EventName = "Easy",
                NpcAggression = 120f,
                NpcRadius = 15f,
                NpcCount = 6,
                NpcHealth = 100,
                NpcName = "Easy Guard",
                MarkerColor = "#32a844",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });

            _stored.Events.Add(new EventSetting
            {
                EventDuration = 800f,
                EventName = "Medium",
                NpcAggression = 120f,
                NpcRadius = 15f,
                NpcCount = 8,
                NpcHealth = 150,
                NpcName = "Medium Guard",
                MarkerColor = "#eddf45",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });

            _stored.Events.Add(new EventSetting
            {
                EventDuration = 1800f,
                EventName = "Hard",
                NpcAggression = 150f,
                NpcRadius = 50f,
                NpcCount = 10,
                NpcHealth = 200,
                NpcName = "Hard Guard",
                MarkerColor = "#3060d9",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });

            _stored.Events.Add(new EventSetting
            {
                EventDuration = 1800f,
                EventName = "Elite",
                NpcAggression = 180f,
                NpcRadius = 50f,
                NpcCount = 12,
                NpcHealth = 350,
                NpcName = "Elite Guard",
                MarkerColor = "#e81728",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });

            _stored.Save();
        }

        #endregion

        #region Management

        private void StartEvent(string name = null)
        {
            EventSetting settings = _stored.FindEvent(name);

            new CrateEvent(this).SetupEvent(settings ?? _stored.Events.GetRandom());
        }

        private void StopEvents()
        {
            CommunityEntity.ServerInstance.StartCoroutine(DespawnRoutine());
        }

        private void RefreshEvents()
        {
            for (int i = 0; i < _events.Count; i++)
            {
                CrateEvent crateEvent = _events.ElementAt(i);

                crateEvent?.RefreshEvent();
            }
        }

        #endregion

        #region Cleanup

        private IEnumerator DespawnRoutine()
        {
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                CrateEvent crateEvent = _events.ElementAt(i);

                crateEvent?.StopEvent();

                yield return CoroutineEx.waitForSeconds(0.25f);
            }
        }

        #endregion

        #region Cache

        private CrateEvent FindEntityEvent(BaseEntity entity)
        {
            CrateEvent crateEvent;

            return _entities.TryGetValue(entity, out crateEvent) ? crateEvent : null;
        }

        private void AddEntity(BaseEntity entity, CrateEvent crateEvent)
        {
            _entities.Add(entity, crateEvent);
        }

        private void DelEntity(BaseEntity entity)
        {
            _entities.Remove(entity);
        }

        private void AddEvent(CrateEvent crateEvent)
        {
            _events.Add(crateEvent);
        }

        private void DelEvent(CrateEvent crateEvent)
        {
            _events.Remove(crateEvent);
        }

        #endregion

        #region Position

        private Vector3 GetRandomPosition()
        {
            Vector3 vector;

            float num = 100f;

            do
            {
                vector = ValidPosition(FindNewPosition());
            } while (vector == Vector3.zero && --num > 0f);

            return vector;
        }

        private Vector3 FindNewPosition()
        {
            Vector3 vector;

            float num = 100f;
            float x = TerrainMeta.Size.x / 3f;

            do
            {
                vector = Vector3Ex.Range(-x, x);
            } while (_filter.GetFactor(vector) == 0f && --num > 0f);

            vector.y = 0f;

            return vector;
        }

        private Vector3 ValidPosition(Vector3 position)
        {
            RaycastHit hit;

            position.y += 200f;

            if (!Physics.Raycast(position, Vector3.down, out hit, position.y, _heightLayer,
                QueryTriggerInteraction.Ignore))
            {
                return Vector3.zero;
            }

            if (_blockedLayers.Contains(hit.collider.gameObject.layer))
            {
                return Vector3.zero;
            }

            position.y = Mathf.Max(hit.point.y, TerrainMeta.HeightMap.GetHeight(position));

            if (NearMonument(position))
            {
                return Vector3.zero;
            }

            if (WaterLevel.GetWaterDepth(position) > 0.1f)
            {
                return Vector3.zero;
            }

            return !IsLayerBlocked(position, 25f, _obstructionLayer) ? position : Vector3.zero;
        }

        private bool IsLayerBlocked(Vector3 position, float radius, int mask)
        {
            List<Collider> colliders = Facepunch.Pool.GetList<Collider>();

            Vis.Colliders(position, radius, colliders, mask, QueryTriggerInteraction.Ignore);

            bool blocked = colliders.Count > 0;

            Facepunch.Pool.FreeList(ref colliders);

            return blocked;
        }

        #endregion

        #region Monument

        private void LoadMonuments()
        {
            foreach (MonumentInfo monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                _monuments.Add(new Monument(monument));
            }
        }
        
        private bool NearMonument(Vector3 position)
        {
            foreach (Monument monument in _monuments)
            {
                if (Vector3.Distance(position, monument.Position) < monument.Size.z)
                {
                    return true;
                }
            }

            return false;
        }
        
        private class Monument
        {
            public Vector3 Position;
            public Vector3 Size;

            public Monument(MonumentInfo monumentInfo)
            {
                Position = monumentInfo.transform.position;

                Vector3 size = monumentInfo.Bounds.extents;

                if (size.z < 50f)
                {
                    size.z = 120f;
                }

                Size = size;
            }
        }

        #endregion
        
        private class CrateEvent
        {
            #region Fields

            private readonly HashSet<NPCPlayerApex> _players = new HashSet<NPCPlayerApex>();
            private readonly HashSet<string> _blocked = new HashSet<string>();

            private MapMarkerGenericRadius _marker;
            private HackableLockedCrate _crate;
            private Coroutine _coroutine;
            private CargoPlane _plane;
            private Vector3 _position;
            private Timer _timer;
            private GuardedCrate _plugin;
            private EventSetting _settings;

            #endregion

            public CrateEvent(GuardedCrate plugin)
            {
                _plugin = plugin;
            }

            #region State Management

            public void SetupEvent(EventSetting settings)
            {
                _settings = settings;
                _position = _plugin.GetRandomPosition(); /*new Vector3(-1804f, 15.9f, -1574f);*/

                if (_position == Vector3.zero)
                {
                    return;
                }

                SpawnPlane();

                _plugin?.AddEvent(this);
            }

            public void StartEvent()
            {
                SpawnMarker();
                SpawnCrate();
                ResetTimer();

                _plugin.MessageAll("EventStarted", _settings.EventName, GetGrid(_position),
                    GetTime((int) _settings.EventDuration));
            }

            public void StopEvent(bool completed = false)
            {
                _timer?.Destroy();

                StopRoutine();
                DespawnPlane();
                DespawnCrate(completed);
                DespawnAI();

                _plugin?.DelEvent(this);
            }

            public void RefreshEvent()
            {
                if (!IsValid(_marker))
                {
                    return;
                }

                _marker.SendUpdate();
            }

            #endregion

            #region Coroutine

            public void StartRoutine()
            {
                _coroutine = CommunityEntity.ServerInstance.StartCoroutine(SpawnAI());
            }

            private void StopRoutine()
            {
                if (_coroutine != null)
                {
                    CommunityEntity.ServerInstance.StopCoroutine(_coroutine);
                }

                _coroutine = null;
            }

            #endregion

            #region Timer

            private void ResetTimer()
            {
                _timer?.Destroy();
                _timer = _plugin.timer.Once(_settings.EventDuration, () => StopEvent());
            }

            #endregion

            #region Cache

            void CacheAdd(NPCPlayerApex player)
            {
                _players.Add(player);

                _plugin.AddEntity(player, this);
            }

            void CacheDel(NPCPlayerApex player)
            {
                _players.Remove(player);

                _plugin.DelEntity(player);
            }

            #endregion

            #region Plane

            private void SpawnPlane()
            {
                _plane = (CargoPlane) GameManager.server.CreateEntity(PLANE_PREFAB);
                _plane.InitDropPosition(_position);
                _plane.Spawn();
                _plane.gameObject.GetOrAddComponent<CargoComponent>().CrateEvent = this;
            }

            #endregion

            #region Marker

            private void SpawnMarker()
            {
                _marker = (MapMarkerGenericRadius) GameManager.server.CreateEntity(MARKER_PREFAB, _position);
                _marker.enableSaving = false;
                _marker.color1 = GetColor(_settings.MarkerColor);
                _marker.color2 = GetColor(_settings.MarkerBorderColor);
                _marker.alpha = _settings.MarkerOpacity;
                _marker.radius = 0.5f;
                _marker.Spawn();
            }

            #endregion

            #region Crate

            private void SpawnCrate()
            {
                _crate = (HackableLockedCrate) GameManager.server.CreateEntity(CRATE_PREFAB, _position,
                    Quaternion.identity);
                _crate.enableSaving = false;
                _crate.shouldDecay = false;
                _crate.Spawn();
                _crate.gameObject.AddComponent<CrateComponent>();

                _marker.SetParent(_crate);
                _marker.transform.localPosition = Vector3.zero;
                _marker.SendUpdate();

                RefillCrate();

                _plugin.AddEntity(_crate, this);
            }

            #endregion

            #region AI

            private IEnumerator SpawnAI()
            {
                for (int i = 0; i < _settings.NpcCount; i++)
                {
                    Vector3 position = FindPointOnNavmesh(_position);

                    if (position == Vector3.zero) continue;

                    SpawnNpc(position, Quaternion.LookRotation(position - _position));

                    yield return CoroutineEx.waitForSeconds(0.25f);
                }
            }

            private void SpawnNpc(Vector3 position, Quaternion rotation)
            {
                NPCPlayerApex npc = (NPCPlayerApex) GameManager.server.CreateEntity(NPC_PREFAB, position, rotation);
                npc.enableSaving = false;
                npc.RadioEffect = new GameObjectRef();
                npc.DeathEffect = new GameObjectRef();
                npc.displayName = _settings.NpcName;
                npc.startHealth = _settings.NpcHealth;
                npc.InitializeHealth(_settings.NpcHealth, _settings.NpcHealth);
                npc.Spawn();

                npc.Stats.VisionRange = _settings.NpcAggression + 3f;
                npc.Stats.DeaggroRange = _settings.NpcAggression + 2f;
                npc.Stats.AggressionRange = _settings.NpcAggression + 1f;
                npc.Stats.LongRange = _settings.NpcAggression;
                npc.Stats.MaxRoamRange = _settings.NpcRadius;
                npc.Stats.Hostility = 1f;
                npc.Stats.Defensiveness = 1f;
                npc.Stats.OnlyAggroMarkedTargets = true;
                npc.InitFacts();

                npc.gameObject.AddComponent<NavigationComponent>()
                    ?.SetDestination(position);

                CacheAdd(npc);

                GiveKit(npc, _settings.Kits.GetRandom(), _settings.UseKits);
            }

            private Vector3 FindPointOnNavmesh(Vector3 position, float radius = 8f)
            {
                int num = 100;

                NavMeshHit hit;
                Vector3 vector;

                do
                {
                    vector = position + Random.insideUnitSphere * (radius * 0.9f);

                    if (NavMesh.SamplePosition(vector, out hit, radius, 1))
                    {
                        return GetSpawnHeight(hit.position);
                    }
                } while (--num > 0);

                return Vector3.zero;
            }

            private Vector3 GetSpawnHeight(Vector3 target)
            {
                float y = TerrainMeta.HeightMap.GetHeight(target);
                float p = TerrainMeta.HighestPoint.y + 250f;

                RaycastHit hit;

                if (Physics.Raycast(new Vector3(target.x, p, target.z), Vector3.down, out hit, target.y + p,
                    Layers.Mask.World, QueryTriggerInteraction.Ignore))
                {
                    y = Mathf.Max(y, hit.point.y);
                }

                return new Vector3(target.x, y, target.z);
            }

            #endregion

            #region Loot

            private List<LootItem> GenerateLoot()
            {
                int num = 100;

                List<LootItem> lootItems = new List<LootItem>();

                do
                {
                    LootItem lootItem = _settings.CustomLoot.GetRandom();

                    if (lootItems.Contains(lootItem)) continue;

                    lootItems.Add(lootItem);
                } while (lootItems.Count < _settings.MaxLootItems && --num > 0);

                return lootItems;
            }

            private void RefillCrate()
            {
                if (!_settings.UseLoot || _settings.CustomLoot.Count <= 0)
                {
                    return;
                }

                List<LootItem> lootItems = GenerateLoot();

                _crate.inventory.Clear();
                _crate.inventory.capacity = lootItems.Count;
                ItemManager.DoRemoves();

                foreach (LootItem lootItem in lootItems)
                {
                    lootItem.CreateItem()
                        ?.MoveToContainer(_crate.inventory);
                }

                lootItems.Clear();
            }

            #endregion

            #region Cleanup

            private void DespawnCrate(bool completed = false)
            {
                if (!IsValid(_crate))
                {
                    return;
                }

                if (!completed)
                {
                    _crate.Kill();

                    return;
                }

                if (_settings.AutoHack)
                {
                    _crate.hackSeconds = HackableLockedCrate.requiredHackSeconds - _settings.AutoHackSeconds;
                    _crate.StartHacking();
                }

                _crate.shouldDecay = true;
                _crate.RefreshDecay();
            }

            private void DespawnPlane()
            {
                if (!IsValid(_plane))
                {
                    return;
                }

                _plane.Kill();
            }

            private void DespawnAI()
            {
                foreach (NPCPlayerApex npc in _players.ToList())
                {
                    if (!IsValid(npc))
                    {
                        continue;
                    }

                    npc.Kill();
                }

                _players.Clear();
            }

            #endregion

            #region Oxide Hooks

            public void OnNPCDeath(NPCPlayerApex npc, BasePlayer player)
            {
                CacheDel(npc);

                if (_players.Count > 0)
                {
                    ResetTimer();

                    return;
                }

                if (player != null)
                {
                    _plugin.MessageAll("EventEnded", GetGrid(_position), player.displayName);

                    StopEvent(true);
                }
                else
                {
                    _plugin.MessageAll("EventClear", GetGrid(_position));

                    StopEvent();
                }
            }

            public object OnCanHackCrate()
            {
                if (_players.Count > 0)
                {
                    return false;
                }

                return null;
            }

            #endregion
        }

        #endregion

        #region Component

        private class NavigationComponent : MonoBehaviour
        {
            private NPCPlayerApex _npc;
            private Vector3 _targetPoint;

            private void Awake()
            {
                _npc = gameObject.GetComponent<NPCPlayerApex>();

                InvokeRepeating(nameof(Relocate), 0f, 5f);
            }

            private void OnDestroy()
            {
                CancelInvoke();

                if (_npc.IsValid() && !_npc.IsDestroyed)
                {
                    _npc.Kill();
                }
            }

            public void SetDestination(Vector3 position)
            {
                _targetPoint = position;
            }

            private void Relocate()
            {
                if (_npc == null || _npc.IsDestroyed)
                {
                    return;
                }

                if (_npc.isMounted)
                {
                    return;
                }

                if (!(_npc.AttackTarget == null || IsOutOfBounds()))
                {
                    return;
                }

                if (_npc.IsStuck)
                {
                    DoWarp();
                }

                if (_npc.GetNavAgent == null || !_npc.GetNavAgent.isOnNavMesh)
                {
                    _npc.finalDestination = _targetPoint;
                }
                else
                {
                    _npc.GetNavAgent.SetDestination(_targetPoint);
                    _npc.IsDormant = false;
                }

                _npc.IsStopped = false;
                _npc.Destination = _targetPoint;
            }

            private bool IsOutOfBounds()
            {
                return _npc.AttackTarget != null &&
                       Vector3.Distance(transform.position, _targetPoint) > _npc.Stats.MaxRoamRange;
            }

            private void DoWarp()
            {
                _npc.Pause();
                _npc.ServerPosition = _targetPoint;
                _npc.GetNavAgent.Warp(_targetPoint);
                _npc.stuckDuration = 0f;
                _npc.IsStuck = false;
                _npc.Resume();
            }
        }

        private class CargoComponent : MonoBehaviour
        {
            public CrateEvent CrateEvent;
            private CargoPlane _plane;
            private bool _hasDropped;

            private void Awake()
            {
                _plane = GetComponent<CargoPlane>();
                _plane.dropped = true;
            }

            private void Update()
            {
                float time = Mathf.InverseLerp(0.0f, _plane.secondsToTake, _plane.secondsTaken);

                if (_hasDropped || !((double) time >= 0.5)) return;

                _hasDropped = true;

                CrateEvent?.StartEvent();

                Destroy(this);
            }
        }

        private class CrateComponent : MonoBehaviour
        {
            private BaseEntity _chute;
            private BaseEntity _crate;
            private bool _hasLanded;

            private void Awake()
            {
                _crate = gameObject.GetComponent<BaseEntity>();
                _crate.GetComponent<Rigidbody>().drag = 0.5f;

                SpawnChute();
            }

            private void FixedUpdate()
            {
                if (_hasLanded)
                {
                    return;
                }

                int size = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up * 0.5f, 1f, Vis.colBuffer,
                    1218511105);

                if (size <= 0)
                {
                    return;
                }

                _hasLanded = true;

                RemoveChute();

                Interface.CallHook("OnCrateLanded", _crate);
            }

            private void SpawnChute()
            {
                _chute = GameManager.server.CreateEntity(CHUTE_PREFAB, transform.position, Quaternion.identity);
                _chute.enableSaving = false;
                _chute.Spawn();
                _chute.SetParent(_crate);
                _chute.transform.localPosition = Vector3.zero;
                _chute.SendNetworkUpdate();
            }

            private void RemoveChute()
            {
                if (!IsValid(_chute))
                {
                    return;
                }

                _chute.Kill();
                _chute = null;
            }
        }

        #endregion

        #region Helpers

        private string Lang(string key, string id = null, params object[] args) =>
            string.Format(lang.GetMessage(key, this, id), args);

        private void MessageAll(string key, params object[] args)
        {
            server.Broadcast(Lang(key, null, args));
        }

        private static string GetGrid(Vector3 position)
        {
            Vector2 r = new Vector2(World.Size / 2 + position.x, World.Size / 2 + position.z);
            float x = Mathf.Floor(r.x / 146.3f) % 26;
            float z = Mathf.Floor(World.Size / 146.3f) - Mathf.Floor(r.y / 146.3f);

            return $"{(char) ('A' + x)}{z - 1}";
        }

        private static Color GetColor(string hex)
        {
            Color color;

            return ColorUtility.TryParseHtmlString(hex, out color) ? color : Color.yellow;
        }

        private static string GetTime(int seconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(seconds);

            return $"{time.Hours:D2}h:{time.Minutes:D2}m:{time.Seconds:D2}s";
        }

        private static void GiveKit(NPCPlayerApex npc, string kit, bool giveKit)
        {
            if (!giveKit)
            {
                return;
            }

            Interface.Oxide.CallHook("GiveKit", npc, kit);

            npc.Invoke(npc.EquipWeapon, 2f);
        }

        private static bool IsValid(BaseEntity entity)
        {
            return entity != null && !entity.IsDestroyed;
        }

        #endregion

        #region Command Methods

        private void StartEvent(IPlayer player, string[] args)
        {
            if (args.Length == 0)
            {
                player.Message(Lang("InvalidSyntax", player.Id));

                return;
            }

            StartEvent(string.Join(" ", args));

            player.Message(Lang("CreateEvent", player.Id));
        }

        private void EditEvent(IPlayer player, string[] args)
        {
            if (args.Length == 0)
            {
                player.Message(Lang("InvalidSyntax", player.Id));
                return;
            }
            
            //
        }

        private void StopEvents(IPlayer player)
        {
            StopEvents();

            player.Message(Lang("CleanEvents", player.Id));
        }

        #endregion

        #region Command

        private void GCCommand(IPlayer player, string command, string[] args)
        {
            if (args.Length < 1)
            {
                player.Message(Lang("InvalidSyntax", player.Id));
                return;
            }

            switch (args[0].ToLower())
            {
                case "start":
                    StartEvent(player, args.Skip(1).ToArray());
                    break;
                case "edit":
                    EditEvent(player, args.Skip(1).ToArray());
                    break;
                case "stop":
                    StopEvents(player);
                    break;
                default:
                    player.Message(Lang("InvalidSyntax", player.Id));
                    break;
            }
        }

        #endregion
    }
}