using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Threading.Tasks;

namespace WcfProxyHelper
{
    public class ProxyGenerator
    {
        public ProxyGenerator()
        {
#if DEBUG
            DebugSupport = true;
#endif
            GenerateAsyncOperations = true;
            AssemblyOutputDirectory = "C:\\temp";
        }

        public bool DebugSupport { get; set; }
        public string AssemblyOutputDirectory { get; set; }
        public bool GenerateAsyncOperations { get; set; }

        public static string GetProxyTypeName(Type type)
        {
            if (type == null)
                throw new ArgumentNullException("type");

            if (type.Name[0] == 'I')
            {
                return type.Namespace + "." + type.Name.Substring(1) + "Client";
            }

            return type.FullName + "Client";
        }

        public Type GenerateProxy(Type serviceContract)
        {
            var assemblyName = new AssemblyName(serviceContract.FullName + ".Proxy");

            bool saveAssembly;
            var assemblyBuilder = CreateAssemblyBuilder(AppDomain.CurrentDomain, assemblyName, out saveAssembly);
            var moduleBuilder = CreateModuleBuilder(assemblyBuilder, saveAssembly);
            var clientType = CreateClientType(serviceContract, moduleBuilder);

            if (saveAssembly)
            {
                assemblyBuilder.Save(assemblyName.Name + ".dll");
            }

            return clientType;
        }

        private AssemblyBuilder CreateAssemblyBuilder(AppDomain appDomain, AssemblyName assemblyName, out bool saveAssembly)
        {
            AssemblyBuilder assemblyBuilder;
            saveAssembly = false;

            if (!string.IsNullOrEmpty(AssemblyOutputDirectory))
            {
                if (!Directory.Exists(AssemblyOutputDirectory))
                {
                    Directory.CreateDirectory(AssemblyOutputDirectory);
                }

                assemblyBuilder = appDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndSave, AssemblyOutputDirectory);
                saveAssembly = true;
            }
            else
            {
                assemblyBuilder = appDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            }

            // See http://blogs.msdn.com/b/rmbyers/archive/2005/06/26/432922.aspx. This should be
            // done before calling DefineDynamicModule().
            if (DebugSupport)
            {
                assemblyBuilder.AddDebuggableAttribute();
            }

            return assemblyBuilder;
        }

        private ModuleBuilder CreateModuleBuilder(AssemblyBuilder assemblyBuilder, bool saveAssembly)
        {
            string assemblyFileName = assemblyBuilder.GetName().Name;

            if (saveAssembly)
            {
                return assemblyBuilder.DefineDynamicModule(assemblyFileName, assemblyFileName + ".dll", DebugSupport);
            }

            return assemblyBuilder.DefineDynamicModule(assemblyFileName, DebugSupport);
        }

        private Type CreateClientType(Type serviceContract, ModuleBuilder moduleBuilder)
        {
            Type clientType;
            if (GenerateAsyncOperations)
            {
                var exif = GenerateAsyncInterface(serviceContract, moduleBuilder);
                clientType = GenerateProxyImpl(exif, moduleBuilder);
            }
            else
            {
                clientType = GenerateProxyImpl(serviceContract, moduleBuilder);
            }

            return clientType;
        }

        // ----------------------------- Async Interface -------------------------------------------------------------

        private Type GenerateAsyncInterface(Type serviceContract, ModuleBuilder moduleBuilder)
        {
            var typeBuilder = moduleBuilder.DefineType(GetAsyncTypeName(serviceContract),
                TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.AutoClass |
                TypeAttributes.AnsiClass | TypeAttributes.Abstract);

            string serviceContractNamespace = serviceContract.GetServiceContractNamespace();

            foreach (var attribute in serviceContract.BuildCustomAttributes())
            {
                typeBuilder.SetCustomAttribute(attribute);
            }

            typeBuilder.AddInterfaceImplementation(serviceContract);

            AddAsyncOperations(typeBuilder, serviceContract, serviceContractNamespace);

            return typeBuilder.CreateType();
        }

        private static string GetAsyncTypeName(Type type)
        {
            return type.FullName + "Async";
        }

        private void AddAsyncOperations(TypeBuilder typeBuilder, Type serviceContract, string serviceContractNamespace)
        {
            foreach (var sc in serviceContract.GetServiceContracts())
            {
                AddAsyncOperations(typeBuilder, sc, serviceContractNamespace);
            }

            foreach (var oc in serviceContract.GetOperationContracts())
            {
                GenerateAsyncOperation(typeBuilder, oc, serviceContractNamespace);
            }
        }

        private void GenerateAsyncOperation(TypeBuilder typeBuilder, MethodInfo methodInfo, string serviceContractNamespace)
        {
            if (IsAsyncOperation(methodInfo))
            {
                // Don't generate Async version of operation contracts that already return a "Task".
                // Otherwise something like "Task<int> GetFooAsync()" would be complemented by
                // "Task<Task<int>> GetFooAsyncAsync()" - pointless.
                return;
            }

            var parameters = methodInfo.GetParameters();
            var parameterTypes = parameters.Select(p => p.ParameterType).ToArray();
            var requiredCustomModifiers = parameters.Select(p => p.GetRequiredCustomModifiers()).ToArray();
            var optionalCustomModifiers = parameters.Select(p => p.GetOptionalCustomModifiers()).ToArray();
            Type[] returnTypeRequiredCustomModifiers = Type.EmptyTypes;
            Type[] returnTypeOptionalCustomModifiers = Type.EmptyTypes;
            if (methodInfo.ReturnParameter != null)
            {
                returnTypeRequiredCustomModifiers = methodInfo.ReturnParameter.GetRequiredCustomModifiers();
                returnTypeOptionalCustomModifiers = methodInfo.ReturnParameter.GetOptionalCustomModifiers();
            }

            var methodBuilder = typeBuilder.DefineMethod(methodInfo.Name + "Async",
                GeneratorHelpers.GetInterfaceMethodAttributes(), CallingConventions.Standard,
                methodInfo.ReturnType == typeof(void) ? typeof(Task) : typeof(Task<>).MakeGenericType(methodInfo.ReturnType),
                returnTypeRequiredCustomModifiers, returnTypeOptionalCustomModifiers,
                parameterTypes, requiredCustomModifiers, optionalCustomModifiers);

            for (var i = 0; i < parameters.Length; ++i)
            {
                var parameter = parameters[i];
                var parameterBuilder = methodBuilder.DefineParameter(i + 1, parameter.Attributes, parameter.Name);
                if (((int)parameter.Attributes & (int)ParameterAttributes.HasDefault) != 0)
                {
                    parameterBuilder.SetConstant(parameter.RawDefaultValue);
                }

                foreach (var attribute in parameter.BuildCustomAttributes())
                {
                    parameterBuilder.SetCustomAttribute(attribute);
                }
            }

            bool isOneWay = IsOnewayOperation(methodInfo);

            foreach (var attribute in methodInfo.BuildCustomAttributes(serviceContractNamespace, isOneWay))
            {
                methodBuilder.SetCustomAttribute(attribute);
            }
        }

        private static bool IsAsyncOperation(MethodInfo methodInfo)
        {
            // Operation returns Task or Task<T>. We could also check for the operation name
            // to end in "Async", but that is not a technical requirement, but just convention.
            // 
            return methodInfo.ReturnType == typeof(Task) ||
                (methodInfo.ReturnType.IsGenericType && methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));
        }

        private static bool IsOnewayOperation(MethodInfo methodInfo)
        {
            var attr = methodInfo.GetCustomAttributes(typeof(OperationContractAttribute), true);
            if (attr.Length > 0)
            {
                var opc = (OperationContractAttribute)attr[0];
                return opc.IsOneWay;
            }

            var implType = methodInfo.DeclaringType;
            if (implType != null && !implType.IsInterface)
            {
                foreach (var interfaceType in implType.GetInterfaces())
                {
                    if (interfaceType.IsDefined(typeof(ServiceContractAttribute), false))
                    {
                        var map = implType.GetInterfaceMap(interfaceType);
                        int index = Array.IndexOf(map.InterfaceMethods, methodInfo);
                        if (index != -1)
                        {
                            return IsOnewayOperation(map.InterfaceMethods[index]);
                        }
                    }
                }
            }

            return false;
        }

        // ----------------------------- Proxy Impl -------------------------------------------------------------

        private Type GenerateProxyImpl(Type serviceContract, ModuleBuilder moduleBuilder)
        {
            var typeBuilder = moduleBuilder.DefineType(GetProxyTypeName(serviceContract),
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.AutoClass |
                TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit | TypeAttributes.AutoLayout);

            typeBuilder.SetParent(typeof(ClientBase<>).MakeGenericType(serviceContract));
            typeBuilder.AddInterfaceImplementation(serviceContract);
            typeBuilder.AddInterfaceImplementation(typeof(IDisposable));
            typeBuilder.AddInterfaceImplementation(typeof(IClientBase));
            typeBuilder.AddGeneratedCodeAttribute();

            if (!DebugSupport)
            {
                typeBuilder.AddDebuggerStepThroughAttribute();
            }

            AddConstructor(typeBuilder, Type.EmptyTypes);
            AddConstructor(typeBuilder, typeof(string));
            AddConstructor(typeBuilder, typeof(string), typeof(string));
            AddConstructor(typeBuilder, typeof(string), typeof(EndpointAddress));
            AddConstructor(typeBuilder, typeof(Binding), typeof(EndpointAddress));
            AddOperationContractMethods(typeBuilder, serviceContract);
            AddPrivatePassThroughs(typeBuilder, typeof(IClientBase));
            AddSafeDispose(typeBuilder);

            return typeBuilder.CreateType();
        }

        private void AddConstructor(TypeBuilder typeBuilder, params Type[] arguments)
        {
            var ctorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard, arguments);

            var gen = ctorBuilder.GetILGenerator();
            gen.Emit(OpCodes.Ldarg_0);

            for (int i = 1; i <= arguments.Length; i++)
            {
                gen.EmitLdarg(i);
            }

            gen.Emit(OpCodes.Call, typeBuilder.BaseType.GetNonPublicConstructor(arguments));

            if (DebugSupport)
            {
                gen.Emit(OpCodes.Nop);
                gen.Emit(OpCodes.Nop);
                gen.Emit(OpCodes.Nop);
            }

            gen.Emit(OpCodes.Ret);
        }

        private void AddOperationContractMethods(TypeBuilder typeBuilder, Type serviceContract)
        {
            foreach (var sc in serviceContract.GetServiceContracts())
            {
                AddOperationContractMethods(typeBuilder, sc);
            }

            var getChannel = typeBuilder.BaseType.GetNonPublicInstancePropertyGetter("Channel");

            foreach (var oc in serviceContract.GetOperationContracts())
            {
                var gen = typeBuilder.GetPublicWrapperMethodBuilder(oc).GetILGenerator();
                gen.EmitMethodPreamble(oc, DebugSupport);
                gen.Emit(OpCodes.Ldarg_0);
                gen.Emit(OpCodes.Call, getChannel);
                gen.EmitMethodCallVirt(oc, DebugSupport);
                gen.EmitMethodPostamble(oc, DebugSupport);
            }
        }

        private void AddPrivatePassThroughs(TypeBuilder typeBuilder, Type interfaceType)
        {
            foreach (var propertyInfo in interfaceType.GetProperties())
            {
                var propertyBuilder = typeBuilder.DefineProperty(propertyInfo.GetFullName(), PropertyAttributes.None, propertyInfo.PropertyType, null);

                if (propertyInfo.CanRead)
                {
                    propertyBuilder.SetGetMethod(AddPassThroughMethod(typeBuilder, propertyInfo.GetGetMethod()));
                }

                if (propertyInfo.CanWrite)
                {
                    propertyBuilder.SetSetMethod(AddPassThroughMethod(typeBuilder, propertyInfo.GetSetMethod()));
                }
            }

            foreach (var methodInfo in interfaceType.GetMethods().Where(m => m.Name.Contains("UI")))
            {
                AddPassThroughMethod(typeBuilder, methodInfo);
            }
        }

        private MethodBuilder AddPassThroughMethod(TypeBuilder typeBuilder, MethodInfo methodInfo)
        {
            var methodBuilder = typeBuilder.GetExplicitInterfaceImplementationBuilder(methodInfo);
            var gen = methodBuilder.GetILGenerator();
            gen.EmitMethodPreamble(methodInfo, DebugSupport);
            gen.Emit(OpCodes.Ldarg_0);
            gen.EmitMethodCallVirt(methodInfo.FindMethod(typeBuilder.BaseType), DebugSupport);
            gen.EmitMethodPostamble(methodInfo, DebugSupport);

            typeBuilder.DefineMethodOverride(methodBuilder, methodInfo);

            return methodBuilder;
        }

        // Generates the equivalent of:
        //public void Dispose()
        //{
        //    if (State != CommunicationState.Closed)
        //    {
        //        if (State != CommunicationState.Faulted)
        //        {
        //            Close();
        //        }
        //        else
        //        {
        //            Abort();
        //        }
        //    }
        //}
        private static void AddSafeDispose(TypeBuilder typeBuilder)
        {
            var other = typeof(IDisposable).GetNonPublicVoidInstanceMethod("Dispose");
            var getState = typeBuilder.BaseType.GetNonPublicInstancePropertyGetter("State");
            var closeOp = typeBuilder.BaseType.GetNonPublicVoidInstanceMethod("Close");
            var abortOp = typeBuilder.BaseType.GetNonPublicVoidInstanceMethod("Abort");

            var methodBuilder = typeBuilder.GetPublicWrapperMethodBuilder(other);
            var gen = methodBuilder.GetILGenerator();
            gen.DeclareLocal(typeof(bool));
            var exitFunction = gen.DefineLabel();
            var callAbort = gen.DefineLabel();
            var aboutToExit = gen.DefineLabel();

            gen.Emit(OpCodes.Nop);
            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Call, getState);
            gen.Emit(OpCodes.Ldc_I4_4);
            gen.Emit(OpCodes.Ceq);
            gen.Emit(OpCodes.Stloc_0);
            gen.Emit(OpCodes.Ldloc_0);
            gen.Emit(OpCodes.Brtrue_S, exitFunction);

            gen.Emit(OpCodes.Nop);
            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Call, getState);
            gen.Emit(OpCodes.Ldc_I4_5);
            gen.Emit(OpCodes.Ceq);
            gen.Emit(OpCodes.Stloc_0);
            gen.Emit(OpCodes.Ldloc_0);
            gen.Emit(OpCodes.Brtrue_S, callAbort);

            gen.Emit(OpCodes.Nop);
            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Call, closeOp);
            gen.Emit(OpCodes.Nop);
            gen.Emit(OpCodes.Nop);
            gen.Emit(OpCodes.Br_S, aboutToExit);

            gen.MarkLabel(callAbort);
            gen.Emit(OpCodes.Nop);
            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Call, abortOp);
            gen.Emit(OpCodes.Nop);
            gen.Emit(OpCodes.Nop);

            gen.MarkLabel(aboutToExit);
            gen.Emit(OpCodes.Nop);

            gen.MarkLabel(exitFunction);
            gen.Emit(OpCodes.Ret);
        }
    }
}