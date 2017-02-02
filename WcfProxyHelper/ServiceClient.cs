using System;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace WcfProxyHelper
{
    public class ServiceClient<T> : ServiceClientBase where T : class
    {
        public ServiceClient(string endpointConfigurationName)
            : base(typeof(T), endpointConfigurationName)
        {
        }

        public ServiceClient(string endpointConfigurationName, string remoteAddress)
            : base(typeof(T), endpointConfigurationName, remoteAddress)
        {
        }

        public ServiceClient(string endpointConfigurationName, EndpointAddress remoteAddress)
            : base(typeof(T), endpointConfigurationName, remoteAddress)
        {
        }

        public ServiceClient(Binding binding, EndpointAddress remoteAddress)
            : base(typeof(T), binding, remoteAddress)
        {
        }

        public T Contract
        {
            get { return (T)ProxyImpl; }
        }
    }

    public class ServiceClient : ServiceClientBase
    {
        public static ServiceClient<T> GetDefaultEndpoint<T>() where T : class
        {
            return new ServiceClient<T>(typeof(T).FullName);
        }

        public static ServiceClient GetDefaultEndpoint(Type type)
        {
            if (type == null)
                throw new ArgumentNullException("type");
            return new ServiceClient(type, type.FullName);
        }

        public ServiceClient(Type type, string endpointConfigurationName)
            : base(type, endpointConfigurationName)
        {
        }

        public ServiceClient(Type type, string endpointConfigurationName, string remoteAddress)
            : base(type, endpointConfigurationName, remoteAddress)
        {
        }

        public ServiceClient(Type type, string endpointConfigurationName, EndpointAddress remoteAddress)
            : base(type, endpointConfigurationName, remoteAddress)
        {
        }

        public ServiceClient(Type type, Binding binding, EndpointAddress remoteAddress)
            : base(type, binding, remoteAddress)
        {
        }

        public object Contract
        {
            get { return ProxyImpl; }
        }
    }
}