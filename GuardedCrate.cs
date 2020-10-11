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
    [Info("Guarded Crate", "Bazz3l", "1.2.1")]
    [Description("Spawns hackable crates at a random location guarded by scientists.")]
    public class GuardedCrate : RustPlugin
    {
        /*
         * TODO
         * Remove ability to build in event areas to prevent people walling/building off crates
         * Different event tiers
         */
        
        #region Fields

        private const string CratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        private const string MarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string ChutePrefab = "assets/prefabs/misc/parachute/parachute.prefab";
        private const string NpcPrefab = "assets/prefabs/npc/scientist/htn/scientist_full_any.prefab";
        private const string PlanePrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        
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
                            NpcRadius = 15f,
                            NpcCount = 4,
                            NpcHealth = 100,
                            MarkerColor = "#32a844"
                        }
                    },
                    {
                        EventTier.Medium, new TierSetting
                        {
                            EventDuration = 1800f,
                            NpcRadius = 25f,
                            NpcCount = 6,
                            NpcHealth = 150,
                            MarkerColor = "#e6aa20"
                        }
                    },
                    {
                        EventTier.Hard, new TierSetting
                        {
                            EventDuration = 1800f,
                            NpcRadius = 50f,
                            NpcCount = 8,
                            NpcHealth = 200,
                            MarkerColor = "#e81728"
                        }
                    }
                }
            };
        }

        private class PluginConfig
        {
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
                { "EventStarted", "Event started at {0}, High value loot protected by armed guards." },
                { "EventEnded", "Event completed at {0}." },
            }, this);
        }

        private void OnServerInitialized()
        {
            //
        }

        private void Init()
        {
            _plugin = this;
            _config = Config.ReadObject<PluginConfig>();
        }

        private void Unload() => StopEvents();
        
        private void OnEntityDeath(HTNPlayer npc, HitInfo hitInfo) => OnAIDeath(npc);

        private void OnEntityKill(HTNPlayer npc) => OnAIDeath(npc);

        #endregion
        
        #region Core

        private enum EventTier
        {
            Easy,
            Medium,
            Hard
        };
        
        private void StartEvent()
        {
            KeyValuePair<EventTier, TierSetting> eventSettings = _config.EventTiers.ElementAtOrDefault(UnityEngine.Random.Range(0, _config.EventTiers.Count));

            CrateEvent crateEvent = new CrateEvent();

            crateEvent.PreEvent(eventSettings.Value);
        }

        private void StopEvents()
        {
            CommunityEntity.ServerInstance.StartCoroutine(DespawnRoutine());
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

        private void OnAIDeath(HTNPlayer npc)
        {
            CrateEvent crateEvent = _crateEvents.FirstOrDefault(x => x.NpcPlayers.Contains(npc));

            crateEvent?.OnNPCDeath(npc);
        }

        private class CrateEvent
        {
            public readonly List<HTNPlayer> NpcPlayers = new List<HTNPlayer>();
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

                _plugin.Message("EventStarted", GetGrid(_position));
            }

            public void StopEvent(bool eventCompleted = false)
            {
                _eventCompleted = eventCompleted;
                
                _eventTimer?.Destroy();
                
                StopSpawnRoutine();
                DespawnPlane();
                DespawnCrate();
                DespawnAI();
                
                _plugin.DelEvent(this);
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
                
                MapMarkerGenericRadius marker = GameManager.server.CreateEntity(MarkerPrefab, _position) as MapMarkerGenericRadius;
                if (marker == null)
                {
                    return;
                }

                Color color;

                ColorUtility.TryParseHtmlString(_eventSettings.MarkerColor, out color);
                
                marker.enableSaving = false;
                marker.alpha  = 0.6f;
                marker.color1 = color;
                marker.color2 = Color.white;
                marker.radius = 0.5f;
                marker.SetParent(_crate, true, true);
                marker.Spawn();
                marker.SendUpdate();
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
                npc._aiDomain.MovementRadius = _eventSettings.NpcRadius;
                npc._aiDomain.Movement = HTNDomain.MovementRule.FreeMove;

                NpcPlayers.Add(npc);

                npc.Invoke(() => GiveKit(npc, _eventSettings.KitName, _eventSettings.UseKits), 1f);
            }

            private IEnumerator SpawnAI()
            {
                for (int i = 0; i < _eventSettings.NpcCount; i++)
                {
                    SpawnNpc(GetPositionAround(_position, 5f, (360 / _eventSettings.NpcCount * i)), Quaternion.identity);
                    
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
                    if (!IsValid(npc))
                    {
                        continue;
                    }
                    
                    npc.Kill();
                }

                NpcPlayers.Clear();
                npcList.Clear();
            }

            public void OnNPCDeath(HTNPlayer npc)
            {
                NpcPlayers.Remove(npc);

                if (NpcPlayers.Count > 0)
                {
                    return;
                }
                
                StopEvent(true);

                _plugin.Message("EventEnded", GetGrid(_position));
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
        
        private static Vector3 GetPositionAround(Vector3 center, float radius, float angle)
        {
            Vector3 position = center;
            
            position.x += radius * Mathf.Sin(angle * Mathf.Deg2Rad);
            position.z += radius * Mathf.Cos(angle * Mathf.Deg2Rad);
            position.y = TerrainMeta.HeightMap.GetHeight(position);
            
            return position;
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

        private void Message(string key, params object[] args) => Server.Broadcast(Lang(key, null, args));

        #endregion

        #region Command
        
        [ChatCommand("gc")]
        private void CommandEvent(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                player.ChatMessage("Unknown command: gc");
                return;
            }
            
            if (args.Length != 1)
            {
                player.ChatMessage("/gc start|stop");
                return;
            }
            
            switch (args[0])
            {
                case "start":
                    StartEvent();
                    break;
                case "stop":
                    StopEvents();
                    break;
                default:
                    player.ChatMessage("/gc start|stop");
                    break;
            }
        }
        
        #endregion
    }
}