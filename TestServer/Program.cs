using System;
using System.Runtime.InteropServices;
using System.ServiceModel;
using System.ServiceModel.Description;
using TestContracts;
using WcfProxyHelper;

namespace TestServer
{
    class Program
    {
        private class Calculator : ICalculator
        {
            public int Add(int x, int y)
            {
                return x + y;
            }
        }

        static int Main(string[] args)
        {
            try
            {
                var host = new ServiceHost(typeof(Calculator));
                host.AddServiceEndpoint(typeof (ICalculator), new NetTcpBinding(),
                    "net.tcp://localhost:12345/Calculator/ICalculator");
                host.Open();
                Console.WriteLine("Hit any key to exit...");
                Console.ReadKey();
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
