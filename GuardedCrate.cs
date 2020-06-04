using System.Collections.Generic;
using System.Linq;
using System;
using Newtonsoft.Json;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Bradley Guards", "Bazz3l", "1.2.2")]
    [Description("Calls for reinforcements when bradley is destroyed.")]
    class BradleyGuards : RustPlugin
    {
        [PluginReference] Plugin Kits;

        #region Fields
        const string _lockedPrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        const string _ch47Prefab = "assets/prefabs/npc/ch47/ch47scientists.entity.prefab";
        const string _landingName = "BradleyLandingZone";

        HashSet<CH47LandingZone> _zones = new HashSet<CH47LandingZone>();
        HashSet<NPCPlayerApex> _npcs = new HashSet<NPCPlayerApex>();

        PluginConfig _config;
        Quaternion _landingRotation;
        Vector3 _landingPosition;
        Vector3 _chinookPosition;
        bool _hasLaunch;
        bool _eventActive;

        static BradleyGuards plugin;
        #endregion

        #region Config
        protected override void LoadDefaultConfig() => Config.WriteObject(GetDefaultConfig(), true);

        PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                GuardMaxSpawn = 10, // Max is 11
                CrateAmount   = 4,
                GuardSettings = new List<GuardSetting> {
                    new GuardSetting("Guard", "guard", 100f),
                    new GuardSetting("Heavy Guard", "guard-heavy", 300f)
                }
            };
        }

        class PluginConfig
        {
            [JsonProperty(PropertyName = "CrateAmount (max amount of crates bradley will spawn)")]
            public int CrateAmount;

            [JsonProperty(PropertyName = "GuardMaxSpawn (max number of guard to spawn note: 11 is max)")]
            public int GuardMaxSpawn;            

            [JsonProperty(PropertyName = "GuardSettings (create different types of guard must contain atleast 1)")]
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

            public GuardSetting(string name, string kit, float health, float minRoamRadius = 30f, float maxRoamRadius = 80f)
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
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string> {
                {"EventStart", "<color=#DC143C>Bradley Guards</color>: Bradley called in reinforcements, prepare to fight."},
                {"EventEnded", "<color=#DC143C>Bradley Guards</color>: All reinforcements are down."},
            }, this);
        }

        void OnServerInitialized()
        {
            CheckLandingPoint();

            if (!_hasLaunch) return;

            CH47LandingZone zone = CreateLandingZone();

            _zones.Add(zone);
        }

        void Init()
        {
            plugin = this;

            _config = Config.ReadObject<PluginConfig>();
        }

        void Unload() => CleanUp();

        void OnEntitySpawned(BradleyAPC bradley)
        {
            bradley.maxCratesToSpawn = _config.CrateAmount;
            
            ClearGuards();

            _eventActive = false;
        }

        void OnEntityTakeDamage(BradleyAPC bradley, HitInfo info) => SpawnEvent(bradley.transform.position);

        void OnEntityDeath(NPCPlayerApex npc, HitInfo info)
        {
            if (!_npcs.Contains(npc)) return;

            _npcs.Remove(npc);

            if (_npcs.Count == 0)
            {
                MessageAll("EventEnded");
            }
        }

        void OnEntityDismounted(BaseMountable mountable, NPCPlayerApex npc)
        {
            if (npc == null || !_npcs.Contains(npc)) return;

            npc.SetFact(NPCPlayerApex.Facts.IsMounted, (byte) 0, true, true);
            npc.SetFact(NPCPlayerApex.Facts.WantsToDismount, (byte) 0, true, true);
            npc.SetFact(NPCPlayerApex.Facts.CanNotWieldWeapon, (byte) 0, true, true);
            npc.Resume();
        }
        #endregion

        #region Core
        void SpawnEvent(Vector3 position)
        {
            if (!_hasLaunch || _eventActive) return;

            CH47HelicopterAIController chinook = GameManager.server.CreateEntity(_ch47Prefab, _chinookPosition, _landingRotation) as CH47HelicopterAIController;
            if (chinook == null) return;

            chinook.SetLandingTarget(_landingPosition);
            chinook.SetMoveTarget(_landingPosition);
            chinook.hoverHeight = 1.5f;
            chinook.Spawn();
            chinook.CancelInvoke(new Action(chinook.SpawnScientists));

            for (int i = 0; i < _config.GuardMaxSpawn; i++)
            {
                SpawnScientist(chinook, _config.GuardSettings.GetRandom(), chinook.transform.position + (chinook.transform.forward * 10f), position);
            }

            for (int j = 0; j < 1; j++)
            {
                SpawnScientist(chinook, _config.GuardSettings.GetRandom(), chinook.transform.position - (chinook.transform.forward * 5f), position);
            }

            _eventActive = true;

            MessageAll("EventStart");
        }

        void SpawnScientist(CH47HelicopterAIController chinook, GuardSetting settings, Vector3 position, Vector3 eventPos)
        {
            BaseEntity entity = GameManager.server.CreateEntity(chinook.scientistPrefab.resourcePath, position, Quaternion.identity);

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
                component.Stats.MaxRoamRange    = settings.GetRoamRange();
                component.Stats.Hostility       = 1;
                component.Stats.Defensiveness   = 1;
                component.InitFacts();
                component.Mount((BaseMountable)chinook);
                component.gameObject.AddComponent<BradleyGuard>()?.Init(RandomCircle(eventPos, 10));

                _npcs.Add(component);

                GiveKit(component, settings.Kit, settings.UseKit);
            }
            else
            {
                entity.Kill(BaseEntity.DestroyMode.None);
            }
        }

        void GiveKit(NPCPlayerApex npc, string kit, bool give)
        {
            if (!give) return;

            npc.inventory.Strip();

            Interface.Oxide.CallHook("GiveKit", npc, kit);
        }

        CH47LandingZone CreateLandingZone()
        {
            return new GameObject(_landingName) {
                layer     = 16, 
                transform = { 
                    position = _landingPosition, 
                    rotation = _landingRotation 
                }
            }.AddComponent<CH47LandingZone>();
        }

        void CleanUp()
        {
            ClearGuards();
            ClearZones();
        }

        void ClearZones()
        {
            foreach(CH47LandingZone zone in _zones)
            {
                UnityEngine.GameObject.Destroy(zone.gameObject);
            }

            _zones.Clear();
        }

        void ClearGuards()
        {
            foreach(NPCPlayerApex npc in _npcs)
            {
                if (npc != null && !npc.IsDestroyed)
                {
                    npc?.Kill();
                }
            }

            _npcs.Clear();
        }

        void CheckLandingPoint()
        {
            foreach (MonumentInfo monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                if (!monument.gameObject.name.Contains("launch_site_1")) continue;

                _hasLaunch = true;          

                _landingRotation = monument.transform.rotation;
                _landingPosition = monument.transform.position + monument.transform.right * 125f;
                _landingPosition.y += 5f;

                _chinookPosition = monument.transform.position + -monument.transform.right * 250f;
                _chinookPosition.y += 150f;
            };
        }
        #endregion

        #region Classes
        class BradleyGuard : MonoBehaviour
        {
            NPCPlayerApex _npc;
            Vector3 _targetDestination;

            public void Init(Vector3 targetDestination)
            {
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

                if (_npc.AttackTarget == null && distance > 15f || _npc.AttackTarget != null && distance > _npc.Stats.MaxRoamRange)
                {
                    if (_npc.GetNavAgent == null || !_npc.GetNavAgent.isOnNavMesh)
                        _npc.finalDestination = _targetDestination;
                    else
                        _npc.GetNavAgent.SetDestination(_targetDestination);

                    _npc.Destination = _targetDestination;
                    _npc.SetFact(NPCPlayerApex.Facts.Speed, (byte)NPCPlayerApex.SpeedEnum.Sprint, true, true);
                }
            }
        }
        #endregion

        #region Helpers
        string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);

        static Vector3 RandomCircle(Vector3 center, float radius)
        {
            float angle = UnityEngine.Random.Range(0f, 100f) * 360;
            Vector3 pos = center;
            pos.x += radius * Mathf.Sin(angle * Mathf.Deg2Rad);
            pos.z += radius * Mathf.Cos(angle * Mathf.Deg2Rad);
            return pos;
        }

        void MessageAll(string key)
        {
            foreach(BasePlayer player in BasePlayer.activePlayerList.Where(x => x.IsConnected))
            {
                player.ChatMessage(Lang(key, player.UserIDString));
            }
        }
        #endregion
    }
}