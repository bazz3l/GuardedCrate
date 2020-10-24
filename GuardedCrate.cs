﻿using System.Collections.Generic;
using System.Collections;
using System;
using Newtonsoft.Json;
using System.Linq;
using Oxide.Core;
using Rust.Ai.HTN;
using UnityEngine;
using VLB;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.3.3")]
    [Description("Spawns hackable crate events at random locations guarded by scientists.")]
    public class GuardedCrate : RustPlugin
    {
        /*
         * TODO Custom loot tables for scientists
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
        private PluginData _stored;
        private static GuardedCrate _plugin;

        #endregion
        
        #region Config
        
        private PluginConfig GetDefaultConfig() => new PluginConfig();

        private class PluginConfig
        {
            [JsonProperty(PropertyName = "AutoEvent (enables auto event spawns)")]
            public bool EnableAutoEvent = true;
            
            [JsonProperty(PropertyName = "AutoEventDuration (time until new event spawns)")]
            public float AutoEventDuration = 1800f;
        }

        #endregion

        #region Storage

        private class PluginData
        {
            [JsonProperty(PropertyName = "Events (specify different event settings)")]
            public readonly List<EventSetting> Events = new List<EventSetting>
            {
                new EventSetting
                {
                    EventDuration = 800f,
                    NpcAggression = 120f,
                    NpcRadius = 15f,
                    NpcCount = 6,
                    NpcHealth = 100,
                    NpcName = "Easy Guard",
                    MarkerColor = "#32a844",
                    MarkerBorderColor = "#ffffff"
                },
                new EventSetting
                {
                    EventDuration = 1200f,
                    NpcAggression = 120f,
                    NpcRadius = 25f,
                    NpcCount = 8,
                    NpcHealth = 150,
                    NpcName = "Medium Guard",
                    MarkerColor = "#e6aa20",
                    MarkerBorderColor = "#ffffff"
                },
                new EventSetting
                {
                    EventDuration = 1800f,
                    NpcAggression = 150f,
                    NpcRadius = 50f,
                    NpcCount = 10,
                    NpcHealth = 200,
                    NpcName = "Hard Guard",
                    MarkerColor = "#e81728",
                    MarkerBorderColor = "#ffffff"
                }
            };
        }
        
        private class EventSetting
        {
            [JsonProperty(PropertyName = "EventDuration (duration the event will be active for)")]
            public float EventDuration;

            [JsonProperty(PropertyName = "AutoHack (enables auto hacking of crates when an event is finished)")]
            public bool AutoHack = true;
            
            [JsonProperty(PropertyName = "AutoHackSeconds (countdown for crate to unlock in seconds)")]
            public float AutoHackSeconds = 60f;

            [JsonProperty(PropertyName = "UseKits (use custom kits plugin)")]
            public bool UseKits;
            
            [JsonProperty(PropertyName = "KitName (custom kit name)")]
            public string KitName;
            
            [JsonProperty(PropertyName = "NpcName (custom name)")]
            public string NpcName;
            
            [JsonProperty(PropertyName = "NpcCount (number of guards to spawn)")]
            public int NpcCount;
            
            [JsonProperty(PropertyName = "NpcHealth (health guards spawn with)")]
            public float NpcHealth;
            
            [JsonProperty(PropertyName = "NpcRadius (max distance guards will roam)")]
            public float NpcRadius;
            
            [JsonProperty(PropertyName = "NpcAggression (max aggression distance guards will target)")]
            public float NpcAggression;

            [JsonProperty(PropertyName = "MarkerColor (marker color)")]
            public string MarkerColor;
            
            [JsonProperty(PropertyName = "MarkerBorderColor (marker border color)")]
            public string MarkerBorderColor;
            
            [JsonProperty(PropertyName = "MarkerOpacity (marker opacity)")]
            public float MarkerOpacity = 1f;
            
            [JsonProperty(PropertyName = "UseLoot (use custom loot table)")]
            public bool UseLoot;
            
            [JsonProperty(PropertyName = "MaxLootItems (max items to spawn in crate)")]
            public int MaxLootItems = 6;
            
            [JsonProperty(PropertyName = "CustomLoot (items to spawn in crate)")]
            public List<LootItem> CustomLoot = new List<LootItem>();
        }

        private class LootItem
        {
            public string Shortname;
            public int MinAmount;
            public int MaxAmount;
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _stored);

        #endregion

        #region Oxide
        
        protected override void LoadDefaultConfig() => Config.WriteObject(GetDefaultConfig(), true);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages (new Dictionary<string, string>
            {
                { "InvalidSyntax", "gc start|stop" },
                { "Permission", "No permission" },
                { "CreateEvent", "<color=#DC143C>Guarded Crate</color>: New event starting stand by." },
                { "CleanEvents", "<color=#DC143C>Guarded Crate</color>: Cleaning up events." },
                { "EventStarted", "<color=#DC143C>Guarded Crate</color>: High value loot at <color=#EDDf45>{0}</color>, eliminate the guards before they leave in <color=#EDDf45>{1}</color>." },
                { "EventEnded", "<color=#DC143C>Guarded Crate</color>: Event ended at <color=#EDDf45>{0}</color>, <color=#EDDf45>{1}</color>, cleared the event." },
                { "EventClear", "<color=#DC143C>Guarded Crate</color>: Event ended at <color=#EDDf45>{0}</color>, You was not fast enough better luck next time." }
            }, this);
        }

        private void OnServerInitialized()
        {
            permission.RegisterPermission(UsePerm, this);
            
            if (_config.EnableAutoEvent)
            {
                timer.Every(_config.AutoEventDuration, () => StartEvent(null));
            }
            
            timer.Every(30f, RefreshEvents);
        }

        private void Init()
        {
            _plugin = this;
            _config = Config.ReadObject<PluginConfig>();
            _stored = Interface.Oxide.DataFileSystem.ReadObject<PluginData>(Name);
        }

        private void Unload() => StopEvents(null);

        private void OnEntityDeath(HTNPlayer npc, HitInfo hitInfo) => OnAIDeath(npc, hitInfo?.InitiatorPlayer);

        private void OnEntityKill(HTNPlayer npc) => OnAIDeath(npc, null);

        private object CanBuild(Planner planner, Construction prefab, Construction.Target target) => OnCanBuild(planner.GetOwnerPlayer());

        #endregion
        
        #region Core

        private void StartEvent(BasePlayer player)
        {
            EventSetting eventSettings = _stored.Events.GetRandom();
            
            CrateEvent crateEvent = new CrateEvent();

            crateEvent.PreEvent(eventSettings);

            if (player == null)
            {
                return;
            }
            
            player.ChatMessage(Lang("CreateEvent", player.UserIDString));
        }

        private void StopEvents(BasePlayer player)
        {
            CommunityEntity.ServerInstance.StartCoroutine(DespawnRoutine());
            
            if (player == null)
            {
                return;
            }
            
            player.ChatMessage(Lang("CleanEvents", player.UserIDString));
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
        
        private object OnCanBuild(BasePlayer player)
        {
            if (player != null && _crateEvents.FirstOrDefault(x => x.Distance(player.ServerPosition)) != null)
            {
                return false;
            }

            return null;
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
            private EventSetting _eventSettings;

            public void PreEvent(EventSetting eventSettings)
            {
                _eventSettings = eventSettings;
                
                SpawnPlane();
                
                _plugin.AddEvent(this);
            }

            public void StartEvent(Vector3 position)
            {
                _position = position;

                SpawnCrate();
                RefillLoot();
                StartSpawnRoutine();
                StartDespawnTimer();

                Message("EventStarted", GetGrid(_position), GetTime((int)_eventSettings.EventDuration));
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
            
            private void ResetDespawnTimer()
            {
                _eventTimer?.Destroy();

                StartDespawnTimer();
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
                _marker = GameManager.server.CreateEntity(MarkerPrefab, _position) as MapMarkerGenericRadius;
                if (_marker == null)
                {
                    return;
                }

                _marker.enableSaving = false;
                _marker.alpha  = _eventSettings.MarkerOpacity;
                _marker.color1 = GetColor(_eventSettings.MarkerColor);
                _marker.color2 = GetColor(_eventSettings.MarkerBorderColor);
                _marker.radius = 0.5f;
                _marker.Spawn();

                _crate = GameManager.server.CreateEntity(CratePrefab, _position, Quaternion.identity) as HackableLockedCrate;
                if (_crate == null)
                {
                    return;
                }
                
                _crate.enableSaving = false;
                _crate.shouldDecay = false;
                _crate.SetWasDropped();
                _crate.Spawn();
                _crate.gameObject.GetOrAddComponent<DropComponent>();
                
                _marker.SetParent(_crate);
                _marker.transform.localPosition = Vector3.zero;
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
                npc.AiDomain.Movement = HTNDomain.MovementRule.FreeMove;
                npc.AiDomain.MovementRadius = _eventSettings.NpcRadius;
                npc.AiDefinition.Engagement.DeaggroRange = _eventSettings.NpcAggression + 2f;
                npc.AiDefinition.Engagement.AggroRange = _eventSettings.NpcAggression + 1f;
                npc.AiDefinition.Engagement.Defensiveness = 1f;
                npc.AiDefinition.Engagement.Hostility = 1f;
                npc.displayName = _eventSettings.NpcName;
                npc.LootPanelName = npc.displayName;
                npc.SendNetworkUpdateImmediate();

                NpcPlayers.Add(npc);

                npc.Invoke(() => GiveKit(npc, _eventSettings.KitName, _eventSettings.UseKits), 1f);
            }

            private IEnumerator SpawnAI()
            {
                for (int i = 0; i < _eventSettings.NpcCount; i++)
                {
                    Vector3 position = PositionAround(_position, 5f, (360 / _eventSettings.NpcCount * i));

                    SpawnNpc(position, Quaternion.LookRotation(position - _position));
                    
                    yield return new WaitForSeconds(0.75f);
                }

                yield return null;
            }

            private List<LootItem> CreateLoot()
            {
                int MAX_LOOP_LIMIT = 1000;

                List<LootItem> lootItems = new List<LootItem>();
                
                while (lootItems.Count < _eventSettings.MaxLootItems && MAX_LOOP_LIMIT-- > 0)
                {
                    LootItem lootItem = _eventSettings.CustomLoot.GetRandom();

                    if (lootItems.Contains(lootItem)) continue;
                        
                    lootItems.Add(lootItem);
                }

                return lootItems;
            }

            private void RefillLoot()
            {
                if (!_eventSettings.UseLoot || _eventSettings.CustomLoot.Count <= 0)
                {
                    return;
                }
                
                _crate.inventory.Clear();
                
                ItemManager.DoRemoves();

                _crate.inventory.capacity = _eventSettings.CustomLoot.Count;
                
                List<LootItem> lootItems = CreateLoot();
                
                foreach (LootItem lootItem in lootItems)
                {
                    ItemDefinition item = ItemManager.FindItemDefinition(lootItem.Shortname);
                    
                    if (item == null) continue;

                    _crate.inventory.AddItem(item, UnityEngine.Random.Range(lootItem.MinAmount, lootItem.MaxAmount));
                }
            }

            private void DespawnCrate()
            {
                if (!IsValid(_crate))
                {
                    return;
                }

                if (!_eventCompleted)
                {
                    _crate.Kill();
                    return;
                }
                
                if (!_eventSettings.AutoHack)
                {
                    return;
                }
                    
                _crate.hackSeconds = HackableLockedCrate.requiredHackSeconds - _eventSettings.AutoHackSeconds;
                _crate.StartHacking();
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

            public bool Distance(Vector3 position) => Vector3Ex.Distance2D(position, _position) <= 20f;

            public void OnNPCDeath(HTNPlayer npc, BasePlayer player)
            {
                NpcPlayers.Remove(npc);
                
                if (NpcPlayers.Count > 0)
                {
                    ResetDespawnTimer();
                    return;
                }

                if (player != null)
                {
                    Message("EventEnded", GetGrid(_position), player.displayName);
                    
                    StopEvent(true);
                }
                else
                {
                    Message("EventClear", GetGrid(_position));
                    
                    StopEvent();
                }
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
                Chute = GameManager.server.CreateEntity(ChutePrefab, Crate.transform.position, Quaternion.identity);
                if (Chute == null)
                {
                    return;
                }
                
                Chute.enableSaving = false;
                Chute.Spawn();
                Chute.SetParent(Crate);
                Chute.transform.localPosition = Vector3.zero;
                Chute.SendNetworkUpdate();
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

        private static  string GetGrid(Vector3 pos)
        {
            char letter = 'A';
            float x     = Mathf.Floor((pos.x + (ConVar.Server.worldsize / 2)) / 146.3f) % 26;
            float count = Mathf.Floor(Mathf.Floor((pos.x + (ConVar.Server.worldsize / 2)) / 146.3f) / 26);
            float z     = Mathf.Floor(ConVar.Server.worldsize / 146.3f) - Mathf.Floor((pos.z + (ConVar.Server.worldsize / 2)) / 146.3f);
            
            letter = (char)(letter + x);
            
            string secondLetter = count <= 0 ? string.Empty : ((char)('A' + (count - 1))).ToString();
            
            return $"{secondLetter}{letter}{z}";
        }

        private static Color GetColor(string hex)
        {
            Color color = Color.black;

            ColorUtility.TryParseHtmlString(hex, out color);
            
            return color;
        }

        private static string GetTime(int secs)
        {
            TimeSpan t = TimeSpan.FromSeconds(secs);
            
            return string.Format("{0:D2}h:{1:D2}m:{2:D2}s", t.Hours, t.Minutes, t.Seconds);
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