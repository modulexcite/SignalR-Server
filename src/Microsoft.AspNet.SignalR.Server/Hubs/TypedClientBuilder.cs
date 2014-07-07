// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.


using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;

namespace Microsoft.AspNet.SignalR.Hubs
{
    internal static class TypedClientBuilder<T>
    {
        private const string clientModule = "Microsoft.AspNet.SignalR.Tests.Server.Hubs.TypedClient";

        // There is one static instance of _builder per T
        private static Lazy<Func<IClientProxy, T>> _builder = new Lazy<Func<IClientProxy, T>>(() => GenerateClientBuilder());

        [SuppressMessage("Microsoft.Design", "CA1000:DoNotDeclareStaticMembersOnGenericTypes", Justification = "This is used internally and by tests.")]
        public static T Build(IClientProxy proxy)
        {
            return _builder.Value(proxy);
        }

        [SuppressMessage("Microsoft.Performance", "CA1804:RemoveUnusedLocals", MessageId = "forceEvaluation", Justification = "Give me a better way to force the evaluation of Lazy<T>")]
        public static void Validate()
        {
            // The following will throw if T is not a valid type
            var forceEvaluation = _builder.Value;
        }

        private static Func<IClientProxy, T> GenerateClientBuilder()
        {
            VerifyInterface();

            var assemblyName = new AssemblyName(clientModule);

#if NET45
            AssemblyBuilder assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
#else
            AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
#endif
            ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule(clientModule);
            Type clientType = GenerateInterfaceImplementation(moduleBuilder);

            return proxy => (T)Activator.CreateInstance(clientType, proxy);
        }

        private static Type GenerateInterfaceImplementation(ModuleBuilder moduleBuilder)
        {
            TypeBuilder type = moduleBuilder.DefineType(typeof(T).Name + "Impl", TypeAttributes.Public,
                typeof(Object), new Type[] { typeof(T) });

            FieldBuilder proxyField = type.DefineField("_proxy", typeof(IClientProxy), FieldAttributes.Private);

            BuildConstructor(type, proxyField);

            foreach (var method in typeof(T).GetMethods())
            {
                BuildMethod(type, method, proxyField);
            }

            return type.CreateTypeInfo().AsType();
        }

        private static void BuildConstructor(TypeBuilder type, FieldInfo proxyField)
        {
            MethodBuilder method = type.DefineMethod(".ctor", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.HideBySig);

            ConstructorInfo ctor = typeof(object).GetTypeInfo().DeclaredConstructors.First(c => c.GetParameters().Length == 0);

            method.SetReturnType(typeof(void));
            method.SetParameters(typeof(IClientProxy));

            ILGenerator generator = method.GetILGenerator();

            // Call object constructor
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Call, ctor);

            // Assign constructor argument to the proxyField
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Stfld, proxyField);
            generator.Emit(OpCodes.Ret);
        }

        private static void BuildMethod(TypeBuilder type, MethodInfo interfaceMethodInfo, FieldInfo proxyField)
        {
            MethodAttributes methodAttributes =
                  MethodAttributes.Public
                | MethodAttributes.Virtual
                | MethodAttributes.Final
                | MethodAttributes.HideBySig
                | MethodAttributes.NewSlot;

            ParameterInfo[] parameters = interfaceMethodInfo.GetParameters();
            Type[] paramTypes = parameters.Select(param => param.ParameterType).ToArray();

            MethodBuilder methodBuilder = type.DefineMethod(interfaceMethodInfo.Name, methodAttributes, typeof(void), paramTypes);

            MethodInfo invokeMethod = typeof(IClientProxy).GetRuntimeMethod("Invoke", new Type[] { typeof(string), typeof(object[]) });

            methodBuilder.SetReturnType(interfaceMethodInfo.ReturnType);
            methodBuilder.SetParameters(paramTypes);

            ILGenerator generator = methodBuilder.GetILGenerator();

            // Declare local variable to store the arguments to IClientProxy.Invoke
            generator.DeclareLocal(typeof(object[]));

            // Get IClientProxy
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, proxyField);

            // The first argument to IClientProxy.Invoke is this method's name
            generator.Emit(OpCodes.Ldstr, interfaceMethodInfo.Name);

            // Create an new object array to hold all the parameters to this method
            generator.Emit(OpCodes.Ldc_I4, parameters.Length);
            generator.Emit(OpCodes.Newarr, typeof(object));
            generator.Emit(OpCodes.Stloc_0);

            // Store each parameter in the object array
            for (int i = 0; i < paramTypes.Length; i++)
            {
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Ldc_I4, i);
                generator.Emit(OpCodes.Ldarg, i + 1);
                generator.Emit(OpCodes.Box, paramTypes[i]);
                generator.Emit(OpCodes.Stelem_Ref);
            }

            // Call IProxy.Invoke
            generator.Emit(OpCodes.Ldloc_0);
            generator.Emit(OpCodes.Callvirt, invokeMethod);

            if (interfaceMethodInfo.ReturnType == typeof(void))
            {
                // void return
                generator.Emit(OpCodes.Pop);
            }

            generator.Emit(OpCodes.Ret);
        }

        private static void VerifyInterface()
        {
            var interfaceType = typeof(T);

            if (!interfaceType.GetTypeInfo().IsInterface)
            {
                throw new InvalidOperationException(
                    String.Format(CultureInfo.CurrentCulture, Resources.Error_TypeMustBeInterface, interfaceType.Name));
            }

            if (interfaceType.GetProperties().Length != 0)
            {
                throw new InvalidOperationException(
                    String.Format(CultureInfo.CurrentCulture, Resources.Error_TypeMustNotContainProperties, interfaceType.Name));
            }

            if (interfaceType.GetEvents().Length != 0)
            {
                throw new InvalidOperationException(
                    String.Format(CultureInfo.CurrentCulture, Resources.Error_TypeMustNotContainEvents, interfaceType.Name));
            }

            foreach (var method in interfaceType.GetMethods())
            {
                VerifyMethod(interfaceType, method);
            }
        }

        private static void VerifyMethod(Type interfaceType, MethodInfo interfaceMethod)
        {
            if (interfaceMethod.ReturnType != typeof(void) && interfaceMethod.ReturnType != typeof(Task))
            {
                throw new InvalidOperationException(
                    String.Format(CultureInfo.CurrentCulture, Resources.Error_MethodMustReturnVoidOrTask,
                        interfaceType.Name,
                        interfaceMethod.Name));
            }

            foreach (var parameter in interfaceMethod.GetParameters())
            {
                VerifyParameter(interfaceType, interfaceMethod, parameter);
            }
        }

        private static void VerifyParameter(Type interfaceType, MethodInfo interfaceMethod, ParameterInfo parameter)
        {
            if (parameter.IsOut)
            {
                throw new InvalidOperationException(
                    String.Format(CultureInfo.CurrentCulture, Resources.Error_MethodMustNotTakeOutParameter,
                        parameter.Name,
                        interfaceType.Name,
                        interfaceMethod.Name));
            }

            if (parameter.ParameterType.IsByRef)
            {
                throw new InvalidOperationException(
                    String.Format(CultureInfo.CurrentCulture, Resources.Error_MethodMustNotTakeRefParameter,
                        parameter.Name,
                        interfaceType.Name,
                        interfaceMethod.Name));
            }
        }
    }
}