using System.ServiceModel;

namespace TestContracts
{
    [ServiceContract]
    public interface ICalculator
    {
        [OperationContract]
        int Add(int x, int y);
    }
}
