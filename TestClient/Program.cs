using System;
using System.Runtime.InteropServices;
using System.ServiceModel;
using TestContracts;
using WcfProxyHelper;

namespace TestClient
{
    class Program
    {
        static int Main(string[] args)
        {
            try
            {
                var binding = new NetTcpBinding();
                var address = new EndpointAddress("net.tcp://localhost:12345/Calculator/ICalculator");
                using (var client = new ServiceClient<ICalculator>(binding, address))
                {
                    Console.WriteLine(client.Contract.Add(1, 1));
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return Marshal.GetHRForException(ex);
            }
        }
    }
}
