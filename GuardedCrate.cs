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
    [Info("Guarded Crate", "Bazz3l", "1.2.0")]
    [Description("Spawns hackable crates at a random location guarded by scientists.")]
    public class GuardedCrate : RustPlugin
    {
        /*
         * TODO
         * Remove ability to build in event areas to prevent people walling/building off crates
         * Different loot/guard levels
         */
        
        #region Fields

        private const string CratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        private const string MarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string ChutePrefab = "assets/prefabs/misc/parachute/parachute.prefab";
        private const string NpcPrefab = "assets/prefabs/npc/scientist/htn/scientist_full_any.prefab";
        private const string PlanePrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        
        private static readonly LayerMask CollisionLayer = LayerMask.GetMask("Water", "Tree",  "Debris", "Clutter",  "Default", "Resource", "Construction", "Terrain", "World", "Deployed");
        private readonly HashSet<CrateEvent> CrateEvents = new HashSet<CrateEvent>();
        private PluginConfig _config;
        private static GuardedCrate _plugin;

        #endregion
        
        #region Config
        
        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                EventDuration = 1800f,
                NpcCount = 10,
                NpcHealth = 150f,
                NpcRadius = 80f
            };
        }

        private class PluginConfig
        {
            [JsonProperty(PropertyName = "EventDuration (duration the event should last for)")]
            public float EventDuration;

            [JsonProperty(PropertyName = "NpcCount (number of guards to spawn)")]
            public int NpcCount;
            
            [JsonProperty(PropertyName = "NpcHealth (health guards spawn with)")]
            public float NpcHealth;
            
            [JsonProperty(PropertyName = "NpcRadius (max distance guards will roam)")]
            public float NpcRadius;
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

        #endregion
        
        #region Core
        
        private void StartNewEvent()
        {
            CrateEvent crateEvent = new CrateEvent();
            crateEvent.EventDuration = _config.EventDuration;
            crateEvent.NpcRadius = _config.NpcRadius;
            crateEvent.NpcHealth = _config.NpcHealth;
            crateEvent.NpcCount = _config.NpcCount;
            crateEvent.PreEvent();
            
            CrateEvents.Add(crateEvent);
        }

        private void StopEvents()
        {
            foreach (CrateEvent crateEvent in CrateEvents)
            {
                crateEvent?.StopEvent();
            }

            CrateEvents.Clear();
        }
            
        private void OnAIDeath(HTNPlayer npc)
        {
            var crateEvent = CrateEvents.FirstOrDefault(x => x.Npcs.Contains(npc));
            if (crateEvent == null)
            {
                return;
            }

            crateEvent.OnAIDeath(npc);

            if (!crateEvent.IsCompleted())
            {
                return;
            }
            
            CrateEvents.Remove(crateEvent);
        }

        private class CrateEvent
        {
            public readonly HashSet<HTNPlayer> Npcs = new HashSet<HTNPlayer>();
            private HackableLockedCrate _crate;
            private CargoPlane _plane;
            private Vector3 _position;
            private Coroutine _coroutine;
            private bool _isCompleted;
            private Timer _eventTimer;
            public float EventDuration;
            public int NpcCount;
            public float NpcHealth;
            public float NpcRadius;

            public void PreEvent()
            {
                SpawnPlane();
            }

            public void StartEvent(Vector3 position)
            {
                _position = position;

                SpawnCrate();
                SpawnRoutine();

                _eventTimer = _plugin.timer.In(EventDuration, StopEvent);

                _plugin.Message("EventStarted", GetGrid(_position));
            }

            public void StopEvent()
            {
                Cleanup();
                
                _plugin.Message("EventEnded", GetGrid(_position));
            }

            public bool IsCompleted()
            {
                return _isCompleted;
            }

            private void Cleanup()
            {
                if (_coroutine != null)
                {
                    CommunityEntity.ServerInstance.StopCoroutine(_coroutine);
                }
                
                _eventTimer?.Destroy();
                
                CleanCrate();
                CleanAI();
            }

            private void SpawnRoutine()
            {
                _coroutine = CommunityEntity.ServerInstance.StartCoroutine(SpawnNpcs());
            }

            private void SpawnNpc(Vector3 position, Quaternion rotation)
            {
                var npc = GameManager.server.CreateEntity(NpcPrefab, position, rotation) as HTNPlayer;
                if (npc == null)
                {
                    return;
                }

                npc.enableSaving = false;
                npc.Spawn();
                npc.SetMaxHealth(NpcHealth);
                npc._aiDomain.MovementRadius = NpcRadius;
                npc._aiDomain.Movement = HTNDomain.MovementRule.FreeMove;

                Npcs.Add(npc);

                npc.Invoke(() => GiveKit(npc, "crate-ai", false), 1f);
            }

            private IEnumerator SpawnNpcs()
            {
                for (int i = 0; i < NpcCount; i++)
                {
                    SpawnNpc(GetPositionAround(_position, 5f, (360 / NpcCount * i)), Quaternion.identity);
                    
                    yield return new WaitForSeconds(0.5f);
                }
                
                _coroutine = null;

                yield return null;
            }
            
            private void SpawnPlane()
            {
                _plane = GameManager.server.CreateEntity(PlanePrefab) as CargoPlane;
                if (_plane == null)
                {
                    return;
                }
                
                _plane.Spawn();
                _plane.gameObject.AddComponent<CargoComponent>().SetEvent(this);
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
                
                var marker = GameManager.server.CreateEntity(MarkerPrefab, _position) as MapMarkerGenericRadius;
                if (marker == null)
                {
                    return;
                }
                
                marker.enableSaving = false;
                marker.alpha  = 0.6f;
                marker.color1 = Color.red;
                marker.color2 = Color.white;
                marker.radius = 0.5f;
                marker.SetParent(_crate, true, true);
                marker.Spawn();
                marker.SendUpdate();
            }

            private void CleanCrate()
            {
                if (_isCompleted || !IsValid(_crate)) return;
                
                _crate.Kill();
            }
            
            private void CleanAI()
            {
                foreach (HTNPlayer npc in Npcs)
                {
                    if (!IsValid(npc)) continue;
                    
                    npc.Kill();
                }

                Npcs.Clear();
            }

            public void OnAIDeath(HTNPlayer npc)
            {
                Npcs.Remove(npc);

                if (Npcs.Count > 0)
                {
                    return;
                }
                
                _isCompleted = true;
                    
                StopEvent();
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
                if (!colliders.Any())
                {
                    return;
                }

                if (HasLanded)
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
            if (entity == null || entity.net == null || entity.IsDestroyed || entity.transform == null)
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
                    StartNewEvent();
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