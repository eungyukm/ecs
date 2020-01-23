﻿using System.Collections;
using System.Collections.Generic;
using EntityId = System.Int32;
using Tick = System.UInt64;
using RPCId = System.Int32;

namespace ME.ECS {

    [System.Flags]
    public enum WorldStep : byte {

        None = 0x0,
        
        // Default types
        Modules = 0x1,
        Systems = 0x2,
        Plugins = 0x4,
        
        // Tick type
        LogicTick = 0x8,
        VisualTick = 0x10,
        Simulate = 0x20,
        
        // Fast links
        ModulesVisualTick = WorldStep.Modules | WorldStep.VisualTick,
        SystemsVisualTick = WorldStep.Systems | WorldStep.VisualTick,
        ModulesLogicTick = WorldStep.Modules | WorldStep.LogicTick,
        PluginsLogicTick = WorldStep.Plugins | WorldStep.LogicTick,
        SystemsLogicTick = WorldStep.Systems | WorldStep.LogicTick,
        PluginsLogicSimulate = WorldStep.Plugins | WorldStep.Simulate | WorldStep.LogicTick,

    }

    public class InStateException : System.Exception {

        public InStateException() : base("[World] Could not perform action because current step is in state (" + Worlds.currentWorld.GetCurrentStep().ToString() + ").") {}

    }

    public class OutOfStateException : System.Exception {

        public OutOfStateException() : base("[World] Could not perform action because current step is out of state (" + Worlds.currentWorld.GetCurrentStep().ToString() + ").") {}

    }

    #if ECS_COMPILE_IL2CPP_OPTIONS
    [Unity.IL2CPP.CompilerServices.Il2CppSetOptionAttribute(Unity.IL2CPP.CompilerServices.Option.NullChecks, false),
     Unity.IL2CPP.CompilerServices.Il2CppSetOptionAttribute(Unity.IL2CPP.CompilerServices.Option.ArrayBoundsChecks, false),
     Unity.IL2CPP.CompilerServices.Il2CppSetOptionAttribute(Unity.IL2CPP.CompilerServices.Option.DivideByZeroChecks, false)]
    #endif
    public partial class World<TState> : IWorld<TState>, IPoolableSpawn, IPoolableRecycle where TState : class, IState<TState>, new() {

        private const int SYSTEMS_CAPACITY = 100;
        private const int MODULES_CAPACITY = 100;
        private const int ENTITIES_CACHE_CAPACITY = 100;
        private const int FILTERS_CAPACITY = 100;
        private const int COMPONENTS_CAPACITY = 100;
        private const int CAPACITIES_CAPACITY = 100;
        private const int ENTITIES_DIRECT_CACHE_CAPACITY = 100;

        private static class EntitiesDirectCache<TStateInner, TEntity> where TEntity : struct, IEntity where TStateInner : class, IState<TState> {

            internal static Dictionary<long, TEntity> dataByEntityId = new Dictionary<long, TEntity>(World<TState>.ENTITIES_DIRECT_CACHE_CAPACITY);
            internal static Dictionary<int, List<TEntity>> entitiesList = new Dictionary<int, List<TEntity>>(4);

        }

        private static int registryWorldId = 0;

        public int id { get; private set; }
        
        private TState resetState;
        private TState currentState;
        private WorldStep currentStep;
        private List<ISystem<TState>> systems;
        private List<IModule<TState>> modules;
        private Dictionary<int, int> capacityCache;

        // State cache:
        //private Dictionary<int, IList> entitiesCache; // key = typeof(T:IData), value = list of T:IData
        private Dictionary<int, IList> filtersCache; // key = typeof(T:IFilter), value = list of T:IFilter
        private Dictionary<int, IComponents<TState>> componentsCache; // key = typeof(T:IData), value = list of T:Components
        
        private float tickTime;
        private double timeSinceStart;

        public World() {

            this.currentState = null;
            this.resetState = null;
            this.currentStep = WorldStep.None;

        }

        public void SetId(int forcedWorldId = 0) {

            if (forcedWorldId > 0) {

                this.id = forcedWorldId;

            } else {
                
                this.id = ++World<TState>.registryWorldId;
                
            }

        }

        public UnityEngine.Vector3 GetRandomInSphere(UnityEngine.Vector3 center, float radius) {
            
            UnityEngine.Random.state = this.currentState.randomState;
            var spherePoint = UnityEngine.Random.insideUnitSphere * radius;
            return spherePoint + center;

        }

        public int GetRandomRange(int from, int to) {

            UnityEngine.Random.state = this.currentState.randomState;
            var result = UnityEngine.Random.Range(from, to);
            this.currentState.randomState = UnityEngine.Random.state;
            return result;

        }

        public float GetRandomRange(float from, float to) {

            UnityEngine.Random.state = this.currentState.randomState;
            var result = UnityEngine.Random.Range(from, to);
            this.currentState.randomState = UnityEngine.Random.state;
            return result;

        }

        public float GetRandomValue() {

            UnityEngine.Random.state = this.currentState.randomState;
            var result = UnityEngine.Random.value;
            this.currentState.randomState = UnityEngine.Random.state;
            return result;
            
        }

        void IWorldBase.SetTickTime(float tickTime) {

            this.tickTime = tickTime;

        }
        
        float IWorldBase.GetTickTime() {

            return this.tickTime;

        }

        double IWorldBase.GetTimeSinceStart() {

            return this.timeSinceStart;

        }

        void IWorldBase.SetTimeSinceStart(double time) {

            this.timeSinceStart = time;

        }

        void IPoolableSpawn.OnSpawn() {
            
            this.systems = PoolList<ISystem<TState>>.Spawn(World<TState>.SYSTEMS_CAPACITY);
            this.modules = PoolList<IModule<TState>>.Spawn(World<TState>.MODULES_CAPACITY);
            //this.entitiesCache = PoolDictionary<int, IList>.Spawn(World<TState>.ENTITIES_CACHE_CAPACITY);
            this.filtersCache = PoolDictionary<int, IList>.Spawn(World<TState>.FILTERS_CAPACITY);
            this.componentsCache = PoolDictionary<int, IComponents<TState>>.Spawn(World<TState>.COMPONENTS_CAPACITY);
            this.capacityCache = PoolDictionary<int, int>.Spawn(World<TState>.CAPACITIES_CAPACITY);

        }

        void IPoolableRecycle.OnRecycle() {
            
            WorldUtilities.ReleaseState(ref this.resetState);
            WorldUtilities.ReleaseState(ref this.currentState);

            for (int i = 0; i < this.systems.Count; ++i) {
                
                this.systems[i].OnDeconstruct();
                PoolSystems.Recycle(this.systems[i]);

            }
            PoolList<ISystem<TState>>.Recycle(ref this.systems);
            
            for (int i = 0; i < this.modules.Count; ++i) {
                
                this.modules[i].OnDeconstruct();
                PoolModules.Recycle(this.modules[i]);

            }
            PoolList<IModule<TState>>.Recycle(ref this.modules);
            
            //PoolDictionary<int, IList>.Recycle(ref this.entitiesCache);
            PoolDictionary<int, IList>.Recycle(ref this.filtersCache);
            PoolDictionary<int, IComponents<TState>>.Recycle(ref this.componentsCache);
            PoolDictionary<int, int>.Recycle(ref this.capacityCache);

        }

        public bool GetEntityData<T>(EntityId entityId, out T data) where T : struct, IEntity {

            T internalData;
            if (EntitiesDirectCache<TState, T>.dataByEntityId.TryGetValue(MathUtils.GetKey(this.id, entityId), out internalData) == true) {

                data = internalData;
                return true;

            }

            data = default(T);
            return false;

        }

        public void SetCapacity<T>(int capacity) where T : IEntity {

            var code = WorldUtilities.GetKey<T>();
            this.capacityCache.Add(code, capacity);

        }

        public int GetCapacity<T>() where T : IEntity {

            var code = WorldUtilities.GetKey<T>();
            return this.GetCapacity<T>(code);

        }

        public int GetCapacity<T>(int code) {

            int cap;
            if (this.capacityCache.TryGetValue(code, out cap) == true) {

                return cap;

            }

            return 100;

        }

        public void Register<TEntity>(ref Components<TEntity, TState> componentsRef, bool freeze, bool restore) where TEntity : struct, IEntity {
            
            var code = WorldUtilities.GetKey<TEntity>();
            var capacity = 100;
            if (componentsRef == null) {

                componentsRef = PoolClass<Components<TEntity, TState>>.Spawn();
                componentsRef.Initialize(capacity);
                componentsRef.SetFreeze(freeze);

            } else {

                componentsRef.SetFreeze(freeze);

            }

            if (freeze == false) {

                if (this.componentsCache.ContainsKey(code) == true) {

                    this.componentsCache[code] = componentsRef;

                } else {

                    this.componentsCache.Add(code, componentsRef);

                }
                
            }

            /*if (restore == true) {

                var data = componentsRef.GetData();
                foreach (var item in data) {

                    var components = item.Value;
                    for (int i = 0, count = components.Count; i < count; ++i) {

                        this.AddComponent<TEntity, IComponent<TState, TEntity>>(Entity.Create<TEntity>(item.Key), components[i]);

                    }

                }

            }*/

        }

        public void Register<TEntity>(ref Filter<TEntity> filterRef, bool freeze, bool restore) where TEntity : struct, IEntity {

            this.RegisterPluginsModuleForEntity<TEntity>();
            
            var code = WorldUtilities.GetKey<TEntity>();
            var capacity = this.GetCapacity<TEntity>(code);
            if (filterRef == null) {

                filterRef = PoolClass<Filter<TEntity>>.Spawn();
                filterRef.Initialize(capacity);
                filterRef.SetFreeze(freeze);

            } else {

                filterRef.SetFreeze(freeze);

            }

            if (freeze == false) {

                IList list;
                if (this.filtersCache.TryGetValue(code, out list) == true) {

                    ((List<Filter<TEntity>>)list).Add(filterRef);

                } else {

                    list = PoolList<Filter<TEntity>>.Spawn(capacity);
                    ((List<Filter<TEntity>>)list).Add(filterRef);
                    this.filtersCache.Add(code, list);

                }

            }

            if (restore == true) {

                // Update entities cache
                for (int i = 0; i < filterRef.Count; ++i) {

                    var item = filterRef[i];
                    var list = PoolList<TEntity>.Spawn(capacity);
                    list.Add(item);
                    this.AddEntity(item, updateFilters: false);

                }

                this.UpdateFilters<TEntity>(code);

            }

        }

        public void UpdateFilters<TEntity>() where TEntity : struct, IEntity {

            this.UpdateFilters<TEntity>(WorldUtilities.GetKey<TEntity>());

        }

        public void UpdateFilters<TEntity>(int code) where TEntity : struct, IEntity {

            List<TEntity> listEntities;
            if (EntitiesDirectCache<TState, TEntity>.entitiesList.TryGetValue(this.id, out listEntities) == true) {

                IList listFilters;
                if (this.filtersCache.TryGetValue(code, out listFilters) == true) {

                    for (int i = 0, count = listFilters.Count; i < count; ++i) {

                        var filter = (Filter<TEntity>)listFilters[i];
                        filter.SetData(listEntities);

                    }

                }

            }

        }

        public bool HasResetState() {

            return this.resetState != null;

        }

        public void SaveResetState() {

            if (this.resetState != null) WorldUtilities.ReleaseState(ref this.resetState);
            this.resetState = WorldUtilities.CreateState<TState>();
            this.resetState.Initialize(this, freeze: true, restore: false);
            this.resetState.CopyFrom(this.GetState());

        }

        public TState GetResetState() {

            return this.resetState;

        }

        public void SetState(TState state) {

            //UnityEngine.Debug.Log(UnityEngine.Time.frameCount + " World SetState(): " + state.tick);
            
            //this.entitiesCache.Clear();
            //this.entitiesDirectCache.Clear();
            this.filtersCache.Clear();
            this.componentsCache.Clear();

            if (this.currentState != null && this.currentState != state) WorldUtilities.ReleaseState(ref this.currentState);
            this.currentState = state;
            state.Initialize(this, freeze: false, restore: true);

            UnityEngine.Random.InitState(0);
            state.randomState = UnityEngine.Random.state;

        }

        public TState GetState() {

            return this.currentState;

        }

        public WorldStep GetCurrentStep() {

            return this.currentStep;

        }

        public bool HasStep(WorldStep step) {

            return (this.currentStep & step) != 0;

        }

        private Entity CreateNewEntity<T>() where T : IEntity {

            return Entity.Create<T>(++this.GetState().entityId);

        }

        public void UpdateEntityCache<T>(T data) where T : struct, IEntity {

            lock (EntitiesDirectCache<TState, T>.dataByEntityId) {

                var key = MathUtils.GetKey(this.id, data.entity.id);
                if (EntitiesDirectCache<TState, T>.dataByEntityId.ContainsKey(key) == true) {

                    EntitiesDirectCache<TState, T>.dataByEntityId[key] = data;
                    
                } else {

                    EntitiesDirectCache<TState, T>.dataByEntityId.Add(key, data);

                }

            }

        }

        public Entity AddEntity<T>(T data, bool updateFilters = true) where T : struct, IEntity {

            if (data.entity.id == 0) data.entity = this.CreateNewEntity<T>();

            List<T> entitiesList;
            if (EntitiesDirectCache<TState, T>.entitiesList.TryGetValue(this.id, out entitiesList) == false) {

                entitiesList = PoolList<T>.Spawn(World<TState>.ENTITIES_CACHE_CAPACITY);
                entitiesList.Add(data);
                EntitiesDirectCache<TState, T>.entitiesList.Add(this.id, entitiesList);

            } else {
                
                entitiesList.Add(data);
                
            }
            
            if (updateFilters == true) {

                var code = WorldUtilities.GetKey(data);
                this.UpdateFilters<T>(code);

            }
            
            this.UpdateEntityCache(data);

            return data.entity;

        }

        public void ForEachEntity<TEntity>(List<TEntity> output) where TEntity : struct, IEntity {

            output.Clear();
            List<TEntity> listEntities;
            if (EntitiesDirectCache<TState, TEntity>.entitiesList.TryGetValue(this.id, out listEntities) == true) {
                
                output.AddRange(listEntities);
                
            }

        }

        public bool HasEntity<TEntity>(EntityId entityId) where TEntity : struct, IEntity {
            
            var key = MathUtils.GetKey(this.id, entityId);
            return EntitiesDirectCache<TState, TEntity>.dataByEntityId.ContainsKey(key);

        }

        public void RemoveEntities<TEntity>(TEntity data) where TEntity : struct, IEntity {

            var key = MathUtils.GetKey(this.id, data.entity.id);
            if (EntitiesDirectCache<TState, TEntity>.dataByEntityId.Remove(key) == true) {

                List<TEntity> listEntities;
                if (EntitiesDirectCache<TState, TEntity>.entitiesList.TryGetValue(this.id, out listEntities) == true) {
                
                    this.DestroyEntityPlugins<TEntity>(data.entity);
                    listEntities.Remove(data);
                    this.RemoveComponents(data.entity);

                }

            }

        }

        public void RemoveEntity<TEntity>(Entity entity) where TEntity : struct, IEntity {

            var key = MathUtils.GetKey(this.id, entity.id);
            if (EntitiesDirectCache<TState, TEntity>.dataByEntityId.Remove(key) == true) {

                List<TEntity> list;
                if (EntitiesDirectCache<TState, TEntity>.entitiesList.TryGetValue(this.id, out list) == true) {
                    
                    for (int i = 0, count = list.Count; i < count; ++i) {

                        if (list[i].entity.id == entity.id) {

                            this.DestroyEntityPlugins<TEntity>(entity);
                            list.RemoveAt(i);
                            this.RemoveComponents(entity);
                            break;

                        }

                    }
                    
                }

            }

        }
        
        /// <summary>
        /// Get first module by type
        /// </summary>
        /// <typeparam name="TModule"></typeparam>
        /// <returns></returns>
        public TModule GetModule<TModule>() where TModule : IModuleBase {

            for (int i = 0, count = this.modules.Count; i < count; ++i) {

                var module = this.modules[i];
                if (module is TModule tModule) {

                    return tModule;

                }

            }

            return default;

        }
        
        public List<TModule> GetModules<TModule>(List<TModule> output) where TModule : IModuleBase {

            output.Clear();
            for (int i = 0, count = this.modules.Count; i < count; ++i) {

                var module = this.modules[i];
                if (module is TModule tModule) {

                    output.Add(tModule);

                }

            }

            return output;

        }

        public bool HasModule<TModule>() where TModule : class, IModule<TState> {

            for (int i = 0, count = this.modules.Count; i < count; ++i) {

                var module = this.modules[i];
                if (module is TModule) {

                    return true;

                }

            }

            return false;

        }

        /// <summary>
        /// Add module by type
        /// Retrieve module from pool, OnConstruct() call
        /// </summary>
        /// <typeparam name="TModule"></typeparam>
        /// <returns></returns>
        public bool AddModule<TModule>() where TModule : class, IModule<TState>, new() {
            
            var instance = PoolModules.Spawn<TModule>();
            instance.world = this;
            if (instance is IModuleValidation instanceValidate) {

                if (instanceValidate.CouldBeAdded() == false) {

                    instance.world = null;
                    PoolModules.Recycle(ref instance);
                    throw new System.Exception("Couldn't add new module `" + instanceValidate + "`(" + nameof(TModule) + ") because of CouldBeAdded() returns false.");
                    //return false;
                    
                }

            }
            
            this.modules.Add(instance);
            instance.OnConstruct();

            return true;

        }

        /// <summary>
        /// Remove modules by type
        /// Return modules into pool, OnDeconstruct() call
        /// </summary>
        public void RemoveModules<TModule>() where TModule : class, IModule<TState>, new() {

            for (int i = 0, count = this.modules.Count; i < count; ++i) {

                var module = this.modules[i];
                if (module is TModule tModule) {

                    PoolModules.Recycle(tModule);
                    this.modules.RemoveAt(i);
                    module.OnDeconstruct();
                    --i;
                    --count;

                }

            }

        }
        
        /// <summary>
        /// Add system by type
        /// Retrieve system from pool, OnConstruct() call
        /// </summary>
        /// <typeparam name="TSystem"></typeparam>
        public bool AddSystem<TSystem>() where TSystem : class, ISystem<TState>, new() {

            var instance = PoolSystems.Spawn<TSystem>();
            instance.world = this;
            if (instance is ISystemValidation instanceValidate) {

                if (instanceValidate.CouldBeAdded() == false) {

                    instance.world = null;
                    PoolSystems.Recycle(ref instance);
                    return false;
                    
                }

            }
            
            this.systems.Add(instance);
            instance.OnConstruct();

            return true;

        }

        /// <summary>
        /// Add system manually
        /// Pool will not be used, OnConstruct() call
        /// </summary>
        /// <param name="instance"></param>
        public bool AddSystem(ISystem<TState> instance) {

            instance.world = this;
            if (instance is ISystemValidation instanceValidate) {

                if (instanceValidate.CouldBeAdded() == false) {
                    
                    instance.world = null;
                    return false;
                    
                }

            }
            
            this.systems.Add(instance);
            instance.OnConstruct();

            return true;

        }

        /// <summary>
        /// Remove system manually
        /// Pool will not be used, OnDeconstruct() call
        /// </summary>
        /// <param name="instance"></param>
        public void RemoveSystem(ISystem<TState> instance) {

            instance.world = null;
            this.systems.Remove(instance);
            instance.OnDeconstruct();

        }

        /// <summary>
        /// Get first system by type
        /// </summary>
        /// <typeparam name="TSystem"></typeparam>
        /// <returns></returns>
        public TSystem GetSystem<TSystem>() where TSystem : ISystemBase {

            for (int i = 0, count = this.systems.Count; i < count; ++i) {

                var system = this.systems[i];
                if (system is TSystem tSystem) {

                    return tSystem;

                }

            }

            return default;

        }

        /// <summary>
        /// Remove systems by type
        /// Return systems into pool, OnDeconstruct() call
        /// </summary>
        public void RemoveSystems<TSystem>() where TSystem : class, ISystemBase, new() {

            for (int i = 0, count = this.systems.Count; i < count; ++i) {

                var system = this.systems[i];
                if (system is TSystem tSystem) {

                    PoolSystems.Recycle(tSystem);
                    this.systems.RemoveAt(i);
                    system.OnDeconstruct();
                    --i;
                    --count;

                }

            }

        }
        
        public TEntity RunComponents<TEntity>(TEntity data, float deltaTime, int index) where TEntity : struct, IEntity {

            var code = WorldUtilities.GetKey(data);
            IComponents<TState> componentsContainer;

            var result = false;
            lock (this.componentsCache) {

                result = this.componentsCache.TryGetValue(code, out componentsContainer);
                
            }

            if (result == true) {

                var item = (Components<TEntity, TState>)componentsContainer;
                var dic = item.GetData();
                HashSet<IComponent<TState, TEntity>> components;
                if (dic.TryGetValue(data.entity.id, out components) == true) {

                    foreach (var listItem in components) {
                        
                        listItem.AdvanceTick(this.currentState, ref data, deltaTime, index);
                        
                    }

                }

            }

            return data;

        }

        public void Update(float deltaTime) {

            if (deltaTime < 0f) return;
            
            var state = this.GetState();
            
            // Setup current static variables
            Worlds.currentWorld = this;
            Worlds<TState>.currentWorld = this;
            Worlds<TState>.currentState = state;

            // Update time
            var prevTick = state.tick;
            this.timeSinceStart += deltaTime;
            if (this.timeSinceStart < 0d) this.timeSinceStart = 0d;

            ////////////////
            this.currentStep = WorldStep.ModulesVisualTick;
            ////////////////
            {

                for (int i = 0, count = this.modules.Count; i < count; ++i) {

                    this.modules[i].Update(state, deltaTime);

                }

            }

            ////////////////
            // Update Logic Tick
            ////////////////
            this.Simulate(prevTick + 1, state.tick + 1);

            ////////////////
            this.currentStep = WorldStep.SystemsVisualTick;
            ////////////////
            {

                for (int i = 0, count = this.systems.Count; i < count; ++i) {

                    this.systems[i].Update(state, deltaTime);

                }

            }
            ////////////////
            this.currentStep = WorldStep.None;
            ////////////////
            
        }

        public void Simulate(Tick from, Tick to) {
            
            if (from > to) {

                //UnityEngine.Debug.LogError( UnityEngine.Time.frameCount + " From: " + from + ", To: " + to);
                return;

            }

            var state = this.GetState();
            
            var fixedDeltaTime = ((IWorldBase)this).GetTickTime();
            for (Tick tick = from; tick < to; ++tick) {

                state.tick = tick;
                
                ////////////////
                this.currentStep = WorldStep.ModulesLogicTick;
                ////////////////
                {
                    
                    for (int i = 0, count = this.modules.Count; i < count; ++i) {

                        this.modules[i].AdvanceTick(state, fixedDeltaTime);

                    }
                    
                }
                ////////////////
                this.currentStep = WorldStep.PluginsLogicTick;
                ////////////////
                {
                    
                    this.PlayPluginsForTick(tick);
                    
                }
                ////////////////
                this.currentStep = WorldStep.SystemsLogicTick;
                ////////////////
                {
                    
                    for (int i = 0, count = this.systems.Count; i < count; ++i) {

                        this.systems[i].AdvanceTick(state, fixedDeltaTime);

                    }

                }

                this.RemoveComponentsOnce<IComponentOnceBase>();

            }
            
            ////////////////
            this.currentStep = WorldStep.PluginsLogicSimulate;
            ////////////////
            {
                
                this.SimulatePluginsForTicks(from, to);
                
            }

        }

        [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void PlayPluginsForTick(Tick tick) {
            
            this.PlayPlugin1ForTick(tick);
            this.PlayPlugin2ForTick(tick);
            this.PlayPlugin3ForTick(tick);
            this.PlayPlugin4ForTick(tick);
            this.PlayPlugin5ForTick(tick);
            this.PlayPlugin6ForTick(tick);
            this.PlayPlugin7ForTick(tick);
            this.PlayPlugin8ForTick(tick);
            this.PlayPlugin9ForTick(tick);
            
        }

        [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void SimulatePluginsForTicks(Tick from, Tick to) {
            
            this.SimulatePlugin1ForTicks(from, to);
            this.SimulatePlugin2ForTicks(from, to);
            this.SimulatePlugin3ForTicks(from, to);
            this.SimulatePlugin4ForTicks(from, to);
            this.SimulatePlugin5ForTicks(from, to);
            this.SimulatePlugin6ForTicks(from, to);
            this.SimulatePlugin7ForTicks(from, to);
            this.SimulatePlugin8ForTicks(from, to);
            this.SimulatePlugin9ForTicks(from, to);
            
        }

        [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void RegisterPluginsModuleForEntity<TEntity>() where TEntity : struct, IEntity {
            
            this.RegisterPlugin1ModuleForEntity<TEntity>();
            this.RegisterPlugin2ModuleForEntity<TEntity>();
            this.RegisterPlugin3ModuleForEntity<TEntity>();
            this.RegisterPlugin4ModuleForEntity<TEntity>();
            this.RegisterPlugin5ModuleForEntity<TEntity>();
            this.RegisterPlugin6ModuleForEntity<TEntity>();
            this.RegisterPlugin7ModuleForEntity<TEntity>();
            this.RegisterPlugin8ModuleForEntity<TEntity>();
            this.RegisterPlugin9ModuleForEntity<TEntity>();

        }

        [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void DestroyEntityPlugins<TEntity>(Entity entity) where TEntity : struct, IEntity {
            
            this.DestroyEntityPlugin1<TEntity>(entity);
            this.DestroyEntityPlugin2<TEntity>(entity);
            this.DestroyEntityPlugin3<TEntity>(entity);
            this.DestroyEntityPlugin4<TEntity>(entity);
            this.DestroyEntityPlugin5<TEntity>(entity);
            this.DestroyEntityPlugin6<TEntity>(entity);
            this.DestroyEntityPlugin7<TEntity>(entity);
            this.DestroyEntityPlugin8<TEntity>(entity);
            this.DestroyEntityPlugin9<TEntity>(entity);

        }

        partial void DestroyEntityPlugin1<TEntity>(Entity entity) where TEntity : struct, IEntity;
        partial void DestroyEntityPlugin2<TEntity>(Entity entity) where TEntity : struct, IEntity;
        partial void DestroyEntityPlugin3<TEntity>(Entity entity) where TEntity : struct, IEntity;
        partial void DestroyEntityPlugin4<TEntity>(Entity entity) where TEntity : struct, IEntity;
        partial void DestroyEntityPlugin5<TEntity>(Entity entity) where TEntity : struct, IEntity;
        partial void DestroyEntityPlugin6<TEntity>(Entity entity) where TEntity : struct, IEntity;
        partial void DestroyEntityPlugin7<TEntity>(Entity entity) where TEntity : struct, IEntity;
        partial void DestroyEntityPlugin8<TEntity>(Entity entity) where TEntity : struct, IEntity;
        partial void DestroyEntityPlugin9<TEntity>(Entity entity) where TEntity : struct, IEntity;

        partial void RegisterPlugin1ModuleForEntity<TEntity>() where TEntity : struct, IEntity;
        partial void RegisterPlugin2ModuleForEntity<TEntity>() where TEntity : struct, IEntity;
        partial void RegisterPlugin3ModuleForEntity<TEntity>() where TEntity : struct, IEntity;
        partial void RegisterPlugin4ModuleForEntity<TEntity>() where TEntity : struct, IEntity;
        partial void RegisterPlugin5ModuleForEntity<TEntity>() where TEntity : struct, IEntity;
        partial void RegisterPlugin6ModuleForEntity<TEntity>() where TEntity : struct, IEntity;
        partial void RegisterPlugin7ModuleForEntity<TEntity>() where TEntity : struct, IEntity;
        partial void RegisterPlugin8ModuleForEntity<TEntity>() where TEntity : struct, IEntity;
        partial void RegisterPlugin9ModuleForEntity<TEntity>() where TEntity : struct, IEntity;
        
        partial void PlayPlugin1ForTick(Tick tick);
        partial void PlayPlugin2ForTick(Tick tick);
        partial void PlayPlugin3ForTick(Tick tick);
        partial void PlayPlugin4ForTick(Tick tick);
        partial void PlayPlugin5ForTick(Tick tick);
        partial void PlayPlugin6ForTick(Tick tick);
        partial void PlayPlugin7ForTick(Tick tick);
        partial void PlayPlugin8ForTick(Tick tick);
        partial void PlayPlugin9ForTick(Tick tick);

        partial void SimulatePlugin1ForTicks(Tick from, Tick to);
        partial void SimulatePlugin2ForTicks(Tick from, Tick to);
        partial void SimulatePlugin3ForTicks(Tick from, Tick to);
        partial void SimulatePlugin4ForTicks(Tick from, Tick to);
        partial void SimulatePlugin5ForTicks(Tick from, Tick to);
        partial void SimulatePlugin6ForTicks(Tick from, Tick to);
        partial void SimulatePlugin7ForTicks(Tick from, Tick to);
        partial void SimulatePlugin8ForTicks(Tick from, Tick to);
        partial void SimulatePlugin9ForTicks(Tick from, Tick to);

        /// <summary>
        /// Add component for current entity only (create component data)
        /// </summary>
        /// <param name="entity"></param>
        /// <typeparam name="TEntity"></typeparam>
        /// <typeparam name="TComponent"></typeparam>
        public TComponent AddComponent<TEntity, TComponent>(Entity entity) where TComponent : class, IComponentBase, new() where TEntity : struct, IEntity {

            TComponent data;
            lock (this) {

                data = PoolComponents.Spawn<TComponent>();

            }

            return this.AddComponent<TEntity, TComponent>(entity, (IComponent<TState, TEntity>)data);

        }

        /// <summary>
        /// Add component for entity
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="data"></param>
        /// <typeparam name="TEntity"></typeparam>
        /// <typeparam name="TComponent"></typeparam>
        public TComponent AddComponent<TEntity, TComponent>(Entity entity, IComponent<TState, TEntity> data) where TComponent : class, IComponentBase where TEntity : struct, IEntity {

            lock (this.componentsCache) {

                var code = WorldUtilities.GetKey(entity);
                IComponents<TState> components;
                if (this.componentsCache.TryGetValue(code, out components) == true) {

                    var item = (Components<TEntity, TState>)components;
                    item.Add(entity, data);

                } else {

                    components = PoolClass<Components<TEntity, TState>>.Spawn();
                    ((Components<TEntity, TState>)components).Add(entity, data);
                    this.componentsCache.Add(code, components);

                }

            }

            return (TComponent)data;

        }

        public TComponent GetComponent<TEntity, TComponent>(Entity entity) where TComponent : class, IComponent<TState, TEntity> where TEntity : struct, IEntity {

            var code = WorldUtilities.GetKey(entity);
            IComponents<TState> components;
            var result = false;
            lock (this.componentsCache) {

                result = this.componentsCache.TryGetValue(code, out components);
            
            }

            if (result == true) {

                return ((Components<TEntity, TState>)components).GetFirst<TComponent>(entity);

            }

            return null;
            
        }

        public void ForEachComponent<TEntity, TComponent>(Entity entity, List<TComponent> output) where TComponent : class, IComponent<TState, TEntity> where TEntity : struct, IEntity {

            output.Clear();
            var code = WorldUtilities.GetKey(entity);
            IComponents<TState> components;
            var result = false;
            lock (this.componentsCache) {
                
                result = this.componentsCache.TryGetValue(code, out components);
                
            }

            if (result == true) {

                ((Components<TEntity, TState>)components).ForEach(entity, output);

            }

        }

        /// <summary>
        /// Check is component exists on entity
        /// </summary>
        /// <param name="entity"></param>
        /// <typeparam name="TEntity"></typeparam>
        /// <typeparam name="TComponent"></typeparam>
        /// <returns></returns>
        public bool HasComponent<TEntity, TComponent>(Entity entity) where TComponent : IComponent<TState, TEntity> where TEntity : struct, IEntity {

            lock (this.componentsCache) {

                var code = WorldUtilities.GetKey(entity);
                IComponents<TState> components;
                if (this.componentsCache.TryGetValue(code, out components) == true) {

                    return ((Components<TEntity, TState>)components).Contains<TComponent>(entity);

                }

            }

            return false;

        }

        /// <summary>
        /// Check is component exists on entity
        /// </summary>
        /// <param name="entity"></param>
        /// <typeparam name="TEntity"></typeparam>
        /// <typeparam name="TComponent"></typeparam>
        /// <returns></returns>
        public bool HasComponentOnce<TEntity, TComponent>(Entity entity) where TComponent : IComponentOnce<TState, TEntity> where TEntity : struct, IEntity {

            lock (this.componentsCache) {

                var code = WorldUtilities.GetKey(entity);
                IComponents<TState> components;
                if (this.componentsCache.TryGetValue(code, out components) == true) {

                    return ((Components<TEntity, TState>)components).ContainsOnce<TComponent>(entity);

                }

            }

            return false;

        }

        /// <summary>
        /// Remove all components from certain entity
        /// </summary>
        /// <param name="entity"></param>
        public void RemoveComponents(Entity entity) {

            var code = WorldUtilities.GetKey(entity);
            IComponents<TState> componentsContainer;

            bool result;
            lock (this.componentsCache) {

                result = this.componentsCache.TryGetValue(code, out componentsContainer);

            }

            if (result == true) {

                componentsContainer.RemoveAll(entity);

            }

        }

        /// <summary>
        /// Remove all components with type from certain entity by predicate
        /// </summary>
        /// <param name="entity"></param>
        public void RemoveComponentsPredicate<TComponent, TComponentPredicate>(Entity entity, TComponentPredicate predicate) where TComponent : class, IComponentBase where TComponentPredicate : IComponentPredicate<TComponent> {

            lock (this.componentsCache) {

                var code = WorldUtilities.GetKey(entity);
                IComponents<TState> componentsContainer;
                if (this.componentsCache.TryGetValue(code, out componentsContainer) == true) {

                    componentsContainer.RemoveAllPredicate<TComponent, TComponentPredicate>(entity, predicate);

                }

            }

        }

        /// <summary>
        /// Remove all components with type from certain entity
        /// </summary>
        /// <param name="entity"></param>
        public void RemoveComponents<TComponent>(Entity entity) where TComponent : class, IComponentBase {

            lock (this.componentsCache) {

                var code = WorldUtilities.GetKey(entity);
                IComponents<TState> componentsContainer;
                if (this.componentsCache.TryGetValue(code, out componentsContainer) == true) {

                    componentsContainer.RemoveAll<TComponent>(entity);

                }

            }

        }

        /// <summary>
        /// Remove all components with type from certain entity
        /// </summary>
        /// <param name="entity"></param>
        public void RemoveComponentsOnce<TComponent>(Entity entity) where TComponent : class, IComponentOnceBase {

            lock (this.componentsCache) {

                var code = WorldUtilities.GetKey(entity);
                IComponents<TState> componentsContainer;
                if (this.componentsCache.TryGetValue(code, out componentsContainer) == true) {

                    componentsContainer.RemoveAllOnce<TComponent>(entity);

                }

            }

        }

        /// <summary>
        /// Remove all components with type TComponent from all entities
        /// </summary>
        /// <typeparam name="TComponent"></typeparam>
        public void RemoveComponents<TComponent>() where TComponent : class, IComponentBase {

            lock (this.componentsCache) {

                foreach (var components in this.componentsCache) {

                    components.Value.RemoveAll<TComponent>();

                }

            }

        }

        /// <summary>
        /// Remove all components with type TComponent from all entities
        /// </summary>
        /// <typeparam name="TComponent"></typeparam>
        public void RemoveComponentsOnce<TComponent>() where TComponent : class, IComponentOnceBase {

            lock (this.componentsCache) {

                foreach (var components in this.componentsCache) {

                    components.Value.RemoveAllOnce<TComponent>();

                }

            }

        }

    }

}