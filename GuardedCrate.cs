using System.Collections.Generic;
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
    [Info("Guarded Crate", "Bazz3l", "1.4.9")]
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
        
        private static readonly LayerMask RaycastLayers = LayerMask.GetMask("Terrain", "World", "Default", "Water", "Tree", "Debris", "Clutter", "Resource", "Construction", "Deployed");
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
            
            [JsonProperty("EnableLock (crate only lootable by the winner or the winners clan/team members)")]
            public bool EnableLock = false;
            
            [JsonProperty("EnableMostKills (give the crate to the player with the most npc kills)")]
            public bool EnableMostKills = false;
            
            [JsonProperty(PropertyName = "Chat Icon (SteamID64)")]
            public ulong ChatIcon = 0;
        }

        #endregion

        #region Storage

        private class PluginData
        {
            [JsonProperty("Events (available events)")]
            public readonly List<EventSetting> Events = new List<EventSetting>();
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
            public float DropSpeed = 0.7f;

            [JsonProperty("AutoHack (enables auto hacking of crates when an event is finished)")]
            public bool AutoHack = true;
            
            [JsonProperty("AutoHackSeconds (countdown for crate to unlock in seconds)")]
            public float AutoHackSeconds = 60f;

            [JsonProperty("UseKits (use custom kits plugin)")]
            public bool UseKits;
            
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
            public float MarkerOpacity;

            [JsonProperty("UseLoot (use custom loot table)")]
            public bool UseLoot;
            
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
            permission.RegisterPermission(UsePerm, this);
            
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
                EventDuration = 1200f,
                EventName = "Easy Level",
                NpcAggression = 150f,
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
                EventDuration = 1200f,
                EventName = "Medium Level",
                NpcAggression = 180f,
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
                NpcAggression = 200f,
                NpcRadius = 50f,
                NpcCount = 10,
                NpcHealth = 200, 
                NpcName = "Hard Guard",
                MarkerColor = "#3060d9",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });
            
            _stored.Events.Add(new EventSetting {
                EventDuration = 1200f,
                EventName = "Elite Level",
                NpcAggression = 250f,
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

            crateEvent.PreEvent(eventSettings);

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
            private readonly Dictionary<BasePlayer, int> _npcKills = new Dictionary<BasePlayer, int>();
            public readonly List<HTNPlayer> NpcPlayers = new List<HTNPlayer>();
            private MapMarkerGenericRadius _marker;
            private HackableLockedCrate _crate;
            private CargoPlane _plane;
            private Vector3 _position;
            private Coroutine _coroutine;
            private Timer _eventTimer;
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
                StartSpawnRoutine();
                StartDespawnTimer();

                _plugin.Broadcast("EventStarted", _eventSettings.EventName, GetGrid(_position), GetTime((int)_eventSettings.EventDuration));
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

            private void SpawnPlane()
            {
                _plane = GameManager.server.CreateEntity(PlanePrefab) as CargoPlane;
                if (_plane == null)
                {
                    return;
                }
                
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
                _crate.Invoke(RefillLoot, 2f);
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
                
                _crate.shouldDecay = true;

                if (_eventSettings.AutoHack)
                {
                    _crate.hackSeconds = HackableLockedCrate.requiredHackSeconds - _eventSettings.AutoHackSeconds;
                    _crate.StartHacking();
                }
                
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

            public bool Distance(Vector3 position) => Vector3Ex.Distance2D(position, _position) <= 20f;

            private void AssignCrate(BasePlayer player)
            {
                if (!IsValid(_crate))
                {
                    return;
                }

                _crate.OwnerID = player.userID;
            }
            
            private void LogNpcKill(BasePlayer player)
            {
                if (player == null || player.IsNpc)
                {
                    return;
                }

                if (!_npcKills.ContainsKey(player))
                {
                    _npcKills[player] = 0;
                }

                _npcKills[player] += 1;
            }

            private BasePlayer GetWinner(BasePlayer player)
            {
                if (!_plugin._config.EnableMostKills)
                {
                    return player;
                }

                Dictionary<BasePlayer, int> winners = _npcKills
                    .OrderByDescending(e => e.Value)
                    .ToDictionary(x => x.Key, x => x.Value);

                return winners.Keys.FirstOrDefault() ?? player;
            }

            public void OnNPCDeath(HTNPlayer npc, BasePlayer player)
            {
                if (!NpcPlayers.Remove(npc))
                {
                    return;
                }
                
                LogNpcKill(player);
                
                if (NpcPlayers.Count > 0)
                {
                    ResetDespawnTimer();

                    return;
                }

                if (player != null)
                {
                    BasePlayer winner = GetWinner(player);

                    AssignCrate(winner);
                    
                    _plugin.Broadcast("EventEnded", GetGrid(_position), winner.displayName);

                    StopEvent(true);
                }
                else
                {
                    _plugin.Broadcast("EventClear", GetGrid(_position));
                    
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

            internal void SetEvent(CrateEvent crateEvent)
            {
                _crateEvent = crateEvent;
            }
        }

        private class DropComponent : MonoBehaviour
        {
            private BaseEntity _chute;
            private BaseEntity _crate;
            private Rigidbody _rb;

            private void Awake()
            {
                _crate = GetComponent<BaseEntity>();
                _rb = GetComponent<Rigidbody>();

                AttachChute();
            }

            private void AttachChute()
            {
                _chute = GameManager.server.CreateEntity(ChutePrefab, transform.position, Quaternion.identity);
                if (_chute == null)
                {
                    return;
                }
                
                _chute.SetParent(_crate, "parachute_attach", false, false);
                _chute.transform.localPosition = Vector3.zero;
                _chute.transform.localRotation = Quaternion.identity;
                _chute.enableSaving = false;
                _chute.Spawn();
            }
            
            private void OnCollisionEnter(Collision collision)
            {
                if ((1 << collision.collider.gameObject.layer & 1084293393) > 0)
                {
                    RemoveParachute();
                }
            }

            private void RemoveParachute()
            {
                if (IsValid(_chute))
                {
                    _chute.Kill();
                }
                
                Destroy(this);
            }

            internal void SetDropSpeed(float dropSpeed = 0.7f)
            {
                _rb.drag = dropSpeed;
            }
        }

        #endregion
        
        #region Helpers

        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        
        private bool OwnerOrInClan(ulong userID, ulong targetID)
        {
            return userID == targetID || SameClan(userID, targetID) || SameTeam(userID, targetID);
        }
        
        private bool SameClan(ulong userID, ulong targetID)
        {
            string playerClan = Clans?.Call<string>("GetClanOf", userID);
            string targetClan = Clans?.Call<string>("GetClanOf", targetID);
            
            if (string.IsNullOrEmpty(targetClan) || string.IsNullOrEmpty(playerClan))
            {
                return false;
            }

            return playerClan == targetClan;
        }

        private bool SameTeam(ulong userID, ulong targetID)
        {
            if (!RelationshipManager.TeamsEnabled())
            {
                return false;
            }
            
            RelationshipManager.PlayerTeam playerTeam = RelationshipManager.Instance.FindPlayersTeam(userID);
            RelationshipManager.PlayerTeam targetTeam = RelationshipManager.Instance.FindPlayersTeam(targetID);
            
            if (playerTeam == null || targetTeam == null)
            {
                return false;
            }

            return playerTeam.teamID == targetTeam.teamID;
        }

        private static Vector3 PositionAround(Vector3 position, float radius, float angle)
        {
            position.x += radius * Mathf.Sin(angle * Mathf.Deg2Rad);
            position.z += radius * Mathf.Cos(angle * Mathf.Deg2Rad);

            RaycastHit rayHit;
            
            if (!Physics.Raycast(position, Vector3.down, out rayHit, float.PositiveInfinity, RaycastLayers, QueryTriggerInteraction.Collide))
            {
                return Vector3.zero;
            }

            return rayHit.point + Vector3.up * 0.5f;
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

        private static string GetTime(int seconds)
        {
            TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
            int days = timeSpan.Days;
            int hours = timeSpan.Hours;
            int mins = timeSpan.Minutes;
            int secs = timeSpan.Seconds;

            if (days > 0)
                return $"{days}d:{hours}h:{mins}m:{secs}s";
            
            if (hours > 0)
                return $"{hours}h:{mins}m:{secs}s";
            
            if (mins > 0)
                return $"{mins}m:{secs}s";
            
            return $"{secs}s";
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
        
        private void Message(BasePlayer player, string message) => Player.Message(player, message, _config.ChatIcon);

        private void Broadcast(string key, params object[] args) => Server.Broadcast(Lang(key, null, args), _config.ChatIcon);

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