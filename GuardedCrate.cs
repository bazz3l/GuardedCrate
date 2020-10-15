using System.Collections.Generic;
using System.Collections;
using Newtonsoft.Json;
using System.Linq;
using Oxide.Core;
using Rust.Ai.HTN;
using UnityEngine;
using VLB;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.2.6")]
    [Description("Spawns hackable crates at a random location guarded by scientists.")]
    public class GuardedCrate : RustPlugin
    {
        /*
         * TODO Remove ability to build in event areas to prevent people walling/building off crates
         * Custom loot tables for crates/scientists
         */
        
        #region Fields

        private const string CratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        private const string MarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string ChutePrefab = "assets/prefabs/misc/parachute/parachute.prefab";
        private const string NpcPrefab = "assets/prefabs/npc/scientist/htn/scientist_full_any.prefab";
        private const string PlanePrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        private const string UsePerm = "guardedcrate.use";
        
        private static readonly LayerMask CollisionLayer = LayerMask.GetMask("Water", "Tree",  "Debris", "Clutter",  "Default", "Resource", "Construction", "Terrain", "World", "Deployed");
        private readonly HashSet<CrateEvent> _crateEvents = new HashSet<CrateEvent>();
        private PluginConfig _config;
        private static GuardedCrate _plugin;

        #endregion
        
        #region Config
        
        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                EventTiers = new Dictionary<EventTier, TierSetting>
                {
                    {
                        EventTier.Easy, new TierSetting
                        {
                            EventDuration = 1800f,
                            NpcAggression = 100f,
                            NpcRadius = 15f,
                            NpcCount = 6,
                            NpcHealth = 100,
                            MarkerColor = "#32a844"
                        }
                    },
                    {
                        EventTier.Medium, new TierSetting
                        {
                            EventDuration = 1800f,
                            NpcAggression = 120f,
                            NpcRadius = 25f,
                            NpcCount = 8,
                            NpcHealth = 150,
                            MarkerColor = "#e6aa20"
                        }
                    },
                    {
                        EventTier.Hard, new TierSetting
                        {
                            EventDuration = 1800f,
                            NpcAggression = 150f,
                            NpcRadius = 50f,
                            NpcCount = 10,
                            NpcHealth = 200,
                            MarkerColor = "#e81728"
                        }
                    }
                }
            };
        }

        private class PluginConfig
        {
            [JsonProperty(PropertyName = "AutoEvent (enables auto event spawns)")]
            public bool EnableAutoEvent = true;
            
            [JsonProperty(PropertyName = "AutoEventDuration (time until new event spawns)")]
            public float AutoEventDuration = 1800f;
            
            [JsonProperty(PropertyName = "EventTiers (specify different tiers)")]
            public Dictionary<EventTier, TierSetting> EventTiers = new Dictionary<EventTier, TierSetting>();
        }

        private class TierSetting
        {
            [JsonProperty(PropertyName = "EventDuration (duration the event should last for)")]
            public float EventDuration;

            [JsonProperty(PropertyName = "UseKits (use custom kits plugin)")]
            public bool UseKits;
            
            [JsonProperty(PropertyName = "KitName (custom kit name)")]
            public string KitName;
            
            [JsonProperty(PropertyName = "NpcCount (number of guards to spawn)")]
            public int NpcCount;
            
            [JsonProperty(PropertyName = "NpcHealth (health guards spawn with)")]
            public float NpcHealth;
            
            [JsonProperty(PropertyName = "NpcRadius (max distance guards will roam)")]
            public float NpcRadius;
            
            [JsonProperty(PropertyName = "NpcAggression (max aggression distance guards will target)")]
            public float NpcAggression;

            [JsonProperty(PropertyName = "MarkerColor (marker color for tier)")]
            public string MarkerColor;
            
            [JsonProperty(PropertyName = "CrateItems (items to spawn in crate)")]
            public Dictionary<string, int> CrateItems = new Dictionary<string, int>();
        }
        
        #endregion

        #region Oxide
        
        protected override void LoadDefaultConfig() => Config.WriteObject(GetDefaultConfig(), true);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages (new Dictionary<string, string>
            {
                { "InvalidSyntax", "gc start|stop" },
                { "Permission", "No permission" },
                { "EventStarted", "<color=#DC143C>Guarded Crate</color>: High value loot at {0}, fight the guards before they leave." },
                { "EventEnded", "<color=#DC143C>Guarded Crate</color>: Event ended at {0}, <color=#EDDf45>{1}</color>, cleared the event." },
                { "EventClear", "<color=#DC143C>Guarded Crate</color>: Event ended at {0}, You was not fast enough better luck next time." }
            }, this);
        }

        private void OnServerInitialized()
        {
            permission.RegisterPermission(UsePerm, this);
            
            if (!_config.EnableAutoEvent)
            {
                return;
            }
            
            timer.Every(_config.AutoEventDuration, () => StartEvent(null));
        }

        private void Init()
        {
            _plugin = this;
            _config = Config.ReadObject<PluginConfig>();
        }

        private void Unload() => StopEvents(null);

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            if (player.IsReceivingSnapshot)
            {
                timer.In(3f, () => OnPlayerConnected(player));
                return;
            }

            RefreshEvents();
        }
        
        private void OnEntityDeath(HTNPlayer npc, HitInfo hitInfo) => OnAIDeath(npc, hitInfo?.InitiatorPlayer);

        private void OnEntityKill(HTNPlayer npc) => OnAIDeath(npc, null);

        #endregion
        
        #region Core

        private enum EventTier
        {
            Easy,
            Medium,
            Hard
        };
        
        private void StartEvent(BasePlayer player)
        {
            KeyValuePair<EventTier, TierSetting> eventSettings = _config.EventTiers.ElementAtOrDefault(UnityEngine.Random.Range(0, _config.EventTiers.Count));

            CrateEvent crateEvent = new CrateEvent();

            crateEvent.PreEvent(eventSettings.Value);

            if (player == null)
            {
                return;
            }
            
            player.ChatMessage("Event starting soon, stand by for cords.");
        }

        private void StopEvents(BasePlayer player)
        {
            CommunityEntity.ServerInstance.StartCoroutine(DespawnRoutine());
            
            if (player == null)
            {
                return;
            }
            
            player.ChatMessage("Cleaning up events.");
        }

        private void RefreshEvents()
        {
            for (int i = 0; i < _crateEvents.Count; i++)
            {
                CrateEvent crateEvent = _crateEvents.ElementAt(i);

                crateEvent?.RefreshEvent();
            }
        }
        
        private IEnumerator DespawnRoutine()
        {
            for (int i = _crateEvents.Count - 1; i >= 0; i--)
            {
                CrateEvent crateEvent = _crateEvents.ElementAt(i);
                
                crateEvent.StopEvent();

                yield return new WaitForSeconds(0.75f);
            }
            
            yield return null;
        }

        private void AddEvent(CrateEvent crateEvent) => _crateEvents.Add(crateEvent);

        private void DelEvent(CrateEvent crateEvent) => _crateEvents.Remove(crateEvent);

        private void OnAIDeath(HTNPlayer npc, BasePlayer player)
        {
            CrateEvent crateEvent = _crateEvents.FirstOrDefault(x => x.NpcPlayers.Contains(npc));

            crateEvent?.OnNPCDeath(npc, player);
        }

        private class CrateEvent
        {
            public readonly List<HTNPlayer> NpcPlayers = new List<HTNPlayer>();
            private MapMarkerGenericRadius _marker;
            private HackableLockedCrate _crate;
            private CargoPlane _plane;
            private Vector3 _position;
            private Coroutine _coroutine;
            private Timer _eventTimer;
            private bool _eventCompleted;
            private TierSetting _eventSettings;

            public void PreEvent(TierSetting eventSettings)
            {
                _eventSettings = eventSettings;
                
                SpawnPlane();
                
                _plugin.AddEvent(this);
            }

            public void StartEvent(Vector3 position)
            {
                _position = position;

                SpawnCrate();
                StartSpawnRoutine();
                StartDespawnTimer();

                Message("EventStarted", GetGrid(_position));
            }

            public void StopEvent(bool completed = false)
            {
                _eventCompleted = completed;

                _eventTimer?.Destroy();
                
                StopSpawnRoutine();
                DespawnPlane();
                DespawnCrate();
                DespawnAI();

                _plugin.DelEvent(this);
            }

            public void RefreshEvent()
            {
                if (!IsValid(_marker))
                {
                    return;
                }
                
                _marker.SendUpdate();
            }

            private void StartSpawnRoutine()
            {
                _coroutine = CommunityEntity.ServerInstance.StartCoroutine(SpawnAI());
            }
            
            private void StopSpawnRoutine()
            {
                if (_coroutine != null)
                {
                    CommunityEntity.ServerInstance.StopCoroutine(_coroutine);
                }
                
                _coroutine = null;
            }

            private void StartDespawnTimer()
            {
                _eventTimer = _plugin.timer.Once(_eventSettings.EventDuration, () => StopEvent());
            }

            private void SpawnPlane()
            {
                _plane = GameManager.server.CreateEntity(PlanePrefab) as CargoPlane;
                if (_plane == null)
                {
                    return;
                }
                
                _plane.Spawn();
                _plane.gameObject.GetOrAddComponent<CargoComponent>().SetEvent(this);
            }
            
            private void SpawnCrate()
            {
                _crate = GameManager.server.CreateEntity(CratePrefab, _position, Quaternion.identity) as HackableLockedCrate;
                if (_crate == null)
                {
                    return;
                }
                
                _crate.enableSaving = false;
                _crate.SetWasDropped();
                _crate.Spawn();
                _crate.gameObject.GetOrAddComponent<DropComponent>();
                
                _marker = GameManager.server.CreateEntity(MarkerPrefab, _position) as MapMarkerGenericRadius;
                if (_marker == null)
                {
                    return;
                }

                Color color;

                ColorUtility.TryParseHtmlString(_eventSettings.MarkerColor, out color);
                
                _marker.enableSaving = false;
                _marker.alpha  = 0.6f;
                _marker.color1 = color;
                _marker.color2 = Color.white;
                _marker.radius = 0.5f;
                _marker.SetParent(_crate, true, true);
                _marker.Spawn();
                _marker.SendUpdate();
            }
            
            private void SpawnNpc(Vector3 position, Quaternion rotation)
            {
                HTNPlayer npc = GameManager.server.CreateEntity(NpcPrefab, position, rotation) as HTNPlayer;
                if (npc == null)
                {
                    return;
                }

                npc.enableSaving = false;
                npc.SetMaxHealth(_eventSettings.NpcHealth);
                npc.Spawn();
                npc.AiDomain.Movement = HTNDomain.MovementRule.RestrainedMove;
                npc.AiDomain.MovementRadius = _eventSettings.NpcRadius;
                npc.AiDefinition.Engagement.DeaggroRange = _eventSettings.NpcAggression + 2f;
                npc.AiDefinition.Engagement.AggroRange = _eventSettings.NpcAggression + 1f;
                npc.AiDefinition.Engagement.Defensiveness = 1f;
                npc.AiDefinition.Engagement.Hostility = 1f;

                NpcPlayers.Add(npc);

                npc.Invoke(() => GiveKit(npc, _eventSettings.KitName, _eventSettings.UseKits), 1f);
            }

            private IEnumerator SpawnAI()
            {
                for (int i = 0; i < _eventSettings.NpcCount; i++)
                {
                    Vector3 position = PositionAround(_position, 5f, (360 / _eventSettings.NpcCount * i));
                    
                    SpawnNpc(position, Quaternion.LookRotation(position));
                    
                    yield return new WaitForSeconds(0.75f);
                }

                yield return null;
            }

            private void DespawnCrate()
            {
                if (!IsValid(_crate) || _eventCompleted)
                {
                    return;
                }
                
                _crate.Kill();
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
                List<BaseEntity> npcList = new List<BaseEntity>(NpcPlayers);

                foreach (BaseEntity npc in npcList)
                {
                    if (!IsValid(npc)) continue;

                    npc.Kill();
                }

                NpcPlayers.Clear();
                npcList.Clear();
            }

            public void OnNPCDeath(HTNPlayer npc, BasePlayer player)
            {
                NpcPlayers.Remove(npc);

                if (NpcPlayers.Count > 0)
                {
                    return;
                }
                
                if (player != null)
                    Message("EventEnded", GetGrid(_position), player.displayName);
                else
                    Message("EventClear", GetGrid(_position));

                StopEvent(true);
            }
        }

        #endregion

        #region Component

        private class CargoComponent : MonoBehaviour
        {
            private CrateEvent _crateEvent;
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

                if (!_hasDropped && (double) time >= 0.5)
                {
                    _hasDropped = true;
                    
                    _crateEvent.StartEvent(transform.position);
                    
                    Destroy(this);
                }
            }

            public void SetEvent(CrateEvent crateEvent)
            {
                _crateEvent = crateEvent;
            }
        }

        private class DropComponent : MonoBehaviour
        {
            private BaseEntity Chute;
            private BaseEntity Crate;
            private bool HasLanded;

            private void Awake()
            {
                Crate = gameObject.GetComponent<BaseEntity>();
                Crate.GetComponent<Rigidbody>().drag = 1.2f;

                SpawnChute();
            }

            private void FixedUpdate()
            {
                Collider[] colliders = Physics.OverlapSphere(transform.position, 1f, CollisionLayer);
                if (!colliders.Any() || HasLanded)
                {
                    return;
                }
                
                HasLanded = true;

                RemoveChute();
                    
                Destroy(this);
            }
            
            private void SpawnChute()
            {
                Chute = GameManager.server.CreateEntity(ChutePrefab);
                if (Chute == null)
                {
                    return;
                }
                
                Chute.enableSaving = false;
                Chute.transform.localPosition = Vector3.zero;
                Chute.SetParent(Crate);
                Chute.Spawn();
            }

            private void RemoveChute()
            {
                if (!IsValid(Chute))
                {
                    return;
                }
                
                Chute.Kill();
            }
        }

        #endregion
        
        #region Helpers

        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        
        private static Vector3 PositionAround(Vector3 position, float radius, float angle)
        {
            position.x += radius * Mathf.Sin(angle * Mathf.Deg2Rad);
            position.z += radius * Mathf.Cos(angle * Mathf.Deg2Rad);

            RaycastHit hit;
            
            if (!Physics.Raycast(position, Vector3.down, out hit, float.PositiveInfinity, CollisionLayer))
            {
                return Vector3.zero;
            }

            return hit.point;
        }
        
        private static string GetGrid(Vector3 position)
        {
            char letter = 'A';
            float worldSize = ConVar.Server.worldsize;
            float x = Mathf.Floor((position.x + worldSize / 2) / 146.3f) % 26;
            float z = Mathf.Floor(worldSize / 146.3f) - Mathf.Floor((position.z + worldSize / 2) / 146.3f);
            letter = (char)(letter + x);

            return $"{letter}{z}";
        }
        
        private static void GiveKit(BasePlayer npc, string kit, bool giveKit)
        {
            if (!giveKit) return;

            npc.inventory.Strip();

            Interface.Oxide.CallHook("GiveKit", npc, kit);
        }

        private static bool IsValid(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed)
            {
                return false;
            }

            return true;
        }

        private static void Message(string key, params object[] args) => _plugin.Server.Broadcast(_plugin.Lang(key, null, args));

        #endregion

        #region Command

        [ChatCommand("gc")]
        private void GCChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, UsePerm))
            {
                player.ChatMessage(Lang("NoPermission"));
                return;
            }
            
            if (args.Length != 1)
            {
                player.ChatMessage(Lang("InvalidSyntax", player.UserIDString));
                return;
            }
            
            switch (args[0])
            {
                case "start":
                    StartEvent(player);
                    break;
                case "stop":
                    StopEvents(player);
                    break;
                default:
                    player.ChatMessage(Lang("InvalidSyntax", player.UserIDString));
                    break;
            }
        }
        
        [ConsoleCommand("gc")]
        private void GCConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!arg.IsRcon)
            {
                arg.ReplyWith(Lang("NoPermission"));
                return;
            }
            
            if (!arg.HasArgs())
            {
                arg.ReplyWith(Lang("InvalidSyntax"));
                return;
            }
            
            switch (arg.GetString(0))
            {
                case "start":
                    StartEvent(null);
                    break;
                case "stop":
                    StopEvents(null);
                    break;
                default:
                    arg.ReplyWith(Lang("InvalidSyntax"));
                    break;
            }
        }
        
        #endregion
    }
}