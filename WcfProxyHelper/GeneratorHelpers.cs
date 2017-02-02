using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.ServiceModel;

namespace WcfProxyHelper
{
    internal static class GeneratorHelpers
    {
        public static readonly object[] EmptyObjects = { };

        private static readonly string s_version;

        static GeneratorHelpers()
        {
            var attr = typeof(GeneratorHelpers).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            s_version = attr != null ? attr.InformationalVersion : "0.0.0.0";
        }

        public static IEnumerable<Type> GetServiceContracts(this Type type)
        {
            return type.GetInterfaces().Where(t => t.GetCustomAttributes(typeof(ServiceContractAttribute), false).Length > 0);
        }

        public static IEnumerable<MethodInfo> GetOperationContracts(this Type type)
        {
            return type.GetMethods().Where(t => t.GetCustomAttributes(typeof(OperationContractAttribute), false).Length > 0);
        }

        public static Type[] GetParameterTypes(this MethodInfo methodInfo)
        {
            return methodInfo.GetParameters().Select(p => p.ParameterType).ToArray();
        }

        public static MethodBuilder GetExplicitInterfaceImplementationBuilder(this TypeBuilder typeBuilder, MethodInfo methodInfo)
        {
            return typeBuilder.DefineMethod(methodInfo.GetFullName(),
                MethodAttributes.Private |
                MethodAttributes.HideBySig |
                MethodAttributes.NewSlot |
                MethodAttributes.Virtual |
                MethodAttributes.Final,
                methodInfo.ReturnType, methodInfo.GetParameterTypes());
        }

        public static MethodBuilder GetPublicWrapperMethodBuilder(this TypeBuilder typeBuilder, MethodInfo methodInfo)
        {
            return typeBuilder.DefineMethod(methodInfo.Name,
                MethodAttributes.Public |
                MethodAttributes.Final |
                MethodAttributes.HideBySig |
                MethodAttributes.NewSlot |
                MethodAttributes.Virtual,
                CallingConventions.Standard,
                methodInfo.ReturnType, methodInfo.GetParameterTypes());
        }

        public static MethodAttributes GetInterfaceMethodAttributes()
        {
            return MethodAttributes.Public |
                   MethodAttributes.HideBySig |
                   MethodAttributes.NewSlot |
                   MethodAttributes.Abstract |
                   MethodAttributes.Virtual;
        }



        // -------------------------------------------------------------------
        public static IEnumerable<CustomAttributeBuilder> BuildCustomAttributes(this ParameterInfo parameterInfo)
        {
            return parameterInfo.GetCustomAttributesData().Select(CopyAttribute).ToList();
        }

        public static IEnumerable<CustomAttributeBuilder> BuildCustomAttributes(this Type type)
        {
            var res = new List<CustomAttributeBuilder>();

            foreach (var ad in type.GetCustomAttributesData())
            {
                // Currently, we don't do anything special for [ServiceContract] other than
                // what we do for "ordinary" attributes. But we might in the future. Thus,
                // the if-statement here is left here as "placeholder".
                if (ad.AttributeType == typeof(ServiceContractAttribute))
                {
                    res.Add(CopyAttribute(ad));
                }
                else
                {
                    res.Add(CopyAttribute(ad));
                }
            }

            return res;
        }

        public static string GetServiceContractNamespace(this Type serviceContract)
        {
            var attr = serviceContract.GetCustomAttribute<ServiceContractAttribute>();

            string ns = attr != null && !string.IsNullOrEmpty(attr.Namespace) ? attr.Namespace : "http://tempuri.org/";

            if (!ns.EndsWith("/"))
                return ns + "/";

            return ns;
        }

        private static int IndexOf(this IList<CustomAttributeNamedArgument> data, string name)
        {
            if (data != null)
            {
                for (int i = 0; i < data.Count; i++)
                {
                    if (data[i].MemberInfo.Name.Equals(name))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static void SetValueIfEmpty(this IList<CustomAttributeNamedArgument> data, MemberInfo memberInfo, string value)
        {
            int pos = data.IndexOf(memberInfo.Name);
            if (pos != -1)
            {
                if (string.IsNullOrEmpty(data[pos].TypedValue.Value as string))
                {
                    data[pos] = new CustomAttributeNamedArgument(data[pos].MemberInfo,
                        new CustomAttributeTypedArgument(data[pos].TypedValue.ArgumentType, value));
                }
            }
            else
            {
                data.Add(new CustomAttributeNamedArgument(memberInfo,
                        new CustomAttributeTypedArgument(value.GetType(), value)));
            }
        }

        public static IEnumerable<CustomAttributeBuilder> BuildCustomAttributes(this MethodInfo methodInfo, string serviceContractNamespace, bool isOneWay)
        {
            var res = new List<CustomAttributeBuilder>();
            var data = new List<CustomAttributeData>(methodInfo.GetCustomAttributesData());

            var oca = typeof(OperationContractAttribute);

            foreach (var ad in data)
            {
                if (ad.AttributeType == oca)
                {
                    var args = new List<CustomAttributeNamedArgument>();
                    string actionName = methodInfo.Name;
                    string actionPrefix = serviceContractNamespace + (methodInfo.DeclaringType != null ? (methodInfo.DeclaringType.Name + "/") : "");

                    if (ad.NamedArguments != null)
                    {
                        foreach (var namedArgument in ad.NamedArguments)
                        {
                            if (namedArgument.MemberInfo.Name == "Name" && namedArgument.TypedValue.Value != null)
                            {
                                actionName = namedArgument.TypedValue.Value.ToString();
                            }

                            args.Add(namedArgument);
                        }
                    }

                    args.SetValueIfEmpty(oca.GetProperty("Action"), actionPrefix + actionName);

                    if (!isOneWay)
                    {
                        args.SetValueIfEmpty(oca.GetProperty("ReplyAction"), actionPrefix + actionName + "Response");
                    }

                    res.Add(CopyAttribute(ad, args));

                }
                else if ("System.ServiceModel".Equals(ad.AttributeType.Namespace))
                {
                    // Don't add any other additional attributes from here (especially not FaultContract).
                    // Otherwise we'll get this during runtime:
                    //
                    //      InvalidOperationException: The synchronous OperationContract method 'Foo' in type 'IBar'
                    //      was matched with the task-based asynchronous OperationContract method 'FooAsync' because
                    //      they have the same operation name 'BeginChange'. When a synchronous OperationContract method
                    //      is matched to a task-based asynchronous OperationContract method, any additional attributes
                    //      must be declared on the synchronous OperationContract method. In this case, the task-based
                    //      asynchronous OperationContract method 'FooAsync' has one or more attributes of type
                    //      'FaultContractAttribute'.
                    //      To fix it, remove the 'FaultContractAttribute' attribute or attributes from method 'FooAsync'.
                    //      Alternatively, changing the name of one of the methods will prevent matching.
                }
                else
                {
                    res.Add(CopyAttribute(ad));
                }
            }

            return res;
        }

        private static CustomAttributeBuilder CopyAttribute(CustomAttributeData attribute)
        {
            return CopyAttribute(attribute, attribute.NamedArguments);
        }

        private static CustomAttributeBuilder CopyAttribute(CustomAttributeData attribute, IList<CustomAttributeNamedArgument> data)
        {
            var args = attribute.ConstructorArguments.Select(a => a.Value).ToArray();

            if (data != null && data.Count > 0)
            {
                var namedPropertyInfos = data.Select(a => a.MemberInfo).OfType<PropertyInfo>().ToArray();
                var namedPropertyValues = data.Where(a => a.MemberInfo is PropertyInfo).Select(a => a.TypedValue.Value).ToArray();
                var namedFieldInfos = data.Select(a => a.MemberInfo).OfType<FieldInfo>().ToArray();
                var namedFieldValues = data.Where(a => a.MemberInfo is FieldInfo).Select(a => a.TypedValue.Value).ToArray();

                return new CustomAttributeBuilder(attribute.Constructor, args, namedPropertyInfos, namedPropertyValues, namedFieldInfos, namedFieldValues);
            }

            return new CustomAttributeBuilder(attribute.Constructor, args);
        }

        // -------------------------------------------------------------------

        public static MethodInfo FindMethod(this MethodInfo methodInfo, Type type)
        {
            var result = type.FindMembers(
                MemberTypes.Method | MemberTypes.Property,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                (current, searchCriteria) =>
                {
                    var search = (MemberInfo)searchCriteria;

                    if (!current.Name.Equals(search.Name))
                        return false;

                    if (current.MemberType != search.MemberType)
                        return false;

                    if (current.MemberType == MemberTypes.Method)
                        return EqualsMethod((MethodInfo)current, (MethodInfo)search);

                    if (current.MemberType == MemberTypes.Property)
                        return EqualsProperty((PropertyInfo)current, (PropertyInfo)search);

                    throw new InvalidOperationException("Should not be here: " + current.MemberType);

                }, methodInfo);

            if (result.Length == 0)
            {
                throw new ArgumentException(string.Format("The method '{0}.{1}({2})' does not exist.",
                       type.FullName, type.Name, string.Join(", ", methodInfo.GetParameters().Select(a => a.Name))));
            }

            Debug.Assert(result.Length == 1, "result.Length == 1", string.Format("Found {0} for {1} in type {2}.", result.Length, methodInfo, type));

            return result[0] as MethodInfo;
        }

        private static bool EqualsProperty(PropertyInfo current, PropertyInfo search)
        {
            if (current.CanRead != search.CanRead || current.CanWrite != search.CanWrite)
                return false;

            if (current.PropertyType != search.PropertyType)
                return false;

            if (current.CanRead && !EqualsMethod(current.GetGetMethod(true), search.GetGetMethod(true)))
                return false;

            if (current.CanWrite && !EqualsMethod(current.GetSetMethod(true), search.GetSetMethod(true)))
                return false;

            return true;
        }

        private static bool EqualsMethod(MethodInfo current, MethodInfo search)
        {
            if (current.ReturnType != search.ReturnType)
                return false;

            var mp = current.GetParameters();
            var sp = search.GetParameters();

            if (mp.Length != sp.Length)
                return false;

            if (mp.Where((t, i) => t.ParameterType != sp[i].ParameterType).Any())
                return false;

            return true;
        }

        public static void AddDebuggableAttribute(this AssemblyBuilder assemblyBuilder)
        {
            var ctor = typeof(DebuggableAttribute).GetConstructor(new[] { typeof(DebuggableAttribute.DebuggingModes) });
            Debug.Assert(ctor != null, "ctor != null", "DebuggableAttribute.DebuggableAttribute(DebuggingModes) ctor missing.");

            var debugBuilder = new CustomAttributeBuilder(ctor, new object[] {
                DebuggableAttribute.DebuggingModes.DisableOptimizations |
                DebuggableAttribute.DebuggingModes.Default });

            assemblyBuilder.SetCustomAttribute(debugBuilder);
        }

        public static void AddDebuggerStepThroughAttribute(this TypeBuilder typeBuilder)
        {
            var ctor = typeof(DebuggerStepThroughAttribute).GetConstructor(Type.EmptyTypes);
            Debug.Assert(ctor != null, "ctor != null", "DebuggerStepThroughAttribute.DebuggerStepThroughAttribute() ctor missing.");

            typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(ctor, EmptyObjects));
        }

        public static void AddGeneratedCodeAttribute(this TypeBuilder typeBuilder)
        {
            var ctor = typeof(GeneratedCodeAttribute).GetConstructor(new[] { typeof(string), typeof(string) });
            Debug.Assert(ctor != null, "ctor != null", "GeneratedCodeAttribute.GeneratedCodeAttribute(string, string) ctor missing.");

            typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(ctor, new object[] { "ILStubGen", s_version }));
        }

        public static MethodInfo GetNonPublicInstancePropertyGetter(this Type type, string name)
        {
            var propertyInfo = type.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            if (propertyInfo == null)
            {
                throw new ArgumentException(string.Format("The instance property '{0}.{1}' does not exist.", type.FullName, name));
            }

            if (!propertyInfo.CanRead)
            {
                throw new ArgumentException(string.Format("The instance property '{0}.{1}' defines no getter.", type.FullName, name));
            }

            return propertyInfo.GetGetMethod(true);
        }

        public static MethodInfo GetNonPublicVoidInstanceMethod(this Type type, string name)
        {
            var methodInfo = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

            if (methodInfo == null)
            {
                throw new ArgumentException(string.Format("The instance method '{0}.{1}' does not exist.", type.FullName, name));
            }

            return methodInfo;
        }

        public static ConstructorInfo GetNonPublicConstructor(this Type type, params Type[] parameterTypes)
        {
            parameterTypes = parameterTypes ?? Type.EmptyTypes;

            var constructorInfo = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes, null);
            if (constructorInfo == null)
            {
                throw new ArgumentException(string.Format("The constructor '{0}.{1}({2})' does not exist.",
                        type.FullName, type.Name, string.Join(", ", parameterTypes.Select(a => a.Name))));
            }

            return constructorInfo;
        }

        public static void EmitMethodPreamble(this ILGenerator gen, MethodInfo methodInfo, bool debug)
        {
            if (debug)
            {
                if (methodInfo.ReturnType != typeof(void))
                {
                    gen.DeclareLocal(methodInfo.ReturnType);
                }
                gen.Emit(OpCodes.Nop);
            }
        }

        public static void EmitMethodPostamble(this ILGenerator gen, MethodInfo methodInfo, bool debug)
        {
            if (debug)
            {
                if (methodInfo.ReturnType != typeof(void))
                {
                    gen.Emit(OpCodes.Stloc_0);
                    var label = gen.DefineLabel();
                    gen.Emit(OpCodes.Br_S, label);
                    gen.MarkLabel(label);
                    gen.Emit(OpCodes.Ldloc_0);
                }
                else
                {
                    gen.Emit(OpCodes.Nop);
                }
            }

            gen.Emit(OpCodes.Ret);
        }

        public static void EmitMethodCallVirt(this ILGenerator gen, MethodInfo methodInfo, bool debug)
        {
            for (int i = 1; i <= methodInfo.GetParameters().Length; i++)
            {
                gen.EmitLdarg(i);
            }

            gen.Emit(OpCodes.Callvirt, methodInfo);
        }

        public static void EmitLdarg(this ILGenerator gen, int arg)
        {
            switch (arg)
            {
                case 0:
                    gen.Emit(OpCodes.Ldarg_0);
                    break;
                case 1:
                    gen.Emit(OpCodes.Ldarg_1);
                    break;
                case 2:
                    gen.Emit(OpCodes.Ldarg_2);
                    break;
                case 3:
                    gen.Emit(OpCodes.Ldarg_3);
                    break;
                default:
                    gen.Emit(OpCodes.Ldarg, arg);
                    break;
            }
        }

        public static string GetFullName(this MemberInfo memberInfo)
        {
            // Don't change this! Several code depends on the namespace name being included with this overload.
            return GetFullName(memberInfo, true);
        }

        public static string GetFullName(this MemberInfo memberInfo, bool includeNamespace)
        {
            if (memberInfo == null)
                throw new ArgumentNullException("memberInfo");

            string name = memberInfo.Name;
            if (memberInfo.DeclaringType != null)
            {
                if (includeNamespace)
                    name = memberInfo.DeclaringType.FullName + "." + name;
                else
                    name = memberInfo.DeclaringType.Name + "." + name;
            }

            return name;
        }
    }
}