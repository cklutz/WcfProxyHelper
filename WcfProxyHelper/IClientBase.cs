using System;
using System.ServiceModel;
using System.ServiceModel.Description;

namespace WcfProxyHelper
{
    public interface IClientBase : IDisposable
    {
        IClientChannel InnerChannel { get; }
        ClientCredentials ClientCredentials { get; }
        ServiceEndpoint Endpoint { get; }
        void DisplayInitializationUI();
    }
}