using System;
using System.Runtime.InteropServices;
using TestContracts;
using WcfProxyHelper;
using System.ServiceModel;
using System.ServiceModel.Description;

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
#if NET48
                var host = new ServiceHost(typeof(Calculator));
                host.AddServiceEndpoint(typeof (ICalculator), new NetTcpBinding(),
                    "net.tcp://localhost:12345/Calculator/ICalculator");
                host.Open();
                Console.WriteLine("Hit any key to exit...");
                Console.ReadKey();
                return 0;
#else
                // TODO: Base upon ASP.NET and use CoreWCF.
                throw new NotImplementedException();
#endif
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return Marshal.GetHRForException(ex);
            }
        }
    }
}
