using System.Collections.Generic;
using System.Reflection;
using System;
using System.Linq;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Entities
{
    /// <summary>
    /// This extends the functionality of the TypeManager class to support hotfix DOTS logic by HybridCLR
    /// Modified based on Entities version 1.3.14
    /// </summary>
    public static partial class TypeManager
    {
        public static void InitialExternalAssemblies(Assembly[] assemblies)
        {    
            if (s_Initialized)
            {     
                
                s_Initialized = false;
                InitializeComponentTypes(assemblies);
                InitializeSystemTypes(assemblies);
                InitializeSharedStatics();
                s_Initialized = true;
                EarlyInitAssemblies(assemblies);
            }
        }

        /// <summary>
        /// Modified by <see cref="InitializeAllComponentTypes"/>
        /// </summary>
        /// <param name="assemblies"></param>
        private static void InitializeComponentTypes(Assembly[] assemblies)
        {
            var combinedComponentTypeSet = CollectComponentTypes(assemblies);
            AddComponentTypes(assemblies, combinedComponentTypeSet);
        }

        /// <summary>
        /// Modified by <see cref="InitializeAllComponentTypes"/>
        /// </summary>
        /// <param name="assemblies"></param>
        /// <returns></returns>
        private static HashSet<Type> CollectComponentTypes(Assembly[] assemblies)
        {
            var combinedComponentTypeSet = new HashSet<Type>();
            foreach (var assembly in assemblies)
            {
                IsAssemblyReferencingEntitiesOrUnityEngine(assembly, out var isAssemblyReferencingEntities,
                    out var isAssemblyReferencingUnityEngine);

#if !DISABLE_TYPEMANAGER_ILPP
                var isAssemblyRelevant = !isAssemblyReferencingEntities && isAssemblyReferencingUnityEngine;
#else
                var isAssemblyRelevant = isAssemblyReferencingEntities || isAssemblyReferencingUnityEngine;
#endif

                if (!isAssemblyRelevant)
                    continue;

                var assemblyTypes = assembly.GetTypes();
                // Register UnityEngine types (Hybrid)
                if (isAssemblyReferencingUnityEngine)
                {
                    foreach (var type in assemblyTypes)
                    {
                        if (UnityEngineObjectType.IsAssignableFrom(type))
                            AddUnityEngineObjectTypeToListIfSupported(combinedComponentTypeSet, type);
                    }
                }

#if DISABLE_TYPEMANAGER_ILPP
                // Register ComponentData types
                if (isAssemblyReferencingEntities)
                {
                    foreach (var type in assemblyTypes)
                    {
                        if (IsSupportedComponentType(type))
                            AddComponentTypeToListIfSupported(combinedComponentTypeSet, type);
                    }
                }
                
                // Register ComponentData concrete generics
                foreach (var registerGenericComponentTypeAttribute in assembly.GetCustomAttributes<RegisterGenericComponentTypeAttribute>())
                {
                    var type = registerGenericComponentTypeAttribute.ConcreteType;

                    if (IsSupportedComponentType(type))
                        combinedComponentTypeSet.Add(type);
                }
#endif
            }

            return combinedComponentTypeSet;
        }

        /// <summary>
        /// Modified by <see cref="InitializeAllComponentTypes"/>
        /// </summary>
        /// <param name="assemblies"></param>
        /// <param name="combinedComponentTypeSet"></param>
        private static void AddComponentTypes(Assembly[] assemblies, HashSet<Type> combinedComponentTypeSet)
        {
            //Types to process by reflection include unity engine types and types
            //that have variable size based on bitness and so will have wrong
            //info on 32 bit platforms. 
            var typesToProcessByReflection = combinedComponentTypeSet.ToList();

            var indexByType = new Dictionary<Type, int>();
            var writeGroupByType = new Dictionary<int, HashSet<TypeIndex>>();
            var descendantCountByType = new Dictionary<Type, int>();

            var startTypeIndex = s_TypeCount;
#if !DISABLE_TYPEMANAGER_ILPP
            RegisterStaticAssemblyTypes(assemblies, ref combinedComponentTypeSet, out var typesToReprocess);
#else
            var typesToReprocess = Array.Empty<Type>();
#endif
            var combinedComponentTypes = combinedComponentTypeSet.ToList();
            typesToProcessByReflection.AddRange(typesToReprocess);

            //at this point, componentTypes needs to be the combined set, and same with componentTypeSet

            var typeTreeNodes = BuildTypeTree(startTypeIndex, combinedComponentTypes, combinedComponentTypeSet,
                descendantCountByType);

            // Sort the component types for descendant info
            for (var i = 0; i < typeTreeNodes.Length; i++)
            {
                var node = typeTreeNodes[i];
                combinedComponentTypes[node.IndexInTypeArray - startTypeIndex] = node.Type;
                indexByType[node.Type] = node.IndexInTypeArray;
            }

            GatherWriteGroups(combinedComponentTypes, startTypeIndex, indexByType, writeGroupByType);

            /*
             * In order to call this function, we need the descendant counts to be filled in properly already.
             * so we have to save the list of stuff that we're going to do by reflection,
             * call the other thing to make the integrated descendant info,
             * and then come back and call this on the limited list of types that we have to do by reflection.
             *
             * Also, here we pass s_TypeCount as the startTypeIndex because we only want to do this part for
             * the unityEngineComponentTypes, so we should start counting after all the types that have already
             * been registered, which is to say at s_TypeCount.
             *
             * By contrast, above we pass the startTypeIndex as the s_TypeCount recorded before registering the
             * types from the ILPP'd assemblies, because we want the write groups and the type trees to include
             * types from ILPP'd assemblies.
             */
            AddAllComponentTypes(typesToProcessByReflection, s_TypeCount, writeGroupByType, descendantCountByType);
            /*
             * now that type indices have been built, we can use them as keys in our hash map
             */
            GatherSharedComponentMethods(s_ManagedTypeToIndex);

            foreach (var typeNode in typeTreeNodes)
            {
                var typeIndex = GetTypeIndex(typeNode.Type).Index;
                s_DescendantCounts[typeNode.IndexInTypeArray] = descendantCountByType[typeNode.Type];
                s_DescendantIndex[typeIndex] = typeNode.IndexInTypeArray;
            }
        }

        /// <summary>
        /// Modified by <see cref="InitializeAllSystemTypes"/>
        /// </summary>
        /// <param name="assemblies"></param>
        private static void InitializeSystemTypes(params Assembly[] assemblies)
        {
            int oldSystemCount = s_SystemCount;

#if DISABLE_TYPEMANAGER_ILPP
            var isystemTypes = GetTypesDerivedFrom(assemblies, typeof(ISystem)).ToList();
            foreach (var asm in assemblies)
            {
                foreach (var attr in asm.GetCustomAttributes<RegisterGenericSystemTypeAttribute>())
                {
                    isystemTypes.Add(attr.ConcreteType);
                }
            }
            
            // Used to detect cycles in the UpdateInGroup tree, so we don't recurse infinitely and crash.
            var visitedSystemGroupsSet = new HashSet<Type>(32);
            
            foreach (var systemType in isystemTypes)
            {
                if (!systemType.IsValueType)
                    continue;
                if (systemType
                    .ContainsGenericParameters) // don't register the open versions of generic isystems, only the closed
                    continue;

                var name = systemType.FullName;
                var size = UnsafeUtility.SizeOf(systemType);
                var hash = GetHashCode64(systemType);
                // isystems can't be groups
                var flags = GetSystemTypeFlags(systemType);
                if (typeof(ISystem).IsAssignableFrom(systemType) && ((flags & SystemTypeInfo.kIsSystemManagedFlag) != 0))
                    Debug.LogError($"System {systemType} has managed fields, but implements ISystem, which is not allowed. If you need to use managed fields, please inherit from SystemBase.");
                var filterFlags = MakeWorldFilterFlags(systemType, ref visitedSystemGroupsSet);
                
                AddSystemTypeToTables(systemType, name, size, hash, flags, filterFlags);
            }

            foreach (var systemType in GetTypesDerivedFrom(assemblies, typeof(ComponentSystemBase)))
            {
                if (systemType.IsAbstract || systemType.ContainsGenericParameters)
                    continue;

                var name = systemType.FullName;
                var size = -1; // Don't get a type size for a managed type
                var hash = GetHashCode64(systemType);
                var flags = GetSystemTypeFlags(systemType);

                var filterFlags = MakeWorldFilterFlags(systemType, ref visitedSystemGroupsSet);

                AddSystemTypeToTables(systemType, name, size, hash, flags, filterFlags);
            }

            /*
             * We need to do this after we've added all the systems to all the tables so that system type indices
             * will all already exist, even for systems later in the list, so that if we find e.g. an UpdateAfter
             * attr that refers to a system later in the list, we can find the typeindex for said later system
             * and put it in the table.
             */
            if(s_SystemAttributes.Length == 0)
                s_SystemAttributes.Add(new UnsafeList<SystemAttribute>());

            for (int i = oldSystemCount; i < s_SystemCount; i++)
            {
                AddSystemAttributesToTable(GetSystemType(i));
            }
#else
            if (s_SystemAttributes.Length == 0)
                s_SystemAttributes.Add(new UnsafeList<SystemAttribute>());

            for (int i = oldSystemCount; i < s_SystemCount; i++)
            {
                var type = GetSystemType(i);

                // Used to detect cycles in the UpdateInGroup tree, so we don't recurse infinitely and crash.
                var visitedSystemGroupsSet = new HashSet<Type>(32);

                AddSystemAttributesToTable(type);
                s_SystemFilterFlagsList[i] = MakeWorldFilterFlags(type, ref visitedSystemGroupsSet);
            }
#endif
        }

        public static void EarlyInitAssemblies(Assembly[] assemblies)
        {
            foreach (var ass in assemblies)
            {
                EarlyInitAssembly(ass);
            }
        }

        public static void EarlyInitAssembly(Assembly assembly)
        {
            foreach (var type in  assembly.GetTypes())
            {
                // class is added by UnmanagedSystemPostprocessor
                if (!type.Name.StartsWith("__UnmanagedPostProcessorOutput__"))
                    continue;

                // Debug.Log($"Invoke UnmanagedPostProcessorOutput. assembly:{assembly.GetName().Name}  type:{type.FullName}");
                MethodInfo method = type.GetMethod("EarlyInit", BindingFlags.Static | BindingFlags.Public);
                if (method != null)
                {
                    method.Invoke(null, null);
                }
                else
                {
                    Debug.LogWarning($"type:{type} EarlyInit not exists.");
                }

                return;
            }
        }

        /// <summary>
        /// Modified by <see cref="GetTypesDerivedFrom(Type)"/>
        /// </summary>
        /// <param name="assemblies"></param>
        /// <param name="type"></param>
        /// <returns></returns>
#if !UNITY_DOTSRUNTIME
        internal static IEnumerable<Type> GetTypesDerivedFrom(IEnumerable<Assembly> assemblies, Type type)
        {
#if UNITY_EDITOR
            return UnityEditor.TypeCache.GetTypesDerivedFrom(type);
#else
            var types = new List<Type>();
            foreach (var assembly in assemblies)
            {
                if (!TypeManager.IsAssemblyReferencingEntities(assembly))
                    continue;

                try
                {
                    var assemblyTypes = assembly.GetTypes();
                    foreach (var t in assemblyTypes)
                    {
                        if (type.IsAssignableFrom(t))
                            types.Add(t);
                    }
                }
                catch (ReflectionTypeLoadException e)
                {
                    foreach (var t in e.Types)
                    {
                        if (t != null && type.IsAssignableFrom(t))
                            types.Add(t);
                    }

                    Debug.LogWarning($"DefaultWorldInitialization failed loading assembly: {(assembly.IsDynamic ? assembly.ToString() : assembly.Location)}");
                }
            }

            return types;
#endif
        }
#endif
    }
}