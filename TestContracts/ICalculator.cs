#if COREWCF
using CoreWCF;
#else
using System.ServiceModel;
#endif

namespace TestContracts
{
    [ServiceContract]
    public interface ICalculator
    {
        [OperationContract]
        int Add(int x, int y);
    }
}
