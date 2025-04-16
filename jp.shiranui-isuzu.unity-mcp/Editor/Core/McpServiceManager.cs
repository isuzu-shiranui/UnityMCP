using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Manages services and their dependencies throughout the UnityMCP system.
    /// </summary>
    internal sealed class McpServiceManager
    {
        private static readonly Lazy<McpServiceManager> instance = new(() => new McpServiceManager());
        private readonly Dictionary<Type, object> services = new();
        private readonly object lockObject = new();

        /// <summary>
        /// Gets the singleton instance of the service manager.
        /// </summary>
        public static McpServiceManager Instance => instance.Value;

        private McpServiceManager() { }

        /// <summary>
        /// Registers a service implementation with the service manager.
        /// </summary>
        /// <typeparam name="TService">The service interface type.</typeparam>
        /// <param name="implementation">The service implementation instance.</param>
        /// <exception cref="ArgumentNullException">Thrown if implementation is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the service type is already registered.</exception>
        public void RegisterService<TService>(TService implementation) where TService : class
        {
            if (implementation == null)
            {
                throw new ArgumentNullException(nameof(implementation));
            }

            var serviceType = typeof(TService);
            lock (this.lockObject)
            {
                if (this.services.ContainsKey(serviceType))
                {
                    throw new InvalidOperationException($"Service of type {serviceType.Name} is already registered.");
                }

                this.services[serviceType] = implementation;
                Debug.Log($"[McpServiceManager] Registered service: {serviceType.Name}");
            }
        }

        /// <summary>
        /// Gets a registered service of the specified type.
        /// </summary>
        /// <typeparam name="TService">The service interface type.</typeparam>
        /// <returns>The registered service implementation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if no service of the specified type is registered.</exception>
        public TService GetService<TService>() where TService : class
        {
            var serviceType = typeof(TService);
            lock (this.lockObject)
            {
                if (!this.services.TryGetValue(serviceType, out var service))
                {
                    throw new InvalidOperationException($"No service of type {serviceType.Name} is registered.");
                }

                return (TService)service;
            }
        }

        /// <summary>
        /// Tries to get a registered service of the specified type.
        /// </summary>
        /// <typeparam name="TService">The service interface type.</typeparam>
        /// <param name="service">The registered service implementation if found; otherwise, null.</param>
        /// <returns>true if a service of the specified type is registered; otherwise, false.</returns>
        public bool TryGetService<TService>(out TService service) where TService : class
        {
            var serviceType = typeof(TService);
            lock (this.lockObject)
            {
                if (this.services.TryGetValue(serviceType, out var serviceObj))
                {
                    service = (TService)serviceObj;
                    return true;
                }

                service = null;
                return false;
            }
        }

        /// <summary>
        /// Removes a registered service of the specified type.
        /// </summary>
        /// <typeparam name="TService">The service interface type.</typeparam>
        /// <returns>true if the service was successfully removed; otherwise, false.</returns>
        public bool RemoveService<TService>() where TService : class
        {
            var serviceType = typeof(TService);
            lock (this.lockObject)
            {
                var removed = this.services.Remove(serviceType);
                if (removed)
                {
                    Debug.Log($"[McpServiceManager] Removed service: {serviceType.Name}");
                }
                return removed;
            }
        }

        /// <summary>
        /// Clears all registered services.
        /// </summary>
        public void ClearAllServices()
        {
            lock (this.lockObject)
            {
                this.services.Clear();
                Debug.Log("[McpServiceManager] Cleared all services");
            }
        }
    }
}
