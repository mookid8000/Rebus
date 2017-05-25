﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Rebus.Extensions
{
    /// <summary>
    /// Provides extensions of <see cref="Type"/>
    /// </summary>
    public static class TypeExtensions
    {
        /// <summary>
        /// Gets the type's base types (i.e. the <see cref="Type"/> for each implemented interface and for each class inherited from, all the way up to <see cref="Object"/>)
        /// </summary>
        public static IEnumerable<Type> GetBaseTypes(this Type type)
        {
            foreach (var implementedInterface in type.GetInterfaces())
            {
                yield return implementedInterface;
            }

            while (type.BaseType != null)
            {
                yield return type.BaseType;
                type = type.BaseType;
            }
        }

        /// <summary>
        /// Gets the assembly-qualified name of the type, without any version info etc.
        /// E.g. "System.String, mscorlib"
        /// </summary>
        public static string GetSimpleAssemblyQualifiedName(this Type type)
        {
            return BuildSimpleAssemblyQualifiedName(type, new StringBuilder()).ToString();
        }

        private static StringBuilder BuildSimpleAssemblyQualifiedName(Type type, StringBuilder sb)
        {
            if (!type.IsGenericType)
            {
                sb.Append($"{type.FullName}, {type.Assembly.GetName().Name}");
                return sb;
            }

            if (type.DeclaringType != null)
                if (!type.DeclaringType.IsGenericType)
                    sb.Append($"{type.DeclaringType.FullName}+");
                else
                    throw new NotSupportedException("Generic declaring types are not supported");
            else
                sb.Append($"{type.Namespace}.");

            sb.Append($"{type.Name}[");
            var arguments = type.GetGenericArguments();
            for (var i = 0; i < arguments.Length; i++)
            {
                sb.Append(i == 0 ? "[" : ",[");
                BuildSimpleAssemblyQualifiedName(arguments[i], sb);
                sb.Append("]");
            }

            sb.Append($"], {type.Assembly.GetName().Name}");

            return sb;
        }
    }
}