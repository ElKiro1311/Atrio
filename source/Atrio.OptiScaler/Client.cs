using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Atrio.OptiScaler
{
    /// <summary>The client doesn't have something the bridge needs: another version, with other names.</summary>
    internal sealed class NotInThisVersionException : Exception
    {
        public NotInThisVersionException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// The way to the classes of OptiScaler Client. Everything goes by name and nothing is compiled against it, so that a
    /// new version of the client can add parameters, move members around or drop some of them without breaking the
    /// bridge: what is missing turns into a function that isn't offered, not into a crash.
    /// </summary>
    internal static class Client
    {
        private const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        public const string AssemblyName = "OptiscalerClient";
        private const string Services = "OptiscalerClient.Services.";
        private const string Models = "OptiscalerClient.Models.";
        private const string Helpers = "OptiscalerClient.Helpers.";
        private const string Views = "OptiscalerClient.Views.";

        private static Assembly assembly;

        public static Assembly Assembly
        {
            get => assembly ??= System.Reflection.Assembly.GetEntryAssembly();
            set => assembly = value;
        }

        public static string Version => Assembly?.GetName().Version?.ToString();

        public static Type FindType(string name)
        {
            return Assembly?.GetType(name, false) ??
                Assembly?.GetType(Services + name, false) ??
                Assembly?.GetType(Models + name, false) ??
                Assembly?.GetType(Helpers + name, false) ??
                Assembly?.GetType(Views + name, false);
        }

        public static Type Type(string name)
        {
            return FindType(name) ?? throw new NotInThisVersionException($"{name} isn't in this version of OptiScaler Client.");
        }

        /// <summary>A new object, through the constructor that asks for the least.</summary>
        public static object New(string name)
        {
            var type = Type(name);
            foreach (var constructor in type.GetConstructors().OrderBy(a => a.GetParameters().Length))
            {
                var parameters = constructor.GetParameters();
                if (parameters.All(a => a.HasDefaultValue))
                {
                    return Unwrap(() => constructor.Invoke(parameters.Select(Default).ToArray()));
                }
            }

            throw new NotInThisVersionException($"{name} can't be created the way the bridge knows.");
        }

        private static object Default(ParameterInfo parameter)
        {
            if (parameter.HasDefaultValue && parameter.DefaultValue != null && parameter.DefaultValue != DBNull.Value)
            {
                var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                return type.IsEnum ? Enum.ToObject(type, parameter.DefaultValue) : parameter.DefaultValue;
            }

            return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
        }

        private static MemberInfo FindValue(Type type, string name)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(name, Everything | BindingFlags.DeclaredOnly);
                if (property != null)
                {
                    return property;
                }

                var field = current.GetField(name, Everything | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }

        private static Type TypeOf(object target)
        {
            return target as Type ?? target?.GetType() ?? throw new NotInThisVersionException("Nothing to ask.");
        }

        public static bool Has(object target, string name)
        {
            var type = TypeOf(target);
            return FindValue(type, name) != null;
        }

        /// <summary>A property or a field, of an object or of a class.</summary>
        public static object Get(object target, string name)
        {
            var type = TypeOf(target);
            var instance = target is Type ? null : target;
            switch (FindValue(type, name))
            {
                case PropertyInfo property when property.GetMethod != null:
                    return Unwrap(() => property.GetValue(property.GetMethod.IsStatic ? null : instance));
                case FieldInfo field:
                    return field.GetValue(field.IsStatic ? null : instance);
                default:
                    throw new NotInThisVersionException($"{type.Name}.{name} isn't in this version of OptiScaler Client.");
            }
        }

        /// <summary>The same without complaining: what isn't there has the value given.</summary>
        public static object TryGet(object target, string name, object otherwise = null)
        {
            if (target == null)
            {
                return otherwise;
            }

            try
            {
                return Get(target, name);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                return otherwise;
            }
        }

        public static string Text(object target, string name)
        {
            return TryGet(target, name)?.ToString();
        }

        public static bool Flag(object target, string name)
        {
            return TryGet(target, name) is bool value && value;
        }

        /// <summary>Writes a property, with the conversion it needs. False if the client doesn't have it.</summary>
        public static bool Set(object target, string name, object value)
        {
            var type = TypeOf(target);
            var instance = target is Type ? null : target;
            switch (FindValue(type, name))
            {
                case PropertyInfo property when property.SetMethod != null:
                    Unwrap(() =>
                    {
                        property.SetValue(property.SetMethod.IsStatic ? null : instance, Adapt(value, property.PropertyType));
                        return null;
                    });
                    return true;
                case FieldInfo field when !field.IsInitOnly && !field.IsLiteral:
                    field.SetValue(field.IsStatic ? null : instance, Adapt(value, field.FieldType));
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The value an enum has under a name, or null when this version doesn't have it.</summary>
        public static object EnumValue(string type, string name)
        {
            var enumType = FindType(type);
            if (enumType == null || !enumType.IsEnum || string.IsNullOrEmpty(name))
            {
                return null;
            }

            var found = Enum.GetNames(enumType).FirstOrDefault(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return found == null ? null : Enum.Parse(enumType, found);
        }

        public static bool CanConvert(object value, Type target)
        {
            if (value == null)
            {
                return !target.IsValueType || Nullable.GetUnderlyingType(target) != null;
            }

            var type = Nullable.GetUnderlyingType(target) ?? target;
            if (type.IsInstanceOfType(value))
            {
                return true;
            }

            if (type.IsEnum)
            {
                return value is string text && Enum.GetNames(type).Any(a => a.Equals(text, StringComparison.OrdinalIgnoreCase));
            }

            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(type))
            {
                return type != typeof(string) && !(value is string) && !(value is bool) && type != typeof(bool);
            }

            return value is string && type == typeof(double) && double.TryParse((string)value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        }

        private static object Adapt(object value, Type target)
        {
            if (value == null)
            {
                return target.IsValueType && Nullable.GetUnderlyingType(target) == null ? Activator.CreateInstance(target) : null;
            }

            var type = Nullable.GetUnderlyingType(target) ?? target;
            if (type.IsInstanceOfType(value))
            {
                return value;
            }

            if (type.IsEnum)
            {
                return value is string text ? Enum.Parse(type, text, true) : Enum.ToObject(type, value);
            }

            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The overload that the given values satisfy: each parameter by its name, the ones left by their type when there
        /// is no doubt, and the rest by the value they have by default. Null when none can be called.
        /// </summary>
        private static Tuple<MethodInfo, object[]> Bind(Type type, string name, IDictionary<string, object> values)
        {
            Tuple<MethodInfo, object[]> best = null;
            var bestScore = -1;
            foreach (var method in Methods(type, name))
            {
                if (method.ContainsGenericParameters)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                var arguments = new object[parameters.Length];
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pending = new List<int>();
                var score = 0;
                for (var i = 0; i < parameters.Length; i++)
                {
                    var key = values.Keys.FirstOrDefault(a => a.Equals(parameters[i].Name, StringComparison.OrdinalIgnoreCase));
                    if (key != null && CanConvert(values[key], parameters[i].ParameterType))
                    {
                        arguments[i] = Adapt(values[key], parameters[i].ParameterType);
                        taken.Add(key);
                        score += 2;
                    }
                    else
                    {
                        pending.Add(i);
                    }
                }

                var possible = true;
                foreach (var i in pending)
                {
                    var left = values.Where(a => !taken.Contains(a.Key) && a.Value != null && parameters[i].ParameterType.IsInstanceOfType(a.Value)).ToList();

                    // A renamed parameter is only taken by its type when it's unmistakable: the classes of the client.
                    if (!parameters[i].HasDefaultValue && left.Count == 1 && parameters[i].ParameterType.Assembly == Assembly)
                    {
                        arguments[i] = left[0].Value;
                        taken.Add(left[0].Key);
                        score++;
                    }
                    else if (parameters[i].HasDefaultValue)
                    {
                        arguments[i] = Default(parameters[i]);
                    }
                    else
                    {
                        possible = false;
                        break;
                    }
                }

                if (possible && score > bestScore)
                {
                    best = Tuple.Create(method, arguments);
                    bestScore = score;
                }
            }

            return best;
        }

        private static IEnumerable<MethodInfo> Methods(Type type, string name)
        {
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (var method in current.GetMethods(Everything | BindingFlags.DeclaredOnly).Where(a => a.Name == name))
                {
                    yield return method;
                }
            }
        }

        /// <summary>Whether a method could be called with these values, without calling it: the contract of a version.</summary>
        public static bool CanCall(string type, string method, params string[] known)
        {
            var found = FindType(type);
            if (found == null)
            {
                return false;
            }

            return Methods(found, method).Any(a => a.GetParameters().All(b =>
                b.HasDefaultValue ||
                known.Contains(b.Name, StringComparer.OrdinalIgnoreCase) ||
                (b.ParameterType.Assembly == Assembly && known.Contains("@" + b.ParameterType.Name, StringComparer.OrdinalIgnoreCase))));
        }

        /// <summary>
        /// Whether a method still has these parameters under their name. The ones with a value by default can go away
        /// without the call failing, and then what the person chose would be quietly lost.
        /// </summary>
        public static bool Takes(string type, string method, params string[] names)
        {
            var found = FindType(type);
            return found != null && Methods(found, method).Any(a =>
                names.All(b => a.GetParameters().Any(c => c.Name.Equals(b, StringComparison.OrdinalIgnoreCase))));
        }

        /// <summary>Calls a method of an object or of a class, and waits for it when it's a task.</summary>
        public static object Call(object target, string name, params object[] namesAndValues)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < namesAndValues.Length; i += 2)
            {
                values[(string)namesAndValues[i]] = namesAndValues[i + 1];
            }

            return CallWith(target, name, values);
        }

        public static object CallWith(object target, string name, IDictionary<string, object> values)
        {
            var type = TypeOf(target);
            var bound = Bind(type, name, values) ??
                throw new NotInThisVersionException($"{type.Name}.{name} isn't the one the bridge knows in this version of OptiScaler Client.");
            var result = Unwrap(() => bound.Item1.Invoke(bound.Item1.IsStatic ? null : target, bound.Item2));
            if (result is Task task)
            {
                // There is no window and no context to come back to in this process.
                task.GetAwaiter().GetResult();
                var taskType = task.GetType();
                return taskType.IsGenericType ? taskType.GetProperty("Result").GetValue(task) : null;
            }

            return result;
        }

        private static object Unwrap(Func<object> action)
        {
            try
            {
                return action();
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        public static List<string> Strings(object list)
        {
            return list is IEnumerable items && !(list is string) ?
                items.Cast<object>().Where(a => a != null).Select(a => a.ToString()).ToList() :
                new List<string>();
        }

        public static List<object> Items(object list)
        {
            return list is IEnumerable items && !(list is string) ? items.Cast<object>().ToList() : new List<object>();
        }
    }
}
