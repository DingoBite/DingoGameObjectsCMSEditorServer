#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace DingoGameObjectsCMSEditorServer.Editor.Clients
{
    public static class DingoCmsMcpClientRegistry
    {
        private static IReadOnlyList<IDingoCmsMcpClientConfigurator>
            _configurators;

        public static IReadOnlyList<IDingoCmsMcpClientConfigurator>
            Configurators => _configurators ??= Discover();

        public static void Invalidate()
        {
            _configurators = null;
        }

        private static IReadOnlyList<IDingoCmsMcpClientConfigurator>
            Discover()
        {
            var result = new List<IDingoCmsMcpClientConfigurator>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<
                         IDingoCmsMcpClientConfigurator>())
            {
                if (!type.IsAbstract
                    && !type.IsInterface
                    && type.GetConstructor(Type.EmptyTypes) != null
                    && Activator.CreateInstance(type) is
                        IDingoCmsMcpClientConfigurator configurator)
                {
                    result.Add(configurator);
                }
            }
            return result
                .OrderBy(item => item.DisplayName, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
#endif
