﻿using System.Collections.Generic;
using System.Collections;
using System;
using Newtonsoft.Json;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust.Ai.HTN;
using UnityEngine;
using VLB;

namespace Oxide.Plugins
{
    [Info("Guarded Crate", "Bazz3l", "1.4.6")]
    [Description("Spawns hackable crate events at random locations guarded by scientists.")]
    public class GuardedCrate : RustPlugin
    {
        [PluginReference] Plugin Clans;
        
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
        
        private class PluginConfig
        {
            [JsonProperty("AutoEvent (enables auto event spawns)")]
            public bool EnableAutoEvent = true;

            [JsonProperty("AutoEventDuration (time until new event spawns)")]
            public float AutoEventDuration = 1800f;
            
            [JsonProperty("EnableLock (locks crate to player or clans if enabled)")]
            public bool EnableLock = true;
        }

        #endregion

        #region Storage

        private class PluginData
        {
            [JsonProperty("Events (available events)")]
            public readonly List<EventSetting> Events = new List<EventSetting>();
            
            [JsonProperty("Positions (custom positions for the events to spawn)")]
            public readonly List<Vector3> Positions = new List<Vector3>();
        }
        
        private class LootItem
        {
            public string Shortname;
            public int MinAmount;
            public int MaxAmount;
        }
        
        private class EventSetting
        {
            [JsonProperty("EventDuration (duration the event will be active for)")]
            public float EventDuration;
            
            [JsonProperty("EventName (event name)")]
            public string EventName;

            [JsonProperty("DropSpeed (sets how fast the drop should fall)")]
            public float DropSpeed = 0.8f;

            [JsonProperty("AutoHack (enables auto hacking of crates when an event is finished)")]
            public bool AutoHack = true;
            
            [JsonProperty("AutoHackSeconds (countdown for crate to unlock in seconds)")]
            public float AutoHackSeconds = 60f;

            [JsonProperty("UseKits (use custom kits plugin)")]
            public bool UseKits = false;
            
            [JsonProperty("Kits (custom kits)")]
            public List<string> Kits = new List<string>();
            
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

        private void LoadData()
        {
            try
            {
                _stored = Interface.Oxide.DataFileSystem.ReadObject<PluginData>(Name);
                
                if (_stored == null)
                {
                    throw new Exception();
                }
            }
            catch
            {
                _stored = new PluginData();
                
                PrintError("Data file contains an error and has been replaced with the default file.");
            }
            
            if (_stored.Events.Count <= 0)
            {
                RegisterDefaultEvents();
            }

            SaveData();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _stored);

        #endregion

        #region Oxide

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages (new Dictionary<string, string>
            {
                { "InvalidSyntax", "gc start|stop" },
                { "Permission", "No permission" },
                { "CreateEvent", "<color=#dc143c>Guarded Crate</color>: New event starting stand by." },
                { "CleanEvents", "<color=#dc143c>Guarded Crate</color>: Cleaning up events." },
                { "EventPos", "<color=#dc143c>Guarded Crate</color>: Position was added." },
                { "EventStarted", "<color=#dc143c>Guarded Crate</color>: <color=#ffc55c>{0}</color>, event started at <color=#ffc55c>{1}</color>, eliminate the guards before they leave in <color=#ffc55c>{2}</color>." },
                { "EventLocked", "<color=#dc143c>Guarded Crate</color>: This crate is locked to the event winners." },
                { "EventEnded", "<color=#dc143c>Guarded Crate</color>: The event ended at the location <color=#ffc55c>{0}</color>, <color=#ffc55c>{1}</color> cleared the event!" },
                { "EventClear", "<color=#dc143c>Guarded Crate</color>: The event ended at <color=#ffc55c>{0}</color>; You were not fast enough; better luck next time!" },
            }, this);
        }
        
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
                
                PrintToConsole($"New config created {Name}.json.");
            }
            catch
            {
                LoadDefaultConfig();
                
                PrintError("Config file contains an error and has been replaced with the default file.");
            }
            
            SaveConfig();
        }
        
        protected override void SaveConfig() => Config.WriteObject(_config);

        private void OnServerInitialized()
        {
            permission.RegisterPermission(UsePerm, this);

            RegisterCommands();
            RegisterHooks();

            if (_config.EnableAutoEvent)
            {
                timer.Every(_config.AutoEventDuration, () => StartEvent(null, new string[] {}));
            }
            
            timer.Every(30f, RefreshEvents);
        }

        private void Init()
        {
            _plugin = this;
            
            LoadData();
        }

        private void Unload()
        {
            StopEvents(null);

            _plugin = null;
        }

        private void OnEntityDeath(HTNPlayer npc, HitInfo hitInfo) => OnAIDeath(npc, hitInfo?.InitiatorPlayer);

        private void OnEntityKill(HTNPlayer npc) => OnAIDeath(npc, null);

        private object CanBuild(Planner planner, Construction prefab, Construction.Target target) => OnCanBuild(planner.GetOwnerPlayer());

        private object CanLootEntity(BasePlayer player, HackableLockedCrate crate)
        {
            if (!IsValid(crate))
            {
                return null;
            }

            if (crate.OwnerID != 0UL && !OwnerOrInClan(player.userID, crate.OwnerID))
            {
                player.ChatMessage(Lang("EventLocked", player.UserIDString));
                
                return false;
            }
            
            return null;
        }

        #endregion
        
        #region Core

        private void RegisterDefaultEvents()
        {
            _stored.Events.Add(new EventSetting
            {
                EventDuration = 800f,
                EventName = "Low Level",
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
                EventName = "Medium Level",
                NpcAggression = 120f,
                NpcRadius = 15f,
                NpcCount = 8,
                NpcHealth = 150,
                NpcName = "Medium Guard",
                MarkerColor = "#eddf45",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });
            
            _stored.Events.Add(new EventSetting {
                EventDuration = 1800f,
                EventName = "Hard Level",
                NpcAggression = 150f,
                NpcRadius = 50f,
                NpcCount = 10,
                NpcHealth = 200, 
                NpcName = "Hard Guard",
                MarkerColor = "#3060d9",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });
            
            _stored.Events.Add(new EventSetting {
                EventDuration = 1800f,
                EventName = "Elite Level",
                NpcAggression = 180f,
                NpcRadius = 50f,
                NpcCount = 12,
                NpcHealth = 350, 
                NpcName = "Elite Guard",
                MarkerColor = "#e81728",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });
        }

        private void RegisterCommands()
        {
            cmd.AddChatCommand("gc", this, GCChatCommand);
            cmd.AddConsoleCommand("gc", this, nameof(GCConsoleCommand));
        }

        private void RegisterHooks()
        {
            if (_config.EnableLock)
            {
                return;
            }
            
            Unsubscribe("CanLootEntity");
        }
        
        private void StartEvent(BasePlayer player, string[] args)
        {
            EventSetting eventSettings = _stored.Events.FirstOrDefault(x => x.EventName == string.Join(" ", args)) ?? _stored.Events.GetRandom();
            
            if (eventSettings == null)
            {
                return;
            }

            CrateEvent crateEvent = new CrateEvent();

            crateEvent.PreEvent(eventSettings, _stored.Positions.GetRandom());

            if (player != null)
            {
                player.ChatMessage(Lang("CreateEvent", player.UserIDString));
            }
        }

        private void StopEvents(BasePlayer player)
        {
            CommunityEntity.ServerInstance.StartCoroutine(DespawnRoutine());

            if (player != null)
            {
                player.ChatMessage(Lang("CleanEvents", player.UserIDString));
            }
        }

        private void RefreshEvents()
        {
            for (int i = 0; i < _crateEvents.Count; i++)
            {
                CrateEvent crateEvent = _crateEvents.ElementAt(i);

                crateEvent?.RefreshEvent();
            }
        }

        private void EventPos(BasePlayer player)
        {
            Vector3 pos = player.ServerPosition;
            
            if (!_stored.Positions.Contains(pos))
            {
                _stored.Positions.Add(pos);
                
                SaveData();
            }
            
            player.ChatMessage(Lang("EventPos", player.UserIDString));
        }
        
        private IEnumerator DespawnRoutine()
        {
            for (int i = _crateEvents.Count - 1; i >= 0; i--)
            {
                CrateEvent crateEvent = _crateEvents.ElementAt(i);
                
                crateEvent.StopEvent();
                
                DelEvent(crateEvent);

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
            private EventSetting _eventSettings;

            public void PreEvent(EventSetting eventSettings, Vector3 position)
            {
                _eventSettings = eventSettings;
                
                SpawnPlane(position);

                _plugin.AddEvent(this);
            }

            public void StartEvent(Vector3 position)
            {
                _position = position;

                SpawnCrate();
                RefillLoot();
                StartSpawnRoutine();
                StartDespawnTimer();

                Message("EventStarted", _eventSettings.EventName, GetGrid(_position), GetTime((int)_eventSettings.EventDuration));
            }

            public void StopEvent(bool completed = false)
            {
                _eventTimer?.Destroy();

                StopSpawnRoutine();
                DespawnPlane();
                DespawnMarker();
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

            private void StartSpawnRoutine()
            {
                _coroutine = CommunityEntity.ServerInstance.StartCoroutine(SpawnAI());
            }
            
            private void StopSpawnRoutine()
            {
                if (_coroutine != null)
                {
                    CommunityEntity.ServerInstance.StopCoroutine(_coroutine);
                    
                    _coroutine = null;
                }
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

            private void SpawnPlane(Vector3 position)
            {
                _plane = GameManager.server.CreateEntity(PlanePrefab) as CargoPlane;
                if (_plane == null)
                {
                    return;
                }
                
                _plane.dropPosition = position;
                _plane.Spawn();
                _plane.GetOrAddComponent<CargoComponent>().SetEvent(this);
            }
            
            private void SpawnCrate()
            {
                _marker = GameManager.server.CreateEntity(MarkerPrefab, _position) as MapMarkerGenericRadius;
                if (_marker == null)
                {
                    return;
                }

                _marker.enableSaving = false;
                _marker.color1 = GetColor(_eventSettings.MarkerColor);
                _marker.color2 = GetColor(_eventSettings.MarkerBorderColor);
                _marker.alpha  = _eventSettings.MarkerOpacity;
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
                _crate.GetOrAddComponent<DropComponent>().SetDropSpeed(_eventSettings.DropSpeed);

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
                npc.Spawn();
                npc.InitializeHealth(_eventSettings.NpcHealth, _eventSettings.NpcHealth);
                npc.AiDomain.Movement = HTNDomain.MovementRule.FreeMove;
                npc.AiDomain.MovementRadius = _eventSettings.NpcRadius;
                npc.AiDefinition.Engagement.DeaggroRange = _eventSettings.NpcAggression + 2f;
                npc.AiDefinition.Engagement.AggroRange = _eventSettings.NpcAggression + 1f;
                npc.displayName = _eventSettings.NpcName;
                npc.LootPanelName = npc.displayName;
                npc.SendNetworkUpdateImmediate();

                NpcPlayers.Add(npc);

                npc.Invoke(() =>
                {
                    GiveInventory(npc, _eventSettings.Kits.GetRandom(), _eventSettings.UseKits);
                    SetActiveItem(npc);
                }, 2f);
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
                int maxLoopLimit = 1000;

                List<LootItem> lootItems = new List<LootItem>();
                
                while (lootItems.Count < _eventSettings.MaxLootItems && maxLoopLimit-- > 0)
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
                
                List<LootItem> lootItems = CreateLoot();
                
                _crate.inventory.Clear();
                
                ItemManager.DoRemoves();
                
                _crate.inventory.capacity = lootItems.Count;

                foreach (LootItem lootItem in lootItems)
                {
                    ItemDefinition item = ItemManager.FindItemDefinition(lootItem.Shortname);
                    
                    if (item == null) continue;

                    _crate.inventory.AddItem(item, UnityEngine.Random.Range(lootItem.MinAmount, lootItem.MaxAmount));
                }
                
                lootItems.Clear();
            }

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

                if (_eventSettings.AutoHack)
                {
                    _crate.hackSeconds = HackableLockedCrate.requiredHackSeconds - _eventSettings.AutoHackSeconds;
                    _crate.StartHacking();                    
                }
                
                _crate.shouldDecay = true;
                _crate.RefreshDecay();                
            }
            
            private void DespawnMarker()
            {
                if (!IsValid(_marker))
                {
                    return;
                }

                _marker.SetParent(null);
                _marker.Kill();
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
            
            private void LockCrateToPlayer(ulong userID)
            {
                if (!IsValid(_crate))
                {
                    return;
                }

                _crate.OwnerID = userID;
            }

            public bool Distance(Vector3 position) => Vector3Ex.Distance2D(position, _position) <= 20f;

            public void OnNPCDeath(HTNPlayer npc, BasePlayer player)
            {
                if (!NpcPlayers.Remove(npc))
                {
                    return;
                }
                
                if (NpcPlayers.Count > 0)
                {
                    ResetDespawnTimer();
                    return;
                }

                if (player != null)
                {
                    LockCrateToPlayer(player.userID);

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
            private Rigidbody _rbody;
            private BaseEntity _chute;
            private BaseEntity _crate;
            private bool _hasLanded;

            private void Awake()
            {
                _crate = GetComponent<BaseEntity>();
                _rbody = GetComponent<Rigidbody>();

                SpawnChute();
            }

            private void FixedUpdate()
            {
                int size = Physics.OverlapSphereNonAlloc(transform.position, 1f, Vis.colBuffer, CollisionLayer);
                if (size <= 0 || _hasLanded)
                {
                    return;
                }
                
                _hasLanded = true;

                RemoveParented();
                    
                Destroy(this);
            }

            private void SpawnChute()
            {
                _chute = GameManager.server.CreateEntity(ChutePrefab, transform.position, Quaternion.identity);
                if (_chute == null)
                {
                    return;
                }
                
                _chute.enableSaving = false;
                _chute.Spawn();
                _chute.SetParent(_crate);
                _chute.transform.localPosition = Vector3.zero;
                _chute.SendNetworkUpdate();
            }

            private void RemoveParented()
            {
                if (IsValid(_chute))
                {
                    _chute.Kill();
                }
            }

            public void SetDropSpeed(float dropSpeed = 0.7f)
            {
                _rbody.drag = dropSpeed;
            }
        }

        #endregion
        
        #region Helpers

        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        
        private bool OwnerOrInClan(ulong userID, ulong targetID)
        {
            if (userID == targetID)
            {
                return true;
            }

            var playerClan = Clans?.Call<string>("GetClanOf", userID);
            var targetClan = Clans?.Call<string>("GetClanOf", targetID);
            if (!string.IsNullOrEmpty(targetClan) && !string.IsNullOrEmpty(playerClan) && playerClan == targetClan)
            {
                return true;
            }
            
            return false;
        }
        
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
            Vector2 r = new Vector2(World.Size / 2 + position.x, World.Size / 2 + position.z);
            float x = Mathf.Floor(r.x / 146.3f) % 26;
            float z = Mathf.Floor(World.Size / 146.3f) - Mathf.Floor(r.y / 146.3f);

            return $"{(char)('A' + x)}{z - 1}";
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
        
        private static void GiveInventory(HTNPlayer npc, string kit, bool giveKit)
        {
            if (!giveKit)
            {
                return;
            }

            npc.inventory.Strip();
            
            Interface.Oxide.CallHook("GiveKit", npc, kit);
        }

        private static void SetActiveItem(HTNPlayer npc)
        {
            Item item = npc.inventory.containerBelt.GetSlot(0);
            if (item != null)
            {
                npc.UpdateActiveItem(item.uid);
            }
        }

        private static bool IsValid(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed)
            {
                return false;
            }

            return true;
        }

        private static void Message(string key, params object[] args) => _plugin?.PrintToChat(_plugin.Lang(key, null, args));

        #endregion

        #region Command
        
        private void GCChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, UsePerm))
            {
                player.ChatMessage(Lang("NoPermission", player.UserIDString));
                return;
            }
            
            if (args.Length < 1)
            {
                player.ChatMessage(Lang("InvalidSyntax", player.UserIDString));
                return;
            }
            
            switch (args[0].ToLower())
            {
                case "start":
                    StartEvent(player, args.Skip(1).ToArray());
                    break;
                case "stop":
                    StopEvents(player);
                    break;
                case "pos":
                    EventPos(player);
                    break;
                default:
                    player.ChatMessage(Lang("InvalidSyntax", player.UserIDString));
                    break;
            }
        }
        
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
            
            switch (arg.GetString(0).ToLower())
            {
                case "start":
                    StartEvent(null, arg.Args.Skip(1).ToArray());
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