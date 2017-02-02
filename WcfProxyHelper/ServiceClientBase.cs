using System;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.Emit;
using System.Runtime.Caching;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;

namespace WcfProxyHelper
{
    public abstract class ServiceClientBase : ICommunicationObject, IDisposable
    {
        // 
        // Example configuration:
        // <configuration>
        //  <system.runtime.caching>
        //    <memoryCache>
        //      <namedCaches>
        //        <add name="ServiceClientBase.ProxyCache" 
        //          cacheMemoryLimitMegabytes="10" 
        //          physicalMemoryLimitPercentage="0"
        //          pollingInterval="00:05:00" />
        //      </namedCaches>
        //    </memoryCache>
        //  </system.runtime.caching>
        //</configuration>
        //
        // Note that we keep both dynamically generated entities (constructors & proxy types) inside the same cache.
        // This is fully supported and OK (and we have them in different key-spaces, so no overlap). You must think
        // of a MemoryCache instance like a "gateway" to some backend cache system, and of like a simple replacement
        // of a static dictionary.
        private static readonly MemoryCache s_cache = new MemoryCache(typeof(ServiceClientBase).Name + ".ProxyCache");
        private static readonly bool s_disableCache;
        private static readonly TimeSpan? s_itemExpiration;

        private static readonly bool s_noDebugSupport;
        private static readonly bool s_noGenAsync;
        private static readonly string s_assemblyOutputDirectory;
        private static readonly TimeSpan s_operationTimeout;

        static ServiceClientBase()
        {
            var appSettings = ConfigurationManager.AppSettings;
            s_disableCache = "true".Equals(appSettings["ServiceClientBase.ProxyCache.Disable"]);
            s_noGenAsync = "false".Equals(appSettings["ServiceClientBase.ProxyGenerator.GenerateAsyncOperations"]);
            s_noDebugSupport = "false".Equals(appSettings["ServiceClientBase.ProxyGenerator.DebugSupport"]);
            s_assemblyOutputDirectory = appSettings["ServiceClientBase.ProxyGenerator.AssemblyOutputDirectory"];
            IgnoreStaticProxies = "true".Equals(appSettings["ServiceClientBase.IgnoreStaticProxies"]);

            // That's right: per default we don't assign any expiring to our cached items.
            // While the system is up and running, there is no point in expiring cache items,
            // as the service contracts cannot change without a recompile.
            string value = appSettings["ServiceClientBase.ProxyGenerator.ItemSlidingExpiration"];
            TimeSpan ts;
            if (!string.IsNullOrEmpty(value) && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out ts))
            {
                s_itemExpiration = ts;
            }

            value = appSettings["ServiceClientBase.OperationTimeout"];
            if (!string.IsNullOrEmpty(value) && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out ts))
            {
                s_operationTimeout = ts;
            }
            else
            {
                s_operationTimeout = TimeSpan.MinValue;
            }
        }

        internal static TimeSpan OperationTimeout { get { return s_operationTimeout; } }
        internal static bool IgnoreStaticProxies { get; set; }

        private readonly Type m_serviceContract;
        private readonly object m_clientImpl;

        protected ServiceClientBase(Type serviceContract, string endpointConfigurationName)
        {
            m_serviceContract = serviceContract;
            m_clientImpl = FindConstructor<Ctor1>(typeof(string))(endpointConfigurationName);
            SetupBehaviors();
        }

        protected ServiceClientBase(Type serviceContract, string endpointConfigurationName, string remoteAddress)
        {
            m_serviceContract = serviceContract;
            m_clientImpl = FindConstructor<Ctor2>(typeof(string), typeof(string))(endpointConfigurationName, remoteAddress);
            SetupBehaviors();
        }

        protected ServiceClientBase(Type serviceContract, string endpointConfigurationName, EndpointAddress remoteAddress)
        {
            m_serviceContract = serviceContract;
            m_clientImpl = FindConstructor<Ctor3>(typeof(string), typeof(EndpointAddress))(endpointConfigurationName, remoteAddress);
            SetupBehaviors();
        }

        protected ServiceClientBase(Type serviceContract, Binding binding, EndpointAddress remoteAddress)
        {
            m_serviceContract = serviceContract;
            m_clientImpl = FindConstructor<Ctor4>(typeof(Binding), typeof(EndpointAddress))(binding, remoteAddress);
            SetupBehaviors();
        }

        private void SetupBehaviors()
        {
            var clientBase = m_clientImpl as IClientBase;
            if (clientBase != null)
            {
                if (s_operationTimeout.Ticks >= 0)
                {
                    clientBase.InnerChannel.OperationTimeout = s_operationTimeout;
                }
            }
        }

        // ------- Dynamic Proxy-Constructor Management --------------------------------------------------------------

        private TDelegate FindConstructor<TDelegate>(params Type[] types)
        {
            var proxyType = FindProxyType();
            string methodName = proxyType.FullName + "." + typeof(TDelegate).Name;

            if (s_disableCache)
            {
                return CreateConstructor<TDelegate>(proxyType, methodName, types);
            }

            var policy = new CacheItemPolicy();
            if (s_itemExpiration != null)
            {
                policy.SlidingExpiration = s_itemExpiration.Value;
            }

            var newEntry = new Lazy<TDelegate>(() => CreateConstructor<TDelegate>(proxyType, methodName, types));
            var existingEntry = (Lazy<TDelegate>)s_cache.AddOrGetExisting(GetConstructorCacheKey(methodName), newEntry, policy);

            return (existingEntry ?? newEntry).Value;
        }

        private static TDelegate CreateConstructor<TDelegate>(Type proxyType, string methodName, params Type[] types)
        {
            var proxyImplCtor = proxyType.GetConstructor(types);
            Debug.Assert(proxyImplCtor != null, "proxyImplCtor != null", "constructor not found: " + typeof(TDelegate));

            var dynamicCtor = new DynamicMethod(methodName, proxyType, types);
            var gen = dynamicCtor.GetILGenerator();
            for (int i = 0; i < types.Length; i++)
            {
                gen.EmitLdarg(i);
            }
            gen.Emit(OpCodes.Newobj, proxyImplCtor);
            gen.Emit(OpCodes.Ret);

            return (TDelegate)(object)dynamicCtor.CreateDelegate(typeof(TDelegate));
        }

        private static string GetConstructorCacheKey(string methodName)
        {
            return "Constructor:" + methodName;
        }

        private delegate object Ctor1(string endpointConfigurationName);
        private delegate object Ctor2(string endpointConfigurationName, string remoteAddress);
        private delegate object Ctor3(string endpointConfigurationName, EndpointAddress remoteAddress);
        private delegate object Ctor4(Binding binding, EndpointAddress remoteAddress);

        // ------- Dynamic Proxy-Implementation Management --------------------------------------------------------------

        private Type FindProxyType()
        {
            if (!IgnoreStaticProxies)
            {
                string typeName = ProxyGenerator.GetProxyTypeName(m_serviceContract);
                Type proxyType = m_serviceContract.Assembly.GetType(typeName, false);
                if (proxyType != null)
                {
                    // Proxytype colocated with the service contract type (e.g. generated by svcutil.exe).
                    return proxyType;
                }
            }

            if (s_disableCache)
            {
                return CreateProxyType(m_serviceContract);
            }

            var policy = new CacheItemPolicy();
            if (s_itemExpiration != null)
            {
                policy.SlidingExpiration = s_itemExpiration.Value;
            }

            var newEntry = new Lazy<Type>(() => CreateProxyType(m_serviceContract));
            var existingEntry = (Lazy<Type>)s_cache.AddOrGetExisting(GetProxyTypeCacheKey(m_serviceContract), newEntry, policy);

            return (existingEntry ?? newEntry).Value;
        }

        protected object ProxyImpl { get { return m_clientImpl; } }

        public dynamic Dynamic
        {
            get { return ProxyImpl; }
        }

        private static string GetProxyTypeCacheKey(Type type)
        {
            return "ProxyType:" + type.AssemblyQualifiedName;
        }

        private static Type CreateProxyType(Type serviceContract)
        {
            var proxyGen = new ProxyGenerator
            {
                DebugSupport = !s_noDebugSupport,
                AssemblyOutputDirectory = s_assemblyOutputDirectory,
                GenerateAsyncOperations = !s_noGenAsync,
            };

            return proxyGen.GenerateProxy(serviceContract);
        }

        #region IClientBase

        public IClientChannel InnerChannel
        {
            get { return ((IClientBase)ProxyImpl).InnerChannel; }
        }

        public ClientCredentials ClientCredentials
        {
            get { return ((IClientBase)ProxyImpl).ClientCredentials; }
        }

        public ServiceEndpoint Endpoint
        {
            get { return ((IClientBase)ProxyImpl).Endpoint; }
        }

        public void DisplayInitializationUI()
        {
            ((IClientBase)ProxyImpl).DisplayInitializationUI();
        }

        #endregion

        #region ICommunicationObject

        public CommunicationState State
        {
            get { return ((ICommunicationObject)ProxyImpl).State; }
        }

        event EventHandler ICommunicationObject.Closed
        {
            add { ((ICommunicationObject)ProxyImpl).Closed += value; }
            remove { ((ICommunicationObject)ProxyImpl).Closed -= value; }
        }

        event EventHandler ICommunicationObject.Closing
        {
            add { ((ICommunicationObject)ProxyImpl).Closing += value; }
            remove { ((ICommunicationObject)ProxyImpl).Closing -= value; }
        }

        event EventHandler ICommunicationObject.Faulted
        {
            add { ((ICommunicationObject)ProxyImpl).Faulted += value; }
            remove { ((ICommunicationObject)ProxyImpl).Faulted -= value; }
        }

        event EventHandler ICommunicationObject.Opened
        {
            add { ((ICommunicationObject)ProxyImpl).Opened += value; }
            remove { ((ICommunicationObject)ProxyImpl).Opened -= value; }
        }

        event EventHandler ICommunicationObject.Opening
        {
            add { ((ICommunicationObject)ProxyImpl).Opening += value; }
            remove { ((ICommunicationObject)ProxyImpl).Opening -= value; }
        }

        public void Open()
        {
            ((ICommunicationObject)ProxyImpl).Open();
        }

        public void Close()
        {
            ((ICommunicationObject)ProxyImpl).Close();
        }

        public void Abort()
        {
            ((ICommunicationObject)ProxyImpl).Abort();
        }

        void ICommunicationObject.Open(TimeSpan timeout)
        {
            ((ICommunicationObject)ProxyImpl).Open(timeout);
        }

        void ICommunicationObject.Close(TimeSpan timeout)
        {
            ((ICommunicationObject)ProxyImpl).Close(timeout);
        }

        IAsyncResult ICommunicationObject.BeginClose(AsyncCallback callback, object state)
        {
            return ((ICommunicationObject)ProxyImpl).BeginClose(callback, state);
        }

        IAsyncResult ICommunicationObject.BeginClose(TimeSpan timeout, AsyncCallback callback, object state)
        {
            return ((ICommunicationObject)ProxyImpl).BeginClose(timeout, callback, state);
        }

        void ICommunicationObject.EndClose(IAsyncResult result)
        {
            ((ICommunicationObject)ProxyImpl).EndClose(result);
        }

        IAsyncResult ICommunicationObject.BeginOpen(AsyncCallback callback, object state)
        {
            return ((ICommunicationObject)ProxyImpl).BeginOpen(callback, state);
        }

        IAsyncResult ICommunicationObject.BeginOpen(TimeSpan timeout, AsyncCallback callback, object state)
        {
            return ((ICommunicationObject)ProxyImpl).BeginOpen(timeout, callback, state);
        }

        void ICommunicationObject.EndOpen(IAsyncResult result)
        {
            ((ICommunicationObject)ProxyImpl).EndOpen(result);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (IgnoreStaticProxies)
            {
                // Generated Dispose() method properly uses Close/Abort.
                ((IDisposable)ProxyImpl).Dispose();
            }
            else
            {
                // Either generator proxy or a static one, directly based on ClientBase<>.
                // Since we don't know which, we must enforce the "safe" pattern manually.
                if (State != CommunicationState.Closed)
                {
                    if (State != CommunicationState.Faulted)
                    {
                        Close();
                    }
                    else
                    {
                        Abort();
                    }
                }
            }
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}