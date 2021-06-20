using System;
using System.Collections.Generic;
using System.Linq;

namespace appsync
{
    public static class ObjectValidation
    {
        public static List<string> GetUnsetStrings<T>(this T instance)
        {
            var type = typeof(T);
            var props = type.GetProperties().ToList();

            return props.Where(p => p.PropertyType == typeof(string)
                                    && instance == null || string.IsNullOrWhiteSpace((string)p.GetValue(instance)))
                            .Select(x => $"{type.Name}.{x.Name}")
                            .ToList();
        }
    }
}
